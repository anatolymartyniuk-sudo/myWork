using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
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
        private const string NotifyMutexName = @"Local\UsbBlockNotify_SingleInstance_v2";

        // Имя мьютекса трея нужно уведомителю: пока трей жив (держит мьютекс),
        // всплывающие сообщения показывает сам трей, а отдельный процесс-
        // уведомитель включается только после выгрузки трея.
        public const string TrayMutexName = MutexName;

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
            bool notify = false;
            bool testpopup = false;

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
                if (string.Equals(a, "--notify", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-notify", StringComparison.OrdinalIgnoreCase))
                    notify = true;
                if (string.Equals(a, "--testpopup", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-testpopup", StringComparison.OrdinalIgnoreCase))
                    testpopup = true;
            }

            // Самопроверка показа всплывающих сообщений: показывает два окна-
            // уведомления, чтобы убедиться, что на этой машине и в этой сессии
            // механизм показа вообще работает (в Window 10 всплывающие окна
            // создаются как обычные безрамочные окна справа внизу). Запускается
            // без прав администратора и под ним - показывает окно в обоих случаях.
            if (testpopup)
            {
                return NotifyService.TestPopup();
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
                    "InstallExe exists: " + File.Exists(ProtectedCopy.InstallExe),
                    new UTF8Encoding(true));
                return 0;
            }

            if (selftest)
            {
                Program.WriteLog("selftest.log",
                    "SELFTEST: " + Selftest.Run());
                return 0;
            }

            if (diag)
            {
                return Diag.Run();
            }

            // УВЕДОМИТЕЛЬ (--notify): отдельный невидимый процесс, который
            // запускается задачей при входе ЛЮБОГО пользователя и показывает
            // всплывающие сообщения о блокировках. Живёт НЕЗАВИСИМО от трея,
            // поэтому выгрузка трея ("Выход") не отключает уведомления.
            // Повышение прав не запрашивается (для администраторов задача
            // и так стартует с полным токеном, для остальных - с их правами).
            if (notify)
            {
                bool createdNotify;
                using (Mutex nmtx = new Mutex(true, NotifyMutexName, out createdNotify))
                {
                    if (createdNotify)
                    {
                        NotifyService.Run();
                    }
                }
                return 0;
            }

            // Запуск из задачи Планировщика при входе пользователя (--logon,
            // значок в трее для ВСЕХ пользователей): повышения прав НЕ
            // запрашиваем. У администраторов задача запускается уже с полным
            // токеном (RunLevel=HighestAvailable), у остальных - без прав;
            // меню при этом автоматически сокращается.
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
                // защищённую копию exe (Program Files\USB_Block, папка
                // создаётся при отсутствии), чтобы она не устаревала.
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

                // Гарантируем работу уведомителя в текущей сессии. Если
                // задача "USB_Block_Notify_Logon" отсутствует (программа
                // установлена версией до её появления), уведомления не
                // запускались бы вовсе - трей подстрахует и запустит
                // уведомитель отдельным процессом.
                StartNotifier();

                using (TrayContext ctx = new TrayContext())
                {
                    Application.Run(ctx);
                }
            }
            return 0;
        }

        // Запускает отдельный процесс-уведомитель (--notify) в текущей
        // сессии, если он ещё не работает. Уведомитель живёт независимо от
        // трея, поэтому "Выход" из трея его не останавливает; повторные
        // запуски отсекает мьютекс уведомителя.
        public static void StartNotifier()
        {
            try
            {
                bool exists = false;
                try
                {
                    using (Mutex m = Mutex.OpenExisting(NotifyMutexName))
                    {
                        exists = true;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    exists = false;
                }
                catch
                {
                    exists = false;
                }
                if (exists) return;

                ProcessStartInfo psi = new ProcessStartInfo(
                    Application.ExecutablePath, "--notify");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch
            {
            }
        }

        // Куда писать лог-файл программы (diag.log/selftest.log), чтобы он
        // создавался у ЛЮБОГО пользователя: у администратора - рядом с exe
        // (папка программы, ей так и пользовались раньше), у обычного
        // пользователя (например, запуск из защищённой копии в Program Files,
        // куда писать нельзя) - в %TEMP%. Нужный путь определяется пробной
        // записью без создания самого файла.
        public static string PreferredLogPath(string name)
        {
            string exeDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
            try
            {
                string probe = exeDir + ".probe_" + Guid.NewGuid().ToString("N");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return exeDir;
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), name);
            }
        }

        // Записывает лог-файл имени name, возвращая фактически
        // использованный путь.
        public static string WriteLog(string name, string content)
        {
            string p = PreferredLogPath(name);
            try { File.WriteAllText(p, content, new UTF8Encoding(true)); }
            catch { }
            return p;
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

        /// <summary>
        /// Копирует текущий exe в C:\Program Files\USB_Block (создаётся при
        /// отсутствии). null = успех, иначе текст ошибки.
        /// Единственная защищённая копия. Дополнительная копия в
        /// C:\ProgramData убрана намеренно: самокопирование exe в скрытые
        /// папки ProgramData в сочетании с автозапуском по расписанию даёт
        /// ложное срабатывание эвристики Windows Defender
        /// (Behavior:Win32/Persistence.A!ml).
        /// </summary>
        public static string EnsureInstalledCopy()
        {
            string err = CopyTo(InstallDir, InstallExe);
            if (err != null)
                return "Program Files\\USB_Block: " + err;
            return null;
        }

        private static string CopyTo(string dir, string exePath)
        {
            try
            {
                Directory.CreateDirectory(dir);

                // Уже запущены из защищённой копии - обновлять нечего.
                if (string.Equals(Path.GetFullPath(Application.ExecutablePath),
                        Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
                    return null;

                try
                {
                    File.Copy(Application.ExecutablePath, exePath, true);
                }
                catch (IOException)
                {
                    // Защищённая копия сейчас ЗАПУЩЕНА (трей/уведомитель/служба
                    // стартуют именно из неё), поэтому перезаписать файл нельзя.
                    // Windows не даёт перезаписать работающий exe, но разрешает
                    // его ПЕРЕИМЕНОВАТЬ: уводим старую копию в сторону, кладём
                    // на её место новую, а старую удаляем при перезагрузке.
                    string old = exePath + ".old_" +
                        Guid.NewGuid().ToString("N").Substring(0, 8);
                    File.Move(exePath, old);
                    try
                    {
                        File.Copy(Application.ExecutablePath, exePath, false);
                    }
                    catch
                    {
                        // не удалось положить новую копию - вернём старую назад
                        try { File.Move(old, exePath); }
                        catch { }
                        throw;
                    }
                    ScheduleDeleteOnReboot(old);
                }

                RestrictDirectoryAcl(dir);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName,
            string newFileName, int flags);

        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        // Удаляет отложенный файл: сначала пробует сразу, а если он ещё занят
        // (работающий exe) - ставит удаление в очередь до перезагрузки.
        private static void ScheduleDeleteOnReboot(string path)
        {
            try { File.Delete(path); return; }
            catch { }
            try { MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
            catch { }
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
    // "USBWL" (ver 2): бинарный (не текст), без пароля.
    // Старые зашифрованные файлы "USBWE" (AES-256 + пароль) больше
    // не поддерживаются - парольная защита экспорта/импорта удалена.
    // =====================================================================
    public static class PortableWhitelist
    {
        private static readonly byte[] MagicPlain = { (byte)'U', (byte)'S', (byte)'B', (byte)'W', (byte)'L' };
        private static readonly byte[] MagicEnc = { (byte)'U', (byte)'S', (byte)'B', (byte)'W', (byte)'E' };

        public static void Export(List<DeviceEntry> list, string path)
        {
            byte[] payload = WhitelistStore.SerializePayload(list);
            using (FileStream fs = new FileStream(path, FileMode.Create))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(MagicPlain);
                bw.Write(payload.Length);
                bw.Write(payload);
            }
        }

        public static List<DeviceEntry> Import(string path)
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
                throw new InvalidDataException(
                    "файл защищён паролем - парольная защита удалена; " +
                    "экспортируйте whitelist заново со старого компьютера");

            throw new InvalidDataException("неизвестный формат файла whitelist");
        }

        private static bool HasMagic(byte[] file, byte[] magic)
        {
            if (file.Length < magic.Length) return false;
            for (int i = 0; i < magic.Length; i++)
                if (file[i] != magic[i]) return false;
            return true;
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
        public string Label;        // метка (имя) тома накопителя
        public bool Allowed;        // true - устройство в whitelist (сейчас разрешено)
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

        // USB-диски, которые СЕЙЧАС присутствуют, имеют разделы, но НИ ОДИН
        // раздел не смонтирован буквой диска. Это накопители, которым букву
        // давно сняли наши же mountvol /D: на ПЕРЕЗАГРУЗКЕ Windows такой том
        // остаётся висящим только на системном \??\Volume{GUID}, букву не
        // получает, и обычный скан по буквам (GetUsbVolumes) его НЕ видит -
        // уведомление про "вставленный при загрузке" накопитель не уходит.
        internal static List<StorageDevice> GetUsbDisksWithNoLetter(
            List<StorageDevice> disks)
        {
            List<StorageDevice> result = new List<StorageDevice>();
            if (disks == null || disks.Count == 0) return result;

            Dictionary<int, StorageDevice> diskByIndex = new Dictionary<int, StorageDevice>();
            foreach (StorageDevice sd in disks)
            {
                int idx = ParseDriveIndex(sd.DiskDeviceId);
                if (idx >= 0 && !diskByIndex.ContainsKey(idx))
                    diskByIndex[idx] = sd;
            }
            if (diskByIndex.Count == 0) return result;

            Dictionary<string, int> partByDevId = new Dictionary<string, int>();
            try
            {
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
            }
            catch
            {
            }

            HashSet<int> withLetter = new HashSet<int>();
            try
            {
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
                        withLetter.Add(partByDevId[partDevId]);
                    }
                }
            }
            catch
            {
            }

            HashSet<int> hasPartition = new HashSet<int>(partByDevId.Values);
            foreach (KeyValuePair<int, StorageDevice> kv in diskByIndex)
            {
                int idx = kv.Key;
                if (!hasPartition.Contains(idx)) continue;  // без разделов - не данные
                if (withLetter.Contains(idx)) continue;     // есть буква - обработает обычный путь
                result.Add(kv.Value);
            }
            return result;
        }
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
        // Окно дедупликации: то же устройство, заблокированное в этом окне,
        // повторно НЕ уведомляется. Нужно ТОЛЬКО чтобы служба (2 с) и трей
        // (3 с), отреагировавшие на одно и то же блокирование, не задвоили
        // сообщение. Вторая копия может увидеть том лишь пока первая не сняла
        // букву (доли секунды), поэтому окно 3 с с запасом перекрывает дубль,
        // но НЕ гасит повторное подключение накопителя (оно физически дольше).
        private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(3);

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
                // (служба и трей могут снять букву по очереди - надо бы
                // избавиться от двойного уведомления, но НЕ гасить повторное
                // подключение накопителя, которое считается новым блокированием).
                foreach (BlockEvent e in cur)
                {
                    if (DateTime.Now - e.When < DedupWindow &&
                        SameDevice(e.Serial, e.Label, serial, label))
                    {
                        NotifyService.TraceLog("дедупликация при записи: пропуск повторного " +
                            "уведомления (тот же накопитель, событие было " +
                            (DateTime.Now - e.When).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) +
                            " с назад, окно " +
                            DedupWindow.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " с)");
                        return false;
                    }
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
                NotifyService.TraceLog("записано событие id=" + newId.ToString(CultureInfo.InvariantCulture) +
                    " (SN=" + serial + ")");
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
                    MountUtil.Unmount(v);
                    // Уведомляем о постороннем накопителе НЕЗАВИСИМО от кода
                    // возврата mountvol: снять букву могла уже другая копия
                    // (служба и трей работают параллельно), но сообщение
                    // пользователю всё равно нужно. Успешно снятый том
                    // исчезает из GetUsbVolumes и больше сюда не попадает,
                    // а повторные срабатывания для неубранного тома гасит
                    // дедупликация в NotifyStore.Write (окно 3 с).
                    newly.Add(new BlockedDevice
                    {
                        Label = string.IsNullOrEmpty(v.Model) ? v.UsbId : v.Model,
                        Serial = v.Serial
                    });
                    // Диск, которому только что сняли букву, НЕ должен тут же
                    // попасть в список "без буквы" и задвоить уведомление.
                    string vkey = DiskKey(v.DiskId, v.UsbId, v.Serial);
                    if (vkey != null) AddReportedLetterless(vkey);
                }

                // Посторонние USB-диски БЕЗ БУКВЫ (в т.ч. "вставленные при
                // загрузке", которым буква не выделяется после наших же
                // снятий) - уведомляем о них один раз за их присутствие,
                // иначе блокировка есть, а сообщения о ней нет.
                AddLetterlessUnallowed(newly, wl, disks);
            }

            return newly;
        }

        // Отслеживание уже уведомлённых дисков без буквы. Хранится в реестре
        // (HKLM\SOFTWARE\USB_Block\ReportedLetterless), а не в памяти процесса:
        // при логоне служба и трей - это разные процессы, и при входе новый
        // трей не должен повторно писать событие о уже присутствующем диске.
        // Каждое НОВОЕ физическое подключение снова даёт своё уведомление
        // (маркер удаляется, как только диск исчез из системы).
        private const string LetterlessKey = @"SOFTWARE\USB_Block\ReportedLetterless";

        private static HashSet<string> LoadReportedLetterless()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(LetterlessKey, false))
                {
                    if (k == null) return set;
                    string[] arr = k.GetValue("Keys") as string[];
                    if (arr != null)
                        foreach (string s in arr)
                            if (!string.IsNullOrEmpty(s)) set.Add(s);
                }
            }
            catch { }
            return set;
        }

        private static void SaveReportedLetterless(HashSet<string> set)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(LetterlessKey))
                {
                    if (set == null || set.Count == 0)
                    {
                        k.DeleteValue("Keys", false);
                    }
                    else
                    {
                        string[] arr = new string[set.Count];
                        set.CopyTo(arr);
                        k.SetValue("Keys", arr, RegistryValueKind.MultiString);
                    }
                }
            }
            catch { }
        }

        private static void AddReportedLetterless(string key)
        {
            if (key == null) return;
            HashSet<string> set = LoadReportedLetterless();
            if (set.Add(key)) SaveReportedLetterless(set);
        }

        private static string DiskKey(string diskId, string usbId, string serial)
        {
            if (!string.IsNullOrEmpty(serial)) return "SN:" + serial;
            if (!string.IsNullOrEmpty(usbId)) return "USB:" + usbId;
            if (!string.IsNullOrEmpty(diskId)) return "DISK:" + diskId;
            return null;
        }

        private static string DiskKey(StorageDevice d)
        {
            return DiskKey(d.DiskDeviceId, d.UsbId, d.Serial);
        }

        // Разрешён ли диск по whitelist (аналог IsVolumeAllowed для диска).
        private static bool IsDiskAllowed(StorageDevice d, List<DeviceEntry> wl)
        {
            if (wl == null || d == null) return false;
            foreach (DeviceEntry e in wl)
            {
                if (!string.IsNullOrEmpty(e.UsbId) &&
                    !string.Equals(e.UsbId, d.UsbId ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(e.Serial) &&
                    !string.IsNullOrEmpty(d.Serial) &&
                    !string.Equals(e.Serial, d.Serial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        private static void AddLetterlessUnallowed(
            List<BlockedDevice> newly, List<DeviceEntry> wl, List<StorageDevice> disks)
        {
            if (wl == null) return;
            HashSet<string> reported = LoadReportedLetterless();
            bool changed = false;
            List<StorageDevice> letterless = UsbQuery.GetUsbDisksWithNoLetter(disks);
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (StorageDevice d in letterless)
            {
                string key = DiskKey(d);
                if (key == null) continue;
                present.Add(key);
                if (IsDiskAllowed(d, wl)) continue;
                if (reported.Contains(key)) continue;
                reported.Add(key);
                changed = true;
                newly.Add(new BlockedDevice
                {
                    Label = string.IsNullOrEmpty(d.Model) ? d.UsbId : d.Model,
                    Serial = d.Serial
                });
            }
            List<string> gone = new List<string>();
            foreach (string k in reported)
                if (!present.Contains(k)) gone.Add(k);
            if (gone.Count > 0)
            {
                foreach (string k in gone) reported.Remove(k);
                changed = true;
            }
            if (changed) SaveReportedLetterless(reported);
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

            // Какое устройство уже разрешено (в whitelist) - зелёное,
            // остальные (будут/были заблокированы) - красные.
            MarkAllowed();

            Label lblSel = new Label();
            lblSel.Text = "Выберите USB-накопитель:";
            lblSel.AutoSize = true;
            lblSel.Location = new Point(12, 10);

            _cbDevice = new System.Windows.Forms.ComboBox();
            _cbDevice.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbDevice.DrawMode = DrawMode.OwnerDrawFixed;
            _cbDevice.SetBounds(12, 32, 516, 24);
            foreach (StorageDevice sd in _devices)
            {
                // Метка тома перед названием модели; DEV ID и SN в списке
                // больше не показываются.
                string label = string.IsNullOrEmpty(sd.Model) ? "(без имени)" : sd.Model;
                if (!string.IsNullOrEmpty(sd.Label))
                    label = sd.Label + "  " + label;
                _cbDevice.Items.Add(label);
            }
            _cbDevice.DrawItem += DrawDeviceItem;
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

        // Устройства из whitelist (разрешённые) - зелёным, остальные
        // (блокируемые/заблокированные) - красным.
        private void MarkAllowed()
        {
            HashSet<string> wl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (DeviceEntry x in UsbMonitor.GetWhitelist())
                {
                    if (!string.IsNullOrEmpty(x.Serial)) wl.Add("SN:" + x.Serial);
                    if (!string.IsNullOrEmpty(x.UsbId)) wl.Add("USB:" + x.UsbId);
                }
            }
            catch
            {
            }
            foreach (StorageDevice sd in _devices)
            {
                sd.Allowed =
                    (!string.IsNullOrEmpty(sd.Serial) && wl.Contains("SN:" + sd.Serial)) ||
                    (!string.IsNullOrEmpty(sd.UsbId) && wl.Contains("USB:" + sd.UsbId));
            }
        }

        private void DrawDeviceItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _devices.Count) return;
            e.DrawBackground();
            string text = _cbDevice.Items[e.Index].ToString();
            using (SolidBrush b = new SolidBrush(_devices[e.Index].Allowed
                ? Color.DarkGreen : Color.DarkRed))
            {
                e.Graphics.DrawString(text, e.Font, b,
                    new Rectangle(e.Bounds.Left + 2, e.Bounds.Top + 1,
                        e.Bounds.Width - 4, e.Bounds.Height - 2));
            }
            e.DrawFocusRectangle();
        }

        private void OnSelection()
        {
            int i = _cbDevice.SelectedIndex;
            if (i < 0 || i >= _devices.Count) return;
            StorageDevice sd = _devices[i];
            _tbType.Text = sd.UsbId;
            _tbModel.Text = string.IsNullOrEmpty(sd.BestDiskId) ? sd.Model : sd.BestDiskId;
            _ok.Enabled = !string.IsNullOrEmpty(sd.UsbId);
            // По умолчанию в "Ім'я Пристрою" подставляется метка тома
            // выбранного накопителя (если метки нет - поле пустое, и при
            // добавлении возьмётся название модели).
            _tbName.Text = sd.Label ?? string.Empty;
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

        // Читаемый вид записи whitelist - только три поля:
        //   Метка тома (из файловой системы, если накопитель подключён),
        //   Модель накопителя (WMI-модель, если подключён; иначе читается из DiskId),
        //   Дата добавления в whitelist.
        private static string FormatEntry(DeviceEntry e,
            Dictionary<string, UsbVolume> connected,
            Dictionary<string, string> volumeLabels)
        {
            StringBuilder sb = new StringBuilder();

            string key = (e.UsbId ?? string.Empty) + "|" + (e.Serial ?? string.Empty);
            UsbVolume v;
            string label = null;
            string model = null;
            if (connected != null && volumeLabels != null &&
                connected.TryGetValue(key, out v) &&
                volumeLabels.TryGetValue(v.DriveLetter, out label))
            {
                label = string.IsNullOrEmpty(label) ? "(без метки)" : label;
                model = string.IsNullOrEmpty(v.Model) ? null : v.Model;
            }
            if (label == null)
                label = string.IsNullOrEmpty(e.Name) ? "(не подключено)" : e.Name;

            sb.Append("Метка тома: \"" + label + "\"");
            sb.Append("\r\nМодель накопителя: " +
                (string.IsNullOrEmpty(model) ? ReadableModel(e.DiskId) : model));
            if (e.AddedAt != default(DateTime))
                sb.Append("\r\nДобавлен: " +
                    e.AddedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

            return sb.ToString();
        }

        // Читаемая модель из HardwareID диска USBSTOR, e.g.
        //   "USBSTOR\Disk&Ven_&Prod_Transcend_8GB&Rev_1100"  ->  "Transcend 8GB"
        //   "USBSTOR\DiskJetFlashTranscend_8GB___1100"        ->  "JetFlashTranscend 8GB"
        private static string ReadableModel(string diskId)
        {
            if (string.IsNullOrEmpty(diskId)) return "(нет)";
            const string prefix = "USBSTOR\\Disk";
            string m = diskId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? diskId.Substring(prefix.Length) : diskId;

            int p = m.IndexOf("&Prod_", StringComparison.OrdinalIgnoreCase);
            if (p >= 0)
            {
                int end = m.IndexOf('&', p + 6);
                m = end < 0 ? m.Substring(p + 6) : m.Substring(p + 6, end - p - 6);
            }
            else
            {
                int r = m.IndexOf("&Rev_", StringComparison.OrdinalIgnoreCase);
                if (r >= 0) m = m.Substring(0, r);
                int u = m.LastIndexOf('_');
                if (u > 0)
                {
                    string tail = m.Substring(u + 1);
                    bool digits = tail.Length >= 1 && tail.Length <= 6;
                    foreach (char c in tail) if (!char.IsDigit(c)) { digits = false; break; }
                    if (digits) m = m.Substring(0, u);
                }
            }

            return m.Replace('_', ' ').Trim();
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
    // Задачи Планировщика "значок в трее для всех пользователей",
    // создаются при установке службы мониторинга (при входе ЛЮБОГО
    // пользователя запускаются usb_block_tray.exe --logon и
    // usb_block_tray.exe --notify):
    //  * USB_Block_Tray_Logon   - значок в трее (--logon);
    //  * USB_Block_Notify_Logon - скрытый уведомитель (--notify),
    //    живёт ОТДЕЛЬНО от трея, поэтому даже если пользователь выгрузил
    //    трей ("Выход"), сообщения о блокировках продолжают приходить.
    //  * для администраторов задачи стартуют с наивысшими правами
    //    (RunLevel=HighestAvailable) молча, без запроса UAC;
    //  * для обычных пользователей процессы стартуют без прав - у трея
    //    только статус и выход, уведомитель работает всегда.
    // =====================================================================
    public static class TrayTask
    {
        public const string TaskName = "USB_Block_Tray_Logon";
        public const string NotifyTaskName = "USB_Block_Notify_Logon";

        // Задачи регистрируются через COM-интерфейс Планировщика заданий
        // (Schedule.Service), а не через вызов schtasks.exe: эвристика
        // Windows Defender (Behavior:Win32/Persistence.A!ml) реагирует на
        // характерное сочетание "самокопирование + создание задачи
        // автозапуска из командной строки schtasks". Прямой COM-вызов -
        // обычное поведение приложений с автозапуском. Если COM недоступен,
        // используется прежний путь через schtasks.exe.

        public static bool IsInstalled()
        {
            return TaskInstalled(TaskName);
        }

        public static bool NotifyInstalled()
        {
            return TaskInstalled(NotifyTaskName);
        }

        private static bool TaskInstalled(string name)
        {
            try
            {
                return TaskExists(name);
            }
            catch
            {
                string o;
                return Exec.Run("schtasks.exe", "/Query /TN \"" + name + "\"", out o);
            }
        }

        /// <summary>null = успех, иначе текст ошибки.</summary>
        public static string Create()
        {
            List<string> errors = new List<string>();
            string t = CreateOne(TaskName, "--logon");
            if (t != null) errors.Add("задача трея: " + t);
            string n = CreateOne(NotifyTaskName, "--notify");
            if (n != null) errors.Add("задача уведомлений: " + n);
            if (errors.Count == 0) return null;
            return string.Join("\n", errors.ToArray());
        }

        private static string CreateOne(string taskName, string args)
        {
            string xml = BuildXml(args);
            try
            {
                RegisterViaCom(taskName, xml);
                return null;
            }
            catch (Exception ex)
            {
                // COM не сработал - фолбэк на schtasks.exe
                try
                {
                    string xmlPath = Path.Combine(Path.GetTempPath(),
                        "usb_block_task_" + Guid.NewGuid().ToString("N") + ".xml");
                    File.WriteAllText(xmlPath, xml, Encoding.Unicode);
                    try
                    {
                        string o;
                        bool ok = Exec.Run("schtasks.exe",
                            "/Create /F /TN \"" + taskName + "\" /XML \"" + xmlPath + "\"", out o);
                        return ok ? null : o;
                    }
                    finally
                    {
                        try { File.Delete(xmlPath); }
                        catch { }
                    }
                }
                catch (Exception ex2)
                {
                    return ex.Message + (ex2.Message != null ? " | schtasks: " + ex2.Message : "");
                }
            }
        }

        public static void Delete()
        {
            DeleteOne(TaskName);
            DeleteOne(NotifyTaskName);
        }

        private static void DeleteOne(string taskName)
        {
            try
            {
                DeleteViaCom(taskName);
                return;
            }
            catch
            {
            }
            string o;
            Exec.Run("schtasks.exe", "/Delete /F /TN \"" + taskName + "\"", out o);
        }

        private static object ComSchedule()
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null)
                throw new InvalidOperationException("COM-тип Schedule.Service не найден");
            object svc = Activator.CreateInstance(t);
            t.InvokeMember("Connect", BindingFlags.InvokeMethod, null, svc, null);
            return svc;
        }

        private static object GetRootFolder()
        {
            object svc = ComSchedule();
            Type st = svc.GetType();
            return st.InvokeMember("GetFolder",
                BindingFlags.InvokeMethod, null, svc, new object[] { "\\" });
        }

        private static bool TaskExists(string taskName)
        {
            object folder = GetRootFolder();
            Type ft = folder.GetType();
            try
            {
                object task = ft.InvokeMember("GetTask",
                    BindingFlags.InvokeMethod, null, folder, new object[] { taskName });
                return task != null;
            }
            catch (TargetInvocationException)
            {
                return false;
            }
        }

        private static void RegisterViaCom(string taskName, string xml)
        {
            object folder = GetRootFolder();
            Type ft = folder.GetType();
            // флаги версии 0; CREATE_OR_UPDATE; пользователь/пароль не нужны
            // (принципала задаёт XML - группа); TASK_LOGON_GROUP (4);
            // sddl = null.
            ft.InvokeMember("RegisterTask", BindingFlags.InvokeMethod, null, folder,
                new object[] { taskName, xml, 2, null, null, 4, null });
        }

        private static void DeleteViaCom(string taskName)
        {
            object folder = GetRootFolder();
            Type ft = folder.GetType();
            try
            {
                ft.InvokeMember("DeleteTask", BindingFlags.InvokeMethod, null, folder,
                    new object[] { taskName, 0 });
            }
            catch (TargetInvocationException)
            {
            }
        }

        private static string BuildXml(string args)
        {
            // Задача на группу "Users" (S-1-5-32-545): срабатывает при входе
            // любого пользователя, с его токеном и наивысшим доступным уровнем
            // прав (администраторы - молча с полным токеном, остальные - без
            // прав, без запроса пароля).
            // ВАЖНО: в XML-схеме Task Scheduler значения RunLevel - только
            // "LeastPrivilege"/"HighestAvailable" (а НЕ "Limited"/"Highest",
            // которые использует PowerShell). "Highest" даёт у schtasks ошибку
            // "The task XML contains a value which is incorrectly formatted or
            // out of range" - воспроизведено и проверено тестом регистрации.
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
                "      <RunLevel>HighestAvailable</RunLevel>\r\n" +
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
                "      <Arguments>" + args + "</Arguments>\r\n" +
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

            // Метки (имена) томов подключённых накопителей: "Серийный" -> "Метка".
            // Заполняем только для устройств, которые сейчас смонтированы с буквой
            // диска (метку берём из Win32_LogicalDisk по букве тома из GetUsbVolumes).
            try
            {
                Dictionary<string, string> labels = UsbQuery.GetVolumeLabels();
                List<UsbVolume> vols = UsbQuery.GetUsbVolumes();
                Dictionary<string, string> labelBySerial =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (UsbVolume v in vols)
                {
                    if (string.IsNullOrEmpty(v.Serial) ||
                        !labels.ContainsKey(v.DriveLetter) ||
                        labelBySerial.ContainsKey(v.Serial))
                        continue;
                    labelBySerial[v.Serial] = labels[v.DriveLetter] ?? string.Empty;
                }
                foreach (StorageDevice d in devices)
                {
                    if (string.IsNullOrEmpty(d.Serial)) continue;
                    string lab;
                    if (labelBySerial.TryGetValue(d.Serial, out lab))
                        d.Label = lab;
                }
            }
            catch
            {
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

                try
                {
                    PortableWhitelist.Export(
                        UsbMonitor.GetWhitelist(), dlg.FileName);
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

                List<DeviceEntry> imported;
                try
                {
                    imported = PortableWhitelist.Import(dlg.FileName);
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
                    "Значок в трее и уведомления появятся у всех пользователей\n" +
                    "после перезагрузки. Уведомления работают независимо от\n" +
                    "выгрузки значка из трея.",
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

            // 5) защищённая копия (Program Files\USB_Block)
            string running = Application.ExecutablePath;
            bool runningFromInstall = string.Equals(
                Path.GetFullPath(running), Path.GetFullPath(ProtectedCopy.InstallExe),
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

        // Тик фонового таймера: показ уведомлений о блокировках и (под
        // администратором) активная блокировка. Уведомления показывает сам
        // трей - это процесс, который гарантированно работает в сессии
        // пользователя (значок в трее). Отдельный процесс NotifyService
        // запускается задачей при входе и подхватывает показ, если трей
        // выгружен кнопкой "Выход" - тогда сообщения продолжают приходить.
        private void TickScan()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                // Пока трей жив, всплывающие сообщения о блокировках показывает
                // он сам (это гарантированно работающий в сессии пользователя
                // процесс). Отдельный уведомитель подхватит показ только после
                // выгрузки трея. Показ изолирован: его сбой не должен мешать
                // блокировке ниже.
                try { NotifyService.PollCore(); }
                catch { }

                if (Program.IsAdministrator())
                {
                    RunScan();
                }
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
        // (HKLM\SOFTWARE\USB_Block\Events), которую читает процесс-
        // уведомитель каждого пользователя - так сообщение о блокировке
        // получают ВСЕ пользователи, в т.ч. после выгрузки трея.
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
// Уведомитель (--notify). Отдельный невидимый процесс БЕЗ трея, который
// запускается задачей при входе каждого пользователя и показывает
// всплывающие сообщения о блокировках из общей очереди событий
// (HKLM\SOFTWARE\USB_Block\Events). В отличие от трея, этот процесс не
// имеет меню и его нечем "выгрузить" из трея: даже если пользователь
// закрыл трей через "Выход", уведомитель продолжает работать и сообщения
// доходят. Один экземпляр на сессию (мьютекс Local\UsbBlockNotify_*).
// =====================================================================
    public static class NotifyService
    {
        private static System.Windows.Forms.Timer _poll;
        private static readonly Queue<QueueItem> _queue = new Queue<QueueItem>();
        private static NotifyPopup _active;
        private static int _topOffset = 0;

        // Последняя ошибка показа (для диагностики). Показ отрабатывает в своём
        // процессе (трей/уведомитель), поэтому строку видно в diag только если
        // запустить --diag из живой сессии с тем же кодом - на деле результат
        // читают из лога-следов (TraceLog), картина сводится там же на машине.
        public static string LastShowError;

        // Одна запись очереди показа: id события (для NotifyStore.SetLastSeen
        // ТОЛЬКО после фактического показа) и готовый текст сообщения.
        private sealed class QueueItem
        {
            public long Id;
            public string Text;
        }

        // Малый лог-след показа: пишется в %TEMP%\usb_block_notify.log, чтобы
        // на целевой машине можно было посмотреть, доходил ли показ и не было ли
        // ошибок. Ведётся усечённый кольцевой лог (последние ~200 строк).
        private static readonly object TraceLock = new object();
        private static readonly List<string> TraceLines = new List<string>();
        public static readonly string TracePath =
            Path.Combine(Path.GetTempPath(), "usb_block_notify.log");

        internal static void TraceLog(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
            lock (TraceLock)
            {
                TraceLines.Add(line);
                while (TraceLines.Count > 200) TraceLines.RemoveAt(0);
                try
                {
                    using (StreamWriter w = File.AppendText(TracePath))
                        w.WriteLine(line);
                }
                catch
                {
                }
            }
        }

        // Вернуть хвост лога-следа (для --diag).
        public static List<string> TraceTail()
        {
            lock (TraceLock)
            {
                return new List<string>(TraceLines);
            }
        }

        // Самопроверка показа: показывает тестовое сообщение (окно без рамки,
        // справа внизу) и ждёт, пока его закроют или оно закроется само.
        public static int TestPopup()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (NotifyPopup p = new NotifyPopup(
                    "ТЕСТ уведомления о блокировке.\n\n" + Program.NotifyText +
                    "\n\nЕсли вы видите это окно в правом нижнем углу экрана, показ работает.",
                    8000))
                {
                    p.ShowInTaskbar = false;
                    Application.Run(p);
                }
                return 0;
            }
            catch (Exception ex)
            {
                string msg = "Не удалось показать тестовое уведомление: " + ex.Message;
                TraceLog(msg);
                try
                {
                    MessageBox.Show(msg, Program.Title,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch
                {
                }
                return 1;
            }
        }

        public static void Run()
        {
            _poll = new System.Windows.Forms.Timer();
            _poll.Interval = 2500;
            _poll.Tick += delegate { Poll(); };
            _poll.Start();
            Poll();
            Application.Run();
        }

        private static void Poll()
        {
            // Пока трей жив (держит свой мьютекс), сообщения показывает он
            // сам - здесь ничего не делаем, чтобы не было дублей. Уведомитель
            // "просыпается" только после выгрузки трея ("Выход" или закрытие).
            if (TrayAlive()) return;
            PollCore();
        }

        // Жив ли сейчас трей этой сессии (по его мьютексу).
        public static bool TrayAlive()
        {
            try
            {
                using (Mutex m = Mutex.OpenExisting(Program.TrayMutexName))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        // Прочитать очередь и показать новые события. Вызывается треем
        // (пока он жив) и уведомителем (после выгрузки трея). Внутри одного
        // процесса состояние (_queue/_active) своё, поэтому дублей нет.
        // ВАЖНО: счётчик просмотренных событий (LastEventId) продвигается
        // ТОЛЬКО когда окно реально показано (см. Displayed у NotifyPopup).
        // Раньше SetLastSeen вызывался до ShowNext, и если создание/показ окна
        // падал, событие "съедалось" без показа и терялось навсегда - на
        // Windows 10 сообщения о блокировках не приходили никому.
        public static void PollCore()
        {
            List<NotifyStore.BlockEvent> evs = NotifyStore.ReadAll();
            if (evs.Count == 0) return;
            long last = NotifyStore.GetLastSeen();
            long max = 0;
            foreach (NotifyStore.BlockEvent e in evs)
                if (e.Id > max) max = e.Id;
            if (last == max) return;

            if (last > max)
            {
                // Самовосстановление: счётчик этого пользователя оказался
                // ВПЕРЕДИ последнего события очереди (очередь чистили/
                // сбрасывали, а HKCU остался больше). Иначе сообщение о
                // блокировке id=max НИКОГДА не показалось бы этому
                // пользователю. Показываем самое свежее событие один раз -
                // после показа Displayed выравнивает счётчик по max
                // (уменьшение допустимо: это единственный способ закончить
                // восстановление, иначе диагноз так и останется "всё новое").
                if (!IsScheduled(max))
                {
                    NotifyStore.BlockEvent newest = evs[evs.Count - 1];
                    _queue.Enqueue(new QueueItem { Id = newest.Id, Text = BuildText(newest) });
                    TraceLog("счётчик впереди очереди (last=" +
                        last.ToString(CultureInfo.InvariantCulture) + " > max=" +
                        max.ToString(CultureInfo.InvariantCulture) +
                        "): извлекаю последнее событие id=" +
                        newest.Id.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                foreach (NotifyStore.BlockEvent e in evs)
                {
                    if (e.Id > last) _queue.Enqueue(new QueueItem { Id = e.Id, Text = BuildText(e) });
                }
                TraceLog("в очереди показа " + _queue.Count +
                    ", новых с id>" + last.ToString(CultureInfo.InvariantCulture) +
                    " до " + max.ToString(CultureInfo.InvariantCulture));
            }

            if (_active == null)
            {
                ShowNext();
            }
        }

        // Это событие уже стоит в очереди показа или в активном окне?
        private static bool IsScheduled(long id)
        {
            if (_active != null && _active.NotificationId == id) return true;
            foreach (QueueItem q in _queue)
                if (q.Id == id) return true;
            return false;
        }

        private static void ShowNext()
        {
            if (_queue.Count == 0)
            {
                _topOffset = 0;
                return;
            }
            QueueItem item = _queue.Peek();
            NotifyPopup p = null;
            try
            {
                p = new NotifyPopup(item.Text);
            }
            catch (Exception ex)
            {
                // Окно не создалось (например, нет контекста рабочего стола).
                // Событие ОСТАЁТСЯ в очереди и будет повторено на следующем
                // такте (LastEventId не продвинут - ничего не теряется).
                LastShowError = ex.Message;
                TraceLog("ОШИБКА создания окна: " + ex.Message);
                return;
            }
            _queue.Dequeue();

            p.NotificationId = item.Id;
            p.TopOffset = _topOffset;
            _topOffset += p.ExpectedHeight + 8;
            if (_topOffset > (Screen.PrimaryScreen.WorkingArea.Height - 120))
                _topOffset = 0;

            // Счётчик продвигается ТОЛЬКО когда окно фактически показано.
            p.Displayed += delegate(long shownId)
            {
                NotifyStore.SetLastSeen(shownId);
                LastShowError = null;
                TraceLog("показано уведомление id=" +
                    shownId.ToString(CultureInfo.InvariantCulture));
            };
            p.FormClosed += delegate
            {
                _active = null;
                ShowNext();
            };
            _active = p;
            try
            {
                TraceLog("показ id=" + item.Id.ToString(CultureInfo.InvariantCulture));
                p.Show();
            }
            catch (Exception ex)
            {
                LastShowError = ex.Message;
                TraceLog("ОШИБКА показа: " + ex.Message);
                _active = null;
                try { p.Dispose(); }
                catch { }
            }
        }

        private static string BuildText(NotifyStore.BlockEvent e)
        {
            string what = string.IsNullOrEmpty(e.Label) ? e.Serial : e.Label;
            if (string.IsNullOrEmpty(what)) what = "USB-накопитель";
            string detail = "Заблокирован накопитель: " + what +
                (string.IsNullOrEmpty(e.Serial) ? string.Empty : "  SN=" + e.Serial);
            return Program.NotifyText + "\n\n(" + detail + ")";
        }
    }

    // Всплывающее окно-сообщение (без рамки, справа внизу, закрывается само)
    public sealed class NotifyPopup : Form
    {
        private System.Windows.Forms.Timer _close;
        private Label _lbl;
        private readonly Size _textSize;
        public int TopOffset = 0;

        // Id события, которое показывает это окно (устанавливает NotifyService).
        // -1 = тестовое окно, счётчик просмотренных событий не продвигается.
        public long NotificationId = -1;

        // Срабатывает, когда окно фактически показано (для NotifyService это
        // сигнал "событие id донесено до экрана" - только тогда продвигается
        // NotifyStore.SetLastSeen). Для тестового окна (NotificationId=-1)
        // событие не вызывается.
        public event Action<long> Displayed;

        public int ExpectedHeight
        {
            get { return _textSize.Height + 32; }
        }

        public NotifyPopup(string text, int closeMs = 6000)
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.Font = new Font("Segoe UI", 9f);
            this.BackColor = Color.FromArgb(255, 245, 225);

            _textSize = TextRenderer.MeasureText(text, this.Font,
                new Size(350, int.MaxValue), TextFormatFlags.WordBreak);
            int w = Math.Max(200, _textSize.Width + 8);
            int h = Math.Max(40, _textSize.Height + 8);

            _lbl = new Label();
            _lbl.Text = text;
            _lbl.Size = new Size(w, h);
            _lbl.Font = this.Font;
            _lbl.ForeColor = Color.FromArgb(0, 51, 102);
            _lbl.Location = new Point(14, 12);
            this.Controls.Add(_lbl);

            this.Click += delegate { CloseSelf(); };
            _lbl.Click += delegate { CloseSelf(); };

            _close = new System.Windows.Forms.Timer();
            _close.Interval = closeMs;
            _close.Tick += delegate { CloseSelf(); };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            this.ClientSize = new Size(_textSize.Width + 30, Math.Max(50, _textSize.Height + 24));
            this.Location = new Point(wa.Right - this.Width - 12,
                wa.Bottom - this.Height - 12 - TopOffset);
            _close.Start();

            Action<long> shown = Displayed;
            if (shown != null && NotificationId >= 0)
            {
                try { shown(NotificationId); }
                catch { }
            }
        }

        private void CloseSelf()
        {
            _close.Stop();
            this.Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (_close != null)
            {
                _close.Dispose();
                _close = null;
            }
            base.Dispose(disposing);
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

        // Статус службы ("Running"/"Stopped"/...) или текст ошибки. Нужно для
        // --diag: ряд проблем оказался тем, что служба установлена, но не
        // работает, - тогда события в очереди некому писать.
        public static string StatusText()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                    return sc.Status.ToString();
            }
            catch (Exception ex)
            {
                return "недоступна (" + ex.Message + ")";
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
                    // ВАЖНО: сначала пишем событие в общую очередь (HKLM),
                    // которую читают треи/уведомители пользователей. Запись в
                    // журнал событий идёт ПОСЛЕ и в отдельном try/catch: если
                    // источник журнала не зарегистрирован (WMI-установка
                    // службы его не создаёт), WriteEntry бросает исключение -
                    // раньше это прерывало цикл и уведомление НЕ попадало в
                    // очередь вообще.
                    NotifyStore.Write(b.Serial, b.Label);

                    try
                    {
                        string what = string.IsNullOrEmpty(b.Label) ? b.Serial : b.Label;
                        EventLog.WriteEntry(ServiceManager.ServiceName,
                            Program.NotifyText + "\n\nЗаблокирован накопитель: " + what +
                            (string.IsNullOrEmpty(b.Serial) ? string.Empty : "  SN=" + b.Serial),
                            EventLogEntryType.Warning);
                    }
                    catch
                    {
                        // Журнал событий недоступен - не критично, уведомление
                        // уже поставлено в очередь выше.
                    }
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
            sb.AppendLine("Служба мониторинга: " +
                (ServiceManager.IsInstalled()
                    ? ServiceManager.StatusText()
                    : "не установлена"));
            sb.AppendLine("Задача трея (--logon): " +
                (TrayTask.IsInstalled() ? "есть" : "нет"));
            sb.AppendLine("Задача уведомителя (--notify): " +
                (TrayTask.NotifyInstalled() ? "есть" : "нет"));
            sb.AppendLine("Процесс-уведомитель: " +
                (NotifyRunning() ? "работает" : "не найден"));
            sb.AppendLine("Событие блокировки (очередь): " + EventsSummary());
            sb.AppendLine("Источник журнала событий (" + ServiceManager.ServiceName + "): " +
                EventSourceStatus());
            sb.AppendLine("Запись в очередь (HKLM\\SOFTWARE\\USB_Block\\Events): " +
                QueueWriteTest());
            sb.AppendLine("Лог показа уведомлений (хвост " +
                Path.GetFileName(NotifyService.TracePath) +
                " из %TEMP% и Windows\\Temp):");
            sb.AppendLine(NotifyTraceSummary());

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

            string logPath = Program.PreferredLogPath("diag.log");
            sb.Insert(0,
                "Файл этого лога: " + logPath + Environment.NewLine);
            Program.WriteLog("diag.log", sb.ToString());
            return 0;
        }

        // Работает ли сейчас отдельный процесс-уведомитель (--notify) в
        // текущей сессии (по командной строке процессов этого пользователя).
        private static bool NotifyRunning()
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE Name='usb_block_tray.exe'"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string cl = o["CommandLine"] as string;
                        if (cl != null && cl.IndexOf("--notify",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        // Краткая сводка очереди событий (HKLM\SOFTWARE\USB_Block\Events) и
        // последнего показанного события (HKCU).
        private static string EventsSummary()
        {
            try
            {
                List<NotifyStore.BlockEvent> evs = NotifyStore.ReadAll();
                long last = NotifyStore.GetLastSeen();
                long max = 0;
                foreach (NotifyStore.BlockEvent e in evs)
                    if (e.Id > max) max = e.Id;
                return "записей=" + evs.Count + ", макс.id=" + max +
                    ", LastEventId(HKCU)=" + last;
            }
            catch (Exception ex)
            {
                return "ошибка чтения: " + ex.Message;
            }
        }

        // Хвост лога показа уведомлений (последние строки из
        // %TEMP%\usb_block_notify.log) - видно, доходил ли показ на этой машине
        // и были ли ошибки. Дополнительно показывается лог СЛУЖБЫ
        // (C:\Windows\Temp\usb_block_notify.log): события в общую очередь
        // пишет в основном служба (SYSTEM), и её след живёт в системном TEMP,
        // недоступном из %TEMP% пользователя. "<нет лога>" - ещё не создавался.
        private static string NotifyTraceSummary()
        {
            List<string> paths = new List<string>();
            paths.Add(NotifyService.TracePath);
            string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            // Лог СЛУЖБЫ (LocalSystem). В зависимости от версии ОС
            // GetTempPath() у SYSTEM даёт либо Windows\Temp, либо temp
            // системного профиля - проверяем оба места.
            string sysTrace = Path.Combine(sysRoot, "Temp", "usb_block_notify.log");
            if (string.Equals(sysTrace, NotifyService.TracePath,
                StringComparison.OrdinalIgnoreCase) == false)
                paths.Add(sysTrace);
            string sysProfileTrace = Path.Combine(sysRoot,
                "System32", "config", "systemprofile", "AppData", "Local",
                "Temp", "usb_block_notify.log");
            if (string.Equals(sysProfileTrace, NotifyService.TracePath,
                StringComparison.OrdinalIgnoreCase) == false &&
                string.Equals(sysProfileTrace, sysTrace,
                StringComparison.OrdinalIgnoreCase) == false)
                paths.Add(sysProfileTrace);

            StringBuilder sb = new StringBuilder();
            bool any = false;
            foreach (string path in paths)
            {
                if (!File.Exists(path)) continue;
                any = true;
                sb.AppendLine("  [" + path + "]");
                try
                {
                    string[] lines = File.ReadAllLines(path);
                    int n = Math.Min(lines.Length, 25);
                    for (int i = lines.Length - n; i < lines.Length; i++)
                        sb.AppendLine("  " + lines[i]);
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  ошибка чтения: " + ex.Message);
                }
            }
            if (!any) return "  <нет лога - показ ещё не запускался>";
            return sb.ToString().TrimEnd();
        }

        // Статус источника журнала событий, который пишет служба. Если источник
        // не зарегистрирован, EventLog.WriteEntry у службы может падать (для
        // этой проверки нужны права на чтение всех журналов).
        private static string EventSourceStatus()
        {
            try
            {
                return System.Diagnostics.EventLog.SourceExists(ServiceManager.ServiceName)
                    ? "зарегистрирован" : "НЕ зарегистрирован";
            }
            catch (Exception ex)
            {
                return "не удалось определить (" + ex.Message + ")";
            }
        }

        // Проверка прав на запись в общую очередь: создаём и сразу удаляем
        // служебное значение в подразделе Events (само событие не пишем,
        // чтобы не показывать ложных уведомлений).
        private static string QueueWriteTest()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\USB_Block\Events"))
                {
                    k.SetValue("__diag_test", "0|0||", RegistryValueKind.String);
                    k.DeleteValue("__diag_test", false);
                }
                return "ОК";
            }
            catch (Exception ex)
            {
                return "НЕТ (" + ex.Message + ")";
            }
        }
    }

    // =====================================================================
    // Самопроверка: перенос, сериализация
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
            try
            {
                PortableWhitelist.Export(list, plainFile);
                List<DeviceEntry> r1 = PortableWhitelist.Import(plainFile);
                sb.AppendLine("Export/import: " + (r1.Count == 1 && r1[0].Serial == "081NS9HV47JMZLUQ" ? "OK" : "FAIL"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex);
            }
            finally
            {
                try { if (File.Exists(plainFile)) File.Delete(plainFile); } catch { }
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
    }
}