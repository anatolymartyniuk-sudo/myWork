using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Management;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;

using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UsbBlockTray
{
    internal static class Program
    {
        private const string MutexName = @"Local\UsbBlockTray_SingleInstance_v2";

        public static readonly string Title = "USB-блокировка";

        // Сообщение при блокировке постороннего накопителя (обязательный текст)
        public static readonly string NotifyText =
            "ДОСТУП ЗАБЛОКОВАНО, ЗВЕРНІТЬСЯ ДО АДМІНІСТРАТОРА";

        [STAThread]
        private static int Main(string[] args)
        {
            bool diag = false;
            bool selftest = false;
            bool makeCopies = false;
            bool service = false;
            bool logon = false;

            foreach (string a in args)
            {
                if (string.Equals(a, "--diag", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-diag", StringComparison.OrdinalIgnoreCase))
                    diag = true;
                if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-selftest", StringComparison.OrdinalIgnoreCase))
                    selftest = true;
                if (string.Equals(a, "--makecopies", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-makecopies", StringComparison.OrdinalIgnoreCase))
                    makeCopies = true;
                if (string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-service", StringComparison.OrdinalIgnoreCase))
                    service = true;
                if (string.Equals(a, "--logon", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-logon", StringComparison.OrdinalIgnoreCase))
                    logon = true;
            }

            if (service)
            {
                // Запуск диспетчером служб (SCM): служба работает с системной
                // учётной записью (LocalSystem), интерфейса нет - мониторинг идёт
                // в фоне, уведомления пишутся в журнал событий.
                try
                {
                    ServiceBase.Run(new UsbBlockService());
                }
                catch (Exception ex)
                {
                    File.WriteAllText(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "service.log"),
                        ex.ToString(), new UTF8Encoding(true));
                    return 1;
                }
                return 0;
            }

            if (makeCopies)
            {
                StringBuilder log = new StringBuilder("Install copies: ");
                log.Append(ProtectedCopy.EnsureInstalledCopy());
                File.WriteAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installcopy.log"),
                    log.ToString() + Environment.NewLine +
                    "InstallExe exists: " + File.Exists(ProtectedCopy.InstallExe) + Environment.NewLine +
                    "MaaExe exists: " + File.Exists(ProtectedCopy.MaaExe),
                    new UTF8Encoding(true));
                return 0;
            }

            if (selftest)
            {
                File.WriteAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log"),
                    "SELFTEST: " + Selftest.Run(), new UTF8Encoding(true));
                return 0;
            }

            if (diag)
            {
                return Diag.Run();
            }

            // Запуск из задачи Планировщика при входе пользователя (--logon,
            // значок в трее + уведомления для ВСЕХ пользователей): повышения
            // прав НЕ запрашиваем. У администраторов задача запускается уже
            // с полным токеном (RunLevel=Highest), у остальных - без прав;
            // меню при этом автоматически сокращается до уведомлений.
            if (!logon && !IsAdministrator())
            {
                // Повышение прав (UAC) на старте - иначе нельзя применять
                // блокировку и управлять настройками
                try
                {
                    System.Diagnostics.ProcessStartInfo psi =
                        new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath);
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    System.Diagnostics.Process.Start(psi);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось получить права администратора.\n" + ex.Message,
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
                return 0;
            }

            // Административные действия выполняются только под администратором.
            if (IsAdministrator())
            {
                // При каждом запуске с правами администратора обновляем
                // защищённые копии exe (Program Files\USB_Block и
                // C:\ProgramData\MAA, папка создаётся при отсутствии), чтобы
                // они не устаревали после правок.
                ProtectedCopy.EnsureInstalledCopy();

                // Автозапуск больше не поддерживается: молча убираем возможные
                // остатки старых версий (задача Планировщика и Run-ключ), чтобы
                // программа не стартовала вместе с Windows.
                AutoStart.Cleanup();
            }

            bool created;
            using (Mutex mtx = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    MessageBox.Show("Программа уже запущена (значок в трее).",
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (TrayContext ctx = new TrayContext())
                {
                    Application.Run(ctx);
                }
            }
            return 0;
        }

        public static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
    }

    // =====================================================================
    // Данные whitelist: одно устройство = одна запись (USB + диск + серийник)
    // =====================================================================
    public sealed class DeviceEntry
    {
        public string Name;
        public string UsbId;   // USB\VID_xxxx&PID_xxxx (нормализованный)
        public string DiskId;  // конкретный HardwareID диска USBSTOR\DISK...
        public string Serial;  // серийный номер накопителя
        public DateTime AddedAt;
    }

    public static class StorePaths
    {
        public static string Directory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "USB_Block"); }
        }

        public static string File
        {
            get { return Path.Combine(Directory, "whitelist.dat"); }
        }
    }

    // =====================================================================
    // Защищённая копия программы (C:\Program Files\USB_Block)
    // Обычный пользователь не может удалить или изменить её (ACL закрыт).
    // Автозапуск и служба всегда создают/используют именно эту копию,
    // поэтому программа работает независимо от того, где её запустили
    // и под каким пользователем выполнен вход.
    // =====================================================================
    public static class ProtectedCopy
    {
        public static string InstallDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "USB_Block");
            }
        }

        public static string InstallExe
        {
            get { return Path.Combine(InstallDir, "usb_block_tray.exe"); }
        }

        // Дополнительная защищённая копия в C:\ProgramData\MAA
        public static string MaaDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData), "MAA");
            }
        }

        public static string MaaExe
        {
            get { return Path.Combine(MaaDir, "usb_block_tray.exe"); }
        }

        /// <summary>
        /// Копирует текущий exe в защищённые папки: Program Files\USB_Block
        /// и C:\ProgramData\MAA (создаётся при отсутствии).
        /// null = успех, иначе текст ошибки.
        /// </summary>
        public static string EnsureInstalledCopy()
        {
            string err1 = CopyTo(InstallDir, InstallExe);
            string err2 = CopyTo(MaaDir, MaaExe);
            if (err1 != null && err2 != null)
                return "Program Files\\USB_Block: " + err1 + "\n" +
                       "ProgramData\\MAA: " + err2;
            if (err1 != null)
                return "Program Files\\USB_Block: " + err1;
            if (err2 != null)
                return "ProgramData\\MAA: " + err2;
            return null;
        }

        private static string CopyTo(string dir, string exePath)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.Copy(Application.ExecutablePath, exePath, true);
                RestrictDirectoryAcl(dir);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static void RestrictDirectoryAcl(string dir)
        {
            try
            {
                DirectorySecurity ds = Directory.GetAccessControl(dir);
                ds.SetAccessRuleProtection(true, false);
                ds.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                ds.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                ds.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier("S-1-5-11"),
                    FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(dir, ds);
            }
            catch
            {
            }
        }
    }

    // =====================================================================
    // Хранилище whitelist в бинарном шифрованном виде (DPAPI + ACL)
    // Не текстовый формат, недоступен для чтения/правки обычным пользователем.
    // Перенос между компьютерами - через PortableWhitelist (.wlb).
    // =====================================================================
    public static class WhitelistStore
    {
        private const byte PayloadVer = 2;
        private static readonly byte[] Magic = { (byte)'U', (byte)'S', (byte)'B', (byte)'W', 1, 0 };

        private static readonly byte[] Entropy =
        {
            0x55, 0x53, 0x42, 0x5F, 0x42, 0x4C, 0x4F, 0x43, 0x4B,
            0x44, 0x45, 0x56, 0x31, 0x00, 0x2E, 0x13, 0x65
        };

        // ---- сериализация списка записей (без служебной шапки) ----
        public static byte[] SerializePayload(List<DeviceEntry> list)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
                {
                    bw.Write(PayloadVer);
                    bw.Write(list.Count);
                    foreach (DeviceEntry e in list)
                    {
                        WriteString(bw, e.Name);
                        WriteString(bw, e.UsbId);
                        WriteString(bw, e.DiskId);
                        WriteString(bw, e.Serial);
                        bw.Write(e.AddedAt.Ticks);
                    }
                }
                return ms.ToArray();
            }
        }

        public static List<DeviceEntry> DeserializePayload(byte[] payload)
        {
            List<DeviceEntry> list = new List<DeviceEntry>();
            using (MemoryStream ms = new MemoryStream(payload))
            using (BinaryReader br = new BinaryReader(ms))
            {
                byte ver = br.ReadByte();
                if (ver != PayloadVer)
                    throw new InvalidDataException("неизвестная версия whitelist");
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    DeviceEntry e = new DeviceEntry();
                    e.Name = ReadString(br);
                    e.UsbId = ReadString(br);
                    e.DiskId = ReadString(br);
                    e.Serial = ReadString(br);
                    long ticks = br.ReadInt64();
                    e.AddedAt = new DateTime(ticks);
                    list.Add(e);
                }
            }
            return list;
        }

        public static List<DeviceEntry> Load()
        {
            string file = StorePaths.File;
            if (!File.Exists(file))
                return new List<DeviceEntry>();

            byte[] raw = File.ReadAllBytes(file);
            byte[] plain = ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.LocalMachine);

            for (int i = 0; i < Magic.Length; i++)
            {
                int b = plain[i];
                if (b != Magic[i])
                    throw new InvalidDataException("неверная сигнатура файла whitelist");
            }

            byte[] payload = new byte[plain.Length - Magic.Length];
            Array.Copy(plain, Magic.Length, payload, 0, payload.Length);
            return DeserializePayload(payload);
        }

        public static void Save(List<DeviceEntry> list)
        {
            byte[] payload = SerializePayload(list);

            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(Magic, 0, Magic.Length);
                ms.Write(payload, 0, payload.Length);
                byte[] plain = ms.ToArray();
                byte[] secret = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);

                Directory.CreateDirectory(StorePaths.Directory);
                File.WriteAllBytes(StorePaths.File, secret);
                RestrictAcl(StorePaths.File);
            }
        }

        private static void WriteString(BinaryWriter bw, string value)
        {
            if (value == null) value = string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            bw.Write((ushort)bytes.Length);
            bw.Write(bytes);
        }

        private static string ReadString(BinaryReader br)
        {
            ushort len = br.ReadUInt16();
            byte[] bytes = br.ReadBytes(len);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void RestrictAcl(string path)
        {
            try
            {
                FileSecurity fs = File.GetAccessControl(path);
                fs.SetAccessRuleProtection(true, false);
                fs.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None,
                    AccessControlType.Allow));
                fs.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None,
                    AccessControlType.Allow));
                File.SetAccessControl(path, fs);
            }
            catch
            {
            }
        }
    }

    // =====================================================================
    // Перенос whitelist между компьютерами: файл .wlb
    // - "USBWL" (ver 2): без пароля, бинарный (не текст)
    // - "USBWE" (ver 1): AES-256 + пароль (PBKDF2 + HMAC-SHA256)
    // =====================================================================
    public static class PortableWhitelist
    {
        private static readonly byte[] MagicPlain = { (byte)'U', (byte)'S', (byte)'B', (byte)'W', (byte)'L' };
        private static readonly byte[] MagicEnc = { (byte)'U', (byte)'S', (byte)'B', (byte)'W', (byte)'E' };
        private const int Iterations = 20000;

        public static void Export(List<DeviceEntry> list, string path, string password)
        {
            byte[] payload = WhitelistStore.SerializePayload(list);

            if (string.IsNullOrEmpty(password))
            {
                using (FileStream fs = new FileStream(path, FileMode.Create))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(MagicPlain);
                    bw.Write(payload.Length);
                    bw.Write(payload);
                }
                return;
            }

            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                byte[] salt = new byte[16];
                byte[] iv = new byte[16];
                rng.GetBytes(salt);
                rng.GetBytes(iv);

                byte[] keyMaterial = DeriveKey(password, salt, 48);
                byte[] aesKey = new byte[32];
                byte[] macKey = new byte[16];
                Array.Copy(keyMaterial, 0, aesKey, 0, 32);
                Array.Copy(keyMaterial, 32, macKey, 0, 16);

                byte[] ct = AesEncrypt(aesKey, iv, payload);
                byte[] mac = ComputeMac(macKey, salt, iv, ct);

                using (FileStream fs = new FileStream(path, FileMode.Create))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(MagicEnc);
                    bw.Write(Iterations);
                    bw.Write(salt);
                    bw.Write(iv);
                    bw.Write(mac);
                    bw.Write(ct.Length);
                    bw.Write(ct);
                }
            }
        }

        public static List<DeviceEntry> Import(string path, string password)
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length < 8) throw new InvalidDataException("файл слишком мал");

            if (HasMagic(file, MagicPlain))
            {
                using (MemoryStream ms = new MemoryStream(file))
                using (BinaryReader br = new BinaryReader(ms))
                {
                    br.ReadBytes(MagicPlain.Length);
                    int len = br.ReadInt32();
                    byte[] payload = br.ReadBytes(len);
                    return WhitelistStore.DeserializePayload(payload);
                }
            }

            if (HasMagic(file, MagicEnc))
            {
                using (MemoryStream ms = new MemoryStream(file))
                using (BinaryReader br = new BinaryReader(ms))
                {
                    br.ReadBytes(MagicEnc.Length);
                    int iter = br.ReadInt32();
                    byte[] salt = br.ReadBytes(16);
                    byte[] iv = br.ReadBytes(16);
                    byte[] mac = br.ReadBytes(32);
                    int ctLen = br.ReadInt32();
                    byte[] ct = br.ReadBytes(ctLen);

                    if (string.IsNullOrEmpty(password))
                        throw new UnauthorizedAccessException("файл защищён паролем - введите пароль");

                    byte[] keyMaterial = DeriveKey(password, salt, 48);
                    byte[] aesKey = new byte[32];
                    byte[] macKey = new byte[16];
                    Array.Copy(keyMaterial, 0, aesKey, 0, 32);
                    Array.Copy(keyMaterial, 32, macKey, 0, 16);

                    byte[] expected = ComputeMac(macKey, salt, iv, ct);
                    if (!ConstantEquals(expected, mac))
                        throw new UnauthorizedAccessException("неверный пароль или файл повреждён");

                    byte[] payload = AesDecrypt(aesKey, iv, ct);
                    return WhitelistStore.DeserializePayload(payload);
                }
            }

            throw new InvalidDataException("неизвестный формат файла whitelist");
        }

        private static byte[] DeriveKey(string password, byte[] salt, int length)
        {
            using (Rfc2898DeriveBytes pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations))
            {
                return pbkdf2.GetBytes(length);
            }
        }

        private static byte[] AesEncrypt(byte[] key, byte[] iv, byte[] data)
        {
            using (Aes aes = new AesManaged())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform enc = aes.CreateEncryptor())
                using (MemoryStream ms = new MemoryStream())
                using (CryptoStream cs = new CryptoStream(ms, enc, CryptoStreamMode.Write))
                {
                    cs.Write(data, 0, data.Length);
                    cs.FlushFinalBlock();
                    return ms.ToArray();
                }
            }
        }

        private static byte[] AesDecrypt(byte[] key, byte[] iv, byte[] ct)
        {
            using (Aes aes = new AesManaged())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform dec = aes.CreateDecryptor())
                using (MemoryStream ms = new MemoryStream(ct))
                using (CryptoStream cs = new CryptoStream(ms, dec, CryptoStreamMode.Read))
                using (MemoryStream outMs = new MemoryStream())
                {
                    cs.CopyTo(outMs);
                    return outMs.ToArray();
                }
            }
        }

        private static byte[] ComputeMac(byte[] macKey, byte[] salt, byte[] iv, byte[] ct)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(salt, 0, salt.Length);
                ms.Write(iv, 0, iv.Length);
                ms.Write(ct, 0, ct.Length);
                byte[] data = ms.ToArray();
                using (HMACSHA256 hmac = new HMACSHA256(macKey))
                {
                    return hmac.ComputeHash(data);
                }
            }
        }

        private static bool HasMagic(byte[] file, byte[] magic)
        {
            if (file.Length < magic.Length) return false;
            for (int i = 0; i < magic.Length; i++)
                if (file[i] != magic[i]) return false;
            return true;
        }

        private static bool ConstantEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    // =====================================================================
    // Работа с системой: WMI, реестр, pnputil
    // =====================================================================
    public sealed class UsbNode
    {
        public string InstanceId;
        public string VidPid;   // USB\VID_xxxx&PID_yyyy (нормализованный)
        public string Serial;
        public string Name;
        public bool Present;
    }

    public sealed class StorageDevice
    {
        public string InstanceId;   // USBSTOR\DISK&...\serial&0
        public string Model;
        public string UsbId;        // USB\VID_xxxx&PID_xxxx
        public string Serial;
        public List<string> HardwareIds = new List<string>();
        public string BestDiskId;   // конкретный HardwareID диска
        public string DiskDeviceId; // \\.\PHYSICALDRIVE<n>
    }

    public sealed class BlockedDevice
    {
        public string Label;
        public string Serial;
    }

    // Смонтированный том USB-накопителя (точка монтирования - буква диска)
    public sealed class UsbVolume
    {
        public string DriveLetter;  // "D" (без двоеточия)
        public string DiskId;       // \\.\PHYSICALDRIVE<n>
        public string UsbId;        // USB\VID_xxxx&PID_xxxx
        public string Serial;
        public string Model;
    }

    public static class Exec
    {
        public static bool Run(string fileName, string arguments, out string output)
        {
            output = string.Empty;
            try
            {
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo(fileName, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    output = (so ?? string.Empty) + (se ?? string.Empty);
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }
    }

    public static class PnPUtil
    {
        public static void Scan()
        {
            string o;
            Exec.Run("pnputil.exe", "/scan-devices", out o);
        }

        public static void EnableDevice(string instanceId)
        {
            string o;
            Exec.Run("pnputil.exe", "/enable-device \"" + instanceId + "\"", out o);
        }

        public static void DisableDevice(string instanceId)
        {
            string o;
            Exec.Run("pnputil.exe", "/disable-device \"" + instanceId + "\"", out o);
        }

        public static void RemoveDevice(string instanceId)
        {
            string o;
            Exec.Run("pnputil.exe", "/remove-device \"" + instanceId + "\"", out o);
        }
    }

    // Заблокированный (снятый с монтирования) том USB-накопителя.
    // Хранится в реестре, чтобы накопитель можно было вернуть в whitelist
    // и разрешить его монтирование даже после перезапуска программы.
    public sealed class BlockedRecord
    {
        public string Letter;      // исходная буква ("E")
        public string VolumePath;  // \\?\Volume{GUID}
        public string Serial;      // серийный номер накопителя
        public string UsbId;       // USB\VID_xxxx&PID_xxxx
    }

    // Запрет монтирования: снятие точки монтирования (буквы диска) тома
    // НЕ отключает драйвер и не запрещает установку; устройство распознаётся,
    // но не получает доступа к данным (том становится "не монтируемым").
    public static class MountUtil
    {
        // Реестр учёта снятых букв: HKLM\SOFTWARE\USB_Block\UnmountedVolumes
        // Значение "X" -> список записей тома через ";", каждая запись:
        //   <путь тома>|<серийник>|<USB ID>
        // Нужен, т.к. mountvol /r сам точки НЕ возвращает (см. справку:
        // /p делает том неподключаемым, /r чистит только записи
        // несуществующих томов).
        private const string TrackKey = @"SOFTWARE\USB_Block\UnmountedVolumes";

        // /p - снять точку монтирования и сделать том недоступным для монтирования.
        // Перед снятием запоминаем GUID тома (и серийник/USB ID), чтобы потом
        // вернуть букву и разрешить накопитель через whitelist.
        public static bool Unmount(UsbVolume v)
        {
            if (v == null || string.IsNullOrEmpty(v.DriveLetter)) return false;
            string volPath = GetVolumePath(v.DriveLetter);
            if (!string.IsNullOrEmpty(volPath))
                Track(v.DriveLetter, volPath, v.Serial, v.UsbId);
            string o;
            return Exec.Run("mountvol.exe", v.DriveLetter + ": /p", out o);
        }

        // /r - очистить записи точек монтирования несуществующих томов
        public static void RestoreAll()
        {
            string o;
            Exec.Run("mountvol.exe", "/r", out o);
        }

        // Заново создаёт точки подключения всех томов, снятых этим приложением
        // (mountvol <буква>: \\?\Volume{GUID}\). Возвращает число восстановленных.
        public static int RestoreUnmounted()
        {
            int restored = 0;
            foreach (BlockedRecord rec in GetTracked())
            {
                if (string.IsNullOrEmpty(rec.VolumePath)) continue;
                string target = FreeLetter(rec.Letter);
                if (string.IsNullOrEmpty(target)) continue;
                if (Remount(target, rec.VolumePath))
                    restored++;
            }
            ClearTrack();
            return restored;
        }

        // Список томов, снятых приложением (для показа заблокированных
        // накопителей и последующего занесения в whitelist).
        public static List<BlockedRecord> GetTracked()
        {
            List<BlockedRecord> result = new List<BlockedRecord>();
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(TrackKey, false))
                {
                    if (k == null) return result;
                    foreach (string letter in k.GetValueNames())
                    {
                        string raw = k.GetValue(letter) as string;
                        if (string.IsNullOrEmpty(raw)) continue;
                        foreach (string item in raw.Split(';'))
                        {
                            if (string.IsNullOrEmpty(item)) continue;
                            string[] parts = item.Split('|');
                            BlockedRecord rec = new BlockedRecord();
                            rec.Letter = letter;
                            rec.VolumePath = parts.Length > 0 ? parts[0] : null;
                            rec.Serial = parts.Length > 1 ? parts[1] : null;
                            rec.UsbId = parts.Length > 2 ? parts[2] : null;
                            if (!string.IsNullOrEmpty(rec.VolumePath))
                                result.Add(rec);
                        }
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        // Разрешает (монтирует) конкретный заблокированный накопитель по
        // серийному номеру: находит запись и заново создаёт точку подключения.
        public static bool AllowTracked(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return false;
            bool ok = false;
            List<BlockedRecord> keep = new List<BlockedRecord>();
            foreach (BlockedRecord rec in GetTracked())
            {
                if (string.Equals(rec.Serial, serial, StringComparison.OrdinalIgnoreCase))
                {
                    string target = FreeLetter(rec.Letter);
                    if (!string.IsNullOrEmpty(target) && Remount(target, rec.VolumePath))
                        ok = true;
                }
                else
                {
                    keep.Add(rec);
                }
            }
            WriteTracked(keep);
            return ok;
        }

        private static void WriteTracked(List<BlockedRecord> list)
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(TrackKey, false);
                if (list.Count == 0) return;
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(TrackKey))
                {
                    foreach (BlockedRecord rec in list)
                    {
                        string item = (rec.VolumePath ?? string.Empty) + "|" +
                                      (rec.Serial ?? string.Empty) + "|" +
                                      (rec.UsbId ?? string.Empty);
                        string existing = k.GetValue(rec.Letter) as string ?? string.Empty;
                        string value = existing.Length > 0 ? existing + ";" + item : item;
                        k.SetValue(rec.Letter, value, RegistryValueKind.String);
                    }
                }
            }
            catch
            {
            }
        }

        public static void ClearTrack()
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(TrackKey, false); }
            catch { }
        }

        // Создаёт точку подключения: mountvol <буква>: <путь тома>
        public static bool Remount(string letter, string volumePath)
        {
            if (string.IsNullOrEmpty(letter) || string.IsNullOrEmpty(volumePath)) return false;
            string o;
            return Exec.Run("mountvol.exe", letter + ": " + volumePath + "\\", out o);
        }

        // Вытаскивает путь тома \\?\Volume{GUID} по букве (mountvol X:\ /L).
        private static string GetVolumePath(string letter)
        {
            string o;
            if (!Exec.Run("mountvol.exe", letter + ":\\ /L", out o)) return null;
            using (StringReader sr = new StringReader(o ?? string.Empty))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    int i = line.IndexOf(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    int j = line.IndexOf('}', i);
                    if (j < 0) continue;
                    string vol = line.Substring(i, line.Length - i).TrimEnd('\\');
                    if (!string.IsNullOrEmpty(vol))
                        return vol;
                }
            }
            return null;
        }

        private static void Track(string letter, string volumePath, string serial, string usbId)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(TrackKey))
                {
                    string existing = k.GetValue(letter) as string ?? string.Empty;
                    if (existing.IndexOf(volumePath, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        string item = volumePath + "|" + (serial ?? string.Empty) +
                                      "|" + (usbId ?? string.Empty);
                        string value = existing.Length > 0 ? existing + ";" + item : item;
                        k.SetValue(letter, value, RegistryValueKind.String);
                    }
                }
            }
            catch
            {
            }
        }

        private static string FreeLetter(string preferred)
        {
            string[] used = Environment.GetLogicalDrives();
            if (!string.IsNullOrEmpty(preferred) && preferred.Length == 1 &&
                char.IsLetter(preferred[0]) && !IsLetterUsed(used, preferred))
                return preferred.ToUpperInvariant();
            for (char c = 'Z'; c >= 'C'; c--)
                if (!IsLetterUsed(used, c.ToString()))
                    return c.ToString();
            return null;
        }

        private static bool IsLetterUsed(string[] used, string letter)
        {
            foreach (string d in used)
                if (string.Equals(d, letter + ":\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    public static class UsbQuery
    {
        private const string EnumBase = @"SYSTEM\CurrentControlSet\Enum";

        public static List<StorageDevice> GetUsbStorages()
        {
            List<StorageDevice> list = new List<StorageDevice>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\cimv2", "SELECT * FROM Win32_DiskDrive WHERE InterfaceType='USB'"))
                using (ManagementObjectCollection coll = searcher.Get())
                {
                    foreach (ManagementObject mo in coll)
                    {
                        string dev = (string)mo["PNPDeviceID"];
                        string model = (string)mo["Model"];
                        if (string.IsNullOrEmpty(dev) ||
                            !dev.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        StorageDevice sd = new StorageDevice();
                        sd.InstanceId = dev;
                        sd.Model = model == null ? string.Empty : model.Trim();
                        sd.Serial = ExtractSerial(dev);
                        sd.HardwareIds = GetHardwareIds(dev);
                        sd.BestDiskId = PickConcreteHwId(sd.HardwareIds);
                        sd.UsbId = FindUsbIdBySerial(sd.Serial);
                        sd.DiskDeviceId = mo["DeviceID"] as string ?? string.Empty;

                        list.Add(sd);
                    }
                }
            }
            catch
            {
            }
            return list;
        }

        // Перечисляет смонтированные тома USB-накопителей с их буквами дисков.
        // Цепочка WMI: Win32_DiskDrive (USB) -> DiskIndex -> Win32_DiskPartition
        // -> Win32_LogicalDiskToPartition -> буква диска.
        public static List<UsbVolume> GetUsbVolumes()
        {
            return GetVolumes(true);
        }

        // То же самое, но для всех дисков (используется диагностикой/selftest)
        public static List<UsbVolume> GetAllVolumes()
        {
            return GetVolumes(false);
        }

        // Метки (имена) томов по буквам дисков: "D" -> "TRANSCEND"
        public static Dictionary<string, string> GetVolumeLabels()
        {
            Dictionary<string, string> map =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\cimv2",
                    "SELECT DeviceID, VolumeName FROM Win32_LogicalDisk"))
                using (ManagementObjectCollection coll = searcher.Get())
                {
                    foreach (ManagementObject mo in coll)
                    {
                        string dev = mo["DeviceID"] as string;
                        if (string.IsNullOrEmpty(dev) || dev.Length < 2 || dev[1] != ':') continue;
                        string label = mo["VolumeName"] as string ?? string.Empty;
                        map[dev.Substring(0, 1)] = label;
                    }
                }
            }
            catch
            {
            }
            return map;
        }

        private static List<UsbVolume> GetVolumes(bool usbOnly)
        {
            List<UsbVolume> result = new List<UsbVolume>();
            try
            {
                List<StorageDevice> disks;
                if (usbOnly)
                {
                    disks = GetUsbStorages();
                }
                else
                {
                    disks = new List<StorageDevice>();
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        "root\\cimv2",
                        "SELECT DeviceID, PNPDeviceID, Model FROM Win32_DiskDrive"))
                    using (ManagementObjectCollection coll = searcher.Get())
                    {
                        foreach (ManagementObject mo in coll)
                        {
                            StorageDevice d = new StorageDevice();
                            d.DiskDeviceId = mo["DeviceID"] as string ?? string.Empty;
                            d.Model = (mo["Model"] as string ?? string.Empty).Trim();
                            d.InstanceId = mo["PNPDeviceID"] as string ?? string.Empty;
                            disks.Add(d);
                        }
                    }
                }
                if (disks.Count == 0) return result;

                Dictionary<int, StorageDevice> diskByIndex = new Dictionary<int, StorageDevice>();
                foreach (StorageDevice sd in disks)
                {
                    int idx = ParseDriveIndex(sd.DiskDeviceId);
                    if (idx >= 0 && !diskByIndex.ContainsKey(idx))
                        diskByIndex[idx] = sd;
                }
                if (diskByIndex.Count == 0) return result;

                Dictionary<string, int> partByDevId = new Dictionary<string, int>();
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\cimv2",
                    "SELECT DeviceID, DiskIndex FROM Win32_DiskPartition"))
                using (ManagementObjectCollection coll = searcher.Get())
                {
                    foreach (ManagementObject mo in coll)
                    {
                        string pd = (string)mo["DeviceID"];
                        object di = mo["DiskIndex"];
                        if (string.IsNullOrEmpty(pd) || di == null) continue;
                        try
                        {
                            partByDevId[pd] = unchecked((int)Convert.ToUInt32(di));
                        }
                        catch
                        {
                        }
                    }
                }

                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\cimv2",
                    "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
                using (ManagementObjectCollection coll = searcher.Get())
                {
                    foreach (ManagementObject mo in coll)
                    {
                        string ant = (string)mo["Antecedent"];
                        string dep = (string)mo["Dependent"];
                        if (string.IsNullOrEmpty(ant) || string.IsNullOrEmpty(dep)) continue;
                        string letter = ExtractDriveLetter(dep);
                        if (string.IsNullOrEmpty(letter)) continue;

                        string partDevId = MatchPartitionId(ant, partByDevId);
                        if (partDevId == null) continue;
                        int idx = partByDevId[partDevId];
                        StorageDevice sd;
                        if (!diskByIndex.TryGetValue(idx, out sd)) continue;

                        result.Add(new UsbVolume
                        {
                            DriveLetter = letter,
                            DiskId = sd.DiskDeviceId,
                            UsbId = sd.UsbId,
                            Serial = sd.Serial,
                            Model = sd.Model
                        });
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        private static int ParseDriveIndex(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return -1;
            int i = deviceId.LastIndexOf("PHYSICALDRIVE", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            string tail = deviceId.Substring(i + "PHYSICALDRIVE".Length);
            int n;
            if (!int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out n))
                return -1;
            return n;
        }

        private static string ExtractDriveLetter(string dependent)
        {
            // \\...:Win32_LogicalDisk.DeviceID="D:"
            int q1 = dependent.IndexOf('"');
            int q2 = dependent.LastIndexOf('"');
            if (q1 < 0 || q2 <= q1) return null;
            string id = dependent.Substring(q1 + 1, q2 - q1 - 1);
            if (id.Length >= 2 && id[1] == ':' && char.IsLetter(id[0]))
                return id.Substring(0, 1).ToUpperInvariant();
            return null;
        }

        private static string MatchPartitionId(string antecedent, Dictionary<string, int> partByDevId)
        {
            foreach (string pd in partByDevId.Keys)
            {
                if (string.IsNullOrEmpty(pd)) continue;
                if (antecedent.IndexOf("\"" + pd + "\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    return pd;
            }
            return null;
        }

        public static List<UsbNode> GetUsbMassStorageNodes()
        {
            List<UsbNode> list = new List<UsbNode>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\cimv2",
                    "SELECT * FROM Win32_PnPEntity"))
                using (ManagementObjectCollection coll = searcher.Get())
                {
                    foreach (ManagementObject mo in coll)
                    {
                        string id = (string)mo["DeviceID"];
                        if (string.IsNullOrEmpty(id) ||
                            !id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (!IsMassStorageNode(id))
                            continue;

                        UsbNode n = new UsbNode();
                        n.InstanceId = id;
                        n.VidPid = ExtractVidPid(id);
                        n.Serial = ExtractSerial(id);
                        n.Name = (string)mo["Name"];
                        n.Present = IsPresent(mo);
                        if (string.IsNullOrEmpty(n.VidPid))
                            continue;
                        list.Add(n);
                    }
                }
            }
            catch
            {
            }
            return list;
        }

        public static List<string> GetAllMassStorageInstances()
        {
            List<string> list = new List<string>(64);
            try
            {
                using (RegistryKey usbstor = Registry.LocalMachine.OpenSubKey(EnumBase + "\\USBSTOR"))
                {
                    if (usbstor != null)
                    {
                        foreach (string diskKey in usbstor.GetSubKeyNames())
                        {
                            if (!diskKey.StartsWith("DISK&", StringComparison.OrdinalIgnoreCase))
                                continue;
                            using (RegistryKey dk = usbstor.OpenSubKey(diskKey))
                            {
                                if (dk == null) continue;
                                foreach (string inst in dk.GetSubKeyNames())
                                    list.Add("USBSTOR\\" + diskKey + "\\" + inst);
                            }
                        }
                    }
                }

                using (RegistryKey usb = Registry.LocalMachine.OpenSubKey(EnumBase + "\\USB"))
                {
                    if (usb != null)
                    {
                        foreach (string devKey in usb.GetSubKeyNames())
                        {
                            if (!devKey.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                                continue;
                            using (RegistryKey dk = usb.OpenSubKey(devKey))
                            {
                                if (dk == null) continue;
                                foreach (string inst in dk.GetSubKeyNames())
                                {
                                    string full = "USB\\" + devKey + "\\" + inst;
                                    if (IsMassStorageNode(full))
                                        list.Add(full);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return list;
        }

        public static bool IsMassStorageNode(string instanceId)
        {
            string[] comp = GetMultiSz(EnumBase + "\\" + instanceId, "CompatibleIDs");
            if (comp != null)
            {
                foreach (string s in comp)
                {
                    if (s.IndexOf("USB\\Class_08", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    if (s.IndexOf("USBSTOR", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            string[] hw = GetMultiSz(EnumBase + "\\" + instanceId, "HardwareID");
            if (hw != null)
            {
                foreach (string s in hw)
                {
                    if (s.IndexOf("USBSTOR", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }

        public static bool IsNodeDisabled(string instanceId)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(EnumBase + "\\" + instanceId, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue("ConfigFlags");
                    if (v is int && (((int)v) & 1) != 0) return true;
                }
            }
            catch
            {
            }
            return false;
        }

        // Приводит любой вариант ID/InstanceId USB-узла к каноническому
        // USB\VID_xxxx&PID_yyyy, отбрасывая \серийник, &MI_xx, &REV_xx и т.п.
        public static string NormalizeVidPid(string usbId)
        {
            if (string.IsNullOrEmpty(usbId)) return usbId;
            string s = usbId.Trim();
            int p = s.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
            if (p < 0) return s;
            string rest = s.Substring(p);
            if (rest.Length < 8 ||
                !rest.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                return s;
            string vid = rest.Substring(0, 8);
            int amp = rest.IndexOf('&');
            if (amp < 0) return ("USB\\" + vid).ToUpperInvariant();
            string after = rest.Substring(amp + 1);
            if (after.Length < 8 ||
                !after.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
                return ("USB\\" + vid).ToUpperInvariant();
            string pid = after.Substring(0, 8);
            return ("USB\\" + vid + "&" + pid).ToUpperInvariant();
        }

        public static string FindUsbIdBySerial(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return null;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\cimv2",
                    "SELECT * FROM Win32_PnPEntity"))
                using (ManagementObjectCollection coll = searcher.Get())
                {
                    foreach (ManagementObject mo in coll)
                    {
                        string id = (string)mo["DeviceID"];
                        if (string.IsNullOrEmpty(id) ||
                            !id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        int i = id.LastIndexOf('\\');
                        if (i < 0) continue;
                        string inst = id.Substring(i + 1);
                        if (string.Equals(inst, serial, StringComparison.OrdinalIgnoreCase))
                        {
                            return NormalizeVidPid(id.Substring(0, i));
                        }
                    }
                }
            }
            catch
            {
            }

            // Fallback по реестру: устройство может быть не подключено сейчас
            // (например, заблокированный и снятый накопитель).
            foreach (string inst in GetAllMassStorageInstances())
            {
                if (!inst.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(ExtractSerial(inst), serial, StringComparison.OrdinalIgnoreCase))
                    return ExtractVidPid(inst);
            }
            return null;
        }

        // Находит конкретный HardwareID диска USBSTOR по серийному номеру
        // (в т.ч. для устройства, том которого сейчас не смонтирован).
        public static string FindDiskIdBySerial(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return null;
            foreach (string inst in GetAllMassStorageInstances())
            {
                if (!inst.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(ExtractSerial(inst), serial, StringComparison.OrdinalIgnoreCase))
                    return PickConcreteHwId(GetHardwareIds(inst));
            }
            return null;
        }

        private static bool IsPresent(ManagementObject mo)
        {
            try
            {
                object v = mo["ConfigManagerErrorCode"];
                if (v == null) return false;
                int code = unchecked((int)Convert.ToUInt32(v));
                return code >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static string[] GetMultiSz(string subkeyPath, string valueName)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(subkeyPath, false))
                {
                    if (k == null) return null;
                    return (string[])k.GetValue(valueName);
                }
            }
            catch
            {
                return null;
            }
        }

        public static List<string> GetHardwareIds(string instanceId)
        {
            string[] arr = GetMultiSz(EnumBase + "\\" + instanceId, "HardwareID");
            return arr == null ? new List<string>() : new List<string>(arr);
        }

        public static string PickConcreteHwId(List<string> hwIds)
        {
            if (hwIds == null) return null;
            foreach (string s in hwIds)
            {
                if (string.IsNullOrEmpty(s)) continue;
                string u = s.ToUpperInvariant();
                if (u.StartsWith("USB\\CLASS_", StringComparison.Ordinal) &&
                    u.IndexOf("USBSTOR", StringComparison.Ordinal) < 0)
                    continue;
                if (u == "USBSTOR\\DISK" || u == "USBSTOR\\RAW" ||
                    u == "USBSTOR\\GENDISK" || u == "GENDISK" ||
                    u == "GENSDISK" || u == "USB\\COMPOSITE" ||
                    u.StartsWith("STORAGE\\", StringComparison.Ordinal))
                    continue;
                return s;
            }
            return null;
        }

        public static string ExtractVidPid(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            return NormalizeVidPid(instanceId);
        }

        public static string ExtractSerial(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            int i = instanceId.LastIndexOf('\\');
            string s = i >= 0 ? instanceId.Substring(i + 1) : instanceId;
            int amp = s.IndexOf('&');
            if (amp > 0)
            {
                string tail = s.Substring(amp + 1);
                bool hex = tail.Length >= 1 && tail.Length <= 2;
                if (hex)
                {
                    foreach (char ch in tail)
                    {
                        if (!Uri.IsHexDigit(ch)) { hex = false; break; }
                    }
                }
                if (hex) s = s.Substring(0, amp);
            }
            return s;
        }
    }

    // =====================================================================
    // Политика DeviceInstall\Restrictions
    // =====================================================================
    public static class PolicyManager
    {
        private const string KeyPath = @"SOFTWARE\USB_Block";
        private const string ValueName = "Blocked";

        // Запрет установки драйверов (DeviceInstall\Restrictions) НЕ используется:
        // блокировка выполняется запретом монтирования тома (см. UsbMonitor/MountUtil).
        // Этот ключ остался от старых версий - вычищаем при выключении блокировки.
        private const string LegacyRestrictions =
            @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions";

        public static bool IsBlocked()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(KeyPath, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(ValueName);
                    return v != null && Convert.ToInt32(v) == 1;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetBlocked(bool blocked)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(KeyPath))
                {
                    k.SetValue(ValueName, blocked ? 1 : 0, RegistryValueKind.DWord);
                }
                if (!blocked)
                {
                    try { Registry.LocalMachine.DeleteSubKeyTree(LegacyRestrictions, false); }
                    catch { }
                }
            }
            catch
            {
            }
        }
    }

    // =====================================================================
    // Очередь событий блокировки (HKLM\SOFTWARE\USB_Block\Events).
    // Записывают блокирующие: служба (SYSTEM) и трей под администратором.
    // Читают ВСЕ треи пользователей (в т.ч. запущенные без прав) и
    // показывают уведомления - так сообщение о блокировке получают все
    // пользователи, независимо от того, кто вошёл в систему.
    // Уникальность хранится в значении (id|ticks|serial|label), номер id
    // монотонно растёт. Не-администратор может только ЧИТАТЬ ключ.
    // =====================================================================
    public static class NotifyStore
    {
        private const string EventsKey = @"SOFTWARE\USB_Block\Events";
        private const int MaxEvents = 32;
        private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(30);

        public sealed class BlockEvent
        {
            public long Id;
            public DateTime When;
            public string Serial;
            public string Label;
        }

        /// <summary>true - событие записано, false - продублировано/ошибка.</summary>
        public static bool Write(string serial, string label)
        {
            try
            {
                List<BlockEvent> cur = ReadAll();
                long lastId = 0;
                foreach (BlockEvent e in cur)
                    if (e.Id > lastId) lastId = e.Id;

                // Дедупликация: то же устройство уже заблокировано недавно
                // (служба и трей могут снять букву по очереди).
                foreach (BlockEvent e in cur)
                {
                    if (DateTime.Now - e.When < DedupWindow &&
                        SameDevice(e.Serial, e.Label, serial, label))
                        return false;
                }

                long newId = lastId + 1;
                List<BlockEvent> trimmed = new List<BlockEvent>();
                int skip = Math.Max(0, cur.Count - (MaxEvents - 1));
                for (int i = skip; i < cur.Count; i++) trimmed.Add(cur[i]);

                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(EventsKey))
                {
                    foreach (BlockEvent e in trimmed)
                        k.SetValue("E" + e.Id, FormatValue(e), RegistryValueKind.String);
                    BlockEvent ne = new BlockEvent
                    {
                        Id = newId,
                        When = DateTime.Now,
                        Serial = serial,
                        Label = label
                    };
                    k.SetValue("E" + ne.Id, FormatValue(ne), RegistryValueKind.String);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool SameDevice(string s1, string l1, string s2, string l2)
        {
            if (!string.IsNullOrEmpty(s1) && !string.IsNullOrEmpty(s2))
                return string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(l1) && !string.IsNullOrEmpty(l2))
                return string.Equals(l1, l2, StringComparison.OrdinalIgnoreCase);
            // Нет общего ключа для сравнения - не дедуплицируем.
            return false;
        }

        /// <summary>Список событий в порядке возрастания id.</summary>
        public static List<BlockEvent> ReadAll()
        {
            List<BlockEvent> result = new List<BlockEvent>();
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(EventsKey, false))
                {
                    if (k == null) return result;
                    foreach (string name in k.GetValueNames())
                    {
                        string raw = k.GetValue(name) as string;
                        if (string.IsNullOrEmpty(raw)) continue;
                        BlockEvent e = ParseValue(raw);
                        if (e != null) result.Add(e);
                    }
                }
            }
            catch
            {
            }
            result.Sort((a, b) => a.Id.CompareTo(b.Id));
            return result;
        }

        private static string FormatValue(BlockEvent e)
        {
            return e.Id.ToString(CultureInfo.InvariantCulture) + "|" +
                   e.When.Ticks.ToString(CultureInfo.InvariantCulture) + "|" +
                   (e.Serial ?? string.Empty) + "|" + (e.Label ?? string.Empty);
        }

        private static BlockEvent ParseValue(string raw)
        {
            try
            {
                string[] p = raw.Split('|');
                if (p.Length < 2) return null;
                BlockEvent e = new BlockEvent();
                e.Id = long.Parse(p[0], CultureInfo.InvariantCulture);
                e.When = new DateTime(long.Parse(p[1], CultureInfo.InvariantCulture));
                e.Serial = p.Length > 2 ? p[2] : null;
                e.Label = p.Length > 3 ? p[3] : null;
                return e;
            }
            catch
            {
                return null;
            }
        }

        // ---- Индивидуальный счётчик просмотренных событий (HKCU) ----

        private const string LastSeenKey = @"Software\USB_Block";
        private const string LastSeenValue = "LastEventId";

        public static long GetLastSeen()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(LastSeenKey, false))
                {
                    if (k == null) return 0;
                    object v = k.GetValue(LastSeenValue);
                    return v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                return 0;
            }
        }

        public static void SetLastSeen(long id)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(LastSeenKey))
                    k.SetValue(LastSeenValue, id, RegistryValueKind.QWord);
            }
            catch
            {
            }
        }

        public static void Clear()
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(EventsKey, false); }
            catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(LastSeenKey, false); }
            catch { }
        }
    }

    // =====================================================================
    // Единая логика мониторинга (используется и треем, и службой)
    // =====================================================================
    public static class UsbMonitor
    {
        private static readonly object Sync = new object();
        private static List<DeviceEntry> _cache;
        private static string _cachePath;
        private static DateTime _cacheStamp;

        public static List<DeviceEntry> GetWhitelist()
        {
            EnsureCache();
            lock (Sync) return new List<DeviceEntry>(_cache);
        }

        public static void Refresh()
        {
            lock (Sync)
            {
                _cache = null;
                _cachePath = null;
                _cacheStamp = default(DateTime);
            }
        }

        private static void EnsureCache()
        {
            lock (Sync)
            {
                string path = StorePaths.File;
                DateTime stamp = File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : default(DateTime);
                if (_cache != null && _cachePath == path && _cacheStamp == stamp)
                    return;
                try
                {
                    _cache = WhitelistStore.Load();
                }
                catch
                {
                    _cache = new List<DeviceEntry>();
                }
                _cachePath = path;
                _cacheStamp = stamp;
            }
        }

        // Сканирует текущее состояние и при включённой блокировке снимает
        // точки монтирования посторонних накопителей: устройство распознаётся
        // Windows, но не получает букву диска, поэтому доступ к данным закрыт
        // (без запрета установки драйверов).
        // Возвращает список вновь заблокированных устройств.
        // nodes/disks возвращаются для дедупликации уведомлений вызывающей стороной.
        public static List<BlockedDevice> Scan(
            out List<UsbNode> nodes, out List<StorageDevice> disks)
        {
            EnsureCache();
            List<DeviceEntry> wl = null;
            lock (Sync) wl = _cache;

            bool blocked = PolicyManager.IsBlocked();
            nodes = UsbQuery.GetUsbMassStorageNodes();
            disks = UsbQuery.GetUsbStorages();

            List<BlockedDevice> newly = new List<BlockedDevice>();

            if (blocked && wl != null)
            {
                List<UsbVolume> volumes = UsbQuery.GetUsbVolumes();
                foreach (UsbVolume v in volumes)
                {
                    if (IsVolumeAllowed(v, wl)) continue;
                    if (MountUtil.Unmount(v))
                    {
                        newly.Add(new BlockedDevice
                        {
                            Label = string.IsNullOrEmpty(v.Model) ? v.UsbId : v.Model,
                            Serial = v.Serial
                        });
                    }
                }
            }

            return newly;
        }

        // Соответствует ли том разрешённому устройству из whitelist
        public static bool IsVolumeAllowed(UsbVolume v, List<DeviceEntry> wl)
        {
            if (wl == null || v == null) return false;
            foreach (DeviceEntry e in wl)
            {
                if (!string.IsNullOrEmpty(e.UsbId) &&
                    !string.Equals(e.UsbId, v.UsbId ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(e.Serial) &&
                    !string.IsNullOrEmpty(v.Serial) &&
                    !string.Equals(e.Serial, v.Serial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return true;
            }
            return false;
        }
    }

    // =====================================================================
    // Скрытое окно для ловли WM_DEVICECHANGE
    // =====================================================================
    public sealed class HiddenWindow : NativeWindow
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private readonly Action _onChange;

        public HiddenWindow(Action onChange)
        {
            _onChange = onChange;
        }

        public void EnsureCreated()
        {
            if (this.Handle == IntPtr.Zero)
            {
                CreateParams cp = new CreateParams();
                cp.Caption = "UsbBlockTrayWindow";
                cp.Width = 0;
                cp.Height = 0;
                cp.Style = unchecked((int)0x80000000);
                this.CreateHandle(cp);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE)
            {
                int w = unchecked((int)(long)m.WParam);
                if (w == DBT_DEVICEARRIVAL || w == DBT_DEVICEREMOVECOMPLETE)
                {
                    Action cb = _onChange;
                    if (cb != null)
                    {
                        try { cb(); }
                        catch { }
                    }
                }
            }
            base.WndProc(ref m);
        }
    }

    // =====================================================================
    // Иконка в трее. Приоритет - картинка stop_usb.jpg, встроенная в конец
    // exe-файла (трейлер [jpg][длина][USBICON]). Если её нет/повреждена -
    // рисуется простая иконка кодом.
    // =====================================================================
    public static class AppIcons
    {
        private static readonly byte[] TrailerMagic =
            { (byte)'U', (byte)'S', (byte)'B', (byte)'I', (byte)'C', (byte)'O', (byte)'N' };

        public static Icon Create()
        {
            try
            {
                Icon fromImage = TryLoadEmbeddedTrayIcon();
                if (fromImage != null) return fromImage;
            }
            catch
            {
            }
            return CreateDrawnFallback();
        }

        // Извлекает картинку из трейлера в конце собственного exe и делает
        // из неё иконку трея 16x16.
        private static Icon TryLoadEmbeddedTrayIcon()
        {
            byte[] exeBytes;
            try
            {
                string self = Application.ExecutablePath;
                if (string.IsNullOrEmpty(self) || !File.Exists(self)) return null;
                exeBytes = File.ReadAllBytes(self);
            }
            catch
            {
                return null;
            }

            const int tailLen = 4 + 7; // uint32 длина + "USBICON"
            if (exeBytes.Length < tailLen + 2) return null;

            for (int i = 0; i < TrailerMagic.Length; i++)
            {
                if (exeBytes[exeBytes.Length - TrailerMagic.Length + i] != TrailerMagic[i])
                    return null;
            }

            uint len = BitConverter.ToUInt32(exeBytes, exeBytes.Length - tailLen);
            if (len == 0 || len > exeBytes.Length - tailLen) return null;

            int start = exeBytes.Length - tailLen - (int)len;
            using (MemoryStream ms = new MemoryStream(exeBytes, start, (int)len, false))
            using (Bitmap bmp = new Bitmap(ms))
            {
                int size = 16;
                using (Bitmap small = new Bitmap(size, size))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.Clear(Color.Transparent);
                        g.DrawImage(bmp, 0, 0, size, size);
                    }
                    IntPtr hIcon = small.GetHicon();
                    using (Icon tmp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
            }
        }

        // Запасная иконка, если картинка не встроена
        private static Icon CreateDrawnFallback()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    using (SolidBrush blue = new SolidBrush(Color.FromArgb(0, 120, 215)))
                    {
                        g.FillRectangle(blue, 11, 2, 10, 5);
                        g.FillRectangle(blue, 7, 7, 18, 18);
                    }

                    using (SolidBrush white = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(white, 13, 16, 6, 5);
                        g.FillRectangle(white, 10, 20, 12, 8);
                    }
                    using (SolidBrush dk = new SolidBrush(Color.FromArgb(0, 90, 160)))
                    {
                        g.FillRectangle(dk, 14, 23, 4, 3);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                using (Icon tmp = Icon.FromHandle(hIcon))
                {
                    return (Icon)tmp.Clone();
                }
            }
        }
    }

    // =====================================================================
    // Простые диалоги
    // =====================================================================
    public sealed class PasswordPromptForm : Form
    {
        private readonly TextBox _tb;
        public string Password { get; private set; }

        public PasswordPromptForm(string caption, string prompt, bool allowEmpty)
        {
            this.Text = caption;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(400, 150);
            this.Font = new Font("Segoe UI", 9f);

            Label lbl = new Label();
            lbl.Text = prompt;
            lbl.AutoSize = true;
            lbl.Location = new Point(12, 10);
            lbl.MaximumSize = new Size(376, 50);

            _tb = new TextBox();
            _tb.UseSystemPasswordChar = true;
            _tb.SetBounds(12, 62, 376, 46);

            Button ok = new Button();
            ok.Text = "OK";
            ok.Size = new Size(95, 28);
            ok.Location = new Point(180, 112);
            ok.Click += delegate
            {
                Password = _tb.Text;
                this.DialogResult = DialogResult.OK;
                this.Close();
            };

            Button cancel = new Button();
            cancel.Text = "Отмена";
            cancel.Size = new Size(95, 28);
            cancel.Location = new Point(283, 112);
            cancel.DialogResult = DialogResult.Cancel;

            if (allowEmpty)
            {
                CheckBox ck = new CheckBox();
                ck.Text = "Без пароля";
                ck.Checked = false;
                ck.AutoSize = true;
                ck.Location = new Point(210, 66);
                ck.CheckedChanged += delegate
                {
                    _tb.Enabled = !ck.Checked;
                    if (ck.Checked) { _tb.Text = string.Empty; _tb.Enabled = false; }
                    else _tb.Enabled = true;
                };
                this.Controls.Add(ck);
            }

            this.Controls.Add(lbl);
            this.Controls.Add(_tb);
            this.Controls.Add(ok);
            this.Controls.Add(cancel);
            this.CancelButton = cancel;
            this.AcceptButton = ok;
        }
    }

    public sealed class AddDeviceForm : Form
    {
        private readonly List<StorageDevice> _devices;
        private readonly System.Windows.Forms.ComboBox _cbDevice;
        private readonly System.Windows.Forms.TextBox _tbName;
        private readonly System.Windows.Forms.TextBox _tbType;
        private readonly System.Windows.Forms.TextBox _tbModel;
        private readonly Button _ok;
        public StorageDevice Selected { get; private set; }
        public string DeviceName { get; private set; }

        public AddDeviceForm(List<StorageDevice> devices)
        {
            _devices = devices;

            this.Text = "Добавить устройство";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(540, 340);
            this.Font = new Font("Segoe UI", 9f);

            Label lblSel = new Label();
            lblSel.Text = "Выберите USB-накопитель:";
            lblSel.AutoSize = true;
            lblSel.Location = new Point(12, 10);

            _cbDevice = new System.Windows.Forms.ComboBox();
            _cbDevice.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbDevice.SetBounds(12, 32, 516, 24);
            foreach (StorageDevice sd in _devices)
            {
                string label = string.IsNullOrEmpty(sd.Model) ? "(без имени)" : sd.Model;
                if (!string.IsNullOrEmpty(sd.UsbId)) label += "   [" + sd.UsbId + "]";
                if (!string.IsNullOrEmpty(sd.Serial)) label += "   SN=" + sd.Serial;
                _cbDevice.Items.Add(label);
            }
            _cbDevice.SelectedIndexChanged += delegate { OnSelection(); };

            Label lblName = new Label();
            lblName.Text = "1. Ім'я Пристрою";
            lblName.AutoSize = true;
            lblName.Location = new Point(12, 70);

            _tbName = new System.Windows.Forms.TextBox();
            _tbName.Text = "Новий Пристрій";
            _tbName.SetBounds(12, 92, 516, 24);

            Label lblType = new Label();
            lblType.Text = "2. Тип Пристрою (ID):";
            lblType.AutoSize = true;
            lblType.Location = new Point(12, 128);

            _tbType = new System.Windows.Forms.TextBox();
            _tbType.ReadOnly = true;
            _tbType.SetBounds(12, 150, 516, 24);

            Label lblModel = new Label();
            lblModel.Text = "3. Модель Пристрою (Vendor ID):";
            lblModel.AutoSize = true;
            lblModel.Location = new Point(12, 188);

            _tbModel = new System.Windows.Forms.TextBox();
            _tbModel.ReadOnly = true;
            _tbModel.SetBounds(12, 210, 516, 24);

            _ok = new Button();
            _ok.Text = "Добавить";
            _ok.Size = new Size(110, 28);
            _ok.Location = new Point(306, 270);
            _ok.Enabled = false;
            _ok.Click += delegate { Commit(); };

            Button cancel = new Button();
            cancel.Text = "Отмена";
            cancel.Size = new Size(110, 28);
            cancel.Location = new Point(424, 270);
            cancel.DialogResult = DialogResult.Cancel;

            this.Controls.Add(lblSel);
            this.Controls.Add(_cbDevice);
            this.Controls.Add(lblName);
            this.Controls.Add(_tbName);
            this.Controls.Add(lblType);
            this.Controls.Add(_tbType);
            this.Controls.Add(lblModel);
            this.Controls.Add(_tbModel);
            this.Controls.Add(_ok);
            this.Controls.Add(cancel);
            this.CancelButton = cancel;
            this.AcceptButton = _ok;

            if (_devices.Count > 0)
                _cbDevice.SelectedIndex = 0;
        }

        private void OnSelection()
        {
            int i = _cbDevice.SelectedIndex;
            if (i < 0 || i >= _devices.Count) return;
            StorageDevice sd = _devices[i];
            _tbType.Text = sd.UsbId;
            _tbModel.Text = string.IsNullOrEmpty(sd.BestDiskId) ? sd.Model : sd.BestDiskId;
            _ok.Enabled = !string.IsNullOrEmpty(sd.UsbId);
            _tbName.Text = "Новий Пристрій";
        }

        private void Commit()
        {
            int i = _cbDevice.SelectedIndex;
            if (i < 0 || i >= _devices.Count)
            {
                MessageBox.Show("Выберите устройство из списка.",
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StorageDevice sd = _devices[i];
            if (string.IsNullOrEmpty(sd.UsbId))
            {
                MessageBox.Show("У устройства нет USB-идентификатора - его нельзя добавить.",
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Selected = sd;
            DeviceName = _tbName.Text;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }

    // Удаление устройства из whitelist: выбор одной записи из списка.
    // Само удаление и последующая блокировка - в TrayContext.DoRemoveDevice.
    public sealed class RemoveDeviceDialog : Form
    {
        private readonly List<DeviceEntry> _entries;
        private readonly System.Windows.Forms.ListBox _list;
        public List<DeviceEntry> Selected { get; private set; }

        public RemoveDeviceDialog(List<DeviceEntry> entries)
        {
            _entries = entries;

            this.Text = "Удалить устройство из whitelist";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(620, 420);
            this.Font = new Font("Segoe UI", 9f);

            Label lbl = new Label();
            lbl.Text = "Выберите устройство для удаления из whitelist:\n" +
                       "после удаления его накопитель будет заблокирован.";
            lbl.AutoSize = false;
            lbl.Size = new Size(596, 36);
            lbl.Location = new Point(12, 10);

            _list = new System.Windows.Forms.ListBox();
            _list.SetBounds(12, 52, 596, 290);
            _list.IntegralHeight = false;
            _list.HorizontalScrollbar = true;
            _list.SelectionMode = SelectionMode.One;

            foreach (DeviceEntry e in _entries)
                _list.Items.Add(Format(e));

            Button ok = new Button();
            ok.Text = "Удалить";
            ok.Size = new Size(110, 28);
            ok.Location = new Point(380, 360);
            ok.Enabled = _entries.Count > 0;
            ok.Click += delegate { Commit(); };

            Button cancel = new Button();
            cancel.Text = "Отмена";
            cancel.Size = new Size(110, 28);
            cancel.Location = new Point(498, 360);
            cancel.DialogResult = DialogResult.Cancel;

            this.Controls.Add(lbl);
            this.Controls.Add(_list);
            this.Controls.Add(ok);
            this.Controls.Add(cancel);
            this.CancelButton = cancel;
            this.AcceptButton = ok;
        }

        private static string Format(DeviceEntry e)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(e.Name) ? "(без имени)" : e.Name);
            sb.Append("   USB ID: " +
                (string.IsNullOrEmpty(e.UsbId) ? "(нет)" : e.UsbId));
            if (!string.IsNullOrEmpty(e.Serial))
                sb.Append("   SN: " + e.Serial);
            if (!string.IsNullOrEmpty(e.DiskId))
                sb.Append("   Диск: " + e.DiskId);
            return sb.ToString();
        }

        private void Commit()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _entries.Count)
            {
                MessageBox.Show("Выберите устройство из списка.",
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Selected = new List<DeviceEntry>();
            Selected.Add(_entries[i]);
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }

    // Просмотр whitelist: вывод разрешённых накопителей с меткой (именем) тома
    public sealed class WhitelistViewForm : Form
    {
        public WhitelistViewForm(List<DeviceEntry> entries,
            Dictionary<string, UsbVolume> connected,
            Dictionary<string, string> volumeLabels)
        {
            this.Text = "Whitelist - разрешённые USB-накопители";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(660, 460);
            this.Font = new Font("Segoe UI", 9f);

            Label lbl = new Label();
            lbl.Text = "Разрешённых устройств: " +
                entries.Count.ToString(CultureInfo.InvariantCulture);
            lbl.AutoSize = true;
            lbl.Location = new Point(12, 10);

            System.Windows.Forms.ListBox lb = new System.Windows.Forms.ListBox();
            lb.SetBounds(12, 34, 636, 376);
            lb.IntegralHeight = false;
            lb.HorizontalScrollbar = true;
            lb.SelectionMode = SelectionMode.One;
            foreach (DeviceEntry e in entries)
                lb.Items.Add(FormatEntry(e, connected, volumeLabels));

            Button close = new Button();
            close.Text = "Закрыть";
            close.Size = new Size(110, 28);
            close.Location = new Point(538, 420);
            close.DialogResult = DialogResult.OK;

            this.Controls.Add(lbl);
            this.Controls.Add(lb);
            this.Controls.Add(close);
            this.AcceptButton = close;
            this.CancelButton = close;
        }

        // Метка (имя) тома показывается из файловой системы, если накопитель
        // сейчас подключён; иначе - сохранённое при добавлении имя устройства.
        // Показываются три поля записи whitelist:
        //   Ім'я Пристрою (задаёт пользователь),
        //   Тип Пристрою (ID) - USB-идентификатор,
        //   Модель Пристрою (Vendor ID) - HardwareID диска USBSTOR.
        private static string FormatEntry(DeviceEntry e,
            Dictionary<string, UsbVolume> connected,
            Dictionary<string, string> volumeLabels)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Ім'я Пристрою: " + (string.IsNullOrEmpty(e.Name) ? "(не задано)" : e.Name));
            sb.Append("\r\nТип Пристрою (ID): " + (string.IsNullOrEmpty(e.UsbId) ? "(нет)" : e.UsbId));
            sb.Append("\r\nМодель Пристрою (Vendor ID): " + (string.IsNullOrEmpty(e.DiskId) ? "(нет)" : e.DiskId));
            sb.Append("\r\nSN: " + (string.IsNullOrEmpty(e.Serial) ? "(нет)" : e.Serial));

            string key = (e.UsbId ?? string.Empty) + "|" + (e.Serial ?? string.Empty);
            UsbVolume v;
            string label;
            if (connected != null && volumeLabels != null &&
                connected.TryGetValue(key, out v) &&
                volumeLabels.TryGetValue(v.DriveLetter, out label))
            {
                sb.Append("\r\nМетка (ім'я) тома: \"" +
                    (string.IsNullOrEmpty(label) ? "(без метки)" : label) +
                    "\"    подключён: " + v.DriveLetter + ":");
            }
            else
            {
                sb.Append("\r\nМетка (ім'я) тома: (устройство не подключено)");
            }

            if (e.AddedAt != default(DateTime))
                sb.Append("\r\nДобавлен: " +
                    e.AddedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

            return sb.ToString();
        }
    }

    // =====================================================================
    // Очистка остатков автозапуска (раньше автозапуск создавал задачу
    // Планировщика schtasks /SC ONLOGON и запись HKLM Run; теперь функция
    // отключена, осталась только молчаливая чистка следов старых версий).
    // =====================================================================
    public static class AutoStart
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "USB_Block_Tray";
        private const string TaskName = "USB_Block_Tray";

        // Удаляет задачу Планировщика и старый Run-ключ, если они остались
        // от старых версий. Вызывается при каждом старте с правами
        // администратора и в "Удалить программу". Ошибки игнорируются.
        public static void Cleanup()
        {
            string o;
            Exec.Run("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F", out o);
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(RunKey))
                {
                    k.DeleteValue(ValueName, false);
                }
            }
            catch
            {
            }
        }
    }

    // =====================================================================
    // Задача Планировщика "значок в трее для всех пользователей".
    // Создаётся при установке службы мониторинга: при входе ЛЮБОГО
    // пользователя запускается usb_block_tray.exe --logon.
    //  * для администраторов задача стартует с наивысшими правами
    //    (RunLevel=Highest) молча, без запроса UAC - полное меню управления;
    //  * для обычных пользователей процесс стартует без прав - только статус,
    //    уведомления о блокировках и выход.
    // =====================================================================
    public static class TrayTask
    {
        public const string TaskName = "USB_Block_Tray_Logon";

        public static bool IsInstalled()
        {
            string o;
            return Exec.Run("schtasks.exe", "/Query /TN \"" + TaskName + "\"", out o);
        }

        /// <summary>null = успех, иначе текст ошибки.</summary>
        public static string Create()
        {
            string xmlPath = null;
            try
            {
                xmlPath = Path.Combine(Path.GetTempPath(),
                    "usb_block_tray_task_" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(xmlPath, BuildXml(), Encoding.Unicode);
                string o;
                bool ok = Exec.Run("schtasks.exe",
                    "/Create /F /TN \"" + TaskName + "\" /XML \"" + xmlPath + "\"", out o);
                return ok ? null : ("Не удалось создать задачу (schtasks): " + o);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                if (xmlPath != null)
                {
                    try { File.Delete(xmlPath); }
                    catch { }
                }
            }
        }

        public static void Delete()
        {
            string o;
            Exec.Run("schtasks.exe", "/Delete /F /TN \"" + TaskName + "\"", out o);
        }

        private static string BuildXml()
        {
            // Задача на группу "Users" (S-1-5-32-545): срабатывает при входе
            // любого пользователя, с его токеном и наивысшим доступным уровнем
            // прав (администраторы - молча с полным токеном, остальные - без
            // прав, без запроса пароля).
            string command = "\"" + ProtectedCopy.InstallExe + "\"";
            return
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo>\r\n" +
                "    <Description>USB-блокировка: значок в трее и уведомления о блокировках для всех пользователей</Description>\r\n" +
                "  </RegistrationInfo>\r\n" +
                "  <Triggers>\r\n" +
                "    <LogonTrigger>\r\n" +
                "      <Enabled>true</Enabled>\r\n" +
                "    </LogonTrigger>\r\n" +
                "  </Triggers>\r\n" +
                "  <Principals>\r\n" +
                "    <Principal id=\"Author\">\r\n" +
                "      <GroupId>S-1-5-32-545</GroupId>\r\n" +
                "      <RunLevel>Highest</RunLevel>\r\n" +
                "    </Principal>\r\n" +
                "  </Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
                "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
                "    <IdleSettings>\r\n" +
                "      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n" +
                "      <RestartOnIdle>false</RestartOnIdle>\r\n" +
                "    </IdleSettings>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>false</Hidden>\r\n" +
                "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
                "    <WakeToRun>false</WakeToRun>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\">\r\n" +
                "    <Exec>\r\n" +
                "      <Command>" + command + "</Command>\r\n" +
                "      <Arguments>--logon</Arguments>\r\n" +
                "    </Exec>\r\n" +
                "  </Actions>\r\n" +
                "</Task>\r\n";
        }
    }

    public sealed class TrayContext : ApplicationContext
    {
        private NotifyIcon _icon;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _miStatus;
        private ToolStripMenuItem _miSvcInstall;
        private ToolStripMenuItem _miSvcRemove;
        private HiddenWindow _hwnd;
        private System.Windows.Forms.Timer _timer;

        private bool _blocked;
        private bool _busy;

        public TrayContext()
        {
            _blocked = PolicyManager.IsBlocked();
            UsbMonitor.Refresh();

            _icon = new NotifyIcon();
            _icon.Icon = AppIcons.Create();
            _icon.Text = Program.Title;
            _icon.Visible = true;
            _icon.DoubleClick += delegate { _menu.Show(Control.MousePosition); };

            BuildMenu();

            _hwnd = new HiddenWindow(HandleDeviceChange);
            _hwnd.EnsureCreated();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 3000;
            _timer.Tick += delegate { TickScan(); };
            _timer.Start();

            TickScan();
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();

            _miStatus = new ToolStripMenuItem(StatusText());
            _menu.Items.Add(_miStatus);
            ApplyStatusColor();
            _menu.Items.Add(new ToolStripSeparator());

            // Не администратор (например, запуск задачи Планировщика при входе
            // обычного пользователя): управление настройками недоступно - только
            // статус, уведомления о блокировках и выход. Меню сокращается.
            if (!Program.IsAdministrator())
            {
                ToolStripMenuItem mInfo = new ToolStripMenuItem(
                    "Управление доступно только администраторам");
                mInfo.Enabled = false;
                _menu.Items.Add(mInfo);
                _menu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem mExitLimited = new ToolStripMenuItem("Выход");
                mExitLimited.Click += delegate { this.ExitThread(); };
                _menu.Items.Add(mExitLimited);

                _icon.ContextMenuStrip = _menu;
                return;
            }

            ToolStripMenuItem mBlock = new ToolStripMenuItem("1 Заблокировать");
            mBlock.Click += delegate { DoBlock(); };
            _menu.Items.Add(mBlock);

            ToolStripMenuItem mUnblock = new ToolStripMenuItem("2 Разблокировать");
            mUnblock.Click += delegate { DoUnblock(); };
            _menu.Items.Add(mUnblock);

            ToolStripMenuItem mAdd = new ToolStripMenuItem("3 Добавить устройство");
            mAdd.Click += delegate { DoAddDevice(); };
            _menu.Items.Add(mAdd);

            ToolStripMenuItem mRemove = new ToolStripMenuItem("4 Удалить устройство из whitelist...");
            mRemove.Click += delegate { DoRemoveDevice(); };
            _menu.Items.Add(mRemove);

            ToolStripMenuItem mExport = new ToolStripMenuItem("5 Экспортировать whitelist...");
            mExport.Click += delegate { DoExport(); };
            _menu.Items.Add(mExport);

            ToolStripMenuItem mImport = new ToolStripMenuItem("6 Импортировать whitelist...");
            mImport.Click += delegate { DoImport(); };
            _menu.Items.Add(mImport);

            ToolStripMenuItem mView = new ToolStripMenuItem("0 Просмотр whitelist...");
            mView.Click += delegate { DoViewWhitelist(); };
            _menu.Items.Add(mView);

            _menu.Items.Add(new ToolStripSeparator());

            _miSvcInstall = new ToolStripMenuItem("7 Установить службу мониторинга");
            _miSvcInstall.Click += delegate { DoInstallService(); };
            _menu.Items.Add(_miSvcInstall);

            _miSvcRemove = new ToolStripMenuItem("8 Удалить службу мониторинга");
            _miSvcRemove.Click += delegate { DoRemoveService(); };
            _menu.Items.Add(_miSvcRemove);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem mUninstall = new ToolStripMenuItem("9 Удалить программу...");
            mUninstall.Click += delegate { DoUninstall(); };
            _menu.Items.Add(mUninstall);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem mExit = new ToolStripMenuItem("Выход");
            mExit.Click += delegate { this.ExitThread(); };
            _menu.Items.Add(mExit);

            _icon.ContextMenuStrip = _menu;
            RefreshServiceMenu();
        }

        private void RefreshServiceMenu()
        {
            bool installed = ServiceManager.IsInstalled();
            if (_miSvcInstall != null)
                _miSvcInstall.Enabled = !installed;
            if (_miSvcRemove != null)
                _miSvcRemove.Enabled = installed;
        }

        private void RefreshStatus()
        {
            _blocked = PolicyManager.IsBlocked();
            if (_miStatus != null)
                _miStatus.Text = StatusText();
            ApplyStatusColor();
        }

        // Цвет надписи статуса: включена - зелёный, выключена - красный.
        // Пункт оставлен Enabled=true (у него нет обработчика Click), иначе
        // WinForms рисует отключённый пункт системным серым цветом и
        // ForeColor игнорируется.
        private void ApplyStatusColor()
        {
            if (_miStatus == null) return;
            _miStatus.ForeColor = _blocked
                ? Color.FromArgb(0, 128, 0)
                : Color.FromArgb(192, 0, 0);
        }

        private string StatusText()
        {
            string s = "Статус: блокировка ";
            s += _blocked ? "включена" : "выключена";
            // Для не-администратора whitelist.dat недоступен (ACL/DPAPI) -
            // счётчик не показываем.
            if (Program.IsAdministrator())
            {
                List<DeviceEntry> wl = UsbMonitor.GetWhitelist();
                s += " | whitelist: " + wl.Count.ToString(CultureInfo.InvariantCulture);
            }
            return s;
        }

        // =============================================================
        // Пункт 1. Заблокировать
        // =============================================================
        private void DoBlock()
        {
            if (!EnsureAdmin()) return;
            try
            {
                PolicyManager.SetBlocked(true);
                _blocked = true;
                RunScan();
                RefreshStatus();
                _icon.ShowBalloonTip(3000, Program.Title,
                    "Блокировка включена. Посторонние USB-накопители будут оставаться без буквы диска.",
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка при блокировке:\n" + ex.Message,
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =============================================================
        // Пункт 2. Разблокировать
        // =============================================================
        private void DoUnblock()
        {
            if (!EnsureAdmin()) return;
            try
            {
                PolicyManager.SetBlocked(false);
                _blocked = false;
                MountUtil.RestoreAll();
                int remounted = MountUtil.RestoreUnmounted();
                RunScan();
                RefreshStatus();
                _icon.ShowBalloonTip(3000, Program.Title,
                    "Блокировка выключена. Томов восстановлено: " + remounted +
                    ". Все USB-накопители доступны.",
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка при разблокировке:\n" + ex.Message,
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =============================================================
        // Пункт 3. Добавить устройство
        // Показывает и подключённые, и ранее заблокированные (снятые с
        // монтирования) накопители; после добавления разрешает монтирование.
        // =============================================================
        private void DoAddDevice()
        {
            if (!EnsureAdmin()) return;

            List<StorageDevice> devices = BuildAddDeviceList();
            if (devices.Count == 0)
            {
                MessageBox.Show("Нет USB-накопителей.\n" +
                                "Подключите накопитель и повторите.",
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<DeviceEntry> current = UsbMonitor.GetWhitelist();
            using (AddDeviceForm f = new AddDeviceForm(devices))
            {
                if (f.ShowDialog() != DialogResult.OK) return;
                StorageDevice sd = f.Selected;
                if (sd == null || string.IsNullOrEmpty(sd.UsbId)) return;

                DeviceEntry e = new DeviceEntry();
                e.Name = string.IsNullOrEmpty(f.DeviceName) || string.IsNullOrEmpty(f.DeviceName.Trim())
                    ? sd.Model : f.DeviceName.Trim();
                e.UsbId = sd.UsbId;
                e.DiskId = sd.BestDiskId;
                e.Serial = sd.Serial;
                e.AddedAt = DateTime.Now;

                bool dup = false;
                foreach (DeviceEntry x in current)
                {
                    if (string.Equals(x.UsbId, e.UsbId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Serial ?? "", e.Serial ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        dup = true;
                        break;
                    }
                }
                if (dup)
                {
                    MessageBox.Show("Этот накопитель уже есть в whitelist.",
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                current.Add(e);
                List<DeviceEntry> added = new List<DeviceEntry>();
                added.Add(e);

                try
                {
                    WhitelistStore.Save(current);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось сохранить whitelist:\n" + ex.Message,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                UsbMonitor.Refresh();
                MountUtil.RestoreAll();

                // Разрешаем монтирование добавленного накопителя: заново
                // создаём точки подключения для тех, что были заблокированы
                // (по серийному номеру), затем перечитываем состояние.
                int mounted = 0;
                foreach (DeviceEntry x in added)
                {
                    if (!string.IsNullOrEmpty(x.Serial) && MountUtil.AllowTracked(x.Serial))
                        mounted++;
                }
                if (!_blocked)
                    MountUtil.RestoreUnmounted();

                if (_blocked)
                {
                    PolicyManager.SetBlocked(true);
                    RunScan();
                }

                RefreshStatus();
                MessageBox.Show("Добавлено устройств: " + added.Count.ToString(CultureInfo.InvariantCulture) + "\n" +
                                "Разрешено к монтированию: " + mounted.ToString(CultureInfo.InvariantCulture) + "\n" +
                                "Накопитель разрешён и получит букву диска автоматически.",
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // Список накопителей для диалога добавления: подключённые сейчас
        // (GetUsbStorages) + заблокированные (снятые с монтирования) из учёта.
        // Для заблокированных восстанавливаются USB ID и HardwareID диска,
        // чтобы запись в whitelist была полноценной.
        private static List<StorageDevice> BuildAddDeviceList()
        {
            List<StorageDevice> devices = UsbQuery.GetUsbStorages();

            foreach (BlockedRecord rec in MountUtil.GetTracked())
            {
                if (string.IsNullOrEmpty(rec.Serial)) continue;

                bool exists = false;
                foreach (StorageDevice d in devices)
                {
                    if (string.Equals(d.Serial, rec.Serial, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists) continue;

                StorageDevice sd = new StorageDevice();
                sd.Serial = rec.Serial;
                sd.UsbId = string.IsNullOrEmpty(rec.UsbId)
                    ? UsbQuery.FindUsbIdBySerial(rec.Serial)
                    : rec.UsbId;
                sd.BestDiskId = UsbQuery.FindDiskIdBySerial(rec.Serial);
                sd.Model = "(заблокирован)";
                devices.Add(sd);
            }

            return devices;
        }

        // =============================================================
        // Пункт 4. Удалить устройство из whitelist и заблокировать его
        // =============================================================
        private void DoRemoveDevice()
        {
            if (!EnsureAdmin()) return;
            try
            {
                List<DeviceEntry> wl = UsbMonitor.GetWhitelist();
                if (wl.Count == 0)
                {
                    MessageBox.Show("Whitelist пуст - удалять нечего.",
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                List<DeviceEntry> selected;
                using (RemoveDeviceDialog dlg = new RemoveDeviceDialog(wl))
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    selected = dlg.Selected;
                }
                if (selected == null || selected.Count == 0) return;

                StringBuilder q = new StringBuilder();
                foreach (DeviceEntry e in selected)
                    q.AppendLine("* " + DescribeEntry(e));
                if (MessageBox.Show(
                        "Удалить из whitelist и заблокировать?\n\n" + q.ToString(),
                        Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                    != DialogResult.Yes)
                    return;

                HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DeviceEntry e in selected) keys.Add(EntryKey(e));
                List<DeviceEntry> result = new List<DeviceEntry>();
                foreach (DeviceEntry e in wl)
                {
                    if (!keys.Contains(EntryKey(e)))
                        result.Add(e);
                }

                try
                {
                    WhitelistStore.Save(result);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось сохранить whitelist:\n" + ex.Message,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                UsbMonitor.Refresh();

                // Блокируем удалённые накопители прямо сейчас (если подключены):
                // снимаем точку подключения тома (mountvol /p).
                List<UsbVolume> volumes = UsbQuery.GetUsbVolumes();
                int blockedNow = 0;
                foreach (DeviceEntry e in selected)
                {
                    foreach (UsbVolume v in volumes)
                    {
                        if (!VolumeMatchesEntry(v, e)) continue;
                        if (MountUtil.Unmount(v)) blockedNow++;
                    }
                }

                if (_blocked)
                {
                    PolicyManager.SetBlocked(true);
                    RunScan();
                }
                RefreshStatus();

                MessageBox.Show(
                    "Удалено из whitelist: " +
                    selected.Count.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "Заблокировано сейчас: " +
                    blockedNow.ToString(CultureInfo.InvariantCulture) + "\n\n" +
                    (_blocked
                        ? "Устройство больше не разрешено и будет блокироваться."
                        : "Внимание: общая блокировка ВЫКЛЮЧЕНА.\n" +
                          "Подключённый накопитель размонтирован, но чтобы он\n" +
                          "блокировался и в дальнейшем, включите пункт «1 Заблокировать»."),
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка при удалении устройства:\n" + ex.Message,
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Совпадает ли подключённый том с удаляемой записью whitelist
        // (по USB ID и, если он известен, по серийному номеру).
        private static bool VolumeMatchesEntry(UsbVolume v, DeviceEntry e)
        {
            if (v == null || e == null) return false;
            if (!string.IsNullOrEmpty(e.UsbId) &&
                !string.Equals(e.UsbId, v.UsbId ?? "", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(e.Serial) && !string.IsNullOrEmpty(v.Serial) &&
                !string.Equals(e.Serial, v.Serial, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        private static string DescribeEntry(DeviceEntry e)
        {
            string s = string.IsNullOrEmpty(e.Name) ? "(без имени)" : e.Name;
            if (!string.IsNullOrEmpty(e.UsbId)) s += "  " + e.UsbId;
            if (!string.IsNullOrEmpty(e.Serial)) s += "  SN=" + e.Serial;
            return s;
        }

        // =============================================================
        // Пункт 5. Экспорт whitelist (перенос на другой компьютер)
        // =============================================================
        private void DoExport()
        {
            if (!EnsureAdmin()) return;

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Экспорт whitelist (перенос на другой компьютер)";
                dlg.Filter = "UsbBlock whitelist (*.wlb)|*.wlb|Все файлы (*.*)|*.*";
                dlg.FileName = "usb_whitelist.wlb";
                dlg.DefaultExt = "wlb";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                using (PasswordPromptForm pf = new PasswordPromptForm(
                    "Защита паролем",
                    "Пароль для файла переноса (пусто - без пароля):",
                    true))
                {
                    if (pf.ShowDialog() != DialogResult.OK) return;
                    try
                    {
                        PortableWhitelist.Export(
                            UsbMonitor.GetWhitelist(), dlg.FileName, pf.Password);
                        MessageBox.Show(
                            "Whitelist экспортирован:\n" + dlg.FileName +
                            "\n\nПеренесите .wlb на другой компьютер и выберите «Импортировать whitelist».",
                            Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Ошибка экспорта:\n" + ex.Message,
                            Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        // =============================================================
        // Пункт 6. Импорт whitelist (с другого компьютера)
        // =============================================================
        private void DoImport()
        {
            if (!EnsureAdmin()) return;

            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Импорт whitelist";
                dlg.Filter = "UsbBlock whitelist (*.wlb)|*.wlb|Все файлы (*.*)|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                string password = string.Empty;
                string prompt = "Файл с паролем? Введите пароль\n" +
                                "(для файла без пароля просто нажмите OK):";
                using (PasswordPromptForm pf = new PasswordPromptForm(
                    "Пароль файла", prompt, true))
                {
                    if (pf.ShowDialog() != DialogResult.OK) return;
                    password = pf.Password;
                }

                List<DeviceEntry> imported;
                try
                {
                    imported = PortableWhitelist.Import(dlg.FileName, password);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось прочитать файл:\n" + ex.Message,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                List<DeviceEntry> current = UsbMonitor.GetWhitelist();
                string msg = "Импортировано записей (устройств): " +
                             imported.Count.ToString(CultureInfo.InvariantCulture) + "\n\n" +
                             "Текущий whitelist: " + current.Count.ToString(CultureInfo.InvariantCulture) +
                             " записей.\n\n" +
                             "Как применить импортированные данные?\n\n" +
                             "  «Да»     - перезаписать (заменить) текущий whitelist\n" +
                             "  «Нет»    - добавить к текущему whitelist (объединить)\n" +
                             "  «Отмена» - отменить";
                DialogResult action = MessageBox.Show(msg, Program.Title,
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (action == DialogResult.Cancel) return;

                List<DeviceEntry> result = (action == DialogResult.Yes)
                    ? imported
                    : MergeWhitelists(current, imported);

                try
                {
                    WhitelistStore.Save(result);
                    UsbMonitor.Refresh();
                    if (_blocked)
                    {
                        PolicyManager.SetBlocked(true);
                        MountUtil.RestoreAll();
                        MountUtil.RestoreUnmounted();
                        RunScan();
                    }
                    RefreshStatus();
                    if (action == DialogResult.Yes)
                    {
                        MessageBox.Show("Whitelist заменён: " +
                            result.Count.ToString(CultureInfo.InvariantCulture) + " устройств.",
                            Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show("Whitelist объединён:\n" +
                            "было " + current.Count.ToString(CultureInfo.InvariantCulture) +
                            ", добавлено " +
                            (result.Count - current.Count).ToString(CultureInfo.InvariantCulture) +
                            ", стало " + result.Count.ToString(CultureInfo.InvariantCulture) + " устройств.",
                            Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Ошибка сохранения:\n" + ex.Message,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // Объединение двух whitelist: сохраняет существующие записи
        // и добавляет запрошенные (без дубликатов по USB ID + серийному номеру)
        internal static List<DeviceEntry> MergeWhitelists(
            List<DeviceEntry> existing, List<DeviceEntry> incoming)
        {
            List<DeviceEntry> result = new List<DeviceEntry>(existing);
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DeviceEntry e in existing)
                keys.Add(EntryKey(e));
            foreach (DeviceEntry e in incoming)
            {
                if (keys.Add(EntryKey(e)))
                    result.Add(e);
            }
            return result;
        }

        private static string EntryKey(DeviceEntry e)
        {
            return (e.UsbId ?? string.Empty) + "|" + (e.Serial ?? string.Empty);
        }

        // =============================================================
        // Пункт 0. Просмотр whitelist (с метками томов)
        // =============================================================
        private void DoViewWhitelist()
        {
            if (!EnsureAdmin()) return;
            try
            {
                List<DeviceEntry> wl = UsbMonitor.GetWhitelist();

                Dictionary<string, UsbVolume> connected =
                    new Dictionary<string, UsbVolume>(StringComparer.OrdinalIgnoreCase);
                foreach (UsbVolume v in UsbQuery.GetUsbVolumes())
                {
                    string key = (v.UsbId ?? string.Empty) + "|" + (v.Serial ?? string.Empty);
                    if (!connected.ContainsKey(key))
                        connected[key] = v;
                }

                Dictionary<string, string> labels = UsbQuery.GetVolumeLabels();

                using (WhitelistViewForm f = new WhitelistViewForm(wl, connected, labels))
                    f.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка при просмотре whitelist:\n" + ex.Message,
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =============================================================
        // Пункт 7. Установить службу мониторинга
        // =============================================================
        private void DoInstallService()
        {
            if (!EnsureAdmin()) return;
            if (ServiceManager.IsInstalled())
            {
                RefreshServiceMenu();
                return;
            }
            try
            {
                string err = ServiceManager.Install();
                if (err != null)
                {
                    MessageBox.Show("Ошибка:\n" + err,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                // Значок в трее и уведомления ДЛЯ ВСЕХ пользователей: задача
                // Планировщика, запускающая трей при входе любого пользователя.
                string tErr = TrayTask.Create();
                RefreshServiceMenu();
                if (tErr != null)
                {
                    MessageBox.Show(
                        "Служба установлена, но не удалось настроить значок в трее\n" +
                        "для других пользователей:\n" + tErr,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _icon.ShowBalloonTip(3000, Program.Title,
                    "Служба мониторинга установлена и запущена от имени SYSTEM.\n" +
                    "Значок в трее появится у всех пользователей после перезагрузки.",
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка:\n" + ex.Message,
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =============================================================
        // Пункт 8. Удалить службу мониторинга
        // =============================================================
        private void DoRemoveService()
        {
            if (!EnsureAdmin()) return;
            if (!ServiceManager.IsInstalled())
            {
                RefreshServiceMenu();
                return;
            }
            try
            {
                string err = ServiceManager.Uninstall();
                if (err != null)
                {
                    MessageBox.Show("Ошибка:\n" + err,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                // Убираем и значок в трее для всех пользователей
                // (задача Планировщика).
                TrayTask.Delete();
                RefreshServiceMenu();
                _icon.ShowBalloonTip(3000, Program.Title,
                    "Служба мониторинга удалена.\n" +
                    "Значок в трее у пользователей будет убран после перезагрузки.",
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка:\n" + ex.Message,
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =============================================================
        // Пункт 9. Удалить программу
        // Удаляет: службу, политику, защищённую копию,
        // данные whitelist и сам файл программы.
        // =============================================================
        private void DoUninstall()
        {
            if (!EnsureAdmin()) return;

            if (MessageBox.Show(
                    "Удалить программу?\n\r" +
                    "Будут удалены:\n" +
                    "  - служба мониторинга (если установлена)\n" +
                    "  - задача значков в трее для пользователей (если была)\n" +
                    "  - защищённая копия в \"Program Files\\USB_Block\"\n" +
                    "  - политика блокировки USB-устройств\n" +
                    "  - сам файл программы",
                    Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            // 1) остатки автозапуска от старых версий (задача Планировщика / Run-ключ)
            AutoStart.Cleanup();

            // 1а) задача "значок в трее для всех пользователей" (если была)
            TrayTask.Delete();

            // 2) служба мониторинга (если установлена)
            try
            {
                ServiceManager.Uninstall();
            }
            catch
            {
            }

            // 2а) очередь событий блокировок и личные счётчики уведомлений
            NotifyStore.Clear();

            // 3) политика блокировки
            try
            {
                PolicyManager.SetBlocked(false);
                MountUtil.RestoreAll();
                MountUtil.RestoreUnmounted();
            }
            catch
            {
            }

            // 4) данные whitelist (с отдельным подтверждением)
            bool deleteData = false;
            if (Directory.Exists(StorePaths.Directory))
            {
                if (MessageBox.Show(
                        "Удалить сохранённый whitelist (список разрешённых устройств)?",
                        Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    deleteData = true;
                }
            }
            if (deleteData)
                TryDeleteDir(StorePaths.Directory);

            // 5) защищённая копия (Program Files\USB_Block и ProgramData\MAA)
            string running = Application.ExecutablePath;
            bool runningFromInstall = string.Equals(
                Path.GetFullPath(running), Path.GetFullPath(ProtectedCopy.InstallExe),
                StringComparison.OrdinalIgnoreCase);
            bool runningFromMaa = string.Equals(
                Path.GetFullPath(running), Path.GetFullPath(ProtectedCopy.MaaExe),
                StringComparison.OrdinalIgnoreCase);

            List<string> delayedDirs = new List<string>();
            if (runningFromInstall)
            {
                // копия - это и есть запущенный процесс: удалим её после выхода
                delayedDirs.Add(ProtectedCopy.InstallDir);
            }
            else
            {
                TryDeleteDir(ProtectedCopy.InstallDir);
            }

            if (runningFromMaa)
            {
                delayedDirs.Add(ProtectedCopy.MaaDir);
            }
            else
            {
                TryDeleteDir(ProtectedCopy.MaaDir);
            }

            // 6) журналы рядом с программой
            foreach (string log in new[] { "diag.log", "selftest.log" })
            {
                string lp = Path.Combine(Path.GetDirectoryName(running) ?? string.Empty, log);
                if (File.Exists(lp))
                {
                    try { File.Delete(lp); }
                    catch { }
                }
            }

            // 7) самоудаление запущенного exe после выхода (и папок-копий)
            ScheduleSelfDelete(running, delayedDirs.Count > 0 ? string.Join(";", delayedDirs.ToArray()) : null);

            MessageBox.Show("Программа удалена.",
                Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);

            this.ExitThread();
        }

        private static bool TryDeleteDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return true;
                Directory.Delete(dir, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Запускает скрытый .cmd, который ждёт выхода приложения и удаляет exe/папку.
        private static void ScheduleSelfDelete(string exePath, string dirsCsv)
        {
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(),
                    "usb_uninstall_" + Guid.NewGuid().ToString("N") + ".cmd");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("ping -n 5 127.0.0.1 >nul");
                if (!string.IsNullOrEmpty(exePath))
                    sb.AppendLine("del /f /q \"" + exePath + "\" 1>nul 2>&1");
                if (!string.IsNullOrEmpty(dirsCsv))
                {
                    foreach (string dir in dirsCsv.Split(';'))
                    {
                        if (string.IsNullOrEmpty(dir)) continue;
                        sb.AppendLine("rmdir /s /q \"" + dir + "\" 1>nul 2>&1");
                    }
                }
                sb.AppendLine("del /f /q \"" + tmp + "\" 1>nul 2>&1");
                File.WriteAllText(tmp, sb.ToString(), Encoding.Default);

                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo(tmp);
                psi.UseShellExecute = true;
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
            }
        }

        // =============================================================
        // Мониторинг
        // =============================================================
        private bool EnsureAdmin()
        {
            if (Program.IsAdministrator()) return true;
            MessageBox.Show("Требуются права администратора.",
                Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private void HandleDeviceChange()
        {
            if (_timer != null && !_busy)
            {
                _timer.Interval = 300;
            }
        }

        // Тик фонового таймера: блокировка (только под администратором) +
        // опрос очереди событий блокировок и уведомление пользователя.
        private void TickScan()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                if (Program.IsAdministrator())
                {
                    RunScan();
                }
                PollAndNotify();
            }
            catch
            {
            }
            finally
            {
                _busy = false;
                _timer.Interval = 3000;
            }
        }

        // Активная блокировка: снимает точки монтирования посторонних
        // накопителей и записывает события в общую очередь
        // (HKLM\SOFTWARE\USB_Block\Events), которую читают треи всех
        // пользователей - так уведомление получают все.
        private void RunScan()
        {
            List<UsbNode> nodes;
            List<StorageDevice> disks;
            List<BlockedDevice> newly = UsbMonitor.Scan(out nodes, out disks);
            if (newly.Count > 0)
            {
                foreach (BlockedDevice b in newly)
                {
                    NotifyStore.Write(b.Serial, b.Label);
                }
            }
        }

        // Читает очередь событий и показывает пользователю те, которых он
        // ещё не видел (последний просмотренный номер хранится в HKCU).
        private void PollAndNotify()
        {
            List<NotifyStore.BlockEvent> evs = NotifyStore.ReadAll();
            if (evs.Count == 0) return;
            long last = NotifyStore.GetLastSeen();
            long max = 0;
            foreach (NotifyStore.BlockEvent e in evs)
            {
                if (e.Id > last && e.Id > max) max = e.Id;
            }
            if (max <= last) return;

            foreach (NotifyStore.BlockEvent e in evs)
            {
                if (e.Id > last) NotifyEvent(e);
            }
            NotifyStore.SetLastSeen(max);
        }

        private void NotifyEvent(NotifyStore.BlockEvent e)
        {
            string what = string.IsNullOrEmpty(e.Label) ? e.Serial : e.Label;
            if (string.IsNullOrEmpty(what)) what = "USB-накопитель";
            string detail = "Заблокирован накопитель: " + what +
                (string.IsNullOrEmpty(e.Serial) ? string.Empty : "  SN=" + e.Serial);

            _icon.ShowBalloonTip(8000, Program.Title, detail, ToolTipIcon.Warning);

            // Устаревшие события (задержанные до следующего входа) - только
            // баллоном, чтобы не раздражать всплывающими окнами.
            bool recent = (DateTime.Now - e.When).TotalMinutes < 2;
            if (!recent) return;

            string msg = Program.NotifyText + "\n\n(" + detail + ")\n\n";
            msg += Program.IsAdministrator()
                ? "Чтобы разрешить этот накопитель:\nзначок в трее -> 3 Добавить устройство."
                : "Управление настройками доступно только администраторам.";
            try
            {
                MessageBox.Show(msg, Program.Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            catch
            {
            }
        }

        protected override void ExitThreadCore()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
            if (_hwnd != null)
            {
                try { _hwnd.DestroyHandle(); }
                catch { }
                _hwnd = null;
            }
            if (_menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }
            if (_icon != null)
            {
                Icon ic = _icon.Icon;
                _icon.Visible = false;
                _icon.Dispose();
                if (ic != null) ic.Dispose();
                _icon = null;
            }
            base.ExitThreadCore();
        }
    }

    // =====================================================================
    // Служба мониторинга (запуск через диспетчер служб, системная учётная
    // запись LocalSystem): в фоне снимает точки монтирования посторонних
    // накопителей, уведомления пишутся в журнал событий.
    // =====================================================================
    public static class ServiceManager
    {
        public const string ServiceName = "UsbBlockMonitor";
        public const string DisplayName = "USB-блокировка (мониторинг)";

        // Установлена ли служба.
        public static bool IsInstalled()
        {
            try
            {
                foreach (ServiceController sc in ServiceController.GetServices())
                {
                    if (string.Equals(sc.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>null = успех, иначе текст ошибки.</summary>
        public static string Install()
        {
            try
            {
                // Служба запускается из защищённой копии: убеждаемся, что она
                // свежая (иначе путь в PathName может указывать на устаревшую
                // или отсутствующую копию).
                string copyErr = ProtectedCopy.EnsureInstalledCopy();
                if (copyErr != null)
                    return "Не удалось обновить защищённую копию для службы:\n" + copyErr;

                ManagementClass serviceClass = new ManagementClass("Win32_Service");
                ManagementBaseObject inParams = serviceClass.GetMethodParameters("Create");
                inParams["Name"] = ServiceName;
                inParams["DisplayName"] = DisplayName;
                inParams["PathName"] = "\"" + ProtectedCopy.InstallExe + "\" --service";
                // ВНИМАНИЕ: параметры ServiceType/ErrorControl метода Create
                // имеют тип uint8 (НЕ string). Передача строки ("Own Process")
                // приводит к FormatException "input string was not in a correct
                // format" - System.Management делает Byte.Parse(string).
                inParams["ServiceType"] = (byte)16;   // 16 = Own Process (пользовательский процесс)
                inParams["ErrorControl"] = (byte)1;   // 1 = Normal (нормальный запуск)
                inParams["StartMode"] = "Automatic";
                // StartName = null -> системная учётная запись (LocalSystem /
                // SYSTEM), служба работает от имени системы.
                inParams["StartName"] = null;
                inParams["DesktopInteract"] = false;
                ManagementBaseObject outParams =
                    serviceClass.InvokeMethod("Create", inParams, null);
                uint ret = (uint)(outParams["ReturnValue"] ?? 0);
                if (ret != 0)
                    return "Не удалось создать службу (код " +
                        ret.ToString(CultureInfo.InvariantCulture) + ")";
                Start();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public static void Start()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped ||
                        sc.Status == ServiceControllerStatus.StopPending)
                    {
                        sc.Start();
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>null = успех (или службы нет), иначе текст ошибки.</summary>
        public static string Uninstall()
        {
            try
            {
                if (!IsInstalled()) return null;
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped &&
                        sc.Status != ServiceControllerStatus.StopPending)
                    {
                        try
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped,
                                TimeSpan.FromSeconds(20));
                        }
                        catch
                        {
                            // останавливается при удалении
                        }
                    }
                }
                using (ManagementObject svc = new ManagementObject(
                    @"\\.\root\cimv2:Win32_Service.Name='" + ServiceName + "'"))
                {
                    ManagementBaseObject outParams = svc.InvokeMethod("Delete", null, null);
                    uint ret = (uint)(outParams["ReturnValue"] ?? 0);
                    if (ret != 0)
                        return "Не удалось удалить службу (код " +
                            ret.ToString(CultureInfo.InvariantCulture) + ")";
                    return null;
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    public sealed class UsbBlockService : ServiceBase
    {
        private System.Timers.Timer _timer;
        private bool _busy;

        public UsbBlockService()
        {
            ServiceName = ServiceManager.ServiceName;
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            _timer = new System.Timers.Timer(2000);
            _timer.AutoReset = true;
            _timer.Elapsed += OnTick;
            _timer.Start();
            OnTick(null, null);
        }

        protected override void OnStop()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Elapsed -= OnTick;
                _timer.Dispose();
                _timer = null;
            }
        }

        private void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                List<UsbNode> nodes;
                List<StorageDevice> disks;
                List<BlockedDevice> newly = UsbMonitor.Scan(out nodes, out disks);
                foreach (BlockedDevice b in newly)
                {
                    string what = string.IsNullOrEmpty(b.Label) ? b.Serial : b.Label;
                    EventLog.WriteEntry(ServiceManager.ServiceName,
                        Program.NotifyText + "\n\nЗаблокирован накопитель: " + what +
                        (string.IsNullOrEmpty(b.Serial) ? string.Empty : "  SN=" + b.Serial),
                        EventLogEntryType.Warning);

                    // Событие в общую очередь (HKLM): треи всех пользователей
                    // подхватят и покажут уведомление.
                    NotifyStore.Write(b.Serial, b.Label);
                }
            }
            catch
            {
            }
            finally
            {
                _busy = false;
            }
        }
    }

    // =====================================================================
    // Диагностика (без прав администратора)
    // =====================================================================
    internal static class Diag
    {
        public static int Run()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("USB_Block tray - диагностика");
            sb.AppendLine("Время: " + DateTime.Now.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Администратор: " + Program.IsAdministrator());
            sb.AppendLine("Защищённая копия: " + ProtectedCopy.InstallExe +
                "  существует=" + File.Exists(ProtectedCopy.InstallExe));
            sb.AppendLine("Защищённая копия (MAA): " + ProtectedCopy.MaaExe +
                "  существует=" + File.Exists(ProtectedCopy.MaaExe));
            sb.AppendLine("Служба мониторинга: " +
                (ServiceManager.IsInstalled() ? "установлена" : "не установлена"));

            sb.AppendLine();
            sb.AppendLine("--- Политика ---");
            sb.AppendLine("Блокировка активна: " + PolicyManager.IsBlocked());

            sb.AppendLine();
            sb.AppendLine("--- Whitelist ---");
            sb.AppendLine("Файл: " + StorePaths.File);
            try
            {
                List<DeviceEntry> wl = WhitelistStore.Load();
                sb.AppendLine("Записей: " + wl.Count);
                foreach (DeviceEntry e in wl)
                {
                    sb.AppendLine("  NAME=" + e.Name + "  USB=" + e.UsbId +
                        "  SN=" + e.Serial + "  DISK=" + e.DiskId);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Ошибка чтения (ожидаемо без прав администратора): " + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine("--- USB-диски (подключённые) ---");
            try
            {
                List<StorageDevice> disks = UsbQuery.GetUsbStorages();
                sb.AppendLine("Найдено: " + disks.Count);
                foreach (StorageDevice sd in disks)
                {
                    sb.AppendLine("  INST=" + sd.InstanceId);
                    sb.AppendLine("      MODEL=" + sd.Model + "  USB=" + sd.UsbId +
                        "  SN=" + sd.Serial + "  DISKID=" + sd.BestDiskId);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Ошибка: " + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine("--- Смонтированные тома USB (точки монтирования) ---");
            try
            {
                List<UsbVolume> volumes = UsbQuery.GetUsbVolumes();
                sb.AppendLine("Найдено: " + volumes.Count);
                foreach (UsbVolume v in volumes)
                {
                    sb.AppendLine("  LETTER=" + v.DriveLetter + "  USB=" + v.UsbId +
                        "  SN=" + v.Serial + "  MODEL=" + v.Model);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Ошибка: " + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine("--- USB-узлы массовой памяти ---");
            try
            {
                List<UsbNode> nodes = UsbQuery.GetUsbMassStorageNodes();
                sb.AppendLine("Найдено: " + nodes.Count);
                foreach (UsbNode n in nodes)
                {
                    sb.AppendLine("  INST=" + n.InstanceId + "  PRESENT=" + n.Present +
                        "  VIDPID=" + n.VidPid + "  SN=" + n.Serial +
                        "  DISABLED=" + UsbQuery.IsNodeDisabled(n.InstanceId));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Ошибка: " + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine("--- Все масс-сторадж узлы (реестр) ---");
            try
            {
                List<string> all = UsbQuery.GetAllMassStorageInstances();
                sb.AppendLine("Найдено: " + all.Count);
                foreach (string s in all)
                    sb.AppendLine("  " + s);
            }
            catch (Exception ex)
            {
                sb.AppendLine("Ошибка: " + ex.Message);
            }

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diag.log");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return 0;
        }
    }

    // =====================================================================
    // Самопроверка: криптография, перенос, сериализация
    // =====================================================================
    internal static class Selftest
    {
        public static string Run()
        {
            StringBuilder sb = new StringBuilder();

            List<DeviceEntry> list = new List<DeviceEntry>();
            list.Add(new DeviceEntry
            {
                Name = "JetFlash Selftest",
                UsbId = "USB\\VID_8564&PID_1000",
                DiskId = "USBSTOR\\DiskJetFlashTranscend_8GB___1100",
                Serial = "081NS9HV47JMZLUQ",
                AddedAt = DateTime.Now
            });

            string dir = Path.GetTempPath();
            string plainFile = Path.Combine(dir, "usb_selftest_plain.wlb");
            string encFile = Path.Combine(dir, "usb_selftest_enc.wlb");
            try
            {
                // без пароля
                PortableWhitelist.Export(list, plainFile, string.Empty);
                List<DeviceEntry> r1 = PortableWhitelist.Import(plainFile, "irrelevant");
                sb.AppendLine("Plain export/import: " + (r1.Count == 1 && r1[0].Serial == "081NS9HV47JMZLUQ" ? "OK" : "FAIL"));

                // с паролем
                PortableWhitelist.Export(list, encFile, "secret123");
                List<DeviceEntry> r2 = PortableWhitelist.Import(encFile, "secret123");
                sb.AppendLine("Encrypted export/import: " + (r2.Count == 1 && r2[0].UsbId == "USB\\VID_8564&PID_1000" ? "OK" : "FAIL"));

                // неверный пароль должен упасть
                bool badOk = false;
                try { PortableWhitelist.Import(encFile, "wrong"); }
                catch (UnauthorizedAccessException) { badOk = true; }
                catch { }
                sb.AppendLine("Wrong password rejected: " + (badOk ? "OK" : "FAIL"));

                // файл зашифрованного должен быть НЕ текстом (garbage)
                byte[] encBytes = File.ReadAllBytes(encFile);
                bool looksBinary = ContainsNonText(encBytes);
                sb.AppendLine("Encrypted file is binary (no text): " + (looksBinary ? "OK" : "FAIL"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex);
            }
            finally
            {
                try { if (File.Exists(plainFile)) File.Delete(plainFile); } catch { }
                try { if (File.Exists(encFile)) File.Delete(encFile); } catch { }
            }

            // сериализация dat-payload
            try
            {
                byte[] payload = WhitelistStore.SerializePayload(list);
                List<DeviceEntry> r = WhitelistStore.DeserializePayload(payload);
                sb.AppendLine("Payload serialize/deserialize: " +
                    (r.Count == 1 && r[0].DiskId.Contains("JetFlash") ? "OK" : "FAIL"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("Payload ERROR: " + ex.Message);
            }

            // WMI-цепочка диск->раздел->буква (проверяем на системном диске)
            try
            {
                List<UsbVolume> all = UsbQuery.GetAllVolumes();
                string sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
                bool hasSystem = false;
                if (!string.IsNullOrEmpty(sysRoot) && sysRoot.Length >= 1)
                {
                    string sysLetter = sysRoot.Substring(0, 1).ToUpperInvariant();
                    foreach (UsbVolume v in all)
                    {
                        if (string.Equals(v.DriveLetter, sysLetter, StringComparison.OrdinalIgnoreCase))
                        {
                            hasSystem = true;
                            break;
                        }
                    }
                }
                sb.AppendLine("Volume map (disk->partition->letter): " +
                    (hasSystem ? "OK" : "FAIL") +
                    " (томов: " + all.Count.ToString(CultureInfo.InvariantCulture) + ")");
            }
            catch (Exception ex)
            {
                sb.AppendLine("Volume map ERROR: " + ex.Message);
            }

            // объединение whitelist (дедупликация по USB ID + серийному номеру)
            try
            {
                List<DeviceEntry> cur = new List<DeviceEntry>
                {
                    new DeviceEntry { UsbId = "USB\\VID_8564&PID_1000", Serial = "S1" },
                    new DeviceEntry { UsbId = "USB\\VID_8564&PID_1000", Serial = "S2" }
                };
                List<DeviceEntry> inc = new List<DeviceEntry>
                {
                    new DeviceEntry { UsbId = "USB\\VID_8564&PID_1000", Serial = "S1" },
                    new DeviceEntry { UsbId = "USB\\VID_1234&PID_5678", Serial = "S3" }
                };
                List<DeviceEntry> merged = TrayContext.MergeWhitelists(cur, inc);
                sb.AppendLine("Merge whitelists (dedup): " +
                    (merged.Count == 3 ? "OK" : "FAIL") + " (записей: " +
                    merged.Count.ToString(CultureInfo.InvariantCulture) + ")");
            }
            catch (Exception ex)
            {
                sb.AppendLine("Merge ERROR: " + ex.Message);
            }

            return sb.ToString().Replace(Environment.NewLine, " | ");
        }

        private static bool ContainsNonText(byte[] data)
        {
            // Эвристика: у AES-шифротекста (равномерный шум) лишь ~37% читаемых
            // байт. У текстового файла читаемых байт - большинство.
            int total = 0;
            int readable = 0;
            foreach (byte b in data)
            {
                total++;
                if (b == '\n' || b == '\r' || b == '\t')
                {
                    readable++;
                    continue;
                }
                if (b >= 0x20 && b <= 0x7E)
                    readable++;
            }
            return readable * 10 < total * 6;
        }
    }
}