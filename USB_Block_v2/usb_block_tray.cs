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

// Версия программы. Та же цифра стоит и в app.manifest (assemblyIdentity):
// при каждой сборке обновляются ОБЕ, иначе в свойствах файла и в манифесте
// разойдутся. Проверка - в самопроверке ("Build version").
[assembly: AssemblyVersion("2.0.4.0")]
[assembly: AssemblyFileVersion("2.0.4.0")]

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

        public static readonly string Title = Loc.T("USB-блокування");

        // Сообщение при блокировке постороннего накопителя (обязательный текст)
        public static readonly string NotifyText =
            Loc.T("ПРИСТРІЙ ЗАБЛОКОВАНО, ЗВЕРНІТЬСЯ ДО АДМІНІСТРАТОРА СЗІ");

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
            bool journal = false;
            bool ensureService = false;

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
                if (string.Equals(a, "--journal", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-journal", StringComparison.OrdinalIgnoreCase))
                    journal = true;
                if (string.Equals(a, "--ensure-service", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-ensure-service", StringComparison.OrdinalIgnoreCase))
                    ensureService = true;
            }

            // КОНТРОЛЬ СЛУЖБЫ (--ensure-service): запускается задачей
            // планировщика от SYSTEM раз в минуту. Если службу остановили
            // или отключили, поднимает её обратно. Без UI.
            if (ensureService)
            {
                ServiceManager.EnsureRunning();
                return 0;
            }

            // ПРОСМОТР ЖУРНАЛА (--journal): только окно, без трея, без
            // мьютекса и без задач - процесс живёт ровно пока открыто окно.
            // Журнал зашифрован и закрыт ACL, поэтому читает его только
            // администратор; от обычного пользователя права поднимаются
            // здесь и запускается второй такой же процесс.
            if (journal)
            {
                if (!IsAdministrator())
                {
                    if (!StartJournalViewer(true))
                    {
                        MessageBox.Show(Loc.T("Журнал закрито для звичайного користувача.\n") +
                            Loc.T("Не вдалося отримати права адміністратора."),
                            Title, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    using (JournalViewForm view = new JournalViewForm())
                    {
                        view.ShowDialog();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Loc.T("Не вдалося відкрити журнал: ") + ex.Message,
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
                return 0;
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
                    MessageBox.Show(Loc.T("Не вдалося отримати права адміністратора.\n") + ex.Message,
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

// Основная комбинация клавиш журнала теперь всегда
                // Ctrl+Alt+O: старое значение, сохранённое прежней версией,
                // заменяется (один раз).
                HotkeyStore.EnsureDefault();

                // Права на файлы журнала и на состояние наблюдения: без них
                // журнал копирования при входе обычного пользователя писать
                // некому. Раздаются здесь и повторяются при каждом цикле
                // (UsbJournalRights.EnsureAllOnce в RunOnce) - файлы могли
                // быть созданы позже.
                UsbJournalRights.EnsureAllOnce();

                // Обновление "на лету": если служба уже установлена (в т.ч.
                // версией БЕЗ защиты), включаем её защиту и обновляем задачи
                // Планировщика. Так после обновления exe защита включается при
                // первом же запуске администратором - без ручной переустановки
                // службы (кнопка "7" на установленной службе ничего не делала).
                if (ServiceManager.IsInstalled())
                {
                    ServiceManager.ApplyRecovery();
                    TrayTask.Create();
                }
            }

            bool created;
            using (Mutex mtx = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    MessageBox.Show(Loc.T("Програму вже запущено (значок у треї)."),
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

        // Запуск отдельного процесса только с окном журнала (--journal).
        // elevate=true - с запросом прав администратора.
        public static bool StartJournalViewer(bool elevate)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    Application.ExecutablePath, "--journal");
                psi.UseShellExecute = true;
                if (elevate) psi.Verb = "runas";
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
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

    // =====================================================================
    // Правила whitelist: один накопитель - одна запись.
    // Записи об ОДНОМ И ТОМ ЖЕ накопителе считаются дубликатами и
    // удаляются при добавлении, импорте и сохранении.
    // Признаки устройства проверяются по очереди:
    //   1) серийный номер (главный признак; сравнение без учёта регистра,
    //      хвост "&0"/"&1f" от USBSTOR отбрасывается);
    //   2) если серийник у одной из записей неизвестен - HardwareID диска;
    //   3) если нет ни серийника, ни HardwareID - модель USB (VID&PID).
    // Запись БЕЗ серийника разрешает всю модель USB (так работает проверка
    // IsVolumeAllowed/IsDiskAllowed), поэтому вторая флешка той же модели
    // с неизвестным серийником - дубликат: добавлять её незачем.
    // =====================================================================
    public static class WhitelistRules
    {
        public static string Norm(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Trim().ToUpperInvariant();
        }

        // Серийный номер в сравнимом виде: без хвоста "&<hex>" от USBSTOR,
        // без пробелов, в верхнем регистре (081ns9hv47jmlzuq&0 = 081NS9HV47JMZLUQ).
        public static string NormSerial(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return string.Empty;
            string s = serial.Trim();
            int amp = s.LastIndexOf('&');
            int tailLen = s.Length - amp - 1;
            if (amp > 0 && tailLen >= 1 && tailLen <= 2)
            {
                bool hex = true;
                for (int i = amp + 1; i < s.Length; i++)
                {
                    if (!Uri.IsHexDigit(s[i])) { hex = false; break; }
                }
                if (hex) s = s.Substring(0, amp);
            }
            return Norm(s);
        }

        // Ключ записи: первый непустой признак (серийник -> диск -> модель -> имя)
        public static string EntryKey(DeviceEntry e)
        {
            if (e == null) return string.Empty;
            string s = NormSerial(e.Serial);
            if (s.Length > 0) return "S:" + s;
            string d = Norm(e.DiskId);
            if (d.Length > 0) return "D:" + d;
            string u = Norm(e.UsbId);
            if (u.Length > 0) return "U:" + u;
            return "N:" + Norm(e.Name);
        }

        // Один и тот же накопитель?
        public static bool SameDevice(DeviceEntry a, DeviceEntry b)
        {
            if (a == null || b == null) return false;

            string sa = NormSerial(a.Serial);
            string sb = NormSerial(b.Serial);
            if (sa.Length > 0 && sb.Length > 0) return sa == sb;

            string da = Norm(a.DiskId);
            string db = Norm(b.DiskId);
            if (da.Length > 0 && db.Length > 0) return da == db;

            string ua = Norm(a.UsbId);
            string ub = Norm(b.UsbId);
            if (ua.Length > 0 && ub.Length > 0) return ua == ub;

            return false;
        }

        // Индекс первой записи о том же накопителе, иначе -1
        public static int Find(List<DeviceEntry> list, DeviceEntry e)
        {
            if (list == null || e == null) return -1;
            for (int i = 0; i < list.Count; i++)
                if (SameDevice(list[i], e)) return i;
            return -1;
        }

        // Чистка списка от дубликатов: остаётся ПЕРВАЯ запись о накопителе,
        // ей дописывается имя, если своё пустое. USB ID/серийник/HardwareID
        // не меняются - иначе изменилось бы то, чем устройство опознаётся.
        // removed - сколько записей-дубликатов отброшено.
        public static List<DeviceEntry> Dedupe(List<DeviceEntry> list, out int removed)
        {
            removed = 0;
            List<DeviceEntry> result = new List<DeviceEntry>();
            if (list == null) return result;

            foreach (DeviceEntry e in list)
            {
                if (e == null) { removed++; continue; }
                int idx = Find(result, e);
                if (idx >= 0)
                {
                    removed++;
                    if (string.IsNullOrEmpty(result[idx].Name) && !string.IsNullOrEmpty(e.Name))
                        result[idx].Name = e.Name;
                    continue;
                }
                result.Add(e);
            }
            return result;
        }

        // Объединение двух списков без дубликатов (в т.ч. внутри каждого
        // списка). Записи existing имеют приоритет: incoming с тем же
        // накопителем отбрасывается, порядок записей сохраняется.
        public static List<DeviceEntry> Merge(List<DeviceEntry> existing, List<DeviceEntry> incoming)
        {
            List<DeviceEntry> result = new List<DeviceEntry>();
            int removed;

            if (existing != null)
            {
                List<DeviceEntry> keep = Dedupe(existing, out removed);
                foreach (DeviceEntry e in keep) result.Add(e);
            }
            if (incoming != null)
            {
                List<DeviceEntry> add = Dedupe(incoming, out removed);
                foreach (DeviceEntry e in add)
                    if (Find(result, e) < 0)
                        result.Add(e);
            }
            return result;
        }
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

        // ---- Журнал подключений и копирований ----
        // Лежит РЯДОМ с whitelist.dat (та же папка ProgramData\USB_Block) и
        // так же закрыт от обычного пользователя: содержимое зашифровано
        // (DPAPI LocalMachine, UsbJournal), файл закрыт ACL. В файле журнала
        // видны только имена накопителей и пути скопированных файлов, но не
        // содержимое самих файлов.
        //
        // Журналов ДВА, у каждого своё кольцо поколений (см. UsbJournal):
        //   - journal.dat: подключения и служебные строки;
        //   - journal_files.dat: записи о копировании.
        // Разделение нужно, чтобы копирование тысяч файлов не вытесняло
        // записи о подключении накопителя из кольца.
        public static string JournalFile
        {
            get { return Path.Combine(Directory, "journal.dat"); }
        }

        public static string JournalFilesFile
        {
            get { return Path.Combine(Directory, "journal_files.dat"); }
        }

        // Текущее поколение журнала (текущий файл) и предыдущие:
        // journal.dat (текущий), journal_1.dat ... journal_9.dat (старые);
        // у журнала копирования та же схема с именем journal_files*.
        // При переполнении текущего файла поколения сдвигаются, самый старый
        // удаляется - каждый журнал живёт кольцом, размер ограничен.
        public static int JournalGenerations = 9;
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
    // Права на файлы журнала и на состояние наблюдения.
    //
    // Зачем это нужно: журнал подключений ведёт служба, а журнал копирования
    // включает пользователь галочкой J - и если он вошёл не администратором,
    // писать в журнал некому: трей при входе обычного пользователя правами не
    // повышен (иначе UAC-вопрос при каждом входе), а папка ProgramData и
    // файлы закрыты ACL от записи.
    //
    // Что разрешено обычному пользователю:
    //   - journal.dat, journal_1..9, journal_files.dat, journal_files_1..9:
    //     ПРОЧИТАТЬ и ДОПИСЫВАТЬ В КОНЕЦ (оба журнала - одинаково).
    //     Дописывание идёт дескриптором с правом FILE_APPEND_DATA, поэтому
    //     изменить или стереть уже записанное нельзя: для этого нужно
    //     удалить файл, а это право осталось у администратора;
    //   - whitelist.dat: ЧИТАТЬ. Без этого обычный пользователь увидит
    //     пустой список разрешённых накопителей, и журнал начал бы писать
    //     ложные «удалено» для всего, что раньше было разрешено;
    //   - HKLM\SOFTWARE\USB_Block\JournalPresence: ЗАПИСЫВАТЬ значения
    //     (состояние «что сейчас подключено» и метка последнего цикла).
    //     Без этого каждый цикл считался бы «после долгого перерыва» и
    //     молчал бы вместо записей.
    //
    // Чего обычному пользователю по-прежнему НЕльзя: создавать новые файлы
    // в папке, удалять и переименовывать файлы журнала, менять права, писать
    // whitelist.dat, трогать остальные настройки. Поэтому ротацию кольца
    // (сдвиг поколений) по-прежнему выполняет только администратор - служба
    // или трей, запущенный с правами.
    // =====================================================================
    public static class UsbJournalRights
    {
        private static readonly SecurityIdentifier AuthenticatedUsers =
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        public static string LastError;

        // ---- Файлы журнала ----

        public static void EnsureFileRights(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
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
                // Читать (нужно, чтобы пересчитать записи и понять, что файл
                // полон) и дописывать в конец.
                fs.AddAccessRule(new FileSystemAccessRule(AuthenticatedUsers,
                    FileSystemRights.Read | FileSystemRights.AppendData,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                File.SetAccessControl(path, fs);
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = path + ": " + ex.Message;
            }
        }

        // Проходит по всем поколениям журнала и чинит права (у файлов,
        // созданных прошлой версией, обычного пользователя не было).
        // Обязаны пройти оба журнала: и подключения, и копирование.
        public static void EnsureAllFileRights()
        {
            for (int stream = 0; stream <= UsbJournal.StreamFiles; stream++)
            {
                for (int g = 0; g <= StorePaths.JournalGenerations; g++)
                {
                    try { EnsureFileRights(UsbJournal.GenerationFileFor(stream, g)); }
                    catch { }
                }
            }
        }

        // Разрешено ли текущему процессу дописывать в журнал (для --diag и
        // для понятного сообщения в трее). Проверяются оба журнала: если
        // есть хоть один существующий файл без права дописывания - не
        // можем (часть журнала молчала бы).
        public static bool CanWriteNow()
        {
            bool anyExists = false;
            for (int stream = 0; stream <= UsbJournal.StreamFiles; stream++)
            {
                string path = UsbJournal.GenerationFileFor(stream, 0);
                try
                {
                    if (!File.Exists(path)) continue;
                    anyExists = true;
                    FileSecurity fs = File.GetAccessControl(path);
                    bool can = false;
                    foreach (FileSystemAccessRule have in
                        fs.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                    {
                        if (have.IdentityReference == AuthenticatedUsers &&
                            (have.FileSystemRights & FileSystemRights.AppendData) != 0)
                        {
                            can = true;
                            break;
                        }
                    }
                    if (!can) return false;
                }
                catch
                {
                    return false;
                }
            }
            // Файлов ещё нет - дописывать пока некуда: это делает только
            // администратор при первом запуске.
            return anyExists || Program.IsAdministrator();
        }

        // ---- Состояние наблюдения в реестре ----

        public static void EnsurePresenceRights()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\USB_Block\JournalPresence"))
                {
                    RegistrySecurity rs = new RegistrySecurity();
                    rs.SetAccessRuleProtection(true, false);
                    rs.AddAccessRule(new RegistryAccessRule(
                        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                        RegistryRights.FullControl, InheritanceFlags.None,
                        PropagationFlags.None, AccessControlType.Allow));
                    rs.AddAccessRule(new RegistryAccessRule(
                        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                        RegistryRights.FullControl, InheritanceFlags.None,
                        PropagationFlags.None, AccessControlType.Allow));
                    // Читать значения может любой (это и так свойство
                    // HKLM), а записывать должен и обычный пользователь.
                    // CreateSubKey тоже нужен: код открывает ключ через
                    // Registry.CreateSubKey, а на существующем ключе это
                    // требует KEY_CREATE_SUB_KEY - без него обычный
                    // пользователь не смог бы записать метку последнего
                    // цикла и молчал бы вместо записей.
                    rs.AddAccessRule(new RegistryAccessRule(AuthenticatedUsers,
                        RegistryRights.SetValue | RegistryRights.ReadKey |
                        RegistryRights.CreateSubKey,
                        InheritanceFlags.None, PropagationFlags.None,
                        AccessControlType.Allow));
                    byte[] sd = rs.GetSecurityDescriptorBinaryForm();
                    IntPtr handle = k.Handle.DangerousGetHandle();
                    if (RegSetKeySecurity(handle, DACL_SECURITY_INFORMATION, sd) != 0)
                    {
                        LastError = "RegSetKeySecurity: " +
                            Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        LastError = null;
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        private const int DACL_SECURITY_INFORMATION = 4;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegSetKeySecurity(IntPtr hKey, int securityInformation,
            byte[] securityDescriptor);

        // ---- Прочитать whitelist ----

        public static void EnsureWhitelistRead()
        {
            try
            {
                string path = StorePaths.File;
                if (!File.Exists(path)) return;
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
                fs.AddAccessRule(new FileSystemAccessRule(AuthenticatedUsers,
                    FileSystemRights.Read, InheritanceFlags.None, PropagationFlags.None,
                    AccessControlType.Allow));
                File.SetAccessControl(path, fs);
            }
            catch (Exception ex)
            {
                LastError = StorePaths.File + ": " + ex.Message;
            }
        }

        // Всё сразу. Вызывает администратор (служба, трей с правами,
        // установка службы): одного раза на процесс достаточно.
        private static bool _done;
        public static void EnsureAllOnce()
        {
            if (_done) return;
            _done = true;
            EnsureAllFileRights();
            EnsurePresenceRights();
            EnsureWhitelistRead();
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
                    throw new InvalidDataException(Loc.T("невідома версія whitelist"));
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
                    throw new InvalidDataException(Loc.T("невірна сигнатура файлу whitelist"));
            }

            byte[] payload = new byte[plain.Length - Magic.Length];
            Array.Copy(plain, Magic.Length, payload, 0, payload.Length);
            return DeserializePayload(payload);
        }

        public static void Save(List<DeviceEntry> list)
        {
            // Дубликаты в файл не попадают ни при каком сценарии: даже
            // если список содержал их до очистки (старая версия, импорт
            // без дедупликации) - они отбрасываются при записи.
            int removed;
            List<DeviceEntry> clean = WhitelistRules.Dedupe(list, out removed);
            byte[] payload = SerializePayload(clean);

            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(Magic, 0, Magic.Length);
                ms.Write(payload, 0, payload.Length);
                byte[] plain = ms.ToArray();
                byte[] secret = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);

                Directory.CreateDirectory(StorePaths.Directory);
                File.WriteAllBytes(StorePaths.File, secret);
                // Сохраняем право на чтение обычному пользователю: без него
                // он не прочитает список разрешённых устройств, и журнал
                // копирования при входе не-админом посчитал бы чужие
                // накопители удалёнными (см. UsbJournalRights).
                UsbJournalRights.EnsureWhitelistRead();
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

        internal static void RestrictAcl(string path)
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
    // Пароль защиты опасных пунктов меню (2, 3, 4, 6, 7, 8, 9).
    // Устанавливается при пункте 7 «Установить службу мониторинга»: сначала
    // вопрос «установить пароль?», при «Да» - ввод пароля с подтверждением.
    // После этого пароль запрашивается перед пунктами 2, 3, 4, 6, 8, 9.
    // Требования к паролю: не короче MinLength символов, обязательно есть
    // строчная и ПРОПИСНАЯ латинская буква и цифра (MatchesPolicy).
    // Сам пароль НЕ хранится: в файле только PBKDF2-хэш (HMAC-SHA256,
    // 20000 итераций, случайная соль 16 байт). Файл закрыт DPAPI
    // LocalMachine и ACL - читается только администратором/SYSTEM, как
    // whitelist.dat. Забытый пароль восстановить нельзя - его можно только
    // УДАЛИТЬ (пункт меню «Удалить пароль»): либо введя текущий пароль,
    // либо кодовое слово RecoveryCode.
    // =====================================================================
    public static class AdminPassword
    {
        private const int Iterations = 20000;
        private const int SaltLen = 16;
        private const int HashLen = 32;
        public const int MinLength = 6;

        // Кодовое слово для удаления пароля, если текущий пароль неизвестен
        // или забыт. Сравнение - за постоянное время, регистр не важен.
        private const string RecoveryCode = "odmin";

        private static readonly byte[] Magic =
            { (byte)'U', (byte)'S', (byte)'B', (byte)'W', (byte)'P', 1 };

        private static readonly byte[] Entropy =
        {
            0x55, 0x53, 0x42, 0x5F, 0x41, 0x44, 0x4D, 0x49, 0x4E,
            0x50, 0x57, 0x44, 0x00, 0x7C, 0x54, 0x91
        };

        public static string FilePath
        {
            get { return Path.Combine(StorePaths.Directory, "adminpass.dat"); }
        }

        public static bool IsSet()
        {
            try { return File.Exists(FilePath); }
            catch { return false; }
        }

        // Требования к паролю: длина, строчная + ПРОПИСНАЯ латинские буквы
        // и цифра. Всё, что сверх этого (пробелы, знаки, кириллица),
        // допускается, но не засчитывается как выполнение требований.
        public static bool MatchesPolicy(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < MinLength)
                return false;
            bool lower = false, upper = false, digit = false;
            for (int i = 0; i < password.Length; i++)
            {
                char c = password[i];
                if (c >= 'a' && c <= 'z') lower = true;
                else if (c >= 'A' && c <= 'Z') upper = true;
                else if (c >= '0' && c <= '9') digit = true;
            }
            return lower && upper && digit;
        }

        public static string PolicyHint()
        {
            return Loc.T("Пароль має бути не коротшим за ") +
                MinLength.ToString(CultureInfo.InvariantCulture) +
                Loc.T(" символів і містити малі та ВЕЛИКІ латинські літери та цифри.");
        }

        // Кодовое слово - второй способ удалить пароль (в --selftest и при
        // удалении пароля из меню). Регистр и края пробелов не важны:
        // забытый пароль нельзя восстановить из-за случайно нажатого Caps Lock.
        public static bool CheckCode(string code)
        {
            string a = (code ?? string.Empty).Trim().ToUpperInvariant();
            string b = RecoveryCode.ToUpperInvariant();
            return FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
        }

        // Запись пароля в виде байтов: сигнатура + число итераций + соль +
        // хэш. Ничего не пишет на диск - используется и при установке
        // пароля, и в --selftest.
        internal static byte[] PackRecord(string password, out byte[] salt)
        {
            salt = new byte[SaltLen];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);
            byte[] hash = Derive(password, salt, Iterations);
            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(Magic, 0, Magic.Length);
                ms.Write(BitConverter.GetBytes(Iterations), 0, 4);
                ms.Write(salt, 0, salt.Length);
                ms.Write(hash, 0, hash.Length);
                return ms.ToArray();
            }
        }

        // Проверка пароля по распакованной записи (без DPAPI и без файла).
        internal static bool CheckPacked(string password, byte[] plain)
        {
            try
            {
                if (plain == null || plain.Length < Magic.Length + 4 + SaltLen + HashLen)
                    return false;
                for (int i = 0; i < Magic.Length; i++)
                {
                    if (plain[i] != Magic[i]) return false; // неверная сигнатура
                }
                int iterations = BitConverter.ToInt32(plain, Magic.Length);
                if (iterations < 1000) iterations = Iterations;
                int off = Magic.Length + 4;
                byte[] salt = new byte[SaltLen];
                byte[] hash = new byte[HashLen];
                Array.Copy(plain, off, salt, 0, SaltLen);
                Array.Copy(plain, off + SaltLen, hash, 0, HashLen);
                return FixedTimeEquals(Derive(password, salt, iterations), hash);
            }
            catch
            {
                return false;
            }
        }

        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            using (Rfc2898DeriveBytes pbkdf2 =
                new Rfc2898DeriveBytes(password ?? string.Empty, salt, iterations,
                    HashAlgorithmName.SHA256))
            {
                return pbkdf2.GetBytes(HashLen);
            }
        }

        // Сравнение за постоянное время - иначе по замеру можно было бы
        // подбирать хэш побайтно.
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static void Set(string password)
        {
            if (!MatchesPolicy(password))
                throw new ArgumentException(PolicyHint());

            byte[] salt;
            byte[] plain = PackRecord(password, out salt);
            byte[] secret = ProtectedData.Protect(plain, Entropy,
                DataProtectionScope.LocalMachine);

            Directory.CreateDirectory(StorePaths.Directory);
            File.WriteAllBytes(FilePath, secret);
            WhitelistStore.RestrictAcl(FilePath);
        }

        // Проверка введённого пароля. Если файл не читается (повреждён или
        // нет прав) - пароль НЕ пропускается: иначе его можно было бы обойти,
        // удалив или подменив файл.
        public static bool Verify(string password)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(FilePath);
                byte[] plain = ProtectedData.Unprotect(raw, Entropy,
                    DataProtectionScope.LocalMachine);
                return CheckPacked(password, plain);
            }
            catch
            {
                return false;
            }
        }

        // Удаление пароля (пункт меню «Удалить пароль»): файл стирается,
        // после чего пункты 2, 3, 4, 6, 8, 9 снова доступны без пароля.
        public static bool Clear()
        {
            try
            {
                if (!File.Exists(FilePath)) return true;
                File.Delete(FilePath);
                return !File.Exists(FilePath);
            }
            catch
            {
                return false;
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
            // В переносимый файл дубликаты не попадают (в т.ч. если
            // текущий whitelist содержал их до очистки).
            int removed;
            List<DeviceEntry> clean = WhitelistRules.Dedupe(list, out removed);
            byte[] payload = WhitelistStore.SerializePayload(clean);
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
            if (file.Length < 8) throw new InvalidDataException(Loc.T("файл замалий"));

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
                    Loc.T("файл захищено паролем - парольний захист видалено; ") +
                    Loc.T("експортуйте whitelist заново зі старого комп'ютера"));

            throw new InvalidDataException(Loc.T("невідомий формат файлу whitelist"));
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
        public string PartitionId;  // "Disk #2, Partition #0" (WMI)
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
                            Model = sd.Model,
                            PartitionId = partDevId
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
                        NotifyService.TraceLog(Loc.T("дедуплікація при записі: пропуск повторного ") +
                            Loc.T("сповіщення (той самий накопичувач, подія була ") +
                            (DateTime.Now - e.When).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) +
                            Loc.T(" с тому, вікно ") +
                            DedupWindow.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + Loc.T(" с)"));
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
                NotifyService.TraceLog(Loc.T("записано подію id=") + newId.ToString(CultureInfo.InvariantCulture) +
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

        // Текст строки блокировки для всплывающего уведомления и для журнала
        // событий Windows (одинаковый в обоих местах). Показываем ТОЛЬКО
        // модель устройства: в очереди label равен модели, а если модели нет -
        // туда подставлен UsbId (VID:PID) либо идентификатор устройства;
        // серийный номер и VID:PID в сообщение НЕ выводятся (этап 79).
        public static string BlockDetail(string label)
        {
            string model = label;
            if (!string.IsNullOrEmpty(model) &&
                (model.IndexOf("VID_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 model.IndexOf('\\') >= 0))
                model = null;
            return string.IsNullOrEmpty(model)
                ? Loc.T("Заблоковано пристрій")
                : Loc.T("Заблоковано пристрій: ") + model;
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
    // ЖУРНАЛ подключений накопителей и копируемых на них файлов.
    // Два НЕЗАВИСИМЫХ журнала в одной папке РЯДОМ с whitelist.dat
    // (C:\ProgramData\USB_Block), у каждого своё кольцо поколений:
    //   - journal.dat -> journal_1.dat ... journal_9.dat:
    //     записи о подключениях/отключениях (DEV+, DEV-, DEV=) и
    //     служебные строки (SYS);
    //   - journal_files.dat -> journal_files_1.dat ... journal_files_9.dat:
    //     записи о копировании (FILE+, FILE~, FILE-, FILE>).
    // Разведение сделано намеренно: копирование тысяч файлов за минуту
    // иначе вытесняло бы (и вытесняло) записи о подключении накопителя
    // из общего кольца - они пропадали из журнала. Теперь файловый поток
    // физически не может удалить запись о подключении, и наоборот.
    // Оба файла закрыты от обычного пользователя ДВОЯКО:
    //   1) каждая запись зашифрована (DPAPI LocalMachine, своя соль);
    //   2) на файл и на папку выставлен ACL (Администраторы + SYSTEM),
    //      тот же приём, что на whitelist.dat.
    // Содержимое скопированных файлов в журнале НЕ хранится - только
    // время, вид операции, путь, размер и сведения о накопителе.
    //
    // Формат файла:
    //   [8 байт "USBJRNL1"][ запись ][ запись ] ...
    //   запись = [uint32 длина][зашифрованный UTF-8 текст записи]
    // Записи только дописываются, поэтому файл можно открыть на Append и
    // не перешивать всё заново (важно: шифрование каждой записи стоит
    // дорого, перешивать весь журнал на каждом цикле нельзя).
    // Переполнение: MaxRecordsPerFile записей -> сдвиг поколений
    // (journal.dat -> journal_1.dat -> ... -> journal_9.dat), старый
    // удаляется; у файла копирования та же схема со своими именами.
    // Читается сначала текущий файл, потом поколения по убыванию
    // свежести. Итого 10 файлов по 5000 записей на каждый из двух
    // журналов. При показе оба журнала сшиваются по времени.
    // =====================================================================
    public static class UsbJournal
    {
        private const string Magic = "USBJRNL1";

        // Записей в текущем файле до ротации. Внутреннее поле - только чтобы
        // самопроверка могла проверить ротацию на маленьком значении.
        internal static int MaxRecordsPerFile = 5000;

        // Соль DPAPI. Как и у whitelist.dat, шифрование не мешает админу
        // прочитать журнал - защиту от обычного пользователя даёт ACL.
        private static readonly byte[] Entropy = new byte[]
        {
            0x55, 0x53, 0x42, 0x5F, 0x4A, 0x52, 0x4E, 0x4C,
            0x5F, 0x56, 0x31, 0x00, 0x2E, 0x13, 0x65, 0xA7
        };

        // Дописать в конец файла так, чтобы это мог сделать и обычный пользователь.
        //
        // Обычный FileStream(path, Append) просит у Windows права GENERIC_WRITE,
        // а это вместе с FILE_APPEND_DATA даёт ещё и FILE_WRITE_DATA - то есть
        // право ПЕРЕПИСАТЬ уже записанные записи. Для журнала, который
        // наблюдает за пользователем, это недопустимо: он и так может
        // дописывать, но не должен уметь стирать.
        //
        // Поэтому открываем файл через CreateFile с правами
        // FILE_APPEND_DATA (+ FILE_READ_ATTRIBUTES на чтение размера).
        // Такой дескриптор по определению пишет только в конец: указатель
        // перед каждой записью ставится Windows в конец файла, а начало
        // файла менять нельзя.
        private static void AppendBytes(string path, byte[] data, bool createIfMissing)
        {
            const uint FILE_APPEND_DATA = 0x0004;
            const uint FILE_READ_ATTRIBUTES = 0x0080;
            const uint FILE_SHARE_READ = 0x00000001;
            const uint FILE_SHARE_WRITE = 0x00000002;
            const uint FILE_SHARE_DELETE = 0x00000004;
            const uint OPEN_ALWAYS = 4;
            const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

            // Право на создание файла есть только у администратора (папка
            // закрыта). Обычный пользователь может только дописывать в
            // уже созданный файл.
            uint access = FILE_APPEND_DATA | FILE_READ_ATTRIBUTES;
            uint disposition = OPEN_ALWAYS;
            if (!createIfMissing && !File.Exists(path))
                throw new IOException(Loc.T("файл журналу ще не створено: ") + path);

            IntPtr h = CreateFileW(path, access,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, disposition, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (h == new IntPtr(-1))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException("CreateFile: " + err.ToString(CultureInfo.InvariantCulture) +
                    Loc.T(" (код ") + err.ToString(CultureInfo.InvariantCulture) + ")");
            }
            try
            {
                long len = 0;
                uint got = 0;
                if (!GetFileSizeEx(h, out len) || len == 0)
                {
                    // Пустой файл (только что созданный): пишем заголовок
                    // формата. Заголовок - единственное, что дописывается
                    // не в конец, и делается это только при создании, то есть
                    // всегда с правами администратора. Дальше - обычная
                    // запись: указатель после заголовка в конце файла, и
                    // кадр ложится сразу за ним.
                    byte[] magic = new UTF8Encoding(false).GetBytes(Magic);
                    if (!WriteFile(h, magic, (uint)magic.Length, out got, IntPtr.Zero))
                        throw new IOException("WriteFile: " +
                            Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                }
                if (data != null && data.Length > 0)
                {
                    if (!WriteFile(h, data, (uint)data.Length, out got, IntPtr.Zero) ||
                        got != data.Length)
                    {
                        throw new IOException("WriteFile: " +
                            Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string fileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileSizeEx(IntPtr hFile, out long lpFileSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nBytes,
            out uint lpBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // Запись журнала: время, вид операции и подробности.
        public const string KindDeviceAdded = "DEV+";   // накопитель подключён
        public const string KindDeviceRemoved = "DEV-"; // накопитель отключён
        public const string KindDeviceFound = "DEV=";  // найден при запуске журнала
        public const string KindFileAdded = "FILE+";   // файл появился (копирование)
        public const string KindFileChanged = "FILE~"; // файл изменён (дописан)
        public const string KindFileRemoved = "FILE-"; // файл удалён
        public const string KindFileRenamed = "FILE>"; // файл переименован/перенесён
        public const string KindNote = "SYS";         // служебная запись журнала

        // Последняя ошибка записи/чтения - для диагностики.
        public static string LastError;

        // Папка журнала. По умолчанию - ProgramData\USB_Block рядом с
        // whitelist.dat. Переопределяется ТОЛЬКО на время самопроверки
        // (--selftest), чтобы проверки писали во временную папку, а не в
        // настоящий журнал машины.
        private static string _dirOverride;
        private static bool _testMode;

        public static void UseDirectoryForTest(string dir)
        {
            _dirOverride = dir;
            _testMode = dir != null;
            ResetCountForTest();
        }

        private static string Dir
        {
            get { return _dirOverride ?? StorePaths.Directory; }
        }

        // Два журнала на одних механиках, но в разных файлах (см. заголовок
        // класса): 0 - подключения и служебные строки, 1 - копирование.
        internal const int StreamDevices = 0;
        internal const int StreamFiles = 1;
        private const int StreamCount = 2;
        private static readonly string[] FileStem = { "journal", "journal_files" };

        private static string CurrentFile()
        {
            return CurrentFile(StreamDevices);
        }

        private static string CurrentFile(int stream)
        {
            return Path.Combine(Dir, FileStem[stream] + ".dat");
        }

        // Текущий файл журнала подключений (с учётом переопределения папки
        // в --selftest).
        public static string CurrentPath
        {
            get { return CurrentFile(StreamDevices); }
        }

        // Текущий файла журнала копирования.
        public static string CurrentFilesPath
        {
            get { return CurrentFile(StreamFiles); }
        }

        private static string GenerationFile(int stream, int generation)
        {
            if (generation <= 0) return CurrentFile(stream);
            return Path.Combine(Dir, FileStem[stream] + "_" +
                generation.ToString(CultureInfo.InvariantCulture) + ".dat");
        }

        // Путь файла поколения снаружи класса: им пользуется UsbJournalRights,
        // который следит за правами на эти файлы (нужны оба журнала).
        public static string GenerationFileFor(int generation)
        {
            return GenerationFile(StreamDevices, generation);
        }

        public static string GenerationFileFor(int stream, int generation)
        {
            return GenerationFile(stream, generation);
        }

        // Сколько записей в текущих файлах (без расшифровки - только по
        // заголовкам длин, поэтому дёшево). Сумма по обоим журналам.
        public static int CurrentRecordCount()
        {
            return CurrentRecordCount(StreamDevices) + CurrentRecordCount(StreamFiles);
        }

        public static int CurrentRecordCount(int stream)
        {
            try
            {
                return CurrentCount(stream);
            }
            catch
            {
                return 0;
            }
        }

        public static int TotalRecordCount()
        {
            return TotalRecordCount(StreamDevices) + TotalRecordCount(StreamFiles);
        }

        public static int TotalRecordCount(int stream)
        {
            int total = 0;
            for (int g = 0; g <= StorePaths.JournalGenerations; g++)
            {
                try
                {
                    total += CountRecords(GenerationFile(stream, g));
                }
                catch
                {
                }
            }
            return total;
        }

        public static long TotalSizeBytes()
        {
            long total = 0;
            for (int stream = 0; stream < StreamCount; stream++)
                for (int g = 0; g <= StorePaths.JournalGenerations; g++)
                {
                    try
                    {
                        string f = GenerationFile(stream, g);
                        if (File.Exists(f)) total += new FileInfo(f).Length;
                    }
                    catch
                    {
                    }
                }
            return total;
        }

        // Дописать одну запись. kind - вид операции (см. константы выше),
        // detail - подробности без времени и вида (вид отделён табуляцией).
        // Куда писать - решает вид: файловые операции уходят в свой журнал.
        public static void Write(string kind, string detail)
        {
            if (string.IsNullOrEmpty(kind)) return;
            string when = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture);
            string text = when + "\t" + kind + "\t" + (detail ?? string.Empty);
            WriteTo(StreamOfKind(kind), text);
        }

        // Запись уже готовым текстом ("время\tвид\tподробности"): вид
        // разбирается из строки, потому что снаружи вызывается только
        // из записи о файле (там свой журнал).
        public static void WriteRaw(string text)
        {
            WriteTo(StreamOfKind(KindFromText(text)), text);
        }

        // Вид записи -> журнал: копирование не должно вытеснять записи
        // о подключениях, поэтому у них разные файлы и разные кольца.
        internal static int StreamOfKind(string kind)
        {
            return IsFileKind(kind) ? StreamFiles : StreamDevices;
        }

        internal static bool IsFileKind(string kind)
        {
            return kind == KindFileAdded ||
                   kind == KindFileChanged ||
                   kind == KindFileRemoved ||
                   kind == KindFileRenamed;
        }

        // Вид из текста записи: "yyyy-MM-dd HH:mm:ss\tВИД\tподробности".
        private static string KindFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int t1 = text.IndexOf('\t');
            if (t1 < 0) return string.Empty;
            int t2 = text.IndexOf('\t', t1 + 1);
            if (t2 < 0) return text.Substring(t1 + 1);
            return text.Substring(t1 + 1, t2 - t1 - 1);
        }

        private static void WriteTo(int stream, string text)
        {
            byte[] plain = new UTF8Encoding(false).GetBytes(text);
            byte[] cipher;
            try
            {
                cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
            }
            catch (Exception ex)
            {
                LastError = Loc.T("шифрування: ") + ex.Message;
                return;
            }

            try
            {
                Directory.CreateDirectory(Dir);
            }
            catch
            {
            }

            string path = CurrentFile(stream);
            try
            {
                // Ротация - до записи: она освобождает текущий файл, и новый
                // файл надо будет и создать, и закрыть ACL заново.
                // Не удалась - запись прекращаем (см. RotateIfFull): писать
                // сверх MaxRecordsPerFile нельзя.
                if (!RotateIfFull(stream)) return;

                // Заголовок формата пишется только при создании файла - на это
                // есть права у администратора. Обычный пользователь файл не
                // создаёт: он лишь дописывает записи в конец.
                bool missing = !File.Exists(path);
                if (missing && !_testMode && !Program.IsAdministrator())
                {
                    LastError = Loc.T("немає файлу журналу - його створить перший запуск ") +
                        Loc.T("з правами адміністратора (пункт J або встановлення служби)");
                    return;
                }
                AppendBytes(path, BuildFrame(cipher), missing);
                _count[stream]++;
                _countLoaded[stream] = true;
                // Файл создан - сразу задаём права: администраторам полный
                // доступ, обычному пользователю - чтение и дописывание в
                // конец (переписывать записи он не может). В самопроверке
                // папка временная и принадлежит текущему пользователю:
                // закрывать её нельзя, иначе проверка чтения сама себе
                // мешала бы.
                if (missing && !_testMode) UsbJournalRights.EnsureFileRights(path);
            }
            catch (Exception ex)
            {
                LastError = Loc.T("запис: ") + ex.Message;
            }
        }

        // Запись в файле журнала: 4 байта длины + шифротекст.
        private static byte[] BuildFrame(byte[] cipher)
        {
            byte[] len = BitConverter.GetBytes(cipher.Length);
            byte[] frame = new byte[len.Length + cipher.Length];
            Buffer.BlockCopy(len, 0, frame, 0, len.Length);
            Buffer.BlockCopy(cipher, 0, frame, len.Length, cipher.Length);
            return frame;
        }

        // Самопроверка дописывания: пишем в заданный файл ровно так же,
        // как в журнал (FILE_APPEND_DATA), минуя шифрование и счётчик.
        public static bool TryAppendForTest(string path, byte[] data)
        {
            try
            {
                AppendBytes(path, data, !File.Exists(path));
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Сколько записей уже в текущем файле. Держим счётчик в памяти:
        // пересчитывать файл на КАЖДУЮ запись нельзя - при массовом
        // копировании это даёт квадратичную работу на тысячах файлов.
        private static readonly int[] _count = new int[StreamCount];
        private static readonly bool[] _countLoaded = new bool[StreamCount];

        private static int CurrentCount(int stream)
        {
            if (!_countLoaded[stream])
            {
                _count[stream] = CountRecords(CurrentFile(stream));
                _countLoaded[stream] = true;
            }
            return _count[stream];
        }

        // Сброс счётчиков при смене папки (самопроверка) - иначе счётчик от
        // прежнего журнала мешал бы определить момент ротации.
        public static void ResetCountForTest()
        {
            for (int stream = 0; stream < StreamCount; stream++)
            {
                _count[stream] = 0;
                _countLoaded[stream] = false;
            }
        }

        // Текущий файл полон - сдвигаем поколения. Удаляем самый старый,
        // затем переименовываем остальные на шаг назад и освобождаем
        // текущий файл под новые записи. Ротация своя у каждого журнала:
        // файлы копирования не двигают поколения записей о подключениях.
        // true, если ротация не понадобилась или удалась.
        private static bool RotateIfFull(int stream)
        {
            string path = CurrentFile(stream);
            if (CurrentCount(stream) < MaxRecordsPerFile) return true;
            if (!Program.IsAdministrator() && !_testMode)
            {
                // Сдвинуть поколения может только администратор: удалять и
                // переименовывать файлы в закрытой папке обычный пользователь
                // не может. Молча писать сверх 5000 записей нельзя - файл
                // поколения рассчитан на это число, и просмотрщик принял бы
                // его за повреждённый. Поэтому останавливаем запись с явной
                // причиной: до появления администратора журнал не пишется.
                LastError = Loc.T("файл журналу заповнений (") +
                    MaxRecordsPerFile.ToString(CultureInfo.InvariantCulture) +
                    Loc.T(" записів), а зсувати покоління може лише адміністратор - ") +
                    Loc.T("запис призупинено (запустіть програму з його правами ") +
                    Loc.T("або встановіть службу моніторингу)");
                _countLoaded[stream] = false;
                return false;
            }
            try
            {
                int last = StorePaths.JournalGenerations;
                try { if (File.Exists(GenerationFile(stream, last))) File.Delete(GenerationFile(stream, last)); }
                catch { }
                for (int g = last - 1; g >= 1; g--)
                {
                    string from = GenerationFile(stream, g);
                    if (!File.Exists(from)) continue;
                    string to = GenerationFile(stream, g + 1);
                    try { File.Delete(to); } catch { }
                    File.Move(from, to);
                }
                string cur = GenerationFile(stream, 1);
                try { if (File.Exists(cur)) File.Delete(cur); } catch { }
                File.Move(path, cur);
                _count[stream] = 0;
                _countLoaded[stream] = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = Loc.T("ротація: ") + ex.Message;
                // Счётчик не доверяем: сдвиг мог не дойти до конца.
                _countLoaded[stream] = false;
                return false;
            }
        }

        private static int CountRecords(string path)
        {
            if (!File.Exists(path)) return 0;
            int count = 0;
            using (FileStream fs = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] magic = new byte[Magic.Length];
                if (fs.Read(magic, 0, magic.Length) != magic.Length) return 0;
                for (int i = 0; i < magic.Length; i++)
                    if (magic[i] != (byte)Magic[i]) return 0;
                byte[] len = new byte[4];
                while (true)
                {
                    if (fs.Read(len, 0, 4) != 4) break;
                    int n = unchecked((int)BitConverter.ToUInt32(len, 0));
                    if (n <= 0 || n > fs.Length - fs.Position) break;
                    fs.Position += n;
                    count++;
                }
            }
            return count;
        }

        // ---- Чтение ----

        // Все записи журнала, свежие сверху. Читаются оба журнала и
        // сшиваются по времени: показ должен быть общим (в окне просмотра
        // записи всё равно разбираются по вкладкам), но читать приходится
        // два независимых файла. limit ограничивает выдачу, чтобы окно
        // просмотра не тянуло в память весь архив.
        public static List<string> ReadRecent(int limit)
        {
            return MergeNewest(ReadStream(StreamDevices, limit),
                ReadStream(StreamFiles, limit), limit);
        }

        private static List<string> ReadStream(int stream, int limit)
        {
            List<string> lines = new List<string>();
            for (int g = 0; g <= StorePaths.JournalGenerations; g++)
            {
                if (limit > 0 && lines.Count >= limit) break;
                string path = GenerationFile(stream, g);
                if (!File.Exists(path)) continue;
                List<string> part;
                try
                {
                    part = ReadFile(path, limit > 0 ? limit - lines.Count : 0);
                }
                catch
                {
                    continue;
                }
                // Внутри файла записи идут от старых к новым, а показывать
                // надо свежими сверху - разворачиваем.
                for (int i = part.Count - 1; i >= 0; i--)
                {
                    lines.Add(part[i]);
                    if (limit > 0 && lines.Count >= limit) break;
                }
            }
            return lines;
        }

        // Сшивка двух списков, уже отсортированных свежими сверху, в один.
        // Совпавшая секунда (запись о подключении и запись о файле из
        // одного цикла) разрешается в пользу журнала копирования: в этот
        // же момент там самая свежая активность. Ограничивается limit.
        private static List<string> MergeNewest(List<string> devices,
            List<string> files, int limit)
        {
            if (files == null || files.Count == 0) return devices ?? new List<string>();
            if (devices == null || devices.Count == 0) return files;
            List<string> merged = new List<string>(devices.Count + files.Count);
            int i = 0, j = 0;
            while (i < devices.Count && j < files.Count)
            {
                if (string.Compare(TimeKey(files[j]), TimeKey(devices[i]),
                    StringComparison.Ordinal) >= 0)
                {
                    merged.Add(files[j++]);
                }
                else
                {
                    merged.Add(devices[i++]);
                }
                if (limit > 0 && merged.Count >= limit) return merged;
            }
            while (i < devices.Count)
            {
                merged.Add(devices[i++]);
                if (limit > 0 && merged.Count >= limit) break;
            }
            while (j < files.Count)
            {
                merged.Add(files[j++]);
                if (limit > 0 && merged.Count >= limit) break;
            }
            return merged;
        }

        // Время записи - первые 19 символов строки ("yyyy-MM-dd HH:mm:ss"):
        // в фиксированном формате оно сравнивается как строка так же, как
        // по времени, и DateTime на каждую запись не нужен.
        private static string TimeKey(string line)
        {
            if (string.IsNullOrEmpty(line)) return string.Empty;
            return line.Length > 19 ? line.Substring(0, 19) : line;
        }

        private static List<string> ReadFile(string path, int limit)
        {
            List<string> result = new List<string>();
            using (FileStream fs = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] magic = new byte[Magic.Length];
                if (fs.Read(magic, 0, magic.Length) != magic.Length) return result;
                for (int i = 0; i < magic.Length; i++)
                    if (magic[i] != (byte)Magic[i])
                        throw new IOException(Loc.T("пошкоджений заголовок файлу журналу"));
                byte[] len = new byte[4];
                while (true)
                {
                    if (limit > 0 && result.Count >= limit) break;
                    if (fs.Read(len, 0, 4) != 4) break;
                    int n = unchecked((int)BitConverter.ToUInt32(len, 0));
                    if (n <= 0 || n > fs.Length - fs.Position) break;
                    byte[] cipher = new byte[n];
                    if (fs.Read(cipher, 0, n) != n) break;
                    byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
                    result.Add(new UTF8Encoding(false).GetString(plain));
                }
            }
            return result;
        }

        // Чтение ОДНОГО файла журнала по любому пути - окно просмотра умеет
        // открыть файл из любого места (копию, перенесённый, снятый с другой
        // машины той же Windows). Записи возвращаются свежими сверху, как в
        // ReadRecent. Шифрование - DPAPI LocalMachine, поэтому файл другой
        // машины прочитать не удастся: это норма, о ней говорится явно.
        public static List<string> ReadExternalFile(string path, int limit)
        {
            if (string.IsNullOrEmpty(path))
                throw new IOException(Loc.T("не вказано файл журналу"));
            if (!File.Exists(path))
                throw new FileNotFoundException(Loc.T("файл журналу не знайдено: ") + path);
            List<string> part;
            try
            {
                part = ReadFile(path, limit);
            }
            catch (CryptographicException)
            {
                throw new IOException(Loc.T("файл не розшифровується: він зроблений ") +
                    Loc.T("на іншій машині або іншим шифруванням (DPAPI)"));
            }
            catch (IOException ex)
            {
                throw new IOException(Loc.T("не схожий на файл журналу USB_Block (") +
                    ex.Message + "): " + path);
            }
            List<string> lines = new List<string>();
            for (int i = part.Count - 1; i >= 0; i--) lines.Add(part[i]);
            return lines;
        }

        // Сколько записей в файле по любому пути (без расшифровки - только
        // по заголовкам длин, поэтому дёшево). Нужно строке состояния окна.
        public static int CountExternalFile(string path)
        {
            try
            {
                return CountRecords(path);
            }
            catch
            {
                return 0;
            }
        }

        // Удаление журнала (пункт «Удалить сохранённые данные?»): оба
        // журнала, все поколения.
        public static void Clear()
        {
            for (int stream = 0; stream < StreamCount; stream++)
            {
                for (int g = 0; g <= StorePaths.JournalGenerations; g++)
                {
                    try
                    {
                        string f = GenerationFile(stream, g);
                        if (File.Exists(f)) File.Delete(f);
                    }
                    catch
                    {
                    }
                }
            }
            // Счётчики в памяти соответствуют удалённым файлам.
            ResetCountForTest();
            LastError = null;
        }
    }

    // =====================================================================
    // НАБЛЮДЕНИЕ ДЛЯ ЖУРНАЛА. Кто именно ведёт журнал, решает мьютекс:
    // раньше успевает тот, кто работает чаще. Если служба установлена и
    // работает, журна�� ведёт она (цикл 2 с); если службы нет или она
    // остановлена - журнал ведёт администраторский трей (цикл 3 с).
    // Двух писателей одновременно не будет: цикл под мьютексом.
    //
    // ЧТО ПИШЕТСЯ В ЖУРНАЛ
    // 1) Подключение и отключение ЛЮБОГО USB-накопителя (и разрешённого,
    //    и постороннего). Состояние "что сейчас подключено" хранится в
    //    HKLM (машино-широко), поэтому служба и трей не путают друг друга
    //    и повторно на тот же накопитель не пишут.
    // 2) Изменения файлов на РАЗРЕШЁННЫХ накопителях с буквой диска:
    //    опрос дерева каталогов и сравнение со снимком. На посторонний
    //    накопитель файлы скопировать нельзя - у него снята буква, - поэтому
    //    он в файловой части журнала не участвует.
    //
    // ГРАНИЦЫ ОПРОСА (важно для честности журнала)
    // - Первое появление тома - это БАЗОВОЕ состояние, оно молча
    //   сохраняется и в журнал НЕ пишется. Иначе подключение накопителя
    //   с тысячами уже лежащих на нём файлов дало бы тысячи записей
    //   "создан".
    // - Между двумя опросами изменения не отслеживаются: файл, созданный
    //   и удалённый внутри одного интервала, не попадёт в журнал.
    // - Обход ограничен по времени и числу файлов. Если лимит превышен
    //   (медленная флешка с огромным числом файлов), снимок НЕ берётся
    //   целиком: иначе недосканированные файлы выглядели бы удалёнными.
    //   Такой цикл пропускается, факт попадает в --diag.
    // =====================================================================
    // =====================================================================
    // ВЕДЕНИЕ ЖУРНАЛА - ВКЛЮЧАЕТСЯ ПО ЖЕЛАНИЮ (пункт J в меню трея)
    // Обход томов накопителей на каждом цикле (раз в пару секунд) -
    // заметная работа, а журнал нужен не всегда. Поэтому он по умолчанию
    // ВЫКЛЮЧЕН, и включается администратором осознанно. Настройка
    // машино-широкая (HKLM), чтобы служба под SYSTEM и трей решали
    // одинаково; выключение действует сразу - проверка идёт на каждом
    // цикле, а не только при старте.
    // =====================================================================
    // Два переключателя ведения журнала:
    //   JournalEnabled - журнал ПОДКЛЮЧЕНИЙ (накопитель появился/исчез,
    //     переименование, метка). Включается сам при установке службы
    //     мониторинга и выключается при её удалении - отдельно его трогать
    //     нечем.
    //   JournalFiles - журнал КОПИРОВАНИЯ (файлы на разрешённых накопителях).
    //     Включается галочкой пункта J и выключается её снятием.
    // Оба значения - обычные DWORD в HKLM\SOFTWARE\USB_Block: писать может
    // только администратор, читать - любой.
    public static class JournalSettings
    {
        private const string ValueKey = @"SOFTWARE\USB_Block";

        // Старое имя значения (не переименовываем: иначе уже установленная
        // служба молча перестала бы вести журнал после обновления).
        private const string ValueName = "JournalEnabled";
        private const string ValueNameFiles = "JournalFiles";

        public static string LastError;

        // Подмена значений только на время самопроверки: обычный
        // пользователь не может писать в HKLM.
        private static bool? _testConn;
        private static bool? _testFiles;

        public static void UseEnabledForTest(bool? enabled)
        {
            _testConn = enabled;
            _testFiles = enabled;
        }

        public static void UseForTest(bool? connections, bool? files)
        {
            _testConn = connections;
            _testFiles = files;
        }

        // Ведётся ли хоть один из журналов. Нет значения в реестре - не
        // ведётся ни одного.
        public static bool IsEnabled()
        {
            return IsConnectionsEnabled() || IsFilesEnabled();
        }

        public static bool IsConnectionsEnabled()
        {
            if (_testConn.HasValue) return _testConn.Value;
            return Read(ValueName);
        }

        public static bool IsFilesEnabled()
        {
            if (_testFiles.HasValue) return _testFiles.Value;
            return Read(ValueNameFiles);
        }

        private static bool Read(string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ValueKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(name);
                    if (v == null) return false;
                    if (v is int) return ((int)v) != 0;
                    string s = v as string;
                    if (s != null)
                        return s == "1" ||
                            string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool Write(string name, bool enabled)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ValueKey))
                    k.SetValue(name, enabled ? 1 : 0, RegistryValueKind.DWord);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public static bool SetEnabled(bool enabled)
        {
            return Write(ValueName, enabled);
        }

        public static bool SetFilesEnabled(bool enabled)
        {
            return Write(ValueNameFiles, enabled);
        }

        // Настройки снимаются при удалении программы.
        public static void Clear()
        {
            ClearOne(ValueName);
            ClearOne(ValueNameFiles);
        }

        private static void ClearOne(string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ValueKey, true))
                {
                    if (k != null) k.DeleteValue(name, false);
                }
            }
            catch
            {
            }
        }
    }

    public static class UsbJournalMonitor
    {
        private const string CycleMutexName = @"Global\UsbBlockJournal_Cycle_v1";
        private const string CycleMutexFallback = @"Local\UsbBlockJournal_Cycle_v1";

        // Кто ведёт журнал, закрепляется в реестре: HKLM\...\JournalPresence\
        // Owner («служба» / «трей: пользователь: id процесса») и OwnerTicks
        // (когда этот владелец работал последний раз).
        //
        // Зачем: мьютекс не даёт двум процессам идти циклу ОДНОВРЕМЕННО, но
        // не мешает им идти его по очереди. Раньше это было незаметно:
        // цикл запускал только администратор, то есть либо служба, либо
        // поднятый вручную трей. Теперь журнал копирования может вести и
        // обычный пользователь - и тогда без закрепления каждый файл
        // записывался бы дважды: один процесс видит его новым, и другой
        // тоже видит новым, потому что у каждого своя память о состоянии.
        private const string OwnerValue = "Owner";
        private const string OwnerTicksValue = "OwnerTicks";
        private static string _ownerToken;
        private static string _lastOwnerSeen;
        private static bool _primed;

        // Идентификатор этого процесса: у службы один на всё время жизни, у
        // трея - свой на каждый запуск (перезапуск трея не должен ждать, пока
        // «старый» владелец отпустит журнал).
        private static string MyToken(bool byService)
        {
            if (byService) return Loc.T("служба");
            string user = Environment.UserName;
            int pid;
            try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { pid = 0; }
            return Loc.T("трей: ") + user + ": " + pid.ToString(CultureInfo.InvariantCulture);
        }

        // Читает закреплённого владельца. Подмена для самопроверки.
        private static string _testOwner;
        private static long _testOwnerTicks;

        public static void UseOwnerForTest(string owner, long ticks)
        {
            _testOwner = owner;
            _testOwnerTicks = ticks;
        }

        private static bool ReadOwner(out string owner, out long ticks)
        {
            owner = null;
            ticks = 0;
            if (_testOwner != null)
            {
                owner = _testOwner;
                ticks = _testOwnerTicks;
                return true;
            }
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(CycleKey, false))
                {
                    if (k == null) return false;
                    owner = k.GetValue(OwnerValue) as string;
                    object t = k.GetValue(OwnerTicksValue);
                    if (t != null) ticks = Convert.ToInt64(t, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
            }
            return owner != null && ticks > 0;
        }

        private static void WriteOwner(string token, long ticks)
        {
            if (_testOwner != null) return;   // самопроверка ничего не пишет
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(CycleKey))
                {
                    k.SetValue(OwnerValue, token, RegistryValueKind.String);
                    k.SetValue(OwnerTicksValue, ticks, RegistryValueKind.QWord);
                }
            }
            catch
            {
            }
        }

        // Наш ли это процесс сейчас ведёт журнал.
        //
        // Правила простые:
        //   - если владелец был совсем недавно (меньше MaxGap назад) и это
        //     не мы - ведёт он. Служба приоритетнее трея: если владельцем
        //     стал трей, а служба ожила - забирает журнал себе (трей
        //     увидит нового владельца и замолчит);
        //   - если владельца нет или он давно молчит (MaxGap) - забираем
        //     журнал себе.
        // Возвращает false, когда вести должен кто-то другой.
        // Токен процесса для записи в реестр. В самопроверке он считается
        // на каждый вызов: иначе первая же подмена закрепила бы токен, и
        // проверка приоритета «служба важнее трея» ничего не проверяла бы.
        private static string OwnerToken(bool byService)
        {
            if (_testOwner != null) return MyToken(byService);
            if (_ownerToken == null) _ownerToken = MyToken(byService);
            return _ownerToken;
        }

        public static bool TryClaimOwnerForTest(bool byService, out string ownerName)
        {
            try
            {
                return TryClaimOwner(byService, DateTime.Now, out ownerName);
            }
            catch
            {
                ownerName = string.Empty;
                return false;
            }
        }

        private static bool TryClaimOwner(bool byService, DateTime nowLocal, out string ownerName)
        {
            string myToken = OwnerToken(byService);
            string owner;
            long ticks;
            bool have = ReadOwner(out owner, out ticks);

            bool fresh = have &&
                ticks > 0 &&
                (nowLocal.Ticks - ticks) >= 0 &&
                TimeSpan.FromTicks(nowLocal.Ticks - ticks) <= MaxGap;

            // Метка из будущего (часы переведены назад, владелец считается
            // живым вечно): молча уступаем журнал, чтобы не наследить
            // тысячами "удалено" после правки времени.
            if (have && ticks > nowLocal.Ticks)
            {
                ownerName = owner ?? string.Empty;
                _lastOwnerSeen = owner;
                return false;
            }

            if (fresh && !string.Equals(owner, myToken, StringComparison.Ordinal))
            {
                // Живой чужой владелец. Служба забирает журнал у трея, трей
                // у службы - нет.
                bool otherIsTray = owner != null &&
                    owner.StartsWith("трей", StringComparison.Ordinal);
                if (!(byService && otherIsTray))
                {
                    ownerName = owner;
                    _lastOwnerSeen = owner;
                    return false;
                }
            }

            if (!string.Equals(owner, myToken, StringComparison.Ordinal))
            {
                // Сменился владелец (или его не было): наш снимок того, что
                // было на носителях, уже неактуален - иначе первая же запись
                // после перехвата превратилась бы в тысячи "удалено".
                _primed = false;
            }
            _lastOwnerSeen = myToken;
            WriteOwner(myToken, nowLocal.Ticks);
            ownerName = myToken;
            return true;
        }

        // Компенсация пропусков: если предыдущий машинный цикл был
        // ДАВНО (кто-то перезапустился, служба стартовала/остановилась),
        // все накопители и тома получают новое базовое состояние молча -
        // иначе один перезапуск превратился бы в тысячи записей
        // "создан"/"удалён" о файлах, которых никто не трогал.
        private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(30);

        // Ограничения обхода тома
        private const int MaxScanFiles = 200000;
        private const int MaxScanMs = 1500;

        private static readonly object Sync = new object();

        // Снимки файлов по томам (только в памяти: пишет один процесс).
        internal sealed class Snap
        {
            public Dictionary<string, FileStamp> Files =
                new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        }

        internal struct FileStamp
        {
            public long Size;
            public long WrittenTicks;
        }

        // Что сейчас подключено: ключ -> описание (для журнала отключения).
        private static Dictionary<string, string> _present;
        private static DateTime _lastCycleLocal = DateTime.MinValue;
        private static string _owner = string.Empty;

        // Диагностика
        public static int LastFileCount;
        public static int LastTruncatedVolumes;
        public static string LastError;

        // Кто ведёт журнал, если это не мы (пусто, если ведём мы).
        public static string OwnerNameElsewhere = string.Empty;

        // Журнал копирования включён, а писать нечем: не хватило прав на
        // файл журнала (или на состояние в реестре). Показывается в трее и
        // в --diag, чтобы молчание не выглядело как «никто ничего не копи-
        // ровал».
        public static string WriteBlockedReason = string.Empty;
        public static bool IsOwner
        {
            get { return !string.IsNullOrEmpty(_owner); }
        }
        public static string OwnerName
        {
            get { return _owner ?? string.Empty; }
        }

        private const string PresenceKey = @"SOFTWARE\USB_Block\JournalPresence";
        private const string CycleKey = @"SOFTWARE\USB_Block\JournalPresence";
        private const string LastCycleValue = "LastCycleTicks";

        // Один цикл наблюдения. Вызывается треем и службой; работу делает
        // только один из них (мьютекс), второй выходит сразу.
        public static void RunOnce(bool byService)
        {
            // Журнал ведётся, только если включён хоть один из двух: подключения
            // включаются установкой службы, копирование - галочкой пункта J.
            // Проверка ДО мьютекса и обхода томов: выключенный журнал не
            // должен стоить ни одного лишнего запроса.
            if (!JournalSettings.IsEnabled())
            {
                lock (Sync) { _owner = string.Empty; }
                return;
            }

            // Администратор (служба или трей с правами) один раз на процесс
            // раздаёт права на файлы журнала и на состояние наблюдения: иначе
            // журнал копирования при входе обычного пользователя писать
            // было бы нечем.
            if (Program.IsAdministrator()) UsbJournalRights.EnsureAllOnce();

            lock (Sync)
            {
                Mutex mtx = Acquire();
                if (mtx == null)
                {
                    _owner = string.Empty;
                    return;
                }
                try
                {
                    bool held = false;
                    try
                    {
                        try { held = mtx.WaitOne(1500); }
                        catch (AbandonedMutexException) { held = true; }
                    }
                    catch
                    {
                        return;
                    }
                    if (!held) return;
                    try
                    {
                        // Кто ведёт журнал - решает закрепление в реестре, а не
                        // только мьютекс: мьютекс запрещает идти циклу
                        // одновременно, но по очереди шли бы оба, и каждую
                        // запись писали бы дважды. Проверяем под мьютексом:
                        // иначе оба успели бы увидеть «владельца нет» и оба
                        // записали бы себя владельцами.
                        bool ours;
                        string ownerName;
                        try
                        {
                            ours = TryClaimOwner(byService, DateTime.Now, out ownerName);
                        }
                        catch
                        {
                            ours = true;
                            ownerName = MyToken(byService);
                        }
                        _owner = ours ? ownerName : string.Empty;
                        OwnerNameElsewhere = ours ? string.Empty : ownerName;
                        if (!ours) return;      // ведёт кто-то другой - молчим
                        Cycle(byService);
                    }
                    finally
                    {
                        try { mtx.ReleaseMutex(); } catch { }
                    }
                }
                finally
                {
                    try { mtx.Dispose(); } catch { }
                }
            }
        }

        private static Mutex Acquire()
        {
            // Служба живёт в сессии 0, трей - в сессии пользователя, поэтому
            // Local\ даёт разные мьютексы и циклы пошли бы параллельно.
            // Нужен Global\; создавать его может SYSTEM и администратор
            // (оба журналируют), при отказе - запасной вариант Local\.
            try
            {
                return new Mutex(false, CycleMutexName);
            }
            catch
            {
                try
                {
                    return new Mutex(false, CycleMutexFallback);
                }
                catch
                {
                    return null;
                }
            }
        }

        private static void Cycle(bool byService)
        {
            WriteBlockedReason = string.Empty;
            _owner = byService ? Loc.T("служба")
                : (Program.IsAdministrator() ? Loc.T("трей (адміністратор)") : Loc.T("трей (звичайний користувач)"));

            DateTime nowLocal = DateTime.Now;
            bool gap = NeedsRebaseline(nowLocal);

            // Может ли этот процесс вообще писать? Если журнал копирования
            // включён, а прав не хватает - запоминаем причину: иначе не-
            // админ увидит пустой журнал и решит, что ничего не копировали.
            if (!Program.IsAdministrator() && JournalSettings.IsFilesEnabled() &&
                !UsbJournalRights.CanWriteNow())
            {
                WriteBlockedReason =
                    Loc.T("немає прав на запис у файл журналу - їх видає перший ") +
                    Loc.T("запуск з правами адміністратора (або служба моніторингу)");
            }

            try
            {
                List<DeviceEntry> wl = UsbMonitor.GetWhitelist();
                List<StorageDevice> disks = UsbQuery.GetUsbStorages();

                // Списки томов и меток нужны и для устройств, и для файлов.
                // Брать их из WMI на каждый накопитель и каждый том нельзя:
                // запрос дорогой, и на большом числе накопителей цикл
                // выродился бы в десятки одинаковых запросов. На цикл -
                // один раз.
                _cycVolumes = UsbQuery.GetUsbVolumes();
                BuildCycleCaches(_cycVolumes);

                // Журналы включаются независимо: подключения - при установке
                // службы, копирование - галочкой пункта J. Выключенная
                // половина не обходится вовсе.
                if (JournalSettings.IsConnectionsEnabled())
                {
                    JournalDevices(disks, wl, gap, nowLocal);
                }
                if (JournalSettings.IsFilesEnabled())
                {
                    JournalFiles(wl, gap);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                // Состояние наблюдения восстанавливаем даже после сбоя цикла:
                // иначе первый же неудачный WMI-запрос обнулил бы его, а
                // следующий цикл записал бы ложные "отключено" для всех
                // накопителей.
                if (_present == null) _present = LoadPresence();
                // Предыдущее состояние для сравнения: сразу заполняем его
                // тем, что лежит в реестре, иначе первый же цикл после запуска
                // переписал бы реестр впустую.
                if (_prev == null)
                    _prev = new Dictionary<string, string>(_present,
                        StringComparer.OrdinalIgnoreCase);
                SaveLastCycle(nowLocal, Program.IsAdministrator());
                _primed = true;
                _cycVolumes = null;
                _cycLetters = null;
                _cycLabels = null;
            }
        }

        // Данные текущего цикла: тома, соответствие "диск -> буква" и
        // метки томов. Заполняются один раз на цикл, используются и при
        // разборе устройств, и при опросе файлов.
        private static List<UsbVolume> _cycVolumes;
        private static Dictionary<string, string> _cycLetters;
        private static Dictionary<string, string> _cycLabels;

        private static void BuildCycleCaches(List<UsbVolume> volumes)
        {
            _cycLetters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _cycLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (volumes != null)
            {
                foreach (UsbVolume v in volumes)
                {
                    if (v == null) continue;
                    if (!string.IsNullOrEmpty(v.DiskId) && !string.IsNullOrEmpty(v.DriveLetter) &&
                        !_cycLetters.ContainsKey(v.DiskId))
                    {
                        _cycLetters[v.DiskId] = v.DriveLetter;
                    }
                }
            }
            try
            {
                Dictionary<string, string> labels = UsbQuery.GetVolumeLabels();
                if (labels != null)
                {
                    foreach (KeyValuePair<string, string> kv in labels)
                    {
                        string key = NormalizeLetter(kv.Key);
                        if (!string.IsNullOrEmpty(key)) _cycLabels[key] = kv.Value;
                    }
                }
            }
            catch
            {
            }
        }

        private static string NormalizeLetter(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return s.Trim().TrimEnd(':', '\\');
        }

        // Был ли перерыв длиннее MaxGap (перезапуск процесса/службы).
        // Ответ хранится в HKLM, а не в памяти процесса: перерыв виден
        // и тогда, когда журнал ведёт ДРУГОЙ процесс (служба вместо трея).
        private static bool NeedsRebaseline(DateTime nowLocal)
        {
            if (!_primed) return true;
            // Если метку последнего цикла писать в реестр нельзя, узнать о
            // чужом цикле невозможно - доверяем памяти процесса.
            if (_lastCycleMemoryOnly) return _lastCycleLocal <= DateTime.MinValue;
            long ticks = 0;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(CycleKey, false))
                {
                    if (k == null) return true;
                    object v = k.GetValue(LastCycleValue);
                    if (v == null) return true;
                    ticks = Convert.ToInt64(v, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                return true;
            }
            if (ticks <= 0) return true;
            TimeSpan since = TimeSpan.FromTicks(nowLocal.Ticks - ticks);
            return since < TimeSpan.Zero || since > MaxGap;
        }

        // Метка последнего ЦИКЛА, а не последней записи. Её читают и не-привилегированные
        // процессы: без права SetValue на ключе обычный пользователь считал бы
        // каждый свой цикл «первым после перерыва» и молчал бы вместо записей
        // (журнал копирования ведётся в том числе обычным пользователем).
        // Поэтому пишем LastCycleTicks всегда, когда хватает прав, а личное
        // состояние (_primed/_lastCycleLocal) - когда вышло без ошибки.
        private static void SaveLastCycle(DateTime nowLocal, bool privileged)
        {
            bool ok = true;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(CycleKey))
                    k.SetValue(LastCycleValue, nowLocal.Ticks, RegistryValueKind.QWord);
            }
            catch
            {
                ok = false;
            }
            if (!ok || !privileged)
            {
                // Права на запись в реестр не позволили: метку последнего
                // цикла держим в памяти процесса - этого хватает, чтобы
                // следующий наш же цикл не сочёл себя первым.
                _lastCycleLocal = nowLocal;
                if (!privileged) _lastCycleMemoryOnly = true;
            }
        }

        // Метка последнего цикла по памяти (когда писать в реестр нельзя).
        private static bool _lastCycleMemoryOnly;

        // ---- Устройства: подключение / отключение ----

        private static void JournalDevices(List<StorageDevice> disks,
            List<DeviceEntry> wl, bool silent, DateTime nowLocal)
        {
            if (_present == null) _present = LoadPresence();

            Dictionary<string, string> now =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (StorageDevice d in disks)
            {
                string key = DeviceKey(d);
                if (key == null) continue;
                string detail = DescribeDevice(d, wl);
                if (!now.ContainsKey(key)) now[key] = detail;
            }

            // Появились. Отдельного поля "Причина" уже нет: вид записи
            // (подключён / обнаружен) и так говорит, было ли устройство
            // вставлено сейчас или просто не было видно, пока журнал
            // не работал; отключение - это свой вид записи.
            foreach (KeyValuePair<string, string> kv in now)
            {
                if (_present.ContainsKey(kv.Key)) continue;
                UsbJournal.Write(silent ? UsbJournal.KindDeviceFound
                    : UsbJournal.KindDeviceAdded, kv.Value);
            }

            // Исчезли.
            foreach (KeyValuePair<string, string> kv in _present)
            {
                if (now.ContainsKey(kv.Key)) continue;
                UsbJournal.Write(UsbJournal.KindDeviceRemoved, kv.Value);
            }

            _present = now;
            // Реестр переписываем только когда набор накопителей (или их
            // описание) действительно изменился: цикл идёт каждые секунды,
            // а запись в реестр на ровном месте - это лишняя работа и
            // ненужные изменения в системе.
            if (!SamePresence(_prev, now)) SavePresence(now);
            _prev = now;
        }

        private static Dictionary<string, string> _prev;

        private static bool SamePresence(
            Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<string, string> kv in a)
            {
                string other;
                if (!b.TryGetValue(kv.Key, out other)) return false;
                if (!string.Equals(kv.Value, other, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        // Описание накопителя для записи журнала. Здесь НЕ пишется VID:PID
        // (идентификатор узла USB - серийника достаточно, а строку он только
        // засоряет), состояние блокировки (это свойство ПРОГРАММЫ, а не
        // подключения: в записи о носителе оно только путает) и причина
        // (вид записи и так говорит: подключён / обнаружен / отключён).
        // Метка тома берётся из кэша цикла по букве диска: сам StorageDevice
        // её не несёт, и без этого поле было бы всегда "Метка=-".
        internal static string DescribeDevice(StorageDevice d, List<DeviceEntry> wl)
        {
            StringBuilder sb = new StringBuilder();
            string letter = LetterOf(d);
            string label = d.Label;
            if (string.IsNullOrEmpty(label)) label = VolumeLabelOf(letter);
            sb.Append("SN=").Append(string.IsNullOrEmpty(d.Serial) ? "-" : d.Serial);
            sb.Append(" | Модель=").Append(string.IsNullOrEmpty(d.Model) ? "-" : d.Model);
            sb.Append(" | Метка=").Append(string.IsNullOrEmpty(label) ? "-" : label);
            bool allowed = IsAllowed(d, wl);
            sb.Append(" | Решение=").Append(allowed ? "разрешён" : "заблокирован");
            sb.Append(" | Буква=").Append(string.IsNullOrEmpty(letter) ? "-" : letter + ":");
            return sb.ToString();
        }

        private static bool IsAllowed(StorageDevice d, List<DeviceEntry> wl)
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

        // Буква диска, если диск смонтирован (по совпадению PHYSICALDRIVE).
        // Внутри цикла берётся из кэша: сопоставление дисков и томов уже
        // сделано один раз при сборе данных цикла.
        private static string LetterOf(StorageDevice d)
        {
            string diskId = d == null ? null : d.DiskDeviceId;
            if (string.IsNullOrEmpty(diskId)) return null;
            if (_cycLetters != null)
            {
                string cached;
                return _cycLetters.TryGetValue(diskId, out cached) ? cached : null;
            }
            // Вне цикла (диагностика, вызовы до/после опроса).
            try
            {
                foreach (UsbVolume v in UsbQuery.GetUsbVolumes())
                    if (string.Equals(v.DiskId, diskId, StringComparison.OrdinalIgnoreCase))
                        return v.DriveLetter;
            }
            catch
            {
            }
            return null;
        }

        private static string DeviceKey(StorageDevice d)
        {
            if (d == null) return null;
            if (!string.IsNullOrEmpty(d.Serial)) return "SN:" + d.Serial;
            if (!string.IsNullOrEmpty(d.UsbId)) return "USB:" + d.UsbId;
            if (!string.IsNullOrEmpty(d.DiskDeviceId)) return "DISK:" + d.DiskDeviceId;
            return null;
        }

        // Состояние присутствия хранится в реестре (машино-широко): при
        // перезапуске программы и при смене владельца журнала (трей <-> служба)
        // состояние не теряется, поэтому повторно на те же накопители записи
        // "подключение" не пишутся. Формат элемента MultiString:
        // ключ TAB описание.
        private static Dictionary<string, string> LoadPresence()
        {
            Dictionary<string, string> map =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(PresenceKey, false))
                {
                    if (k == null) return map;
                    string[] arr = k.GetValue("Keys") as string[];
                    if (arr == null) return map;
                    foreach (string s in arr)
                    {
                        if (string.IsNullOrEmpty(s)) continue;
                        int tab = s.IndexOf('\t');
                        if (tab <= 0) continue;
                        map[s.Substring(0, tab)] = s.Substring(tab + 1);
                    }
                }
            }
            catch
            {
            }
            return map;
        }

        private static void SavePresence(Dictionary<string, string> map)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(PresenceKey))
                {
                    if (map == null || map.Count == 0)
                    {
                        k.DeleteValue("Keys", false);
                        return;
                    }
                    string[] arr = new string[map.Count];
                    int i = 0;
                    foreach (KeyValuePair<string, string> kv in map)
                        arr[i++] = kv.Key + "\t" + kv.Value;
                    k.SetValue("Keys", arr, RegistryValueKind.MultiString);
                }
            }
            catch
            {
            }
        }

        // ---- Файлы на разрешённых томах: опрос и сравнение ----

        private static void JournalFiles(List<DeviceEntry> wl, bool silent)
        {
            List<UsbVolume> volumes = _cycVolumes ?? UsbQuery.GetUsbVolumes();
            LastTruncatedVolumes = 0;
            int scanned = 0;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UsbVolume v in volumes)
            {
                string key = VolumeKey(v);
                if (key == null) continue;
                seen.Add(key);

                if (!UsbMonitor.IsVolumeAllowed(v, wl))
                {
                    // Пособочный диск постороннего накопителя: копировать
                    // на него нельзя (буква снята), следить не за чем.
                    if (_snaps != null) _snaps.Remove(key);
                    continue;
                }
                scanned += WatchVolume(v, key, silent);
            }

            // Тома, которых больше нет (отключён, снята буква, буква сменилась).
            // Снимок молча забываем: иначе удаление накопителя дало бы
            // тысячи записей "удалён".
            if (_snaps != null)
            {
                List<string> gone = new List<string>();
                foreach (string k in _snaps.Keys)
                    if (!seen.Contains(k)) gone.Add(k);
                foreach (string k in gone) _snaps.Remove(k);
            }
            LastFileCount = scanned;
        }

        private static Dictionary<string, Snap> _snaps;

        // Идентификатор тома для снимка. Одного серийного номера мало: у
        // накопителя с несколькими разделами он общий, и второй раздел
        // затёр бы снимок первого - а это десятки тысяч ложных "удалён".
        // Номера физического диска тоже мало: разделы одного диска его
        // разделяют (буквы F: и G: на одном диске дали ОДИН ключ, и
        // снимки перетирали друг друга каждый цикл). Поэтому в ключ
        // входит номер РАЗДЕЛА (WMI: "Disk #2, Partition #0"); если он
        // неизвестен, остаётся прежняя лестница: серийник, диск, буква.
        internal static string VolumeKey(UsbVolume v)
        {
            if (v == null) return null;
            bool hasSerial = !string.IsNullOrEmpty(v.Serial);
            string part = !string.IsNullOrEmpty(v.PartitionId) ? v.PartitionId : v.DiskId;
            bool hasPart = !string.IsNullOrEmpty(part);
            if (hasSerial && hasPart) return "V:" + v.Serial + "#" + part;
            if (hasSerial) return "V:" + v.Serial;
            if (hasPart) return "D:" + part;
            if (!string.IsNullOrEmpty(v.DriveLetter)) return "L:" + v.DriveLetter;
            return null;
        }

        // Опрос одного тома. Возвращает число просмотренных файлов.
        private static int WatchVolume(UsbVolume v, string key, bool silent)
        {
            string root = v.DriveLetter + @":\";
            Dictionary<string, FileStamp> current;
            bool truncated;
            int count = ScanVolume(root, out current, out truncated);

            if (truncated)
            {
                // Обход не успел - снимок целиком неверен. Берём предыдущий
                // (или, если его нет, оставляем том "неизвестным" до
                // следующего успешного обхода) и в журнал НЕ пишем.
                LastTruncatedVolumes++;
                if (_snaps == null || !_snaps.ContainsKey(key)) return count;
                return count;
            }

            if (_snaps == null) _snaps = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);

            Snap prev;
            bool first = !_snaps.TryGetValue(key, out prev);
            if (first)
            {
                // Первое появление тома - базовое состояние, молча.
                prev = new Snap();
                prev.Files = current;
                _snaps[key] = prev;
                return count;
            }

            List<string> events = Diff(prev.Files, current);
            prev.Files = current;

            if (silent)
            {
                // Восстановление после перерыва: изменения за время, когда
                // никого не было, честно записать нельзя. Записываем одну
                // служебную строку вместо потока сравнений.
                if (events.Count > 0)
                {
                    UsbJournal.Write(UsbJournal.KindNote,
                        Loc.T("Нагляд за томом ") + v.DriveLetter + Loc.T(": знімок оновлено після ") +
                        Loc.T("перерви, змін за час простою не записано"));
                }
                return count;
            }

            foreach (string ev in events)
                UsbJournal.WriteRaw(FormatEvent(ev) + VolumeDeviceInfo(v));
            return count;
        }

        // Сведения о накопителе, дописываемые к каждой записи о файле.
        // Модели здесь нет: она уже есть в записи о подключении накопителя,
        // а в строке каждого файла она только повторяется (при копировании
        // тысяч файлов это тысячи лишних символов).
        internal static string VolumeDeviceInfo(UsbVolume v)
        {
            return " | SN=" + (string.IsNullOrEmpty(v.Serial) ? "-" : v.Serial) +
                " | Метка=" + VolumeLabelOf(v.DriveLetter);
        }

        private static string VolumeLabelOf(string letter)
        {
            string key = NormalizeLetter(letter);
            if (string.IsNullOrEmpty(key)) return "-";
            if (_cycLabels != null)
            {
                string cached;
                if (_cycLabels.TryGetValue(key, out cached) && !string.IsNullOrEmpty(cached))
                    return cached;
                return "-";
            }
            try
            {
                Dictionary<string, string> labels = UsbQuery.GetVolumeLabels();
                string l;
                if (labels.TryGetValue(key, out l) && !string.IsNullOrEmpty(l)) return l;
            }
            catch
            {
            }
            return "-";
        }

        // Разбор "вид\tпуть\tразмер" обратно в готовую запись журнала.
        // Путь уже полный (начинается с буквы тома), поэтому том подставлять
        // не нужно.
        private static string FormatEvent(string ev)
        {
            string[] p = ev.Split('\t');
            StringBuilder sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append('\t').Append(p[0]).Append('\t');
            sb.Append(Term(p.Length > 1 ? p[1] : string.Empty));
            if (p.Length > 2 && !string.IsNullOrEmpty(p[2]))
                sb.Append(" | ").Append(p[2]);
            return sb.ToString();
        }

        private static string Term(string s)
        {
            return string.IsNullOrEmpty(s) ? "-" : s;
        }

        // Сравнение снимков. Возвращает список "вид\tпуть\tразмер".
        // Переименование отличается от пары удалить+создать: если в одной
        // папке удалён ровно один файл и создан ровно один с тем же
        // размером и тем же временем изменения - это перенос/переименование.
        internal static List<string> Diff(
            Dictionary<string, FileStamp> oldFiles,
            Dictionary<string, FileStamp> newFiles)
        {
            List<string> added = new List<string>();
            List<string> changed = new List<string>();
            List<string> removed = new List<string>();

            foreach (KeyValuePair<string, FileStamp> kv in newFiles)
            {
                FileStamp was;
                if (!oldFiles.TryGetValue(kv.Key, out was))
                {
                    added.Add(kv.Key);
                }
                else if (was.Size != kv.Value.Size || was.WrittenTicks != kv.Value.WrittenTicks)
                {
                    changed.Add(kv.Key);
                }
            }
foreach (string path in oldFiles.Keys)
                if (!newFiles.ContainsKey(path))
                    removed.Add(path);

            // Переименование отличается от пары "удалён+создан": если в
            // одной папке удалён ровно один файл и создан ровно один с тем
            // же размером и тем же временем изменения - это перенос или
            // переименование. Списки раскладываются по папкам один раз,
            // иначе перебор пар был бы квадратичным.
            Dictionary<string, List<string>> removedByDir = GroupByDir(removed);
            Dictionary<string, List<string>> addedByDir = GroupByDir(added);

            List<string> renames = new List<string>();
            HashSet<string> renamedFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> renamedTo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, List<string>> dir in addedByDir)
            {
                if (dir.Value.Count != 1) continue;
                List<string> removedHere;
                if (!removedByDir.TryGetValue(dir.Key, out removedHere)) continue;
                if (removedHere.Count != 1) continue;
                string from = removedHere[0];
                string to = dir.Value[0];
                FileStamp os = oldFiles[from];
                FileStamp ns = newFiles[to];
                if (os.Size != ns.Size || os.WrittenTicks != ns.WrittenTicks) continue;
                renamedFrom.Add(from);
                renamedTo.Add(to);
                renames.Add(UsbJournal.KindFileRenamed + "\t" + to + "\t" + from);
            }

            List<string> result = new List<string>(renames);
            foreach (string path in removed)
                if (!renamedFrom.Contains(path))
                    result.Add(UsbJournal.KindFileRemoved + "\t" + path + "\t" +
                        SizeOf(oldFiles[path]));
            foreach (string path in changed)
                result.Add(UsbJournal.KindFileChanged + "\t" + path + "\t" +
                    SizeOf(newFiles[path]));
            foreach (string path in added)
                if (!renamedTo.Contains(path))
                    result.Add(UsbJournal.KindFileAdded + "\t" + path + "\t" +
                        SizeOf(newFiles[path]));
            return result;
        }

        private static Dictionary<string, List<string>> GroupByDir(List<string> paths)
        {
            Dictionary<string, List<string>> map =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in paths)
            {
                string dir = ParentDir(p);
                List<string> list;
                if (!map.TryGetValue(dir, out list))
                {
                    list = new List<string>();
                    map[dir] = list;
                }
                list.Add(p);
            }
            return map;
        }

        private static string SizeOf(FileStamp s)
        {
            return "Размер=" + s.Size.ToString(CultureInfo.InvariantCulture) + Loc.T(" байт");
        }

        private static string ParentDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int i = path.LastIndexOfAny(new char[] { '\\', '/' });
            return i < 0 ? string.Empty : path.Substring(0, i);
        }

        // Обход дерева каталогов тома. Возвращает снимок; truncated=true,
        // если упёрлись в лимит по времени или числу файлов - такой снимок
        // использовать нельзя (недосканированные файлы сочлись бы удалёнными).
        internal static int ScanVolume(string root, out Dictionary<string, FileStamp> files,
            out bool truncated)
        {
            files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
            truncated = false;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Stack<string> dirs = new Stack<string>();
            dirs.Push(root);
            int count = 0;

            while (dirs.Count > 0)
            {
                string dir = dirs.Pop();
                string[] entries;
                try
                {
                    entries = Directory.GetFileSystemEntries(dir);
                }
                catch
                {
                    continue;
                }
                foreach (string entry in entries)
                {
                    string name = SafeName(entry);
                    if (IsSystemName(name)) continue;
                    FileAttributes attr;
                    try
                    {
                        attr = File.GetAttributes(entry);
                    }
                    catch
                    {
                        continue;
                    }
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                    {
                        // Точки повторного входа ( junctions, symlinks) не
                        // обходим - иначе можно уйти в цикл.
                        continue;
                    }
                    if ((attr & FileAttributes.Directory) != 0)
                    {
                        dirs.Push(entry);
                        continue;
                    }
                    try
                    {
                        FileInfo fi = new FileInfo(entry);
                        if (!fi.Exists) continue;
                        files[entry] = new FileStamp
                        {
                            Size = fi.Length,
                            WrittenTicks = fi.LastWriteTimeUtc.Ticks
                        };
                    }
                    catch
                    {
                        continue;
                    }
                    count++;
                    if (count >= MaxScanFiles ||
                        (count % 256 == 0 && sw.ElapsedMilliseconds > MaxScanMs))
                    {
                        truncated = true;
                        return count;
                    }
                }
            }
            return count;
        }

        private static string SafeName(string path)
        {
            try
            {
                return Path.GetFileName(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // Служебные папки: их содержимое копированием с ПК не считается.
        private static bool IsSystemName(string name)
        {
            return string.Equals(name, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "RECYCLER", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "System Volume Information (G)", StringComparison.OrdinalIgnoreCase);
        }
    }

    // =====================================================================
    // Скрытое окно для ловли WM_DEVICECHANGE и WM_HOTKEY
    // =====================================================================
    public sealed class HiddenWindow : NativeWindow
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int WM_HOTKEY = 0x0312;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private readonly Action _onChange;
        private readonly Action _onHotkey;

        public HiddenWindow(Action onChange)
            : this(onChange, null)
        {
        }

        public HiddenWindow(Action onChange, Action onHotkey)
        {
            _onChange = onChange;
            _onHotkey = onHotkey;
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
            else if (m.Msg == WM_HOTKEY && HotkeyStore.IsOurs(m.WParam.ToInt32()))
            {
                Action hk = _onHotkey;
                if (hk != null)
                {
                    try { hk(); }
                    catch { }
                }
            }
            base.WndProc(ref m);
        }
    }

    // =====================================================================
    // ГОРЯЧАЯ КЛАВИША открытия журнала.
    //
    // Основная комбинация не назначается: это Ctrl+Alt+O, она работает сразу
    // после установки службы (значение "Hotkey" в реестре). Дополнительная
    // комбинация ("Hotkey2") задаётся галочкой пункта J "вести журнал
    // копирования" - второе сочетание на ту же самую функцию.
    //
    // Обе хранятся в HKLM\SOFTWARE\USB_Block (REG_SZ, читать может любой -
    // в самой комбинации ничего секретного нет; записывает только
    // администратор). Хранится именно в HKLM, а не в зашифрованном файле,
    // потому что горячую клавишу должен уметь зарегистрировать и трей
    // обычного пользователя: нажатие поднимет права и откроет журнал
    // отдельным процессом --journal.
    //
    // Кто регистрирует: трей, а если он выгружен ("Выход") - уведомитель.
    // Мьютекс тут не нужен: RegisterHotKey для одной комбинации в системе
    // один, и второй процесс получит отказ - так они и делят клавишу
    // без всякой координации.
    // =====================================================================
    public static class HotkeyStore
    {
        private const string ValueKey = @"SOFTWARE\USB_Block";
        private const string ValueName = "Hotkey";
        private const string ValueName2 = "Hotkey2";

        // Идентификаторы горячих клавиш в WM_HOTKEY.
        public const int HotkeyId = 0xB10C;      // основная (Ctrl+Alt+O)
        public const int HotkeyId2 = 0xB10D;     // дополнительная (галочка J)

        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;

        // Основная комбинация: одинаковая на всех машинах, назначать её
        // пользователю незачем, поэтому и не задаём вопроса.
        public const string DefaultText = "Ctrl+Alt+O";

        public static string LastError;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public static bool Exists()
        {
            return !string.IsNullOrEmpty(GetText());
        }

        public static string GetText()
        {
            return Read(ValueName);
        }

        // Дополнительная комбинация (галочка J). Пусто - не задана.
        public static string GetText2()
        {
            return Read(ValueName2);
        }

        private static string Read(string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ValueKey, false))
                {
                    if (k == null) return null;
                    return k.GetValue(name) as string;
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool Write(string name, string text)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ValueKey))
                    k.SetValue(name, text ?? string.Empty, RegistryValueKind.String);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = Loc.T("запис: ") + ex.Message;
                return false;
            }
        }

        public static bool SetText(string text)
        {
            return Write(ValueName, text);
        }

        public static bool SetText2(string text)
        {
            return Write(ValueName2, text);
        }

        // Основная комбинация всегда одна и та же (Ctrl+Alt+O). Значение в
        // реестре нужно только для того, чтобы её знали трей и уведомитель,
        // поэтому при обновлении программы старое сочетание заменяется на
        // нынешнее, а не остаётся от старой версии.
        public static bool SetDefault()
        {
            return Write(ValueName, DefaultText);
        }

        private const string ValueNameMigrated = "HotkeyDefaultSet";

        // Разовое приведение к новой схеме: основная комбинация больше не
        // выбирается пользователем, она всегда Ctrl+Alt+O. Сохранённое
        // старой версией значение (например, Ctrl+Alt+D0 или Ctrl+Alt+U)
        // молча заменяется - иначе после обновления программа продолжала бы
        // открывать журнал по клавишам, о которых в меню больше никто не
        // говорит. Отметка нужна, чтобы не переписывать значение на каждом
        // запуске.
        public static void EnsureDefault()
        {
            string current;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ValueKey, false))
                {
                    if (k == null) return;
                    object done = k.GetValue(ValueNameMigrated);
                    if (done != null) return;
                    current = k.GetValue(ValueName) as string;
                }
            }
            catch
            {
                return;
            }
            // Значения нет - основная клавиша появится вместе со службой
            // (пункт 7). Создавать её тут нельзя: без службы открывать нечего.
            if (string.IsNullOrEmpty(current)) return;
            if (!string.Equals(current.Trim(), DefaultText, StringComparison.OrdinalIgnoreCase))
            {
                if (!SetDefault()) return;
            }
            SetMigrated();
        }

        private static void SetMigrated()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ValueKey))
                    k.SetValue(ValueNameMigrated, 1, RegistryValueKind.DWord);
            }
            catch
            {
            }
        }

        // Комбинации снимаются вместе со службой: открывать журнал без службы
        // нечего.
        public static void Clear()
        {
            ClearOne(ValueName);
            ClearOne(ValueName2);
        }

        // Снимается только дополнительная (снятие галочки J).
        public static void ClearExtra()
        {
            ClearOne(ValueName2);
        }

        private static void ClearOne(string name)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(ValueKey, true))
                {
                    if (k != null) k.DeleteValue(name, false);
                }
            }
            catch
            {
            }
        }

        // "Ctrl+Alt+U" -> модификаторы и виртуальная клавиша.
        // Возвращает false, если строка разобрать нельзя.
        // "Win" здесь только для совместимости: окно назначения клавиши его
        // больше не создаёт (см. HotkeyCaptureForm.Accept), но сочетание,
        // сохранённое старой версией или вписанное руками, разобрать надо.
        public static bool TryParse(string text, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            vk = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split('+');
            if (parts.Length < 2) return false;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string m = parts[i].Trim();
                if (string.Equals(m, "Ctrl", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModControl;
                else if (string.Equals(m, "Alt", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModAlt;
                else if (string.Equals(m, "Shift", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModShift;
                else if (string.Equals(m, "Win", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModWin;
                else
                    return false;
            }
            if (modifiers == 0) return false;   // одна клавиша без Ctrl/Alt/Shift

            string main = parts[parts.Length - 1].Trim();
            if (main.Length == 0) return false;
            Keys k;
            try
            {
                k = (Keys)Enum.Parse(typeof(Keys), main, true);
            }
            catch
            {
                return false;
            }
            if (!Enum.IsDefined(typeof(Keys), k)) return false;
            vk = unchecked((uint)k);
            return IsMainKey(k, vk);
        }

        // Пригодна ли клавиша для основной (не модификатора).
        public static bool IsMainKey(Keys k, uint vk)
        {
            // Основная клавиша - это виртуальная клавиша Windows, 0x01..0xFF.
            // Больше ничего быть не может: в перечислении Keys есть служебные
            // значения-биты модификаторов (Shift = 0x10000, Control = 0x20000,
            // Alt = 0x40000). Для RegisterHotKey они не клавиши вовсе, и
            // разбирать их как таковые нельзя - иначе в строке вида
            // "Ctrl+Shift" слово Shift посчиталось бы клавишей.
            if (vk == 0 || vk > 0xFF) return false;
            if (k == Keys.ControlKey || k == Keys.Menu || k == Keys.ShiftKey ||
                k == Keys.LControlKey || k == Keys.RControlKey ||
                k == Keys.LMenu || k == Keys.RMenu ||
                k == Keys.LShiftKey || k == Keys.RShiftKey ||
                k == Keys.LWin || k == Keys.RWin)
            {
                return false;
            }
            return true;
        }

        // Клавиша-модификатор (её нельзя назвать основной).
        public static bool IsModifierKey(Keys k)
        {
            return IsMainKey(k, unchecked((uint)k)) == false &&
                (k == Keys.ControlKey || k == Keys.Menu || k == Keys.ShiftKey ||
                 k == Keys.LWin || k == Keys.RWin || k == Keys.LControlKey ||
                 k == Keys.RControlKey || k == Keys.LMenu || k == Keys.RMenu ||
                 k == Keys.LShiftKey || k == Keys.RShiftKey);
        }

        // Обратное преобразование - для показа в окне и в --diag.
        public static string Format(uint modifiers, uint vk)
        {
            StringBuilder sb = new StringBuilder();
            if ((modifiers & ModControl) != 0) sb.Append("Ctrl+");
            if ((modifiers & ModAlt) != 0) sb.Append("Alt+");
            if ((modifiers & ModShift) != 0) sb.Append("Shift+");
            if ((modifiers & ModWin) != 0) sb.Append("Win+");
            sb.Append(((Keys)unchecked((int)vk)).ToString());
            return sb.ToString();
        }

        // Зарегистрировать на окне основную и (если задана) дополнительную
        // комбинацию. Возвращает true, если зарегистрирована хотя бы одна -
        // вторая может быть занята другой программой, это не повод ругаться.
        public static bool Register(IntPtr hwnd)
        {
            LastError = null;
            bool any = RegisterOne(hwnd, HotkeyId, GetText());
            string extra = GetText2();
            if (!string.IsNullOrEmpty(extra))
            {
                if (RegisterOne(hwnd, HotkeyId2, extra)) any = true;
                else if (LastError != null)
                {
                    // Основная зарегистрировалась - причина неудачи второй
                    // не должна выглядеть как отказ всей горячей клавиши.
                    LastError = LastError + Loc.T(" (додаткову не зареєстровано)");
                }
            }
            return any;
        }

        private static bool RegisterOne(IntPtr hwnd, int id, string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            uint mods, vk;
            if (!TryParse(text, out mods, out vk))
            {
                LastError = Loc.T("невірно збережена комбінація: ") + text;
                return false;
            }
            try
            {
                if (RegisterHotKey(hwnd, id, mods, vk)) return true;
                int err = Marshal.GetLastWin32Error();
                LastError = Loc.T("комбінація ") + text + Loc.T(" зайнята іншою програмою (код ") +
                    err.ToString(CultureInfo.InvariantCulture) + ")";
            }
            catch (Exception ex)
            {
                LastError = "RegisterHotKey: " + ex.Message;
            }
            return false;
        }

        // Обе комбинации - наше, значит наше и право их снять.
        public static void Unregister(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try { UnregisterHotKey(hwnd, HotkeyId); }
            catch { }
            try { UnregisterHotKey(hwnd, HotkeyId2); }
            catch { }
        }

        // Наше ли это сообщение о нажатии (основная или дополнительная).
        public static bool IsOurs(int id)
        {
            return id == HotkeyId || id == HotkeyId2;
        }
    }

    // =====================================================================
    // Окно задания комбинации: «нажмите клавиши».
    // Работает как при первом запуске, так и для смены комбинации.
    // Пока включён режим захвата, обычный ввод с клавиатуры не доходит до
    // элементов окна (в том числе пробел и Enter не нажимают кнопки).
    // Требуется хотя бы один модификатор (Ctrl/Alt/Shift/Win) - иначе
    // программа перехватывала бы обычный набор текста во всей системе.
    // =====================================================================
    public sealed class HotkeyCaptureForm : Form
    {
        private readonly TextBox _box;
        private readonly Button _capture;
        private readonly Label _hint;
        private bool _capturing;
        private string _value;

        public string Hotkey
        {
            get { return _value; }
        }

        public HotkeyCaptureForm(string current)
        {
            _value = current;

            this.Text = Loc.T("Комбінація клавіш для журналу");
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ClientSize = new Size(470, 206);
            this.AutoScaleDimensions = new SizeF(7F, 15F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.Font = SystemFonts.MessageBoxFont;

            Label title = new Label();
            title.Text = Loc.T("Комбінація, якою відкривається журнал підключень");
            title.AutoSize = true;
            title.Location = new Point(12, 12);
            this.Controls.Add(title);

            _box = new TextBox();
            _box.Location = new Point(12, 36);
            _box.Size = new Size(446, 23);
            _box.ReadOnly = true;
            _box.TextAlign = HorizontalAlignment.Center;
            _box.TabStop = true;
            this.Controls.Add(_box);

            _capture = new Button();
            _capture.Text = Loc.T("Задати...");
            _capture.Location = new Point(12, 70);
            _capture.Size = new Size(140, 27);
            _capture.Click += delegate { StartCapture(); };
            this.Controls.Add(_capture);

            _hint = new Label();
            _hint.AutoSize = false;
            _hint.Size = new Size(446, 46);
            _hint.Location = new Point(12, 106);
            this.Controls.Add(_hint);

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(292, 163);
            ok.Size = new Size(78, 27);
            this.Controls.Add(ok);
            this.AcceptButton = ok;

            Button cancel = new Button();
            cancel.Text = Loc.T("Скасувати");
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(380, 163);
            cancel.Size = new Size(78, 27);
            this.Controls.Add(cancel);
            this.CancelButton = cancel;

            ShowValue();
        }

        private void ShowValue()
        {
            if (string.IsNullOrEmpty(_value))
            {
                _box.Text = Loc.T("не задана");
                _hint.Text = Loc.T("Натисніть «Задати...» і наберіть комбінацію, наприклад Ctrl+Alt+U.");
            }
            else
            {
                _box.Text = _value;
                _hint.Text = Loc.T("Натисніть «Задати...», щоб замінити комбінацію.");
            }
        }

        private void StartCapture()
        {
            _capturing = true;
            _box.Text = Loc.T("натисніть клавіші...");
            _hint.Text = Loc.T("Потрібне основне поле і хоча б один із Ctrl, Alt, Shift. ") +
                Loc.T("Клавіша Win не використовується. Esc - скасування захоплення.");
            _capture.Enabled = false;
            _box.Focus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Пока идёт захват, клавиатура принадлежит только полю ввода:
            // пробел не должен нажимать OK, а цифры не должны попадать в него.
            if (_capturing && (keyData & Keys.KeyCode) != Keys.None)
            {
                Accept(msg, keyData);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetKeyState(int vKey);

        // Нажата ли клавиша Win (VK_LWIN/VK_RWIN). Старший бит GetKeyState
        // означает «нажата в этот момент», младший - «была нажата».
        private static bool WinHeld()
        {
            try
            {
                return (GetKeyState(0x5B) & 0x8000) != 0 ||
                       (GetKeyState(0x5C) & 0x8000) != 0;
            }
            catch
            {
                return false;
            }
        }

        private void Accept(Message msg, Keys keyData)
        {            if (keyData == Keys.Escape)
            {
                _capturing = false;
                _capture.Enabled = true;
                ShowValue();
                return;
            }

            Keys code = keyData & Keys.KeyCode;
            uint mods = 0;
            // Модификаторы берём из ФИЗИЧЕСКОГО состояния клавиш
            // (ModifierKeys), а не из битов keyData. В System.Windows.Forms
            // у Win нет отдельного бита-модификатора, а Checks Win делали
            // по кодам клавиш LWin/RWin (0x5B/0x5C) - и это срабатывало
            // само: у X, C, V, Z, W и других кодов биты совпадают с
            // 0x5B/0x5C, поэтому в сочетание сам дорисовывался Win.
            Keys held = ModifierKeys;
            if ((held & Keys.Control) == Keys.Control) mods |= HotkeyStore.ModControl;
            if ((held & Keys.Alt) == Keys.Alt) mods |= HotkeyStore.ModAlt;
            if ((held & Keys.Shift) == Keys.Shift) mods |= HotkeyStore.ModShift;
            // Win для журнала не используется: сочетания с Win перехватывает
            // сама Windows (меню «Пуск»), и Win в поле означал бы клавишу,
            // которая работает не везде. В Keys у Win нет бита-модификатора,
            // поэтому состояние самих клавиш спрашиваем напрямую.
            if (WinHeld())
            {
                _hint.Text = Loc.T("Клавіша Win у комбінації для журналу не використовується - ") +
                    Loc.T("відпустіть її і наберіть комбінацію ще раз.");
                return;
            }

            bool bareModifier = HotkeyStore.IsModifierKey(code);
            if (bareModifier)
            {
                _hint.Text = Loc.T("Додайте основну клавішу (наприклад Ctrl+Alt+U).");
                return;
            }
            if (mods == 0)
            {
                _hint.Text = Loc.T("Потрібен хоча б один із Ctrl, Alt, Shift - інакше ") +
                    Loc.T("комбінація перехоплюватиме звичайний набір тексту.");
                return;
            }
            if (!Enum.IsDefined(typeof(Keys), code) ||
                !HotkeyStore.IsMainKey(code, unchecked((uint)code)))
            {
                _hint.Text = Loc.T("Ця клавіша не підходить як основна (Ctrl, Alt ") +
                    Loc.T("і Shift задаються як додаток). Виберіть звичайну клавішу.");
                return;
            }

            _capturing = false;
            _capture.Enabled = true;
            _value = HotkeyStore.Format(mods, unchecked((uint)code));
            ShowValue();
        }
    }

    // =====================================================================
    // Иконка в трее. Приоритет - файл stop_usb.ico, встроенный в exe как
    // ресурс (сборка: -resource:stop_usb.ico,usb_block.tray.ico); из него
    // берётся картинка 16x16. Если ресурса нет/повреждён - картинка
    // stop_usb.jpg из трейлера в конце exe ([jpg][длина][USBICON]). Если и
    // её нет - рисуется простая иконка кодом.
    // =====================================================================
    public static class AppIcons
    {
        private static readonly byte[] TrailerMagic =
            { (byte)'U', (byte)'S', (byte)'B', (byte)'I', (byte)'C', (byte)'O', (byte)'N' };

        // Имя ресурса в собственной сборке (не имя файла).
        private const string IcoResourceName = "usb_block.tray.ico";

        // Рисуем не 16x16, а 32x32: Windows сама уменьшит под размер трея,
        // а на 32x32 сглаживание заметно лучше.
        private const int TrayIconSize = 32;

        public static Icon Create()
        {
            try
            {
                Icon ico = TryLoadEmbeddedIco();
                if (ico != null) return ico;
            }
            catch
            {
            }
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

        // Значок из встроенного ресурса stop_usb.ico. Там есть картинки
        // 16, 32, 48 и 256 пикселей; берём ту, что для трея.
        private static Icon TryLoadEmbeddedIco()
        {
            using (Stream res = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(IcoResourceName))
            {
                if (res == null) return null;
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[8192];
                    int got;
                    while ((got = res.Read(buf, 0, buf.Length)) > 0)
                        ms.Write(buf, 0, got);
                    ms.Position = 0;
                    return new Icon(ms, TrayIconSize, TrayIconSize);
                }
            }
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

            this.Text = Loc.T("Додати пристрій");
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(540, 340);
            this.Font = SystemFonts.MessageBoxFont;
            this.AutoScaleDimensions = new SizeF(7F, 15F);   // метрики Segoe UI 9 пт (проектные)
            this.AutoScaleMode = AutoScaleMode.Font;

            // Какое устройство уже разрешено (в whitelist) - зелёное,
            // остальные (будут/были заблокированы) - красные.
            MarkAllowed();

            Label lblSel = new Label();
            lblSel.Text = Loc.T("Виберіть USB-накопичувач:");
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
                string label = string.IsNullOrEmpty(sd.Model) ? Loc.T("(без імені)") : sd.Model;
                if (!string.IsNullOrEmpty(sd.Label))
                    label = sd.Label + "  " + label;
                _cbDevice.Items.Add(label);
            }
            _cbDevice.DrawItem += DrawDeviceItem;
            _cbDevice.SelectedIndexChanged += delegate { OnSelection(); };

            Label lblName = new Label();
            lblName.Text = Loc.T("1. Ім'я Пристрою");
            lblName.AutoSize = true;
            lblName.Location = new Point(12, 70);

            _tbName = new System.Windows.Forms.TextBox();
            _tbName.Text = Loc.T("Новий Пристрій");
            _tbName.SetBounds(12, 92, 516, 24);

            Label lblType = new Label();
            lblType.Text = Loc.T("2. Тип Пристрою (ID):");
            lblType.AutoSize = true;
            lblType.Location = new Point(12, 128);

            _tbType = new System.Windows.Forms.TextBox();
            _tbType.ReadOnly = true;
            _tbType.SetBounds(12, 150, 516, 24);

            Label lblModel = new Label();
            lblModel.Text = Loc.T("3. Модель Пристрою (Vendor ID):");
            lblModel.AutoSize = true;
            lblModel.Location = new Point(12, 188);

            _tbModel = new System.Windows.Forms.TextBox();
            _tbModel.ReadOnly = true;
            _tbModel.SetBounds(12, 210, 516, 24);

            _ok = new Button();
            _ok.Text = Loc.T("Додати");
            _ok.Size = new Size(110, 28);
            _ok.Location = new Point(306, 270);
            _ok.Enabled = false;
            _ok.Click += delegate { Commit(); };

            Button cancel = new Button();
            cancel.Text = Loc.T("Скасувати");
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
                MessageBox.Show(Loc.T("Виберіть пристрій зі списку."),
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StorageDevice sd = _devices[i];
            if (string.IsNullOrEmpty(sd.UsbId))
            {
                MessageBox.Show(Loc.T("У пристрою немає USB-ідентифікатора - його не можна додати."),
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Selected = sd;
            DeviceName = _tbName.Text;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }

    // Выравнивание записей по столбцам: ширина каждого столбца берётся по
    // самой длинной записи, поэтому столбцы стоят ровно друг под другом.
    // Пустые поля в конце строки отбрасываются, последнее поле не добивается
    // пробелами. Разделитель столбцов задаёт вызывающий код.
    internal static class TextAlign
    {
        public static List<string> Align(List<string[]> rows, string separator)
        {
            List<string[]> clean = new List<string[]>(rows.Count);
            int count = 0;
            foreach (string[] r in rows)
            {
                int len = r.Length;
                while (len > 0 && string.IsNullOrEmpty(r[len - 1])) len--;
                string[] c;
                if (len == r.Length)
                {
                    c = r;
                }
                else
                {
                    c = new string[len];
                    Array.Copy(r, c, len);
                }
                clean.Add(c);
                if (len > count) count = len;
            }

            int[] width = new int[count];
            foreach (string[] r in clean)
                for (int j = 0; j < r.Length; j++)
                    if (r[j].Length > width[j]) width[j] = r[j].Length;

            List<string> result = new List<string>(clean.Count);
            foreach (string[] r in clean)
            {
                StringBuilder sb = new StringBuilder();
                for (int j = 0; j < r.Length; j++)
                {
                    if (j > 0) sb.Append(separator);
                    if (j < r.Length - 1) sb.Append(r[j].PadRight(width[j]));
                    else sb.Append(r[j]);
                }
                result.Add(sb.ToString());
            }
            return result;
        }
    }

    // Удаление устройств из whitelist: отметка одной или нескольких записей
    // (кнопка «Обрати всі»). Само удаление и последующая блокировка -
    // в TrayContext.DoRemoveDevice.
    public sealed class RemoveDeviceDialog : Form
    {
        private readonly List<DeviceEntry> _entries;
        private readonly System.Windows.Forms.CheckedListBox _list;
        public List<DeviceEntry> Selected { get; private set; }

        public RemoveDeviceDialog(List<DeviceEntry> entries)
        {
            _entries = entries;

            this.Text = Loc.T("Видалити пристрій із WHITELIST");
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(760, 404);
            this.Font = SystemFonts.MessageBoxFont;
            this.AutoScaleDimensions = new SizeF(7F, 15F);   // метрики Segoe UI 9 пт (проектные)
            this.AutoScaleMode = AutoScaleMode.Font;

            Label lbl = new Label();
            lbl.Text = Loc.T("Виберіть пристрій для видалення з WHITELIST:\n") +
                       Loc.T("після видалення його накопичувач буде заблоковано.");
            lbl.AutoSize = false;
            lbl.Size = new Size(736, 36);
            lbl.Location = new Point(12, 10);

            _list = new System.Windows.Forms.CheckedListBox();
            _list.SetBounds(12, 52, 736, 300);
            _list.IntegralHeight = false;
            _list.HorizontalScrollbar = true;
            _list.CheckOnClick = true;
            // Моноширинный шрифт - столбцы выровнены (как в журнале).
            _list.Font = new Font("Consolas", 8.5f);

            // Записи показываются выровненными по столбцам.
            List<string[]> rows = new List<string[]>(_entries.Count);
            foreach (DeviceEntry e in _entries)
            {
                rows.Add(new string[]
                {
                    string.IsNullOrEmpty(e.Name) ? Loc.T("(без імені)") : e.Name,
                    string.IsNullOrEmpty(e.Serial) ? string.Empty : "SN: " + e.Serial,
                    string.IsNullOrEmpty(e.DiskId) ? string.Empty : Loc.T("Диск: ") + e.DiskId
                });
            }
            _list.Items.AddRange(TextAlign.Align(rows, " | ").ToArray());

            Button selectAll = new Button();
            selectAll.Text = Loc.T("Обрати всі");
            selectAll.Size = new Size(110, 28);
            selectAll.Location = new Point(12, 364);
            selectAll.Enabled = _entries.Count > 0;
            selectAll.Click += delegate { SetAllChecked(true); };

            Button ok = new Button();
            ok.Text = Loc.T("Видалити");
            ok.Size = new Size(110, 28);
            ok.Location = new Point(520, 364);
            ok.Enabled = _entries.Count > 0;
            ok.Click += delegate { Commit(); };

            Button cancel = new Button();
            cancel.Text = Loc.T("Скасувати");
            cancel.Size = new Size(110, 28);
            cancel.Location = new Point(638, 364);
            cancel.DialogResult = DialogResult.Cancel;

            this.Controls.Add(lbl);
            this.Controls.Add(_list);
            this.Controls.Add(selectAll);
            this.Controls.Add(ok);
            this.Controls.Add(cancel);
            this.CancelButton = cancel;
            this.AcceptButton = ok;
        }

        private void SetAllChecked(bool check)
        {
            for (int i = 0; i < _list.Items.Count; i++)
                _list.SetItemChecked(i, check);
        }

        private void Commit()
        {
            Selected = new List<DeviceEntry>();
            for (int i = 0; i < _entries.Count && i < _list.Items.Count; i++)
            {
                if (_list.GetItemChecked(i)) Selected.Add(_entries[i]);
            }
            if (Selected.Count == 0)
            {
                MessageBox.Show(Loc.T("Виберіть пристрій зі списку."),
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }

    // Ввод пароля защиты: одно поле (проверка) или два с подтверждением
    // (установка нового). Возвращает DialogResult.OK только если пароль
    // введён (и, для установки, подтверждён).
    public sealed class PasswordForm : Form
    {
        private readonly bool _confirm;
        private readonly System.Windows.Forms.TextBox _tbPass;
        private readonly System.Windows.Forms.TextBox _tbRepeat;
        public string Password { get; private set; }

        public PasswordForm(string caption, string prompt, bool confirm)
            : this(caption, prompt, confirm, null)
        {
        }

        // fieldLabel - подпись первого поля («Пароль:», «Кодовое слово:» и т.п.);
        // при confirm=true используется как подпись «Новый пароль».
        public PasswordForm(string caption, string prompt, bool confirm,
            string fieldLabel)
        {
            _confirm = confirm;

            int topPass = confirm ? 78 : 70;
            int topRepeat = topPass + 50;
            int topButtons = (confirm ? topRepeat : topPass) + 36;

            this.Text = caption;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.ClientSize = new Size(470, topButtons + 60);
            this.Font = SystemFonts.MessageBoxFont;
            this.AutoScaleDimensions = new SizeF(7F, 15F);   // метрики Segoe UI 9 пт (проектные)
            this.AutoScaleMode = AutoScaleMode.Font;

            Label lbl = new Label();
            lbl.Text = prompt;
            lbl.AutoSize = false;
            lbl.Size = new Size(446, 56);
            lbl.Location = new Point(12, 10);

            Label lblPass = new Label();
            lblPass.Text = string.IsNullOrEmpty(fieldLabel)
                ? (confirm ? Loc.T("Новий пароль:") : Loc.T("Пароль:"))
                : fieldLabel;
            lblPass.AutoSize = true;
            lblPass.Location = new Point(12, topPass - 20);

            _tbPass = new System.Windows.Forms.TextBox();
            _tbPass.UseSystemPasswordChar = true;
            _tbPass.SetBounds(12, topPass, 446, 24);

            Label lblRepeat = new Label();
            lblRepeat.Text = Loc.T("Повторіть пароль:");
            lblRepeat.AutoSize = true;
            lblRepeat.Visible = confirm;
            lblRepeat.Location = new Point(12, topRepeat - 20);

            _tbRepeat = new System.Windows.Forms.TextBox();
            _tbRepeat.UseSystemPasswordChar = true;
            _tbRepeat.Visible = confirm;
            _tbRepeat.SetBounds(12, topRepeat, 446, 24);

            Button ok = new Button();
            ok.Text = Loc.T("ОК");
            ok.Size = new Size(110, 28);
            ok.Location = new Point(240, topButtons);
            ok.Click += delegate { Commit(); };

            Button cancel = new Button();
            cancel.Text = Loc.T("Скасувати");
            cancel.Size = new Size(110, 28);
            cancel.Location = new Point(358, topButtons);
            cancel.DialogResult = DialogResult.Cancel;

            this.Controls.Add(lbl);
            this.Controls.Add(lblPass);
            this.Controls.Add(_tbPass);
            this.Controls.Add(lblRepeat);
            this.Controls.Add(_tbRepeat);
            this.Controls.Add(ok);
            this.Controls.Add(cancel);
            this.CancelButton = cancel;
            this.AcceptButton = ok;

            this.Shown += delegate { _tbPass.Focus(); };
        }

        private void Commit()
        {
            string pass = _tbPass.Text ?? string.Empty;
            if (pass.Length == 0)
            {
                MessageBox.Show(Loc.T("Введіть пароль."), Program.Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                _tbPass.Focus();
                return;
            }

            if (_confirm)
            {
                if (!AdminPassword.MatchesPolicy(pass))
                {
                    MessageBox.Show(AdminPassword.PolicyHint(), Program.Title,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _tbPass.Focus();
                    return;
                }
                if (!string.Equals(pass, _tbRepeat.Text ?? string.Empty, StringComparison.Ordinal))
                {
                    MessageBox.Show(Loc.T("Паролі не збігаються."), Program.Title,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _tbRepeat.Focus();
                    _tbRepeat.SelectAll();
                    return;
                }
            }

            Password = pass;
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
            this.Text = Loc.T("WHITELIST - дозволені USB-накопичувачі");
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(660, 460);
            this.Font = SystemFonts.MessageBoxFont;
            this.AutoScaleDimensions = new SizeF(7F, 15F);   // метрики Segoe UI 9 пт (проектные)
            this.AutoScaleMode = AutoScaleMode.Font;

            Label lbl = new Label();
            lbl.Text = Loc.T("Дозволених пристроїв: ") +
                entries.Count.ToString(CultureInfo.InvariantCulture);
            lbl.AutoSize = true;
            lbl.Location = new Point(12, 10);

            // Моноширинный текст с выровненными по вертикали столбцами
            // (как в журнале): метка тома, модель, дата добавления.
            System.Windows.Forms.TextBox box = new System.Windows.Forms.TextBox();
            box.Font = new Font("Consolas", 8.5f);
            box.Multiline = true;
            box.ReadOnly = true;
            box.WordWrap = false;
            box.ScrollBars = ScrollBars.Both;
            box.BackColor = SystemColors.Window;
            box.ForeColor = SystemColors.WindowText;
            box.SetBounds(12, 34, 636, 376);

            List<string[]> rows = new List<string[]>(entries.Count);
            foreach (DeviceEntry e in entries)
                rows.Add(EntryFields(e, connected, volumeLabels));
            box.Lines = TextAlign.Align(rows, " | ").ToArray();

            Button close = new Button();
            close.Text = Loc.T("Закрити");
            close.Size = new Size(110, 28);
            close.Location = new Point(538, 420);
            close.DialogResult = DialogResult.OK;

            this.Controls.Add(lbl);
            this.Controls.Add(box);
            this.Controls.Add(close);
            this.AcceptButton = close;
            this.CancelButton = close;
        }

        // Читаемый вид записи whitelist - три поля:
        //   Метка тома (из файловой системы, если накопитель подключён),
        //   Модель накопителя (WMI-модель, если подключён; иначе читается из DiskId),
        //   Дата добавления в whitelist.
        private static string[] EntryFields(DeviceEntry e,
            Dictionary<string, UsbVolume> connected,
            Dictionary<string, string> volumeLabels)
        {
            string key = (e.UsbId ?? string.Empty) + "|" + (e.Serial ?? string.Empty);
            UsbVolume v;
            string label = null;
            string model = null;
            if (connected != null && volumeLabels != null &&
                connected.TryGetValue(key, out v) &&
                volumeLabels.TryGetValue(v.DriveLetter, out label))
            {
                label = string.IsNullOrEmpty(label) ? Loc.T("(без мітки)") : label;
                model = string.IsNullOrEmpty(v.Model) ? null : v.Model;
            }
            if (label == null)
                label = string.IsNullOrEmpty(e.Name) ? Loc.T("(не підключено)") : e.Name;

            return new string[]
            {
                Loc.T("Мітка тому: \"") + label + "\"",
                Loc.T("Модель накопичувача: ") +
                    (string.IsNullOrEmpty(model) ? ReadableModel(e.DiskId) : model),
                e.AddedAt == default(DateTime)
                    ? string.Empty
                    : Loc.T("Додано: ") +
                      e.AddedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            };
        }

        // Читаемая модель из HardwareID диска USBSTOR, e.g.
        //   "USBSTOR\Disk&Ven_&Prod_Transcend_8GB&Rev_1100"  ->  "Transcend 8GB"
        //   "USBSTOR\DiskJetFlashTranscend_8GB___1100"        ->  "JetFlashTranscend 8GB"
        private static string ReadableModel(string diskId)
        {
            if (string.IsNullOrEmpty(diskId)) return Loc.T("(немає)");
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
    // Окно просмотра журнала. Открывается только администратором (журнал
    // зашифрован и закрыт ACL, как whitelist.dat), и только по горячей
    // клавише: отдельного пункта в меню трея нет намеренно - журнал не
    // должен попадать под руку тому, кто сёл за машину.
    // Две вкладки: подключения накопителей и файлы на разрешённых
    // накопителях. Текст только для чтения, но его можно выделить и
    // скопировать (Ctrl+A / Ctrl+C) - журнал пригодится для разбора.
    // Записи выводятся свежими сверху, служебные строки - в своей вкладке
    // не теряются, а помечены в общем виде.
    // =====================================================================
    public sealed class JournalViewForm : Form
    {
        // Ограничение выдачи: журнал живёт кольцом до 100000 записей,
        // в окно столько не влезет. Показываем последние LimitPerTab.
        private const int LimitPerTab = 20000;

        private readonly TabControl _tabs;
        private readonly TextBox _devices;
        private readonly TextBox _files;
        private readonly TextBox _notes;
        private readonly Label _status;
        private readonly Button _open;
        private readonly Button _live;

        // Открытый файл журнала из другого места; null - обычный журнал
        // этой машины. Файл читается только для показа, ничего не пишется.
        private string _external;

        public JournalViewForm()
        {
            this.Text = Loc.T("Журнал USB-блокування");
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimizeBox = false;
            this.ClientSize = new Size(1000, 620);
            this.Font = SystemFonts.MessageBoxFont;
            this.AutoScaleDimensions = new SizeF(7F, 15F);
            this.AutoScaleMode = AutoScaleMode.Font;

            _tabs = new TabControl();
            _tabs.SetBounds(12, 8, 976, 560);
            this.Controls.Add(_tabs);

            _devices = MakeBox();
            _files = MakeBox();
            _notes = MakeBox();
            _tabs.TabPages.Add(MakePage(Loc.T("Підключення накопичувачів"), _devices));
            _tabs.TabPages.Add(MakePage(Loc.T("Файли на накопичувачах"), _files));
            _tabs.TabPages.Add(MakePage(Loc.T("Службові записи"), _notes));

            _status = new Label();
            _status.SetBounds(12, 574, 434, 22);
            this.Controls.Add(_status);

            _open = new Button();
            _open.Text = Loc.T("Відкрити файл...");
            _open.SetBounds(452, 572, 124, 27);
            _open.Click += delegate { OpenExternal(); };
            this.Controls.Add(_open);

            _live = new Button();
            _live.Text = Loc.T("До журналу");
            _live.SetBounds(584, 572, 116, 27);
            _live.Enabled = false;
            _live.Click += delegate { _external = null; Fill(); };
            this.Controls.Add(_live);

            Button refresh = new Button();
            refresh.Text = Loc.T("Оновити");
            refresh.SetBounds(708, 572, 108, 27);
            refresh.Click += delegate { Fill(); };
            this.Controls.Add(refresh);

            Button copy = new Button();
            copy.Text = Loc.T("Копіювати");
            copy.SetBounds(824, 572, 116, 27);
            copy.Click += delegate { CopyCurrent(); };
            this.Controls.Add(copy);

            this.AcceptButton = refresh;
            this.CancelButton = null;
            Fill();
        }

        private static TextBox MakeBox()
        {
            TextBox tb = new TextBox();
            // Моноширинный шрифт - в журнале важны отступы и выравнивание
            // колонок. Обычный системный шрифт для этого не годится.
            tb.Font = new Font("Consolas", 8.5f);
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.WordWrap = false;
            tb.ScrollBars = ScrollBars.Both;
            tb.BackColor = SystemColors.Window;
            tb.ForeColor = SystemColors.WindowText;
            tb.Dock = DockStyle.Fill;
            return tb;
        }

        private static TabPage MakePage(string title, Control child)
        {
            TabPage page = new TabPage();
            page.Text = title;
            page.Padding = new Padding(6);
            page.Controls.Add(child);
            return page;
        }

        private void Fill()
        {
            try
            {
                // Открытый чужой файл показывается вместо журнала машины
                // (и только для показа - ни записи, ни состояние наблюдения
                // при этом не трогаются).
                bool external = _external != null;
                List<string> lines = external
                    ? UsbJournal.ReadExternalFile(_external, LimitPerTab * 3)
                    : UsbJournal.ReadRecent(LimitPerTab * 3);
                List<string> dev = new List<string>();
                List<string> files = new List<string>();
                List<string> notes = new List<string>();
                int shownDev = 0, shownFiles = 0, shownNotes = 0;

                foreach (string line in lines)
                {
                    string kind = KindOf(line);
                    if (kind == UsbJournal.KindNote)
                    {
                        if (shownNotes++ < LimitPerTab) notes.Add(line);
                    }
                    else if (IsFileKind(kind))
                    {
                        if (shownFiles++ < LimitPerTab) files.Add(line);
                    }
                    else
                    {
                        if (shownDev++ < LimitPerTab) dev.Add(line);
                    }
                }

                // Показ выравнивается по столбцам: время, вид операции и поля
                // записи встают друг под друга (см. FormatRecords). Служебные
                // записи - свободный текст, их поля не выравниваем.
                SetText(_devices, FormatRecords(dev, true));
                SetText(_files, FormatRecords(files, true));
                SetText(_notes, FormatRecords(notes, false));

                _live.Enabled = external;
                this.Text = external
                    ? Loc.T("Журнал USB-блокування - ") + Path.GetFileName(_external)
                    : Loc.T("Журнал USB-блокування");

                long bytes = external
                    ? new FileInfo(_external).Length
                    : UsbJournal.TotalSizeBytes();
                int total = external
                    ? UsbJournal.CountExternalFile(_external)
                    : UsbJournal.TotalRecordCount();
                _status.Text =
                    (external
                        ? Loc.T("ВІДКРИТО ФАЙЛ: ") + _external + "   "
                        : string.Empty) +
                    (external || JournalSettings.IsEnabled()
                        ? string.Empty
                        : Loc.T("ВЕДЕННЯ ЖУРНАЛУ ВИМКНЕНО (немає встановленої служби) ") +
                          Loc.T("- показано раніше записані записи.   ")) +
                    (external || JournalSettings.IsFilesEnabled()
                        ? string.Empty
                        : Loc.T("Журнал копіювання вимкнено (меню трея, пункт J) - ") +
                          Loc.T("файлові записи не оновлюються.   ")) +
                    Loc.T("Записів: ") +
                    total.ToString(CultureInfo.InvariantCulture) +
                    Loc.T("   Розмір: ") + bytes.ToString(CultureInfo.InvariantCulture) + Loc.T(" байт") +
                    Loc.T("   Показано: пристроїв ") + shownDev.ToString(CultureInfo.InvariantCulture) +
                    Loc.T(" / файлів ") + shownFiles.ToString(CultureInfo.InvariantCulture) +
                    (shownNotes > 0 ? Loc.T(" / службових ") + shownNotes.ToString(CultureInfo.InvariantCulture) : string.Empty) +
                    Loc.T("   Записи старші за поріг показу не показані.");
            }
            catch (Exception ex)
            {
                _live.Enabled = _external != null;
                _status.Text = (_external != null
                    ? Loc.T("Не вдалося прочитати файл ") + _external + ": "
                    : Loc.T("Не вдалося прочитати журнал: ")) + ex.Message;
            }
        }

        // Открытие файла журнала из любого места: копии, перенесённого,
        // снятого с другой машины. Показывается только для чтения.
        private void OpenExternal()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = Loc.T("Відкрити файл журналу");
                dialog.Filter = Loc.T("Файли журналу (journal*.dat)|journal*.dat|") +
                    Loc.T("Усі файли (*.*)|*.*");
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                try
                {
                    if (Directory.Exists(StorePaths.Directory))
                        dialog.InitialDirectory = StorePaths.Directory;
                }
                catch
                {
                }
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _external = dialog.FileName;
                Fill();
            }
        }

        private void CopyCurrent()
        {
            try
            {
                TextBox box = _tabs.SelectedIndex == 0 ? _devices
                    : _tabs.SelectedIndex == 1 ? _files : _notes;
                if (box.TextLength == 0) return;
                box.SelectionStart = 0;
                box.SelectionLength = box.TextLength;
                box.Focus();
                SendKeys.SendWait("^c");
            }
            catch
            {
            }
        }

        private static void SetText(TextBox box, List<string> lines)
        {
            box.SuspendLayout();
            try
            {
                box.Lines = lines.ToArray();
            }
            finally
            {
                box.ResumeLayout();
            }
            box.SelectionStart = 0;
            box.SelectionLength = 0;
        }

        private static string KindOf(string line)
        {
            int t1 = line.IndexOf('\t');
            if (t1 < 0) return string.Empty;
            int t2 = line.IndexOf('\t', t1 + 1);
            if (t2 < 0) return string.Empty;
            return line.Substring(t1 + 1, t2 - t1 - 1);
        }

        private static bool IsFileKind(string kind)
        {
            return UsbJournal.IsFileKind(kind);
        }

        // Превращает записи журнала в строки для показа, выравнивая столбцы
        // по вертикали: время, вид операции словами и поля записи (для
        // накопителя - SN, Модель, Метка, Решение, Буква; для файла - путь,
        // размер, SN, Метка) встают друг под друга. Ширину каждого столбца
        // берём по самой длинной записи, чтобы ни одна не «съехала».
        private static List<string> FormatRecords(List<string> raw, bool alignColumns)
        {
            int n = raw.Count;
            bool[] plain = new bool[n];
            string[] when = new string[n];
            string[] word = new string[n];
            string[][] cols = new string[n][];
            int whenW = 0, wordW = 0, count = 0;

            for (int i = 0; i < n; i++)
            {
                string[] p = raw[i].Split('\t');
                if (p.Length < 3)
                {
                    plain[i] = true;
                    continue;
                }
                when[i] = p[0];
                word[i] = KindText(p[1]);
                if (alignColumns)
                {
                    // Записи хранятся с постоянными метками полей (Модель=,
                    // Метка=, Решение= и т.п. - их читает самопроверка),
                    // поэтому английские подписи подставляются только при
                    // показе, а не при записи.
                    string[] parts = p[2].Split(new string[] { " | " }, StringSplitOptions.None);
                    for (int k = 0; k < parts.Length; k++) parts[k] = FieldLabel(parts[k]);
                    cols[i] = parts;
                }
                else
                {
                    cols[i] = new string[] { p[2] };
                }
                if (cols[i].Length > count) count = cols[i].Length;
                if (when[i].Length > whenW) whenW = when[i].Length;
                if (word[i].Length > wordW) wordW = word[i].Length;
            }

            int[] width = new int[count];
            for (int i = 0; i < n; i++)
            {
                if (plain[i]) continue;
                for (int j = 0; j < cols[i].Length; j++)
                    if (cols[i][j].Length > width[j]) width[j] = cols[i][j].Length;
            }

            List<string> result = new List<string>(n);
            for (int i = 0; i < n; i++)
            {
                if (plain[i])
                {
                    result.Add(raw[i]);
                    continue;
                }
                StringBuilder sb = new StringBuilder();
                sb.Append(when[i].PadRight(whenW)).Append(' ');
                sb.Append(word[i].PadRight(wordW)).Append("  ");
                for (int j = 0; j < cols[i].Length; j++)
                {
                    if (j > 0) sb.Append(" | ");
                    // Последнее поле строки не добиваем пробелами - оно и так
                    // упирается в конец строки.
                    if (j < cols[i].Length - 1) sb.Append(cols[i][j].PadRight(width[j]));
                    else sb.Append(cols[i][j]);
                }
                result.Add(sb.ToString());
            }
            return result;
        }

        // Подписи полей в записи журнала хранятся на украинском/русском
        // ("Модель=", "Метка=", "Решение=", "Буква=", "Размер=") - их
        // читает самопроверка и разбор. Для английского интерфейса подписи
        // (и значение "разрешён"/"заблокирован") переводятся только при
        // показе; сами данные не меняются. Незнакомые поля (путь файла,
        // "SN=") возвращаются как есть.
        private static string FieldLabel(string col)
        {
            if (string.IsNullOrEmpty(col)) return col;
            int eq = col.IndexOf('=');
            if (eq < 0) return col;
            string label = col.Substring(0, eq + 1);
            string value = col.Substring(eq + 1);
            if (label == "Модель=") return Loc.T("Модель=") + value;
            if (label == "Метка=") return Loc.T("Метка=") + value;
            if (label == "Буква=") return Loc.T("Буква=") + value;
            if (label == "Решение=") return Loc.T("Решение=") + Loc.T(value);
            if (label == "Размер=") return Loc.T("Размер=") + TranslateSize(value);
            return col;
        }

        private static string TranslateSize(string value)
        {
            const string unit = " байт";
            if (value.EndsWith(unit, StringComparison.Ordinal))
                return value.Substring(0, value.Length - unit.Length) + Loc.T(unit);
            return value;
        }

        private static string KindText(string kind)
        {
            if (kind == UsbJournal.KindDeviceAdded) return Loc.T("підключено");
            if (kind == UsbJournal.KindDeviceRemoved) return Loc.T("відключено");
            if (kind == UsbJournal.KindDeviceFound) return Loc.T("виявлено");
            if (kind == UsbJournal.KindFileAdded) return Loc.T("скопійовано");
            if (kind == UsbJournal.KindFileChanged) return Loc.T("змінено");
            if (kind == UsbJournal.KindFileRemoved) return Loc.T("видалено");
            if (kind == UsbJournal.KindFileRenamed) return Loc.T("перенесено");
            if (kind == UsbJournal.KindNote) return Loc.T("службове");
            return kind;
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
        public const string GuardTaskName = "USB_Block_Guard";

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

        public static bool GuardInstalled()
        {
            return TaskInstalled(GuardTaskName);
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
            string t = CreateOne(TaskName, "--logon", false);
            if (t != null) errors.Add(Loc.T("задача трея: ") + t);
            string n = CreateOne(NotifyTaskName, "--notify", true);
            if (n != null) errors.Add(Loc.T("задача сповіщень: ") + n);
            string g = CreateGuardTask();
            if (g != null) errors.Add(Loc.T("задача контролю служби: ") + g);
            if (errors.Count == 0) return null;
            return string.Join("\n", errors.ToArray());
        }

        // Создать/обновить задачу контроля службы (--ensure-service от SYSTEM).
        // null = успех, иначе текст ошибки. Идемпотентно: пересоздаёт задачу.
        public static string CreateGuardTask()
        {
            string xml = BuildGuardXml();
            return CreateTask(GuardTaskName, xml, 5, "SYSTEM");
        }

        private static string CreateOne(string taskName, string args, bool repeat)
        {
            string xml = BuildXml(args, repeat);
            return CreateTask(taskName, xml, 4, null);
        }

        private static string CreateTask(string taskName, string xml, int logonType, object user)
        {
            try
            {
                RegisterViaCom(taskName, xml, logonType, user);
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
            DeleteOne(GuardTaskName);
        }

        public static void DeleteGuard()
        {
            DeleteOne(GuardTaskName);
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
                throw new InvalidOperationException(Loc.T("COM-тип Schedule.Service не знайдено"));
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

        private static void RegisterViaCom(string taskName, string xml, int logonType, object user)
        {
            object folder = GetRootFolder();
            Type ft = folder.GetType();
            // флаги: TASK_CREATE_OR_UPDATE (6) - повторная регистрация
            // обновляет существующую задачу, а не падает с "уже есть";
            // принципала задаёт XML; logonType: 4 = TASK_LOGON_GROUP (группа
            // Users), 5 = TASK_LOGON_SERVICE_ACCOUNT (SYSTEM); sddl = null.
            ft.InvokeMember("RegisterTask", BindingFlags.InvokeMethod, null, folder,
                new object[] { taskName, xml, 6, user, null, logonType, null });
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

        private static string BuildXml(string args, bool repeat)
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
            // Для задачи сповіщувача добавляем повтор раз в минуту: если
            // пользователь завершит процесс сповіщувача, он поднимется снова.
            string repetition = repeat
                ? "      <Repetition>\r\n" +
                  "        <Interval>PT1M</Interval>\r\n" +
                  "        <StopAtDurationEnd>false</StopAtDurationEnd>\r\n" +
                  "      </Repetition>\r\n"
                : string.Empty;
            return
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo>\r\n" +
                Loc.T("    <Description>USB-блокування: значок у треї та сповіщення про блокування для всіх користувачів</Description>\r\n") +
                "  </RegistrationInfo>\r\n" +
                "  <Triggers>\r\n" +
                "    <LogonTrigger>\r\n" +
                "      <Enabled>true</Enabled>\r\n" +
                repetition +
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

        // Задача контроля службы: раз в минуту от имени SYSTEM запускает
        // --ensure-service, который поднимает службу, если её остановили или
        // отключили. Скрытая, без ограничения времени выполнения.
        private static string BuildGuardXml()
        {
            string command = "\"" + ProtectedCopy.InstallExe + "\"";
            return
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo>\r\n" +
                "    <Description>" + Loc.T("USB-блокування: контроль роботи служби моніторингу") + "</Description>\r\n" +
                "  </RegistrationInfo>\r\n" +
                "  <Triggers>\r\n" +
                "    <TimeTrigger>\r\n" +
                "      <Repetition>\r\n" +
                "        <Interval>PT1M</Interval>\r\n" +
                "        <StopAtDurationEnd>false</StopAtDurationEnd>\r\n" +
                "      </Repetition>\r\n" +
                "      <StartBoundary>2020-01-01T00:00:00</StartBoundary>\r\n" +
                "      <Enabled>true</Enabled>\r\n" +
                "    </TimeTrigger>\r\n" +
                "  </Triggers>\r\n" +
                "  <Principals>\r\n" +
                "    <Principal id=\"Author\">\r\n" +
                "      <UserId>S-1-5-18</UserId>\r\n" +
                "      <RunLevel>HighestAvailable</RunLevel>\r\n" +
                "    </Principal>\r\n" +
                "  </Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
                "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
                "    <IdleSettings>\r\n" +
                "      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n" +
                "      <RestartOnIdle>false</RestartOnIdle>\r\n" +
                "    </IdleSettings>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>true</Hidden>\r\n" +
                "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
                "    <WakeToRun>false</WakeToRun>\r\n" +
                "    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>\r\n" +
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\">\r\n" +
                "    <Exec>\r\n" +
                "      <Command>" + command + "</Command>\r\n" +
                "      <Arguments>--ensure-service</Arguments>\r\n" +
                "    </Exec>\r\n" +
                "  </Actions>\r\n" +
                "</Task>\r\n";
        }

        // Тестові хуки для --selftest (чисті перевірки без реєстрації задач).
        internal static string GuardXmlForTest()
        {
            return BuildGuardXml();
        }

        internal static string NotifyXmlForTest()
        {
            return BuildXml("--notify", true);
        }
    }

    public sealed class TrayContext : ApplicationContext
    {
        private NotifyIcon _icon;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _miStatus;
        private ToolStripMenuItem _miSvcInstall;
        private ToolStripMenuItem _miSvcRemove;
        private ToolStripMenuItem _miJournal;
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

            _hwnd = new HiddenWindow(HandleDeviceChange, HandleHotkey);
            _hwnd.EnsureCreated();

            // Горячая клавиша открытия журнала задаётся при установке службы.
            // Регистрирует её тот процесс, который выиграл: пока жив трей -
            // он, после "Выход" - уведомитель. Занятая клавиша (например
            // другая программа) не повод отказываться от работы программы,
            // поэтому отказ пишем в LastError для --diag и идём дальше.
            HotkeyStore.Register(_hwnd.Handle);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 3000;
            _timer.Tick += delegate { TickScan(); };
            _timer.Start();

            TickScan();
        }

        // Нажата горячая клавиша журнала. От администратора окно
        // открывается здесь же, от обычного пользователя - права
        // поднимаются и журнал открывает отдельный процесс --journal.
        private void HandleHotkey()
        {
            try
            {
                if (Program.IsAdministrator())
                {
                    using (JournalViewForm form = new JournalViewForm())
                    {
                        form.ShowDialog();
                    }
                }
                else if (!Program.StartJournalViewer(true))
                {
                    MessageBox.Show(Loc.T("Не вдалося відкрити журнал з правами адміністратора."),
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show(Loc.T("Не вдалося відкрити журнал: ") + ex.Message,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
                catch
                {
                }
            }
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();

            _miStatus = new ToolStripMenuItem(StatusText());
            _menu.Items.Add(_miStatus);
            ApplyStatusColor();
            _menu.Items.Add(new ToolStripSeparator());

            // Меню рисует сама Windows (RenderMode.System): цвета, размеры и
            // шрифт пунктов берутся из системной темы, свой Renderer не
            // задаётся.
            // ВАЖНО (ошибка этапа 68): сеттер RenderMode сбрасывает Renderer
            // на штатный, поэтому наоборот - сначала Renderer, потом
            // RenderMode - нельзя: свой Renderer молча исчезал, и меню
            // выглядело как обычное системное.
            _menu.RenderMode = ToolStripRenderMode.System;

            // Не администратор (например, запуск задачи Планировщика при входе
            // обычного пользователя): управление настройками недоступно - только
            // статус, уведомления о блокировках и выход. Меню сокращается.
            if (!Program.IsAdministrator())
            {
                ToolStripMenuItem mInfo = new ToolStripMenuItem(
                    Loc.T("Керування доступне лише адміністраторам"));
                mInfo.Enabled = false;
                _menu.Items.Add(mInfo);
                _menu.Items.Add(new ToolStripSeparator());

                _menu.Items.Add(BuildLanguageMenu());
                _menu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem mExitLimited = new ToolStripMenuItem(Loc.T("Вихід"));
                mExitLimited.Click += delegate { this.ExitThread(); };
                _menu.Items.Add(mExitLimited);

                _icon.ContextMenuStrip = _menu;
                return;
            }

            ToolStripMenuItem mBlock = new ToolStripMenuItem(Loc.T("1 Блокувати пристрої"));
            mBlock.Click += delegate { DoBlock(); };
            _menu.Items.Add(mBlock);

            ToolStripMenuItem mUnblock = new ToolStripMenuItem(Loc.T("2 Розблокувати пристрої"));
            mUnblock.Click += delegate { DoUnblock(); };
            _menu.Items.Add(mUnblock);

            ToolStripMenuItem mAdd = new ToolStripMenuItem(Loc.T("3 Додати пристрій"));
            mAdd.Click += delegate { DoAddDevice(); };
            _menu.Items.Add(mAdd);

            ToolStripMenuItem mRemove = new ToolStripMenuItem(Loc.T("4 Видалити пристрій із WHITELIST..."));
            mRemove.Click += delegate { DoRemoveDevice(); };
            _menu.Items.Add(mRemove);

            ToolStripMenuItem mExport = new ToolStripMenuItem(Loc.T("5 Експортувати WHITELIST..."));
            mExport.Click += delegate { DoExport(); };
            _menu.Items.Add(mExport);

            ToolStripMenuItem mImport = new ToolStripMenuItem(Loc.T("6 Імпортувати WHITELIST..."));
            mImport.Click += delegate { DoImport(); };
            _menu.Items.Add(mImport);

            ToolStripMenuItem mView = new ToolStripMenuItem(Loc.T("0 Переглянути WHITELIST..."));
            mView.Click += delegate { DoViewWhitelist(); };
            _menu.Items.Add(mView);

            // Галочка J - это журнал КОПИРОВАНИЯ (про файлы). Журнал подключений
            // включается сам при установке службы мониторинга (пункт 7) и
            // выключается её удалением, поэтому в этом пункте его нет.
            // При включении здесь же задаётся дополнительное сочетание -
            // второе на ту же функцию, что и Ctrl+Alt+O.
            _miJournal = new ToolStripMenuItem(Loc.T("J Вести журнал копіювання"));
            _miJournal.Click += delegate { DoToggleJournal(); };
            _menu.Items.Add(_miJournal);

            _menu.Items.Add(new ToolStripSeparator());

            _miSvcInstall = new ToolStripMenuItem(Loc.T("7 Встановити службу"));
            _miSvcInstall.Click += delegate { DoInstallService(); };
            _menu.Items.Add(_miSvcInstall);

            _miSvcRemove = new ToolStripMenuItem(Loc.T("8 Видалити службу"));
            _miSvcRemove.Click += delegate { DoRemoveService(); };
            _menu.Items.Add(_miSvcRemove);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem mUninstall = new ToolStripMenuItem(Loc.T("9 Видалити застосунок..."));
            mUninstall.Click += delegate { DoUninstall(); };
            _menu.Items.Add(mUninstall);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem mDelPass = new ToolStripMenuItem(Loc.T("Видалити пароль"));
            mDelPass.Click += delegate { DoRemovePassword(); };
            _menu.Items.Add(mDelPass);

            _menu.Items.Add(new ToolStripSeparator());

            _menu.Items.Add(BuildLanguageMenu());
            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem mExit = new ToolStripMenuItem(Loc.T("Вихід"));
            mExit.Click += delegate { this.ExitThread(); };
            _menu.Items.Add(mExit);

            _icon.ContextMenuStrip = _menu;
            RefreshServiceMenu();
            RefreshJournalMenu();
        }

        // Подменю выбора языка интерфейса. Доступно и администратору, и
        // обычному пользователю (выбор хранится в HKCU для этого
        // пользователя и применяется после перезапуска трея). Названия
        // языков показываются на самих языках и не переводятся.
        private ToolStripMenuItem BuildLanguageMenu()
        {
            ToolStripMenuItem m = new ToolStripMenuItem("Мова / Language");
            ToolStripMenuItem mUk = new ToolStripMenuItem("Українська");
            ToolStripMenuItem mEn = new ToolStripMenuItem("English");
            mUk.Checked = Loc.Current == LanguageSettings.Ukrainian;
            mEn.Checked = Loc.Current == LanguageSettings.English;
            mUk.Click += delegate { ChooseLanguage(LanguageSettings.Ukrainian); };
            mEn.Click += delegate { ChooseLanguage(LanguageSettings.English); };
            m.DropDownItems.Add(mUk);
            m.DropDownItems.Add(mEn);
            return m;
        }

        private void ChooseLanguage(string lang)
        {
            if (lang == Loc.Current) return;
            if (!LanguageSettings.SetUserLanguage(lang))
            {
                MessageBox.Show(
                    "Не вдалося зберегти вибір мови в реєстрі.",
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string msg = (lang == LanguageSettings.English)
                ? "Language will change after the tray restarts. Restart now?"
                : "Мову буде змінено після перезапуску трея. Перезапустити зараз?";
            if (MessageBox.Show(msg, Program.Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                RestartTray();
            }
        }

        // Перезапуск трея: новый процесс стартует с небольшой задержкой,
        // чтобы старый успел освободить мьютекс одиночного экземпляра.
        private void RestartTray()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c ping -n 3 127.0.0.1 >nul & start \"\" \"" +
                    Application.ExecutablePath + "\"";
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch
            {
            }
            this.ExitThread();
        }

        private void RefreshServiceMenu()
        {
            bool installed = ServiceManager.IsInstalled();
            if (_miSvcInstall != null)
                _miSvcInstall.Enabled = !installed;
            if (_miSvcRemove != null)
                _miSvcRemove.Enabled = installed;
        }

        // Пункт J показывает текущее состояние и обновляется после
        // переключения (CheckOnClick не используем: если запись настройки
        // не удалась, галочка врёт).
        private void RefreshJournalMenu()
        {
            if (_miJournal == null) return;
            bool on = JournalSettings.IsFilesEnabled();
            string extra = HotkeyStore.GetText2();
            _miJournal.Checked = on;
            _miJournal.Text = Loc.T("J Вести журнал копіювання ") +
                (on ? Loc.T("(увімкнено") + (string.IsNullOrEmpty(extra) ? "" : ", " + extra) + ")"
                     : Loc.T("(вимкнено)"));
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
            string s = Loc.T("СТАТУС: ");
            s += _blocked ? Loc.T("БЛОКУВАННЯ УВІМКНЕНО") : Loc.T("БЛОКУВАННЯ ВИМКНЕНО");
            // Для не-администратора whitelist.dat недоступен (ACL/DPAPI) -
            // счётчик не показываем.
            if (Program.IsAdministrator())
            {
                List<DeviceEntry> wl = UsbMonitor.GetWhitelist();
                s += Loc.T(" | ДОЗВОЛЕНО: ") + wl.Count.ToString(CultureInfo.InvariantCulture);
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
                    Loc.T("Блокування увімкнено. Чужі USB-накопичувачі залишатимуться без літери диска."),
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Помилка блокування:\n") + ex.Message,
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =============================================================
        // Пункт 2. Разблокировать
        // =============================================================
        private void DoUnblock()
        {
            if (!EnsureAdmin()) return;
            if (!EnsurePassword()) return;
            try
            {
                PolicyManager.SetBlocked(false);
                _blocked = false;
                MountUtil.RestoreAll();
                int remounted = MountUtil.RestoreUnmounted();
                RunScan();
                RefreshStatus();
                _icon.ShowBalloonTip(3000, Program.Title,
                    Loc.T("Блокування вимкнено. Томів відновлено: ") + remounted +
                    Loc.T(". Усі USB-накопичувачі доступні."),
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Помилка розблокування:\n") + ex.Message,
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
            if (!EnsurePassword()) return;

            List<StorageDevice> devices = BuildAddDeviceList();
            if (devices.Count == 0)
            {
                MessageBox.Show(Loc.T("Немає USB-накопичувачів.\n") +
                                Loc.T("Підключіть накопичувач і повторіть."),
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

                // Проверка дубликата: тот же накопитель уже разрешён -
                // вторая запись не добавляется (одна запись = одно устройство).
                int dupIndex = WhitelistRules.Find(current, e);
                if (dupIndex >= 0)
                {
                    MessageBox.Show(DuplicateMessage(e, current[dupIndex]),
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
                    MessageBox.Show(Loc.T("Не вдалося зберегти whitelist:\n") + ex.Message,
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
                MessageBox.Show(Loc.T("Додано пристроїв: ") + added.Count.ToString(CultureInfo.InvariantCulture) + "\n" +
                                Loc.T("Дозволено до монтування: ") + mounted.ToString(CultureInfo.InvariantCulture) + "\n" +
                                Loc.T("Накопичувач дозволено і він отримає літеру диска автоматично."),
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // Сообщение о дубликате при добавлении: что уже есть в whitelist
        // (имя, USB ID, HardwareID диска, серийник, дата) и что делать.
        private static string DuplicateMessage(DeviceEntry added, DeviceEntry exists)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Loc.T("Цей накопичувач уже є в whitelist - повторно додавати не потрібно."));
            sb.AppendLine();
            sb.AppendLine(Loc.T("У whitelist уже записано:"));

            string name = string.IsNullOrEmpty(exists.Name) ? Loc.T("(без імені)") : exists.Name;
            sb.AppendLine(Loc.T("  Ім'я пристрою: ") + name);
            if (!string.IsNullOrEmpty(exists.UsbId))
                sb.AppendLine(Loc.T("  Тип пристрою (ID): ") + exists.UsbId);
            if (!string.IsNullOrEmpty(exists.DiskId))
                sb.AppendLine(Loc.T("  Модель пристрою: ") + exists.DiskId);
            if (!string.IsNullOrEmpty(exists.Serial))
                sb.AppendLine(Loc.T("  Серійний номер: ") + exists.Serial);
            else
                sb.AppendLine(Loc.T("  Серійний номер: не визначено (запис дозволяє всю модель ") +
                              (string.IsNullOrEmpty(exists.UsbId) ? "" : exists.UsbId) + ")");
            if (exists.AddedAt != default(DateTime))
                sb.AppendLine(Loc.T("  Додано: ") + exists.AddedAt.ToString("yyyy-MM-dd HH:mm"));

            if (added != null && !string.IsNullOrEmpty(added.Serial) &&
                !string.Equals(WhitelistRules.NormSerial(added.Serial),
                               WhitelistRules.NormSerial(exists.Serial), StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine();
                sb.AppendLine(Loc.T("Увага: серійні номери відрізняються (") +
                              (string.IsNullOrEmpty(added.Serial) ? Loc.T("не визначено") : added.Serial) +
                              Loc.T(" і ") + (string.IsNullOrEmpty(exists.Serial) ? Loc.T("не визначено") : exists.Serial) +
                              Loc.T("), а збіглися HardwareID диска або модель USB. Якщо це ІНШИЙ накопичувач - ") +
                              Loc.T("спочатку видаліть старий запис (пункт «4 Видалити пристрій із WHITELIST»), ") +
                              Loc.T("після чого додайте цей заново."));
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine(Loc.T("Накопичувач уже дозволено. Щоб внести зміни, спочатку видаліть ") +
                              Loc.T("старий запис (пункт «4 Видалити пристрій із WHITELIST»)."));
            }
            return sb.ToString();
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
                sd.Model = Loc.T("(заблоковано)");
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
            if (!EnsurePassword()) return;
            try
            {
                List<DeviceEntry> wl = UsbMonitor.GetWhitelist();
                if (wl.Count == 0)
                {
                    MessageBox.Show(Loc.T("WHITELIST порожній - видаляти нічого."),
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
                        Loc.T("Видалити з WHITELIST і заблокувати?\n\n") + q.ToString(),
                        Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                    != DialogResult.Yes)
                    return;

                // Удаляются выбранные записи и их дубликаты (тот же накопитель)
                HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DeviceEntry e in selected) keys.Add(WhitelistRules.EntryKey(e));
                List<DeviceEntry> result = new List<DeviceEntry>();
                foreach (DeviceEntry e in wl)
                {
                    if (!keys.Contains(WhitelistRules.EntryKey(e)))
                        result.Add(e);
                }

                try
                {
                    WhitelistStore.Save(result);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Loc.T("Не вдалося зберегти whitelist:\n") + ex.Message,
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
                    Loc.T("Видалено з WHITELIST: ") +
                    selected.Count.ToString(CultureInfo.InvariantCulture) + "\n" +
                    Loc.T("Заблоковано зараз: ") +
                    blockedNow.ToString(CultureInfo.InvariantCulture) + "\n\n" +
                    (_blocked
                        ? Loc.T("Пристрій більше не дозволено і блокуватиметься.")
                        : Loc.T("Увага: загальне блокування ВИМКНЕНО.\n") +
                          Loc.T("Підключений накопичувач розмонтовано, але щоб він\n") +
                          Loc.T("блокувався і надалі, увімкніть пункт «1 Блокувати пристрої».")),
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Помилка видалення пристрою:\n") + ex.Message,
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
            string s = string.IsNullOrEmpty(e.Name) ? Loc.T("(без імені)") : e.Name;
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
                dlg.Title = Loc.T("Експорт WHITELIST (перенос на інший комп'ютер)");
                dlg.Filter = Loc.T("UsbBlock whitelist (*.wlb)|*.wlb|Усі файли (*.*)|*.*");
                dlg.FileName = "usb_whitelist.wlb";
                dlg.DefaultExt = "wlb";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                try
                {
                    PortableWhitelist.Export(
                        UsbMonitor.GetWhitelist(), dlg.FileName);
                    MessageBox.Show(
                        Loc.T("WHITELIST експортовано:\n") + dlg.FileName +
                        Loc.T("\n\nПеренесіть .wlb на інший комп'ютер і виберіть «Імпортувати WHITELIST»."),
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Loc.T("Помилка експорту:\n") + ex.Message,
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
            if (!EnsurePassword()) return;

            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = Loc.T("Імпорт WHITELIST");
                dlg.Filter = Loc.T("UsbBlock whitelist (*.wlb)|*.wlb|Усі файли (*.*)|*.*");
                if (dlg.ShowDialog() != DialogResult.OK) return;

                List<DeviceEntry> raw;
                try
                {
                    raw = PortableWhitelist.Import(dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Loc.T("Не вдалося прочитати файл:\n") + ex.Message,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Дедупликация файла импорта: одинаковые накопители внутри
                // одного .wlb не добавляются (файл мог быть собран вручную
                // или старой версией без проверки).
                int dupInFile;
                List<DeviceEntry> imported = WhitelistRules.Dedupe(raw, out dupInFile);
                if (imported.Count == 0)
                {
                    MessageBox.Show(Loc.T("Файл не містить жодного пристрою - імпорт скасовано."),
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Текущий whitelist тоже чистим: в нём могли остаться
                // дубликаты от старой версии (или ручной правки файла).
                int dupCurrent;
                List<DeviceEntry> current = WhitelistRules.Dedupe(
                    UsbMonitor.GetWhitelist(), out dupCurrent);

                StringBuilder msg = new StringBuilder();
                msg.AppendLine(Loc.T("Імпортовано записів (пристроїв): ") +
                    imported.Count.ToString(CultureInfo.InvariantCulture) + ".");
                if (dupInFile > 0)
                    msg.AppendLine(Loc.T("Дублікатів у файлі відкинуто: ") +
                        dupInFile.ToString(CultureInfo.InvariantCulture) + ".");
                msg.AppendLine();
                msg.AppendLine(Loc.T("Поточний whitelist: ") +
                    current.Count.ToString(CultureInfo.InvariantCulture) + Loc.T(" записів."));
                if (dupCurrent > 0)
                    msg.AppendLine(Loc.T("Дублікатів у поточному whitelist буде прибрано: ") +
                        dupCurrent.ToString(CultureInfo.InvariantCulture) + ".");
                msg.AppendLine();
                msg.AppendLine(Loc.T("Як застосувати імпортовані дані?"));
                msg.AppendLine();
                msg.AppendLine(Loc.T("  «Так»       - перезаписати (замінити) поточний whitelist"));
                msg.AppendLine(Loc.T("  «Ні»        - додати до поточного whitelist (об'єднати)"));
                msg.AppendLine(Loc.T("  «Скасувати» - скасувати"));
                DialogResult action = MessageBox.Show(msg.ToString(), Program.Title,
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (action == DialogResult.Cancel) return;

                // Дубликаты не попадают в результат ни при замене, ни при
                // объединении; при объединении считаем, сколько записей
                // импорта реально добавилось, а сколько уже было.
                int addedCount = 0;
                List<DeviceEntry> result;
                if (action == DialogResult.Yes)
                {
                    result = imported;
                    addedCount = imported.Count;
                }
                else
                {
                    result = MergeWhitelists(current, imported);
                    addedCount = result.Count - current.Count;
                }
                int skipped = imported.Count - addedCount;

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
                        MessageBox.Show(Loc.T("WHITELIST замінено: ") +
                            result.Count.ToString(CultureInfo.InvariantCulture) + Loc.T(" пристроїв.") +
                            DupReport(dupInFile, 0, 0),
                            Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show(Loc.T("WHITELIST об'єднано:\n") +
                            Loc.T("було ") + current.Count.ToString(CultureInfo.InvariantCulture) +
                            Loc.T(", додано ") +
                            addedCount.ToString(CultureInfo.InvariantCulture) +
                            Loc.T(", стало ") + result.Count.ToString(CultureInfo.InvariantCulture) +
                            Loc.T(" пристроїв.") + DupReport(dupInFile, skipped, dupCurrent),
                            Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Loc.T("Помилка збереження:\n") + ex.Message,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // Отчёт о дедупликации после импорта (пустая строка, если дублей нет):
        // inFile - дубликаты внутри файла, already - записи импорта, которые
        // уже были в whitelist, inCurrent - дубликаты в прежнем списке.
        private static string DupReport(int inFile, int already, int inCurrent)
        {
            if (inFile <= 0 && already <= 0 && inCurrent <= 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine();
            if (inFile > 0)
                sb.AppendLine(Loc.T("Дублікатів у файлі відкинуто: ") +
                    inFile.ToString(CultureInfo.InvariantCulture) + ".");
            if (already > 0)
                sb.AppendLine(Loc.T("Пропущено (накопичувач уже був у whitelist): ") +
                    already.ToString(CultureInfo.InvariantCulture) + ".");
            if (inCurrent > 0)
                sb.AppendLine(Loc.T("Дублікатів у попередньому whitelist прибрано: ") +
                    inCurrent.ToString(CultureInfo.InvariantCulture) + ".");
            return sb.ToString();
        }

        // Объединение двух whitelist без дубликатов (WhitelistRules.Merge):
        // сохраняет существующие записи, добавляет только те, которых
        // ещё нет; дубликаты внутри обоих списков также отбрасываются.
        internal static List<DeviceEntry> MergeWhitelists(
            List<DeviceEntry> existing, List<DeviceEntry> incoming)
        {
            return WhitelistRules.Merge(existing, incoming);
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
                MessageBox.Show(Loc.T("Помилка перегляду whitelist:\n") + ex.Message,
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =============================================================
        // Пункт J. Ведение журнала копирования
        // Журнал подключений тут не при чём: он включается установкой
        // службы. Этот пункт - про файлы на разрешённых накопителях.
        // По умолчанию выключено: включается только по желанию
        // администратора. Пароль не спрашивается - запись в журнал не
        // меняет доступ к накопителям, а включает её администратор
        // машины (окно просмотра журнала всё равно закрыто ACL).
        // =============================================================
        private void DoToggleJournal()
        {
            if (!EnsureAdmin()) return;
            bool was = JournalSettings.IsFilesEnabled();
            bool want = !was;

            // Включение просим подтвердить дополнительным сочетанием: по
            // умолчанию журнал открывается клавишами Ctrl+Alt+O, а второе
            // сочетание - уже по желанию. Отмена в этом окне отменяет и
            // включение журнала: нельзя оставить «галочку стоит, а ничего
            // не пишется».
            string extra = null;
            if (want)
            {
                extra = AskHotkeyExtra();
                if (extra == null) return;
            }

            if (!JournalSettings.SetFilesEnabled(want))
            {
                MessageBox.Show(Loc.T("Не вдалося змінити налаштування журналу:\n") +
                        (JournalSettings.LastError ?? Loc.T("немає прав на запис у реєстр")),
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                RefreshJournalMenu();
                return;
            }
            if (want)
            {
                // Пользователь может сознательно отказаться от
                // дополнительной клавиши - тогда просто не пишем её.
                if (!string.IsNullOrEmpty(extra) && !HotkeyStore.SetText2(extra))
                {
                    // Настройка записалась, а сочетание - нет: галочка
                    // должна соответствовать действительности, поэтому
                    // возвращаем всё как было.
                    JournalSettings.SetFilesEnabled(false);
                    MessageBox.Show(Loc.T("Не вдалося зберегти додаткову комбінацію:\n") +
                            (HotkeyStore.LastError ?? Loc.T("немає прав на запис у реєстр")),
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    RefreshJournalMenu();
                    return;
                }
            }
            else
            {
                HotkeyStore.ClearExtra();
            }
            // Сочетание добавилось или снялось - регистрацию перечитываем.
            if (_hwnd != null)
            {
                HotkeyStore.Unregister(_hwnd.Handle);
                HotkeyStore.Register(_hwnd.Handle);
            }
            RefreshJournalMenu();
            _icon.ShowBalloonTip(3000, Program.Title,
                want
                    ? Loc.T("Журнал копіювання УВІМКНЕНО.\n") +
                      Loc.T("Записуються: скопійовані, змінені, видалені\n") +
                      Loc.T("і перейменовані файли на дозволених накопичувачах\n") +
                      Loc.T("(без вмісту файлів і без імені користувача).\n") +
                      Loc.T("Зберігається 10 файлів по 5000 записів.\n") +
                      Loc.T("Журнал відкривається клавішами ") +
                      HotkeyStore.DefaultText +
                      (string.IsNullOrEmpty(extra) ? "" : Loc.T(" і ") + extra) + "."
                    : Loc.T("Журнал копіювання ВИМКНЕНО.\n") +
                      Loc.T("Додаткову комбінацію знято, основну ") +
                      HotkeyStore.DefaultText + Loc.T(" працює.\n") +
                      Loc.T("Уже накопичені записи лишаються - їх можна відкрити."),
                ToolTipIcon.Info);
        }

        // =============================================================
        // Пункт 7. Установить службу мониторинга
        // =============================================================
        private void DoInstallService()
        {
            if (!EnsureAdmin()) return;
            if (ServiceManager.IsInstalled())
            {
                // Служба уже установлена: не выходим молча, а восстанавливаем
                // защиту и обновляем задачи Планировщика (нужно после
                // обновления exe, чтобы включить автоперезапуск и задачу
                // контроля, если их не было).
                ServiceManager.ApplyRecovery();
                string rep = TrayTask.Create();
                RefreshServiceMenu();
                _icon.ShowBalloonTip(3000, Program.Title,
                    rep == null
                        ? Loc.T("Службу вже встановлено: налаштування оновлено\n") +
                          Loc.T("(автоперезапуск і задачі відновлено).")
                        : Loc.T("Службу вже встановлено, але частину налаштувань\n") +
                          Loc.T("відновити не вдалося:\n") + rep,
                    rep == null ? ToolTipIcon.Info : ToolTipIcon.Warning);
                return;
            }
            // Пароль защиты спрашивается ДО установки службы: отмена в окне
            // пароля отменяет и установку службы.
            if (!AskSetPassword()) return;

            // Комбинацию клавиш журнала не спрашиваем: основная всегда одна
            // и та же (Ctrl+Alt+O), дополнительную задаёт галочка пункта J.
            // Журнал подключений включается вместе со службой: пока службы нет,
            // вести его некому. Журнал копирования - отдельно, галочкой J.

            try
            {
                string err = ServiceManager.Install();
                if (err != null)
                {
                    MessageBox.Show(Loc.T("Помилка:\n") + err,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                // Значок в трее и уведомления ДЛЯ ВСЕХ пользователей: задача
                // Планировщика, запускающая трей при входе любого пользователя.
                string tErr = TrayTask.Create();
                RefreshServiceMenu();

                // Журнал подключений и комбинация клавиш включаются ПОСЛЕ установки
                // службы: она должна существовать ровно тогда, когда журнал
                // ведёт служба, и сниматься вместе с ней (пункт 8).
                bool journalOk = JournalSettings.SetEnabled(true);
                bool hotkeyOk = HotkeyStore.SetDefault();
                if (hotkeyOk)
                {
                    // Регистрирует трей этого процесса; у остальных
                    // пользователей подхватит уведомитель при следующем входе
                    // (или сам уведомитель текущей сессии).
                    if (_hwnd != null) HotkeyStore.Register(_hwnd.Handle);
                }

                if (tErr != null)
                {
                    MessageBox.Show(
                        Loc.T("Службу встановлено, але не вдалося налаштувати значок у треї\n") +
                        Loc.T("для інших користувачів:\n") + tErr,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _icon.ShowBalloonTip(3000, Program.Title,
                    Loc.T("Службу моніторингу встановлено та запущено від імені SYSTEM.\n") +
                    Loc.T("Журнал підключень УВІМКНЕНО автоматично.\n") +
                    Loc.T("Журнал копіювання вмикається галочкою пункту J.\n") +
                    Loc.T("Значок у треї та сповіщення з'являться в усіх користувачів\n") +
                    Loc.T("після перезавантаження. Сповіщення працюють незалежно від\n") +
                    Loc.T("вигрузки значка з трея.\n") +
                    Loc.T("Служба та сповіщувач перезапускаються автоматично, якщо їх завершити.\n") +
                    (hotkeyOk
                        ? Loc.T("Журнал відкривається клавішами ") + HotkeyStore.DefaultText + "."
                        : Loc.T("Увага: комбінацію клавіш журналу зберегти не вдалося") +
                          (HotkeyStore.LastError != null ? " (" + HotkeyStore.LastError + ")" : "") + ".") +
                    (journalOk
                        ? ""
                        : Loc.T("\nУвага: журнал підключень увімкнути не вдалося") +
                          (JournalSettings.LastError != null ? " (" + JournalSettings.LastError + ")" : "") + "."),
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Помилка:\n") + ex.Message,
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // =============================================================
        // Пункт 8. Удалить службу мониторинга
        // =============================================================
        private void DoRemoveService()
        {
            if (!EnsureAdmin()) return;
            if (!EnsurePassword()) return;
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
                    MessageBox.Show(Loc.T("Помилка:\n") + err,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                // Убираем и значок в трее для всех пользователей
                // (задача Планировщика).
                TrayTask.Delete();

                // Службы нет - журнал подключений вести некому: выключаем его.
                // Журнал копирования - галочка пункта J, её состояние не
                // трогаем (это отдельная настройка пользователя).
                // Комбинации клавиш тоже снимаются: без службы основная
                // клавиша всё равно не нужна, а дополниная назначалась
                // вместе с копированием.
                JournalSettings.SetEnabled(false);
                HotkeyStore.Clear();
                if (_hwnd != null) HotkeyStore.Unregister(_hwnd.Handle);

                RefreshServiceMenu();
                RefreshJournalMenu();
                _icon.ShowBalloonTip(3000, Program.Title,
                    Loc.T("Службу моніторингу видалено.\n") +
                    Loc.T("Журнал підключень ВИМКНЕНО.\n") +
                    Loc.T("Значок у треї в користувачів буде прибрано після перезавантаження."),
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Помилка:\n") + ex.Message,
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
            if (!EnsurePassword()) return;

            if (MessageBox.Show(
                    Loc.T("Видалити застосунок?\n\r") +
                    Loc.T("Буде видалено:\n") +
                    Loc.T("  - службу моніторингу (якщо встановлено)\n") +
                    Loc.T("  - задачу значків у треї для користувачів (якщо була)\n") +
                    Loc.T("  - захищену копію в \"Program Files\\USB_Block\"\n") +
                    Loc.T("  - політику блокування USB-пристроїв\n") +
                    Loc.T("  - сам файл програми"),
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
                        Loc.T("Видалити збережені дані?\n") +
                        Loc.T("  - whitelist (список дозволених пристроїв)\n") +
                        Loc.T("  - пароль захисту меню\n") +
                        Loc.T("  - журнал підключень і копіювань\n") +
                        Loc.T("Без підтвердження ці файли лишаться на місці"),
                        Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    deleteData = true;
                }
            }
            if (deleteData)
            {
                // Журнал удаляем явно: на всякий случай, даже если папка
                // почему-то не удалилась целиком.
                UsbJournal.Clear();
                TryDeleteDir(StorePaths.Directory);
            }

            // 4б) состояние наблюдения, горячая клавиша и включение журнала
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(
                    @"SOFTWARE\USB_Block\JournalPresence", false);
            }
            catch
            {
            }
            HotkeyStore.Clear();
            JournalSettings.Clear();

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

            MessageBox.Show(Loc.T("Програму видалено."),
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
            MessageBox.Show(Loc.T("Потрібні права адміністратора."),
                Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // =============================================================
        // Пароль защиты опасных пунктов меню
        // =============================================================
        private const int PasswordAttempts = 3;

        // Перед пунктами 2, 3, 4, 6, 8, 9. Если пароль не установлен -
        // пункт выполняется без запроса.
        private bool EnsurePassword()
        {
            if (!AdminPassword.IsSet()) return true;

            for (int attempt = 1; attempt <= PasswordAttempts; attempt++)
            {
                using (PasswordForm f = new PasswordForm(Loc.T("Пароль захисту"),
                    Loc.T("Введіть пароль для доступу до цього пункту меню:"), false))
                {
                    if (f.ShowDialog() != DialogResult.OK) return false;
                    if (AdminPassword.Verify(f.Password)) return true;
                }

                if (attempt < PasswordAttempts)
                {
                    MessageBox.Show(Loc.T("Невірний пароль.\nЗалишилося спроб: ") +
                        (PasswordAttempts - attempt).ToString(CultureInfo.InvariantCulture) + ".",
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            MessageBox.Show(Loc.T("Перевищено число спроб введення пароля.\nПункт не виконано."),
                Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Пункт J (включение журнала копирования): задать ДОПОЛНИТЕЛЬНУЮ
        // комбинацию - второе сочетание на ту же функцию, что и основная
        // Ctrl+Alt+O. Основную спрашивать негде: она одинаковая на всех
        // машинах и включается вместе со службой.
        // Возвращает комбинацию для сохранения; null - пользователь отказался
        // (включение журнала отменяется). Пустая строка - согласился, но
        // задавать ничего не хочет: тогда дополнительной клавиши не будет,
        // журнал откроется основной.
        private string AskHotkeyExtra()
        {
            string current = HotkeyStore.GetText2();
            if (string.IsNullOrEmpty(current)) current = HotkeyStore.GetText();
            while (true)
            {
                using (HotkeyCaptureForm form = new HotkeyCaptureForm(current))
                {
                    if (form.ShowDialog() != DialogResult.OK) return null;
                    current = form.Hotkey;
                }
                if (!string.IsNullOrEmpty(current)) return current;

                DialogResult dr = MessageBox.Show(
                    Loc.T("Додаткову комбінацію не задано.\n\n") +
                    Loc.T("Журнал копіювання буде увімкнено, і відкривати його можна\n") +
                    Loc.T("лише основною комбінацією ") + HotkeyStore.DefaultText + ".\n\n" +
                    Loc.T("(«Ні» - повернутися до вибору комбінації)"),
                    Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr == DialogResult.Yes) return string.Empty;
            }
        }

        private bool AskSetPassword()
        {
            StringBuilder sb = new StringBuilder();
            if (AdminPassword.IsSet())
            {
                sb.AppendLine(Loc.T("Пароль захисту вже встановлено."));
                sb.AppendLine();
                sb.AppendLine(Loc.T("Встановити новий пароль зараз?"));
                sb.AppendLine(Loc.T("«Ні» - лишити попередній пароль."));
            }
            else
            {
                sb.AppendLine(Loc.T("Встановити пароль для захисту небезпечних пунктів меню?"));
                sb.AppendLine();
                sb.AppendLine(Loc.T("Пароль запитуватиметься при:"));
                sb.AppendLine(Loc.T("  2 Розблокувати пристрої"));
                sb.AppendLine(Loc.T("  3 Додати пристрій"));
                sb.AppendLine(Loc.T("  4 Видалити пристрій із WHITELIST"));
                sb.AppendLine(Loc.T("  6 Імпортувати WHITELIST"));
                sb.AppendLine(Loc.T("  8 Видалити службу"));
                sb.AppendLine(Loc.T("  9 Видалити застосунок"));
                sb.AppendLine();
                sb.AppendLine(AdminPassword.PolicyHint());
                sb.AppendLine(Loc.T("Запам'ятайте пароль: він зберігається лише у вигляді хеша."));
                sb.AppendLine(Loc.T("Забутий пароль видаляється пунктом «Видалити пароль»."));
            }

            if (MessageBox.Show(sb.ToString(), Program.Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return true;

            using (PasswordForm f = new PasswordForm(Loc.T("Встановлення пароля"),
                Loc.T("Задайте пароль для пунктів 2, 3, 4, 6, 8, 9.\n") +
                    AdminPassword.PolicyHint(), true,
                Loc.T("Новий пароль:")))
            {
                if (f.ShowDialog() != DialogResult.OK) return false;
                try
                {
                    AdminPassword.Set(f.Password);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Loc.T("Не вдалося зберегти пароль:\n") + ex.Message,
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }

            MessageBox.Show(Loc.T("Пароль встановлено.\n\n") +
                Loc.T("Його запитуватиметься в пунктах:\n") +
                Loc.T("  2 Розблокувати пристрої\n") +
                Loc.T("  3 Додати пристрій\n") +
                Loc.T("  4 Видалити пристрій із WHITELIST\n") +
                Loc.T("  6 Імпортувати WHITELIST\n") +
                Loc.T("  8 Видалити службу\n") +
                Loc.T("  9 Видалити застосунок"),
                Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        // =============================================================
        // Пункт меню «Удалить пароль»
        // Пароль удаляется либо вводом текущего пароля, либо кодовым словом
        // (если пароль забыт). Защиты с паролём после этого не остаётся.
        // =============================================================
        private void DoRemovePassword()
        {
            if (!EnsureAdmin()) return;

            if (!AdminPassword.IsSet())
            {
                MessageBox.Show(Loc.T("Пароль не встановлено - видаляти нічого."),
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Loc.T("Видалити пароль захисту?"));
            sb.AppendLine();
            sb.AppendLine(Loc.T("  «Так»       - підтвердити поточним паролем"));
            sb.AppendLine(Loc.T("  «Ні»        - підтвердити кодовим словом (якщо пароль забуто)"));
            sb.AppendLine(Loc.T("  «Скасувати» - нічого не робити"));
            sb.AppendLine();
            sb.AppendLine(Loc.T("Після видалення пункти 2, 3, 4, 6, 8, 9 будуть"));
            sb.AppendLine(Loc.T("доступні без пароля."));
            DialogResult how = MessageBox.Show(sb.ToString(), Program.Title,
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (how == DialogResult.Cancel) return;

            bool confirmed = how == DialogResult.Yes
                ? AskCurrentPassword()
                : AskRecoveryCode();
            if (!confirmed) return;

            if (!AdminPassword.Clear())
            {
                MessageBox.Show(Loc.T("Не вдалося видалити файл пароля:\n") +
                        AdminPassword.FilePath,
                    Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            MessageBox.Show(Loc.T("Пароль видалено.\n\n") +
                Loc.T("Пункти 2, 3, 4, 6, 8, 9 тепер доступні без пароля."),
                Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Текущий пароль (как в EnsurePassword, но без требования, чтобы он
        // был установлен).
        private bool AskCurrentPassword()
        {
            for (int attempt = 1; attempt <= PasswordAttempts; attempt++)
            {
                using (PasswordForm f = new PasswordForm(Loc.T("Видалення пароля"),
                    Loc.T("Введіть поточний пароль:"), false, Loc.T("Пароль:")))
                {
                    if (f.ShowDialog() != DialogResult.OK) return false;
                    if (AdminPassword.Verify(f.Password)) return true;
                }

                if (attempt < PasswordAttempts)
                {
                    MessageBox.Show(Loc.T("Невірний пароль.\nЗалишилося спроб: ") +
                        (PasswordAttempts - attempt).ToString(CultureInfo.InvariantCulture) + ".",
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            MessageBox.Show(Loc.T("Перевищено число спроб введення пароля.\nПароль не видалено."),
                Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Кодовое слово - для случая, когда текущий пароль забыт.
        private bool AskRecoveryCode()
        {
            for (int attempt = 1; attempt <= PasswordAttempts; attempt++)
            {
                using (PasswordForm f = new PasswordForm(Loc.T("Видалення пароля"),
                    Loc.T("Введіть кодове слово:"), false, Loc.T("Кодове слово:")))
                {
                    if (f.ShowDialog() != DialogResult.OK) return false;
                    if (AdminPassword.CheckCode(f.Password)) return true;
                }

                if (attempt < PasswordAttempts)
                {
                    MessageBox.Show(Loc.T("Невірне кодове слово.\nЗалишилося спроб: ") +
                        (PasswordAttempts - attempt).ToString(CultureInfo.InvariantCulture) + ".",
                        Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            MessageBox.Show(Loc.T("Перевищено число спроб введення кодового слова.\nПароль не видалено."),
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
                else
                {
                    // Обычный пользователь: блокировку он не применяет (это
                    // требует прав), но журнал копирования вести может - он
                    // дописывает в конец файла, на что ему выданы права.
                    // Без этого включённая галочка J молчала бы при входе
                    // не-админом.
                    RunJournalOnly();
                    CheckJournalWritable();
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

        // Один раз предупреждаем, если журнал копирования включён, а писать
        // в него нечем (права выдаёт первый запуск с правами администратора).
        private bool _journalWarned;

        private void CheckJournalWritable()
        {
            if (_journalWarned) return;
            if (string.IsNullOrEmpty(UsbJournalMonitor.WriteBlockedReason)) return;
            _journalWarned = true;
            _icon.ShowBalloonTip(6000, Program.Title,
                Loc.T("Журнал копіювання увімкнено, але записати в нього неможливо.\n") +
                UsbJournalMonitor.WriteBlockedReason + ".\n" +
                Loc.T("Запустіть програму з правами адміністратора або встановіть\n") +
                Loc.T("службу моніторингу (пункт 7)."),
                ToolTipIcon.Warning);
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

            // Журнал подключений и копирований. Работает и здесь, и в
            // службе, но по мьютексу в каждый момент пишет только один из
            // них: пока служба жива, журнал ведёт она.
            try { UsbJournalMonitor.RunOnce(false); }
            catch { }
        }

        // Журнал копирования при входе ОБЫЧНОГО пользователя. Блокировку
        // (снятие точек монтирования) и опрос WMI здесь не делаем - это
        // требует прав администратора. Только цикл журнала: он теперь умеет
        // писать без прав (дописывание в конец файла, см. UsbJournalRights).
        private void RunJournalOnly()
        {
            if (!JournalSettings.IsEnabled()) return;
            try { UsbJournalMonitor.RunOnce(false); }
            catch { }
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
                // Снимаем горячую клавишу до разрушения окна, иначе она
                // осталась бы висеть в системе до конца сеанса - с окном
                // трея её больше некому обрабатывать.
                try { HotkeyStore.Unregister(_hwnd.Handle); }
                catch { }
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

        // Самопроверка показа: показывает тестовое сообщение (окно с рамкой,
        // справа внизу) и ждёт, пока его закроют или оно закроется само.
        public static int TestPopup()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (NotifyPopup p = new NotifyPopup(
                    Loc.T("ТЕСТ сповіщення про блокування.\n\n") + Program.NotifyText +
                    Loc.T("\n\nЯкщо ви бачите це вікно в правому нижньому кутку екрана, показ працює."),
                    8000))
                {
                    p.ShowInTaskbar = false;
                    Application.Run(p);
                }
                return 0;
            }
            catch (Exception ex)
            {
                string msg = Loc.T("Не вдалося показати тестове сповіщення: ") + ex.Message;
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
            // Пока трей жив, горячую клавишу держит он. Этот процесс
            // регистрирует её "на всякий случай": пока трей работает,
            // RegisterHotKey вернёт отказ (комбинация в системе одна), а
            // после "Выход" из трея регистрация пройдёт - и журнал по-прежнему
            // открывается. Никакой координации между процессами не нужно.
            HiddenWindow hotkey = new HiddenWindow(null, NotifyService.HandleHotkey);
            try
            {
                hotkey.EnsureCreated();
                HotkeyStore.Register(hotkey.Handle);
            }
            catch
            {
            }

            _poll = new System.Windows.Forms.Timer();
            _poll.Interval = 2500;
            _poll.Tick += delegate { Poll(); };
            _poll.Start();
            Poll();
            Application.Run();
        }

        // Горячая клавиша в уведомителе: журнал открывает отдельный процесс
        // (от администратора - сразу, от обычного пользователя - с запросом
        // прав). Сам уведомитель журнал не показывает: он и так работает без
        // интерфейса и прав может не иметь.
        private static void HandleHotkey()
        {
            try
            {
                if (!Program.StartJournalViewer(!Program.IsAdministrator()))
                {
                    TraceLog(Loc.T("гаряча клавіша: не вдалося запустити перегляд журналу"));
                }
                else
                {
                    TraceLog(Loc.T("гаряча клавіша: запущено перегляд журналу (--journal)"));
                }
            }
            catch
            {
            }
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
                    TraceLog(Loc.T("лічильник попереду черги (last=") +
                        last.ToString(CultureInfo.InvariantCulture) + " > max=" +
                        max.ToString(CultureInfo.InvariantCulture) +
                        Loc.T("): витягую останню подію id=") +
                        newest.Id.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                foreach (NotifyStore.BlockEvent e in evs)
                {
                    if (e.Id > last) _queue.Enqueue(new QueueItem { Id = e.Id, Text = BuildText(e) });
                }
                TraceLog(Loc.T("у черзі показу ") + _queue.Count +
                    Loc.T(", нових з id>") + last.ToString(CultureInfo.InvariantCulture) +
                    Loc.T(" до ") + max.ToString(CultureInfo.InvariantCulture));
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
                TraceLog(Loc.T("ПОМИЛКА створення вікна: ") + ex.Message);
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
                TraceLog(Loc.T("показано сповіщення id=") +
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
                TraceLog(Loc.T("показ id=") + item.Id.ToString(CultureInfo.InvariantCulture));
                p.Show();
            }
            catch (Exception ex)
            {
                LastShowError = ex.Message;
                TraceLog(Loc.T("ПОМИЛКА показу: ") + ex.Message);
                _active = null;
                try { p.Dispose(); }
                catch { }
            }
        }

        private static string BuildText(NotifyStore.BlockEvent e)
        {
            return Program.NotifyText + "\n\n(" +
                NotifyStore.BlockDetail(e.Label) + ")";
        }
    }

    // Всплывающее окно-сообщение (рамка с закруглёнными углами, справа
    // внизу, закрывается само)
    public sealed class NotifyPopup : Form
    {
        // Радиус скругления углов окна и толщина рамки: рамка рисуется
        // GDI+ внутри клиентской области, а регион обрезает углы фона.
        private const int Corner = 10;
        private const int Border = 2;

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
            this.Font = SystemFonts.MessageBoxFont;
            this.AutoScaleDimensions = new SizeF(7F, 15F);   // метрики Segoe UI 9 пт (проектные)
            this.AutoScaleMode = AutoScaleMode.Font;
            this.BackColor = SystemColors.Control;
            // Скруглённые углы и рамка рисуются GDI+ целиком в OnPaint:
            // двойная буферизация и одна отрисовка без WM_ERASEBKGND,
            // иначе рамка и углы мигают при показе окна.
            this.SetStyle(ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);

            _textSize = TextRenderer.MeasureText(text, this.Font,
                new Size(350, int.MaxValue), TextFormatFlags.WordBreak);
            int w = Math.Max(200, _textSize.Width + 8);
            int h = Math.Max(40, _textSize.Height + 8);

            _lbl = new Label();
            _lbl.Text = text;
            _lbl.Size = new Size(w, h);
            _lbl.Font = this.Font;
            _lbl.ForeColor = SystemColors.ControlText;
            _lbl.Location = new Point(14, 12);
            this.Controls.Add(_lbl);

            this.Click += delegate { CloseSelf(); };
            _lbl.Click += delegate { CloseSelf(); };

            _close = new System.Windows.Forms.Timer();
            _close.Interval = closeMs;
            _close.Tick += delegate { CloseSelf(); };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Границы считаются ДО показа окна: при StartPosition=Manual
            // и Location, задававшемся только в OnShown, окно успевало
            // появиться в левом верхнем углу (DefaultLocation) - DWM
            // показывал этот кадр, и пользователь видел вспышку перед
            // переходом в правый нижний угол. OnLoad вызывается до того,
            // как окно станет видимым; TopOffset к этому моменту уже
            // установлен вызывающей стороной (ShowNext).
            ApplyBounds();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Повторный расчёт (идемпотентный, обычно ничего не меняется):
            // страхует от масштабирования по шрифту после загрузки.
            ApplyBounds();
            _close.Start();

            Action<long> shown = Displayed;
            if (shown != null && NotificationId >= 0)
            {
                try { shown(NotificationId); }
                catch { }
            }
        }

        // Размер по тексту, скруглённый регион и позиция в правом нижнем
        // углу рабочей области (с учётом TopOffset для стопки окон).
        private void ApplyBounds()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            this.ClientSize = new Size(_textSize.Width + 30, Math.Max(50, _textSize.Height + 24));
            // Скруглённые углы: регион по итоговой клиентской области.
            using (GraphicsPath path = RoundRect(
                new Rectangle(Point.Empty, this.ClientSize), Corner))
            {
                Region old = this.Region;
                this.Region = new Region(path);
                if (old != null) old.Dispose();
            }
            this.Location = new Point(wa.Right - this.Width - 12,
                wa.Bottom - this.Height - 12 - TopOffset);
        }

        // Рамка с закруглёнными углами: контур того же пути, что и регион
        // (PenAlignment.Inset - штрих идёт внутрь и не вылезает за край).
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle r = new Rectangle(0, 0,
                Math.Max(1, ClientSize.Width - 1),
                Math.Max(1, ClientSize.Height - 1));
            using (GraphicsPath path = RoundRect(r, Corner))
            using (Pen pen = new Pen(SystemColors.ControlDarkDark, Border))
            {
                pen.Alignment = PenAlignment.Inset;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(pen, path);
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
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
            if (this.Region != null)
            {
                this.Region.Dispose();
                this.Region = null;
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
        public static readonly string DisplayName = Loc.T("USB-блокування (моніторинг)");

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
                return Loc.T("недоступна (") + ex.Message + ")";
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
                    return Loc.T("Не вдалося оновити захищену копію для служби:\n") + copyErr;

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
                    return Loc.T("Не вдалося створити службу (код ") +
                        ret.ToString(CultureInfo.InvariantCulture) + ")";
                Start();
                ApplyRecovery();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // Настроить автоматический перезапуск службы средствами SCM: если
        // процесс службы завершится аварийно (в т.ч. его "Убить" в диспетчере
        // задач), Windows поднимет службу заново. Первый сбой - через 5 c,
        // последующие - через 30 c; счётчик сбоев сбрасывается раз в сутки.
        public static void ApplyRecovery()
        {
            try
            {
                string output;
                Exec.Run("sc.exe", "failure " + ServiceName +
                    " reset= 86400 actions= restart/5000/restart/30000/restart/30000",
                    out output);
            }
            catch
            {
            }
        }

        // Снять настройку перезапуска (перед удалением службы, чтобы она не
        // пыталась подняться снова).
        public static void ClearRecovery()
        {
            try
            {
                string output;
                Exec.Run("sc.exe",
                    "failure " + ServiceName + " reset= 0 actions= \"\"", out output);
            }
            catch
            {
            }
        }

        // Контроль службы (режим --ensure-service, запускается задачей от
        // SYSTEM раз в минуту): если службу остановили или отключили -
        // вернуть её в рабочее состояние.
        public static void EnsureRunning()
        {
            try
            {
                if (!IsInstalled()) return;
                try
                {
                    using (ManagementObject svc = new ManagementObject(
                        @"\\.\root\cimv2:Win32_Service.Name='" + ServiceName + "'"))
                    {
                        object startMode = svc["StartMode"];
                        if (startMode != null && !string.Equals(
                            startMode.ToString(), "Auto", StringComparison.OrdinalIgnoreCase))
                        {
                            ManagementBaseObject inParams = svc.GetMethodParameters("Change");
                            inParams["StartMode"] = "Automatic";
                            svc.InvokeMethod("Change", inParams, null);
                        }
                    }
                }
                catch
                {
                }
                Start();
            }
            catch
            {
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
                ClearRecovery();
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
                        return Loc.T("Не вдалося видалити службу (код ") +
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
        private int _healTicks;

        public UsbBlockService()
        {
            ServiceName = ServiceManager.ServiceName;
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            // Самоконтроль: при каждом запуске службы гарантируем настройку
            // автоматического перезапуска (SCM) и наличие задачи контроля
            // (--ensure-service). Это же служит миграцией для установок,
            // сделанных до появления защиты: служба сама создаёт свою задачу.
            try { ServiceManager.ApplyRecovery(); }
            catch { }
            try { TrayTask.CreateGuardTask(); }
            catch { }

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
                        EventLog.WriteEntry(ServiceManager.ServiceName,
                            Program.NotifyText + "\n\n" +
                            NotifyStore.BlockDetail(b.Label),
                            EventLogEntryType.Warning);
                    }
                    catch
                    {
                        // Журнал событий недоступен - не критично, уведомление
                        // уже поставлено в очередь выше.
                    }
                }

                // Журнал подключений и копирований. Если служба работает,
                // журнал ведёт она; если её остановили - подхватит трей.
                // Решает мьютекс, дублей не будет.
                try { UsbJournalMonitor.RunOnce(true); }
                catch { }

                // Самовосстановление защиты: раз в минуту (таймер 2 с x30)
                // возвращаем настройку автоперезапуска и пересоздаём задачу
                // контроля, если её удалили или отключили. Служба работает от
                // SYSTEM, поэтому вместе с задачей "USB_Block_Guard" образуется
                // "взаимное лечение": задача поднимает службу, служба - задачу.
                if (++_healTicks >= 30)
                {
                    _healTicks = 0;
                    try { ServiceManager.ApplyRecovery(); }
                    catch { }
                    try { TrayTask.CreateGuardTask(); }
                    catch { }
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
            sb.AppendLine(Loc.T("USB_Block tray - діагностика"));
            sb.AppendLine(Loc.T("Час: ") + DateTime.Now.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(Loc.T("Адміністратор: ") + Program.IsAdministrator());
            sb.AppendLine(Loc.T("Захищена копія: ") + ProtectedCopy.InstallExe +
                "  існує=" + File.Exists(ProtectedCopy.InstallExe));
            sb.AppendLine(Loc.T("Служба моніторингу: ") +
                (ServiceManager.IsInstalled()
                    ? ServiceManager.StatusText()
                    : Loc.T("не встановлена")));
            sb.AppendLine(Loc.T("Задача трея (--logon): ") +
                (TrayTask.IsInstalled() ? Loc.T("є") : Loc.T("немає")));
            sb.AppendLine(Loc.T("Задача сповіщувача (--notify): ") +
                (TrayTask.NotifyInstalled() ? Loc.T("є") : Loc.T("немає")));
            sb.AppendLine(Loc.T("Задача контролю служби (--ensure-service): ") +
                (TrayTask.GuardInstalled() ? Loc.T("є") : Loc.T("немає")));
            sb.AppendLine(Loc.T("Процес-сповіщувач: ") +
                (NotifyRunning() ? Loc.T("працює") : Loc.T("не знайдено")));
            sb.AppendLine(Loc.T("Подія блокування (черга): ") + EventsSummary());
            sb.AppendLine(Loc.T("Джерело журналу подій (") + ServiceManager.ServiceName + "): " +
                EventSourceStatus());
            sb.AppendLine(Loc.T("Запис у чергу (HKLM\\SOFTWARE\\USB_Block\\Events): ") +
                QueueWriteTest());
            sb.AppendLine(Loc.T("Лог показу сповіщень (хвіст ") +
                Path.GetFileName(NotifyService.TracePath) +
                Loc.T(" з %TEMP% і Windows\\Temp):"));
            sb.AppendLine(NotifyTraceSummary());

            sb.AppendLine();
            sb.AppendLine(Loc.T("--- Політика ---"));
            sb.AppendLine(Loc.T("Блокування активне: ") + PolicyManager.IsBlocked());
            sb.AppendLine(Loc.T("Пароль захисту меню: ") +
                (AdminPassword.IsSet() ? Loc.T("встановлено") : Loc.T("не встановлено")));

            sb.AppendLine();
            sb.AppendLine(Loc.T("--- Журнал підключень і копіювань ---"));
            sb.AppendLine(Loc.T("Журнал підключень: ") +
                (JournalSettings.IsConnectionsEnabled()
                    ? Loc.T("УВІМКНЕНО (вмикається встановленням служби, вимкається її видаленням)")
                    : Loc.T("ВИМКНЕНО - записи про підключення не пишуться")));
            sb.AppendLine(Loc.T("Журнал копіювання: ") +
                (JournalSettings.IsFilesEnabled()
                    ? Loc.T("УВІМКНЕНО (галочка пункту J в меню трея)")
                    : Loc.T("ВИМКНЕНО (галочка пункту J в меню трея) - записи про файли не пишуться")));
            sb.AppendLine(Loc.T("Розмір кільця кожного журналу: ") +
                (StorePaths.JournalGenerations + 1).ToString(CultureInfo.InvariantCulture) +
                Loc.T(" файлів по ") + UsbJournal.MaxRecordsPerFile.ToString(CultureInfo.InvariantCulture) +
                Loc.T(" записів (два незалежні журнали: підключення і копіювання)"));
            sb.AppendLine(Loc.T("Файл підключень: ") + StorePaths.JournalFile +
                "  існує=" + File.Exists(StorePaths.JournalFile));
            sb.AppendLine(Loc.T("Файл копіювання: ") + StorePaths.JournalFilesFile +
                "  існує=" + File.Exists(StorePaths.JournalFilesFile));
            sb.AppendLine(Loc.T("Поколінь: ") + (StorePaths.JournalGenerations + 1) +
                Loc.T("  записів усього: ") +
                UsbJournal.TotalRecordCount().ToString(CultureInfo.InvariantCulture) +
                Loc.T(" (підключень і службових: ") +
                UsbJournal.TotalRecordCount(UsbJournal.StreamDevices).ToString(CultureInfo.InvariantCulture) +
                Loc.T(", файлових: ") +
                UsbJournal.TotalRecordCount(UsbJournal.StreamFiles).ToString(CultureInfo.InvariantCulture) + ")" +
                Loc.T("  у поточних файлах: ") +
                UsbJournal.CurrentRecordCount().ToString(CultureInfo.InvariantCulture) +
                Loc.T("  розмір усього: ") +
                UsbJournal.TotalSizeBytes().ToString(CultureInfo.InvariantCulture) + Loc.T(" байт"));
            sb.AppendLine(Loc.T("Веде журнал: ") + JournalOwnerSummary());
            sb.AppendLine(Loc.T("Право дописувати в журнал у цього процесу: ") +
                UsbJournalRights.CanWriteNow());
            if (!string.IsNullOrEmpty(UsbJournalMonitor.WriteBlockedReason))
                sb.AppendLine(Loc.T("  запис неможливий: ") + UsbJournalMonitor.WriteBlockedReason);
            if (!string.IsNullOrEmpty(UsbJournalMonitor.OwnerNameElsewhere))
                sb.AppendLine(Loc.T("  журнал веде інший процес: ") +
                    UsbJournalMonitor.OwnerNameElsewhere);
            if (!string.IsNullOrEmpty(UsbJournalRights.LastError))
                sb.AppendLine(Loc.T("  помилка видачі прав: ") + UsbJournalRights.LastError);
            sb.AppendLine(Loc.T("Останній опит: файлів ") +
                UsbJournalMonitor.LastFileCount.ToString(CultureInfo.InvariantCulture) +
                Loc.T(", томів обрізано за лімітом ") +
                UsbJournalMonitor.LastTruncatedVolumes.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(UsbJournalMonitor.LastError))
                sb.AppendLine(Loc.T("Помилка спостереження: ") + UsbJournalMonitor.LastError);
            if (!string.IsNullOrEmpty(UsbJournal.LastError))
                sb.AppendLine(Loc.T("Помилка журналу: ") + UsbJournal.LastError);
            sb.AppendLine(Loc.T("Основна клавіша журналу: ") + JournalHotkeySummary(""));
            sb.AppendLine(Loc.T("Додаткова клавіша журналу (галочка J): ") +
                JournalHotkeySummary("2"));
            if (!string.IsNullOrEmpty(HotkeyStore.LastError))
                sb.AppendLine(Loc.T("  помилка реєстрації: ") + HotkeyStore.LastError);

            sb.AppendLine();
            sb.AppendLine("--- " + Loc.T("Мова / Language") + " ---");
            sb.AppendLine(Loc.T("Поточна мова: ") + Loc.Current);
            System.Collections.Generic.IList<string> missingEnDiag = Loc.MissingEnglish;
            sb.AppendLine(Loc.T("Відсутні ключі англійської (missing English): ") +
                missingEnDiag.Count.ToString(CultureInfo.InvariantCulture));
            if (missingEnDiag.Count > 0)
            {
                for (int i = 0; i < Math.Min(10, missingEnDiag.Count); i++)
                {
                    sb.AppendLine("  " + missingEnDiag[i]);
                }
                if (missingEnDiag.Count > 10)
                    sb.AppendLine("  ... (" + missingEnDiag.Count.ToString(CultureInfo.InvariantCulture) + ")");
            }
            sb.AppendLine();

            sb.AppendLine();
            sb.AppendLine("--- WHITELIST ---");
            sb.AppendLine(Loc.T("Файл: ") + StorePaths.File);
            try
            {
                List<DeviceEntry> wl = WhitelistStore.Load();
                sb.AppendLine(Loc.T("Записів: ") + wl.Count);
                foreach (DeviceEntry e in wl)
                {
                    sb.AppendLine("  NAME=" + e.Name + "  USB=" + e.UsbId +
                        "  SN=" + e.Serial + "  DISK=" + e.DiskId);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine(Loc.T("Помилка читання (очікувано без прав адміністратора): ") + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine(Loc.T("--- USB-диски (підключені) ---"));
            try
            {
                List<StorageDevice> disks = UsbQuery.GetUsbStorages();
                sb.AppendLine(Loc.T("Знайдено: ") + disks.Count);
                foreach (StorageDevice sd in disks)
                {
                    sb.AppendLine("  INST=" + sd.InstanceId);
                    sb.AppendLine("      MODEL=" + sd.Model + "  USB=" + sd.UsbId +
                        "  SN=" + sd.Serial + "  DISKID=" + sd.BestDiskId);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine(Loc.T("Помилка: ") + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine(Loc.T("--- Смонтувані томи USB (точки монтування) ---"));
            try
            {
                List<UsbVolume> volumes = UsbQuery.GetUsbVolumes();
                sb.AppendLine(Loc.T("Знайдено: ") + volumes.Count);
                foreach (UsbVolume v in volumes)
                {
                    sb.AppendLine("  LETTER=" + v.DriveLetter + "  USB=" + v.UsbId +
                        "  SN=" + v.Serial + "  MODEL=" + v.Model);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine(Loc.T("Помилка: ") + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine(Loc.T("--- USB-вузли масової пам'яті ---"));
            try
            {
                List<UsbNode> nodes = UsbQuery.GetUsbMassStorageNodes();
                sb.AppendLine(Loc.T("Знайдено: ") + nodes.Count);
                foreach (UsbNode n in nodes)
                {
                    sb.AppendLine("  INST=" + n.InstanceId + "  PRESENT=" + n.Present +
                        "  VIDPID=" + n.VidPid + "  SN=" + n.Serial +
                        "  DISABLED=" + UsbQuery.IsNodeDisabled(n.InstanceId));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine(Loc.T("Помилка: ") + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine(Loc.T("--- Усі мас-сторадж вузли (реєстр) ---"));
            try
            {
                List<string> all = UsbQuery.GetAllMassStorageInstances();
                sb.AppendLine(Loc.T("Знайдено: ") + all.Count);
                foreach (string s in all)
                    sb.AppendLine("  " + s);
            }
            catch (Exception ex)
            {
                sb.AppendLine(Loc.T("Помилка: ") + ex.Message);
            }

            string logPath = Program.PreferredLogPath("diag.log");
            sb.Insert(0,
                Loc.T("Файл цього лога: ") + logPath + Environment.NewLine);
            Program.WriteLog("diag.log", sb.ToString());
            return 0;
        }

        // Кто ведёт журнал на этой машине. --diag сам журнал не ведёт, поэтому
        // смотрим на то, что видно снаружи: работает ли служба и жив ли
        // трей. Если не работает никто - журнал пополняться не будет, и это
        // надо видеть в диагностике, а не угадывать по пустому файлу.
        private static string JournalOwnerSummary()
        {
            if (UsbJournalMonitor.IsOwner) return UsbJournalMonitor.OwnerName;
            if (!JournalSettings.IsEnabled())
                return Loc.T("журнал вимкнено (пункт J в меню трея)");
            // Метка последнего цикла надёжнее поиска процесса: командную
            // строку процесса с повышенными правами обычный пользователь
            // через WMI не видит (CommandLine приходит пустым), и живой
            // трей выглядел бы в диагностике как незапущенный.
            string age = JournalCycleAge();
            if (age != null)
                return Loc.T("цикл спостереження оновлюється, останній ") + age + Loc.T(" (трей або служба)");
            try
            {
                if (ServiceManager.IsInstalled())
                {
                    string st = ServiceManager.StatusText();
                    if (st.IndexOf("Работает", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        st.IndexOf("Running", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return Loc.T("служба ") + ServiceManager.ServiceName + " (" + st + ")";
                    }
                    return Loc.T("служба ") + ServiceManager.ServiceName + Loc.T(" встановлена, але ") +
                        Loc.T("зупинена (") + st + Loc.T(") - журнал веде трей, якщо він запущений");
                }
            }
            catch
            {
            }
            if (TrayRunning()) return Loc.T("трей (поточний користувач)");
            return Loc.T("НІХТО НЕ ВЕДЕ: службу не встановлено і трей не запущено - ") +
                Loc.T("журнал не поповнюватиметься");
        }

        // Насколько давно был цикл наблюдения (его пишет тот, кто ведёт
        // журнал). null - метки нет ни разу: журнал не включали.
        private static string JournalCycleAge()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\USB_Block\JournalPresence", false))
                {
                    if (k == null) return null;
                    object v = k.GetValue("LastCycleTicks");
                    if (v == null) return null;
                    long ticks = Convert.ToInt64(v, CultureInfo.InvariantCulture);
                    if (ticks <= 0) return null;
                    double s = (DateTime.Now - new DateTime(ticks)).TotalSeconds;
                    if (s < 0) return Loc.T("щойно");
                    if (s < 90) return ((int)s).ToString(CultureInfo.InvariantCulture) + Loc.T(" с тому");
                    if (s < 5400)
                        return ((int)(s / 60)).ToString(CultureInfo.InvariantCulture) + Loc.T(" хв тому");
                    return ((int)(s / 3600)).ToString(CultureInfo.InvariantCulture) + Loc.T(" год тому");
                }
            }
            catch
            {
                return null;
            }
        }

        // Комбинация журнала для --diag: сохранённая и разобранная по частям,
        // чтобы было видно расхождение между записанной строкой и тем, что
        // из неё получилось (бывает при правке реестра руками).
        private static string JournalHotkeySummary(string which)
        {
            string text = which == "2" ? HotkeyStore.GetText2() : HotkeyStore.GetText();
            if (string.IsNullOrEmpty(text)) return Loc.T("не задана");
            uint mods, vk;
            if (!HotkeyStore.TryParse(text, out mods, out vk))
                return text + Loc.T("  (НЕ РОЗПІЗНАЄТЬСЯ, не спрацює)");
            return text + "  -> Ctrl=" + ((mods & HotkeyStore.ModControl) != 0) +
                " Alt=" + ((mods & HotkeyStore.ModAlt) != 0) +
                " Shift=" + ((mods & HotkeyStore.ModShift) != 0) +
                " Win=" + ((mods & HotkeyStore.ModWin) != 0) +
                " клавіша=0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
        }

        // Работает ли сейчас отдельный процесс-уведомитель (--notify) в
        // текущей сессии (по командной строке процессов этого пользователя).
        private static bool NotifyRunning()
        {
            return ProcessRunning("--notify");
        }

        // Жив ли сейчас трей (процесс usb_block_tray.exe без служебных
        // аргументов --logon/--notify/--diag/...).
        private static bool TrayRunning()
        {
            return ProcessRunning(null);
        }

        // Ищет процесс usb_block_tray.exe; marker - требуемая часть
        // командной строки (null - любой запуск без служебных аргументов).
        private static bool ProcessRunning(string marker)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE Name='usb_block_tray.exe'"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string cl = o["CommandLine"] as string;
                        if (cl == null) continue;
                        if (marker != null)
                        {
                            if (cl.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                                return true;
                            continue;
                        }
                        // Трей - это запуск без аргументов. Любой служебный
                        // флаг (--notify, --logon, --diag, --selftest,
                        // --journal, --testpopup, --service, --makecopies)
                        // означает, что это не он.
                        string[] flags =
                        {
                            "--notify", "-notify", "--logon", "-logon",
                            "--diag", "-diag", "--selftest", "-selftest",
                            "--journal", "-journal", "--testpopup", "-testpopup",
                            "--service", "-service", "--makecopies", "-makecopies"
                        };
                        bool clean = true;
                        foreach (string f in flags)
                            if (cl.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                clean = false;
                                break;
                            }
                        if (clean) return true;
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
                return "записів=" + evs.Count + Loc.T(", макс.id=") + max +
                    ", LastEventId(HKCU)=" + last;
            }
            catch (Exception ex)
            {
                return Loc.T("помилка читання: ") + ex.Message;
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
                    sb.AppendLine(Loc.T("  помилка читання: ") + ex.Message);
                }
            }
            if (!any) return Loc.T("  <немає лога - показ ще не запускався>");
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
                    ? Loc.T("зареєстровано") : Loc.T("НЕ зареєстровано");
            }
            catch (Exception ex)
            {
                return Loc.T("не вдалося визначити (") + ex.Message + ")";
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
                return Loc.T("ОК");
            }
            catch (Exception ex)
            {
                return Loc.T("НІ (") + ex.Message + ")";
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

            // Версия: AssemblyVersion/FileVersion обязаны совпадать между
            // собой и с app.manifest (там assemblyIdentity) - иначе в
            // свойствах файла и в манифесте разойдутся цифры. Манифест
            // рядом с exe есть только в папке разработчика; если его нет,
            // сверяемся только внутри сборки.
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string asmVer = asm.GetName().Version.ToString();
                string fileVer = FileVersionInfo
                    .GetVersionInfo(asm.Location).FileVersion;
                string manifestVer = null;
                string manifestPath = Path.Combine(
                    Path.GetDirectoryName(asm.Location) ?? string.Empty,
                    "app.manifest");
                if (File.Exists(manifestPath))
                {
                    foreach (string ln in File.ReadAllLines(manifestPath))
                    {
                        int p = ln.IndexOf("assemblyIdentity", StringComparison.Ordinal);
                        if (p < 0) continue;
                        int v = ln.IndexOf("version=\"", p, StringComparison.Ordinal);
                        if (v < 0) continue;
                        v += "version=\"".Length;
                        int e = ln.IndexOf('"', v);
                        if (e > v) manifestVer = ln.Substring(v, e - v);
                        break;
                    }
                }
                bool verOk = !string.IsNullOrEmpty(asmVer) &&
                    asmVer == fileVer &&
                    (manifestVer == null || manifestVer == asmVer);
                sb.AppendLine("Build version (assembly/file/манифест): " +
                    (verOk ? "OK" : "FAIL") +
                    " (assembly=" + asmVer +
                    " file=" + fileVer +
                    " манифест=" + (manifestVer ?? "нет рядом") + ")");
            }
            catch (Exception ex)
            {
                sb.AppendLine("Build version ERROR: " + ex.Message);
            }

            // Сообщение о блокировке: в нём только модель устройства -
            // без SN и без VID:PID (одинаковый текст у уведомления и
            // записи в журнале событий Windows).
            try
            {
                string dModel = NotifyStore.BlockDetail("Samsung Portable SSD T3");
                string dUsbId = NotifyStore.BlockDetail("USB\\VID_8564&PID_1000");
                string dEmpty = NotifyStore.BlockDetail(null);
                bool dOk = dModel == "Заблоковано пристрій: Samsung Portable SSD T3" &&
                    dUsbId == "Заблоковано пристрій" &&
                    dEmpty == "Заблоковано пристрій" &&
                    !dModel.Contains("SN=") && !dModel.Contains("VID");
                sb.AppendLine("Notify detail (только модель, без SN и VID:PID): " +
                    (dOk ? "OK" : "FAIL") +
                    " [" + dModel + "] [" + dUsbId + "] [" + dEmpty + "]");
            }
            catch (Exception ex)
            {
                sb.AppendLine("Notify detail ERROR: " + ex.Message);
            }

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

            // Значок в трее: он должен браться из встроенного stop_usb.ico,
            // а не рисоваться кодом. Проверяем, что ресурс в сборке есть и
            // иконка из него грузится нужного размера (иначе получится
            // нечитаемый значок 16x16 из растянутой картинки).
            try
            {
                bool resPresent =
                    Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream("usb_block.tray.ico") != null;
                using (Icon ic = AppIcons.Create())
                {
                    string size = ic != null
                        ? ic.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                          ic.Height.ToString(CultureInfo.InvariantCulture)
                        : "нет";
                    bool iconOk = ic != null && ic.Width >= 16 && ic.Height >= 16 &&
                        ic.Width <= 48 && ic.Height <= 48;
                    sb.AppendLine("Tray icon (stop_usb.ico): " +
                        (resPresent && iconOk ? "OK" : "FAIL") +
                        " (ресурс=" + resPresent + " размер=" + size + ")");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Tray icon ERROR: " + ex.Message);
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

            // проверка дубликатов при ДОБАВЛЕНИИ устройства
            try
            {
                List<DeviceEntry> wl = new List<DeviceEntry>
                {
                    new DeviceEntry
                    {
                        Name = "Флешка 1",
                        UsbId = "USB\\VID_8564&PID_1000",
                        DiskId = "USBSTOR\\Disk&Ven_8564&Prod_X&Rev_1100\\S1&0",
                        Serial = "S1",
                        AddedAt = DateTime.Now
                    }
                };
                // тот же накопитель: серийник в другом регистре и с хвостом "&0"
                DeviceEntry sameSn = new DeviceEntry
                {
                    Name = "Флешка 1 (повтор)",
                    UsbId = "usb\\vid_8564&pid_1000",
                    Serial = "s1&0"
                };
                // тот же накопитель: серийник неизвестен, совпал HardwareID диска
                DeviceEntry sameDisk = new DeviceEntry
                {
                    UsbId = "USB\\VID_8564&PID_1000",
                    DiskId = "USBSTOR\\Disk&Ven_8564&Prod_X&Rev_1100\\S1&0"
                };
                // ДРУГАЯ флешка той же модели - дубликатом не считается
                DeviceEntry other = new DeviceEntry
                {
                    UsbId = "USB\\VID_8564&PID_1000",
                    DiskId = "USBSTOR\\Disk&Ven_8564&Prod_X&Rev_1100\\S2&0",
                    Serial = "S2"
                };
                bool ok = WhitelistRules.Find(wl, sameSn) == 0 &&
                          WhitelistRules.Find(wl, sameDisk) == 0 &&
                          WhitelistRules.Find(wl, other) < 0;
                sb.AppendLine("Dedup add: " + (ok ? "OK" : "FAIL"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("Dedup add ERROR: " + ex.Message);
            }

            // проверка дубликатов при ИМПОРТЕ (внутри файла и против текущего)
            try
            {
                List<DeviceEntry> file = new List<DeviceEntry>
                {
                    new DeviceEntry { Name = "A", UsbId = "USB\\VID_8564&PID_1000", Serial = "S1" },
                    new DeviceEntry { Name = "A (копия)", UsbId = "USB\\VID_8564&PID_1000", Serial = "S1" },
                    new DeviceEntry { Name = "B", UsbId = "USB\\VID_1234&PID_5678", Serial = "S3" }
                };
                int dupInFile;
                List<DeviceEntry> clean = WhitelistRules.Dedupe(file, out dupInFile);

                List<DeviceEntry> baseWl = new List<DeviceEntry>
                {
                    new DeviceEntry { Name = "A", UsbId = "USB\\VID_8564&PID_1000", Serial = "S1" },
                    new DeviceEntry { Name = "A (ещё раз)", UsbId = "USB\\VID_8564&PID_1000", Serial = "s1&0" }
                };
                int dupBase;
                List<DeviceEntry> baseClean = WhitelistRules.Dedupe(baseWl, out dupBase);
                List<DeviceEntry> merged = TrayContext.MergeWhitelists(baseClean, clean);

                bool ok = clean.Count == 2 && dupInFile == 1 &&
                          baseClean.Count == 1 && dupBase == 1 &&
                          merged.Count == 2;
                sb.AppendLine("Dedup import: " + (ok ? "OK" : "FAIL") +
                    " (файл: " + clean.Count.ToString(CultureInfo.InvariantCulture) +
                    " из " + file.Count.ToString(CultureInfo.InvariantCulture) +
                    ", после слияния: " + merged.Count.ToString(CultureInfo.InvariantCulture) + ")");
            }
            catch (Exception ex)
            {
                sb.AppendLine("Dedup import ERROR: " + ex.Message);
            }

            // пароль защиты: требования к паролю, кодовое слово и проверка
            // хэша (файл пароля при этом не создаётся и не меняется)
            try
            {
                byte[] salt;
                byte[] packed = AdminPassword.PackRecord("Passw0rd", out salt);
                byte[] salt2;
                byte[] packed2 = AdminPassword.PackRecord("Passw0rd", out salt2);
                bool hash = AdminPassword.CheckPacked("Passw0rd", packed) &&
                            AdminPassword.CheckPacked("Passw0rd", packed2) &&
                            !AdminPassword.CheckPacked("passw0rd", packed) &&
                            !AdminPassword.CheckPacked("Passw0rd ", packed) &&
                            !AdminPassword.CheckPacked("Passw0rd", new byte[] { 1, 2, 3 }) &&
                            !SameBytes(packed, packed2) && salt.Length == 16;
                bool policy = AdminPassword.MatchesPolicy("Passw0rd") &&
                              AdminPassword.MatchesPolicy("Abc123") &&
                              !AdminPassword.MatchesPolicy("Pw0rd") &&
                              !AdminPassword.MatchesPolicy("passw0rd") &&
                              !AdminPassword.MatchesPolicy("PASSW0RD") &&
                              !AdminPassword.MatchesPolicy("PasswrD") &&
                              !AdminPassword.MatchesPolicy("АБвГ123") &&
                              !AdminPassword.MatchesPolicy("") && !AdminPassword.MatchesPolicy(null);
                bool code = AdminPassword.CheckCode("odmin") &&
                            AdminPassword.CheckCode("ODMIN") &&
                            AdminPassword.CheckCode(" odmin ") &&
                            !AdminPassword.CheckCode("admin") &&
                            !AdminPassword.CheckCode("odmi") &&
                            !AdminPassword.CheckCode("");
                sb.AppendLine("Password guard: " + (hash && policy && code ? "OK" : "FAIL"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("Password guard ERROR: " + ex.Message);
            }

            // ---- Журнал: запись/чтение, дифф снимков, горячая клавиша ----
            // Работаем во временной папке и отдельным набором поколений,
            // чтобы не тронуть настоящий журнал машины.
            string jDir = Path.Combine(Path.GetTempPath(),
                "usb_selftest_journal_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(jDir);
                // Настоящий журнал машины не трогаем: на время проверок
                // журнал пишет во временную папку.
                UsbJournal.UseDirectoryForTest(jDir);

                // 1) запись -> чтение: содержимое возвращается тем же,
                //    а порядок обратный (свежие сверху); записи о файлах
                //    уходят в СВОЙ журнал и при чтении сшиваются с
                //    журналом подключений по времени
                int gens = StorePaths.JournalGenerations;
                StorePaths.JournalGenerations = 0;      // только текущий файл
                try
                {
                    UsbJournal.Write(UsbJournal.KindDeviceAdded, "SN=ТЕСТ | Модель=Проверка");
                    UsbJournal.Write(UsbJournal.KindFileAdded, @"X:\папка\файл.txt | Размер=1 байт");
                    UsbJournal.Write(UsbJournal.KindFileRenamed, @"X:\папка\новое.txt | X:\папка\старое.txt");
                    List<string> back = UsbJournal.ReadRecent(0);
                    bool roundTrip = back.Count == 3 &&
                        back[0].Contains("старое.txt") &&
                        back[1].Contains("файл.txt") &&
                        back[2].Contains("Модель=Проверка") &&
                        UsbJournal.CurrentRecordCount() == 3 &&
                        // вид записи решает, в какой файл она попала:
                        // подключение - в journal.dat, файлы - в journal_files.dat
                        UsbJournal.CurrentRecordCount(UsbJournal.StreamDevices) == 1 &&
                        UsbJournal.CurrentRecordCount(UsbJournal.StreamFiles) == 2 &&
                        // файлы не должны содержать открытый текст
                        !File.ReadAllText(UsbJournal.CurrentPath,
                            System.Text.Encoding.ASCII).Contains("Проверка") &&
                        !File.ReadAllText(UsbJournal.CurrentFilesPath,
                            System.Text.Encoding.ASCII).Contains("старое.txt");
                    sb.AppendLine("Journal write/read (encrypted, newest first): " +
                        (roundTrip ? "OK" : "FAIL"));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Journal write/read ERROR: " + ex.Message);
                }
                finally
                {
                    StorePaths.JournalGenerations = gens;
                }

                // 1г) чтение файла журнала по любому пути (окно просмотра
                //     умеет открывать файл из любого места): тот же порядок,
                //     что и при обычном чтении, для КАЖДОГО из двух журналов;
                //     не-журнальный файл - явная ошибка, а не пустой список
                try
                {
                    List<string> extDev = UsbJournal.ReadExternalFile(
                        UsbJournal.CurrentPath, 0);
                    List<string> extFiles = UsbJournal.ReadExternalFile(
                        UsbJournal.CurrentFilesPath, 0);
                    bool extOk = extDev.Count == 1 &&
                        extDev[0].Contains("Модель=Проверка") &&
                        extFiles.Count == 2 &&
                        extFiles[0].Contains("старое.txt") &&
                        extFiles[1].Contains("файл.txt") &&
                        UsbJournal.CountExternalFile(UsbJournal.CurrentPath) == 1 &&
                        UsbJournal.CountExternalFile(UsbJournal.CurrentFilesPath) == 2;
                    bool extBad = false;
                    string notJournal = Path.Combine(jDir, "not_a_journal.dat");
                    File.WriteAllText(notJournal, "просто текст, не журнал");
                    try
                    {
                        UsbJournal.ReadExternalFile(notJournal, 0);
                    }
                    catch (IOException)
                    {
                        extBad = true;
                    }
                    sb.AppendLine("Journal external file (открытие из любого места): " +
                        (extOk && extBad ? "OK" : "FAIL") +
                        " (подключений=" + extDev.Count.ToString(CultureInfo.InvariantCulture) +
                        " файловых=" + extFiles.Count.ToString(CultureInfo.InvariantCulture) +
                        " не_журнал_ошибка=" + extBad.ToString() + ")");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Journal external file ERROR: " + ex.Message);
                }

                // 1б) ротация: при переполнении текущего файла поколения
                //     сдвигаются, ничего не теряется и порядок не ломается
                try
                {
                    int gens2 = StorePaths.JournalGenerations;
                    StorePaths.JournalGenerations = 3;      // journal_1.._3
                    int max2 = UsbJournal.MaxRecordsPerFile;
                    UsbJournal.MaxRecordsPerFile = 4;
                    // Начинаем с чистого журнала: записи предыдущей проверки
                    // иначе попали бы в первый файл и сбили счёт.
                    UsbJournal.Clear();
                    UsbJournal.ResetCountForTest();
                    try
                    {
                        for (int i = 1; i <= 10; i++)
                            UsbJournal.Write(UsbJournal.KindDeviceAdded,
                                "SN=РОТАЦИЯ" + i.ToString(CultureInfo.InvariantCulture));

                        List<string> rot = UsbJournal.ReadRecent(0);
                        // 10 записей по 4 на файл: journal.dat - 2,
                        // journal_1 - 4, journal_2 - 4; потерь быть не должно
                        bool rotOk = rot.Count == 10 &&
                            rot[0].Contains("РОТАЦИЯ10") &&
                            rot[9].Contains("РОТАЦИЯ1") &&
                            UsbJournal.TotalRecordCount() == 10;
                        sb.AppendLine("Journal rotation: " +
                            (rotOk ? "OK" : "FAIL") + " (всего " +
                            rot.Count.ToString(CultureInfo.InvariantCulture) + " из 10, " +
                            "в файле " +
                            UsbJournal.CurrentRecordCount().ToString(CultureInfo.InvariantCulture) + ")");
                    }
                    finally
                    {
                        UsbJournal.MaxRecordsPerFile = max2;
                        StorePaths.JournalGenerations = gens2;
                        UsbJournal.ResetCountForTest();
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Journal rotation ERROR: " + ex.Message);
                }

                // 1д) два журнала кольцуются РАЗДЕЛЬНО: поток копирования
                //     не двигает поколения записей о подключениях (иначе
                //     копирование тысяч файлов стирало записи о подключении)
                try
                {
                    int gens3 = StorePaths.JournalGenerations;
                    StorePaths.JournalGenerations = 3;      // journal_1.._3
                    int max3 = UsbJournal.MaxRecordsPerFile;
                    UsbJournal.MaxRecordsPerFile = 4;
                    UsbJournal.Clear();
                    UsbJournal.ResetCountForTest();
                    try
                    {
                        for (int i = 1; i <= 6; i++)
                            UsbJournal.Write(UsbJournal.KindDeviceAdded,
                                "SN=РАЗДЕЛЬНО" + i.ToString(CultureInfo.InvariantCulture));
                        for (int i = 1; i <= 6; i++)
                            UsbJournal.Write(UsbJournal.KindFileAdded,
                                @"X:\раздельно\" + i.ToString(CultureInfo.InvariantCulture) + ".txt");

                        // 6 подключений: journal.dat - 2, journal_1 - 4;
                        // 6 файлов: journal_files.dat - 2, journal_files_1 - 4
                        int devCur = UsbJournal.CurrentRecordCount(UsbJournal.StreamDevices);
                        int filesCur = UsbJournal.CurrentRecordCount(UsbJournal.StreamFiles);
                        List<string> all = UsbJournal.ReadRecent(0);
                        bool splitOk = devCur == 2 && filesCur == 2 &&
                            UsbJournal.TotalRecordCount(UsbJournal.StreamDevices) == 6 &&
                            UsbJournal.TotalRecordCount(UsbJournal.StreamFiles) == 6 &&
                            UsbJournal.TotalRecordCount() == 12 &&
                            all.Count == 12 &&
                            File.Exists(UsbJournal.CurrentPath) &&
                            File.Exists(UsbJournal.CurrentFilesPath);
                        sb.AppendLine("Journal split rings (копирование не вытесняет подключения): " +
                            (splitOk ? "OK" : "FAIL") +
                            " (подключений в текущем " + devCur.ToString(CultureInfo.InvariantCulture) +
                            ", файловых в текущем " + filesCur.ToString(CultureInfo.InvariantCulture) +
                            ", всего прочитано " + all.Count.ToString(CultureInfo.InvariantCulture) + ")");
                    }
                    finally
                    {
                        UsbJournal.MaxRecordsPerFile = max3;
                        StorePaths.JournalGenerations = gens3;
                        UsbJournal.ResetCountForTest();
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Journal split rings ERROR: " + ex.Message);
                }

                // 2) дифф снимков: создание, изменение, удаление, переименование
                try
                {
                    Dictionary<string, UsbJournalMonitor.FileStamp> a =
                        new Dictionary<string, UsbJournalMonitor.FileStamp>(
                            StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, UsbJournalMonitor.FileStamp> b =
                        new Dictionary<string, UsbJournalMonitor.FileStamp>(
                            StringComparer.OrdinalIgnoreCase);

                    a[@"D:\a.txt"] = Stamp(10, 111);
                    a[@"D:\b.txt"] = Stamp(20, 222);
                    a[@"D:\c.txt"] = Stamp(30, 333);
                    a[@"D:\sub\d.txt"] = Stamp(40, 444);

                    b[@"D:\a.txt"] = Stamp(10, 111);      // без изменений
                    b[@"D:\b.txt"] = Stamp(99, 555);      // изменён
                    // c.txt исчез, e.txt появился с тем же размером и тем же
                    // временем изменения - это переименование. Новый файл
                    // намеренно кладём в ПОДПАПКУ: если бы он лежал в той же
                    // папке, в ней было бы два "создан" и переименование
                    // (один удалён + один создан) не распозналось бы.
                    b[@"D:\e.txt"] = Stamp(30, 333);
                    b[@"D:\sub\d.txt"] = Stamp(40, 444);
                    b[@"D:\sub\new.txt"] = Stamp(50, 555);  // создан

                    List<string> diff = UsbJournalMonitor.Diff(a, b);
                    int added = 0, changed = 0, removed = 0, renamed = 0;
                    foreach (string e in diff)
                    {
                        string kind = e.Split('\t')[0];
                        if (kind == UsbJournal.KindFileAdded) added++;
                        else if (kind == UsbJournal.KindFileChanged) changed++;
                        else if (kind == UsbJournal.KindFileRemoved) removed++;
                        else if (kind == UsbJournal.KindFileRenamed) renamed++;
                    }
                    bool diffOk = added == 1 && changed == 1 && removed == 0 && renamed == 1 &&
                        diff.Count == 3;
                    sb.AppendLine("Journal diff (add/change/remove/rename): " +
                        (diffOk
                            ? "OK (+" + added + " ~" + changed + " -" + removed +
                              " >" + renamed + ")"
                            : "FAIL (+" + added + " ~" + changed + " -" + removed +
                              " >" + renamed + ", всего " + diff.Count + ")"));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Journal diff ERROR: " + ex.Message);
                }

                // 3) обход тома на временной папке: служебные папки пропускаются,
                //    найденные файлы попадают в снимок
                try
                {
                    string root = Path.Combine(jDir, "vol");
                    Directory.CreateDirectory(Path.Combine(root, "$RECYCLE.BIN"));
                    File.WriteAllText(Path.Combine(root, "плоский.txt"), "a");
                    File.WriteAllText(Path.Combine(root, "$RECYCLE.BIN", "мусор.txt"), "b");
                    Dictionary<string, UsbJournalMonitor.FileStamp> scanned;
                    bool truncated;
                    UsbJournalMonitor.ScanVolume(root, out scanned, out truncated);
                    bool scanOk = !truncated &&
                        scanned.ContainsKey(Path.Combine(root, "плоский.txt")) &&
                        scanned.Count == 1;
                    sb.AppendLine("Journal volume scan: " + (scanOk ? "OK" : "FAIL") +
                        " (файлов " + scanned.Count + ", служебные пропущены)");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Journal volume scan ERROR: " + ex.Message);
                }

                // 3б) два раздела ОДНОГО накопителя: серийный номер и номер
                //     физического диска у них общие, а снимки обязаны быть
                //     раздельными - иначе каждый цикл перетирал бы снимок
                //     и писал бы десятки тысяч ложных «удалён» (буквы F: и
                //     G: на одном диске). Смена буквы ключ менять не должна.
                try
                {
                    UsbVolume p1 = new UsbVolume();
                    p1.Serial = "ОДИН_SN";
                    p1.DiskId = @"\\.\PHYSICALDRIVE2";
                    p1.PartitionId = "Disk #2, Partition #0";
                    p1.DriveLetter = "F";
                    UsbVolume p2 = new UsbVolume();
                    p2.Serial = "ОДИН_SN";
                    p2.DiskId = @"\\.\PHYSICALDRIVE2";
                    p2.PartitionId = "Disk #2, Partition #1";
                    p2.DriveLetter = "G";
                    UsbVolume p3 = new UsbVolume();   // тот же раздел, другая буква
                    p3.Serial = "ОДИН_SN";
                    p3.DiskId = @"\\.\PHYSICALDRIVE2";
                    p3.PartitionId = "Disk #2, Partition #1";
                    p3.DriveLetter = "H";
                    UsbVolume p4 = new UsbVolume();   // раздел неизвестен - запасной ключ
                    p4.Serial = "ОДИН_SN";
                    p4.DiskId = @"\\.\PHYSICALDRIVE2";
                    p4.DriveLetter = "F";
                    string k1 = UsbJournalMonitor.VolumeKey(p1);
                    string k2 = UsbJournalMonitor.VolumeKey(p2);
                    string k3 = UsbJournalMonitor.VolumeKey(p3);
                    string k4 = UsbJournalMonitor.VolumeKey(p4);
                    bool partOk = k1 != null && k2 != null && k4 != null &&
                        !string.Equals(k1, k2, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(k2, k3, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(k1, k4, StringComparison.OrdinalIgnoreCase);
                    sb.AppendLine("Journal multi-partition key: " +
                        (partOk ? "OK" : "FAIL"));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Journal multi-partition key ERROR: " + ex.Message);
                }

                // 5) два независимых переключателя журнала:
                //    - подключения (включается установкой службы),
                //    - копирование (галочка пункта J).
                //    Полностью выключенный журнал не должен запускать даже
                //    цикл наблюдения, а включённая половина - должна.
                try
                {
                    JournalSettings.UseForTest(false, false);
                    bool off = !JournalSettings.IsEnabled();
                    UsbJournalMonitor.RunOnce(false);      // не должен начать цикл
                    bool noOwner = !UsbJournalMonitor.IsOwner;

                    // Только подключения: общий признак включён, файлы - нет.
                    JournalSettings.UseForTest(true, false);
                    bool onlyConn = JournalSettings.IsEnabled() &&
                        JournalSettings.IsConnectionsEnabled() &&
                        !JournalSettings.IsFilesEnabled();

                    // Только копирование - ровно наоборот.
                    JournalSettings.UseForTest(false, true);
                    bool onlyFiles = JournalSettings.IsEnabled() &&
                        !JournalSettings.IsConnectionsEnabled() &&
                        JournalSettings.IsFilesEnabled();

                    JournalSettings.UseForTest(true, true);
                    bool both = JournalSettings.IsEnabled();
                    JournalSettings.UseEnabledForTest(null);
                    bool switchOk = off && noOwner && onlyConn && onlyFiles && both;
                    sb.AppendLine("Journal switch (connections/files apart): " +
                        (switchOk ? "OK" : "FAIL") +
                        " (выкл=" + off + " без_цикла=" + noOwner +
                        " только_подключения=" + onlyConn +
                        " только_копирование=" + onlyFiles + " оба=" + both + ")");
                }
                catch (Exception ex)
                {
                    JournalSettings.UseEnabledForTest(null);
                    sb.AppendLine("Journal switch ERROR: " + ex.Message);
                }

                // 5б) основная комбинация клавиш - всегда Ctrl+Alt+O,
                //     дополнительная (галочка J) - отдельная и не обязательна
                try
                {
                    bool defaultOk = string.Equals(HotkeyStore.DefaultText, "Ctrl+Alt+O",
                        StringComparison.OrdinalIgnoreCase);
                    uint dmods, dvk;
                    bool mainParsed = HotkeyStore.TryParse(HotkeyStore.DefaultText,
                        out dmods, out dvk) &&
                        (dmods & HotkeyStore.ModControl) != 0 &&
                        (dmods & HotkeyStore.ModAlt) != 0 &&
                        dvk == (uint)Keys.O;
                    bool idsDiffer = HotkeyStore.HotkeyId != HotkeyStore.HotkeyId2;
                    bool oursMain = HotkeyStore.IsOurs(HotkeyStore.HotkeyId);
                    bool oursExtra = HotkeyStore.IsOurs(HotkeyStore.HotkeyId2);
                    bool oursOther = !HotkeyStore.IsOurs(0x1234);
                    // У обеих комбинаций разные идентификаторы: иначе
                    // вторая перебила бы первую в WM_HOTKEY.
                    bool hotkeyOk = defaultOk && mainParsed && idsDiffer &&
                        oursMain && oursExtra && oursOther;
                    sb.AppendLine("Journal hotkeys (main Ctrl+Alt+O + extra for J): " +
                        (hotkeyOk ? "OK" : "FAIL") +
                        " (умолчание=" + defaultOk + " разбор=" + mainParsed +
                        " разные_id=" + idsDiffer + " чужие_игнор=" + oursOther + ")");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Journal hotkeys ERROR: " + ex.Message);
                }

                // 5в) дописывание строго в конец: обычный пользователь дописывает
                //     в журнал дескриптором с правом FILE_APPEND_DATA, поэтому
                //     переписать или стереть уже записанное он не может.
                //     Проверяем на временном файле: (1) что дописывание
                //     работает, (2) что начало файла не изменилось.
                try
                {
                    string probe = Path.Combine(Path.GetTempPath(),
                        "usb_append_probe_" + Guid.NewGuid().ToString("N") + ".bin");
                    File.WriteAllText(probe, "HEAD");
                    bool appended = UsbJournal.TryAppendForTest(probe,
                        Encoding.UTF8.GetBytes("TAIL"));
                    string head = File.ReadAllText(probe);
                    bool headKept = head.StartsWith("HEAD", StringComparison.Ordinal) &&
                        head.EndsWith("TAIL", StringComparison.Ordinal);
                    // ACL самого файла журнала: обычному пользователю должны
                    // достаться только чтение и дописывание в конец
                    // (AppendData). Ни Write, ни Delete быть не должно - иначе
                    // он перепишет или подменит уже записанное. Проверяем
                    // правами файла, а не попыткой записи: результат не должен
                    // зависеть от того, под чем запущена самопроверка.
                    bool aclChecked = false, aclAllow = false, aclNoWrite = false;
                    bool aclPending = false;
                    try
                    {
                        string real = UsbJournal.GenerationFileFor(0);
                        if (File.Exists(real))
                        {
                            // Под администратором права приводим к нужным
                            // сразу: иначе проверка читала бы ACL, оставшийся
                            // от прежней версии (только администраторы).
                            if (Program.IsAdministrator())
                                UsbJournalRights.EnsureFileRights(real);
                            FileSecurity sec = File.GetAccessControl(real);
                            AuthorizationRuleCollection rules =
                                sec.GetAccessRules(true, false, typeof(SecurityIdentifier));
                            aclChecked = true;
                            SecurityIdentifier users =
                                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
                            foreach (FileSystemAccessRule r in rules)
                            {
                                if (r.IdentityReference != users) continue;
                                FileSystemRights have = r.FileSystemRights;
                                aclAllow = aclAllow ||
                                    (have & FileSystemRights.AppendData) != 0;
                                aclNoWrite = aclNoWrite && (have & FileSystemRights.Write) == 0 &&
                                    (have & FileSystemRights.Delete) == 0;
                            }
                            if (!aclAllow && !Program.IsAdministrator())
                            {
                                // Прав на пользователя в ACL ещё нет, и выдать
                                // их может только администратор (или служба).
                                // Это не поломка: самопроверка идёт обычно без
                                // прав. Отмечаем как «ожидается» и не ругаемся.
                                aclPending = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("Journal ACL ERROR: " + ex.Message);
                    }
                    bool appendOnlyOk = appended && headKept &&
                        (aclPending ? true : (aclChecked ? (aclAllow && aclNoWrite) : true));
                    sb.AppendLine("Journal append-only (право дописывать, не переписывать): " +
                        (appendOnlyOk ? (aclPending ? "OK (ждём прав администратора)" : "OK") : "FAIL") +
                        " (дописал=" + appended + " начало_цело=" + headKept +
                        " acl_проверен=" + aclChecked +
                        " дописывать_можно=" + aclAllow +
                        " переписать_нельзя=" + aclNoWrite +
                        " ждём_прав=" + aclPending + ")");
                    try { File.Delete(probe); } catch { }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Journal append-only ERROR: " + ex.Message);
                }

                // 5г) закрепление владельца цикла: мьютекс запрещает идти
                //     циклу одновременно, но без закрепления в реестре служба
                //     и обычный трей писали бы каждую запись дважды.
                try
                {
                    long now = DateTime.Now.Ticks;
                    long fresh = now - TimeSpan.FromSeconds(5).Ticks;   // владелец жив
                    long stale = now - TimeSpan.FromSeconds(60).Ticks;  // владелец молчит

                    // трей не отбирает журнал у живой службы
                    UsbJournalMonitor.UseOwnerForTest("служба", fresh);
                    string who;
                    bool trayVsService = !UsbJournalMonitor.TryClaimOwnerForTest(false, out who);

                    // служба отбирает журнал у трея (приоритет)
                    UsbJournalMonitor.UseOwnerForTest("трей: u: 1", fresh);
                    bool svcVsTray = UsbJournalMonitor.TryClaimOwnerForTest(true, out who);

                    // никто не владеет - забираем
                    UsbJournalMonitor.UseOwnerForTest("трей: u: 1", stale);
                    bool trayAfterStale = UsbJournalMonitor.TryClaimOwnerForTest(false, out who);

                    // свой владелец продолжаем вести
                    bool trayKeeps = UsbJournalMonitor.TryClaimOwnerForTest(false, out who);

                    // чужая метка с будущего (часы переведены назад) -
                    // молча отдаём журнал, чтобы не наследить
                    UsbJournalMonitor.UseOwnerForTest("служба", now + TimeSpan.FromHours(1).Ticks);
                    bool futureOwner = !UsbJournalMonitor.TryClaimOwnerForTest(false, out who);

                    bool ownerOk = trayVsService && svcVsTray && trayAfterStale &&
                        trayKeeps && futureOwner;
                    sb.AppendLine("Journal owner (служба важнее трея, дубли не пишем): " +
                        (ownerOk ? "OK" : "FAIL") +
                        " (трей_у_службы=" + trayVsService +
                        " служба_у_трея=" + svcVsTray +
                        " трей_после_молчания=" + trayAfterStale +
                        " трей_держит=" + trayKeeps +
                        " метка_из_будущего=" + futureOwner + ")");
                    UsbJournalMonitor.UseOwnerForTest(null, 0);
                }
                catch (Exception ex)
                {
                    UsbJournalMonitor.UseOwnerForTest(null, 0);
                    sb.AppendLine("Journal owner ERROR: " + ex.Message);
                }

                // 3в) состав записей: в записи о подключении не должно быть
                //      VID:PID, "Блокировка=" и "Причина=", метка тома должна
                //      быть заполнена; в записи о файле - модели накопителя
                try
                {
                    StorageDevice sd = new StorageDevice();
                    sd.Serial = "SN_ПРОВЕРКА";
                    sd.UsbId = @"USB\VID_8564&PID_1000\081NS9HV47JMZLUQ";
                    sd.Model = "Модель_Проверка";
                    sd.Label = "Метка_Проверка";
                    // DiskDeviceId не задан - буква не ищется, WMI не трогается
                    string devLine = UsbJournalMonitor.DescribeDevice(sd, null);

                    UsbVolume uv = new UsbVolume();
                    uv.Serial = "SN_ПРОВЕРКА";
                    uv.Model = "Модель_Проверка";
                    string fileLine = UsbJournalMonitor.VolumeDeviceInfo(uv);

                    // Без метки у самого устройства: берётся из кэша цикла
                    // по букве, а буквы нет - остаётся "-"
                    StorageDevice bare = new StorageDevice();
                    bare.Serial = "SN_ПРОВЕРКА";
                    string bareLine = UsbJournalMonitor.DescribeDevice(bare, null);

                    bool fieldsOk =
                        devLine.IndexOf("VID:PID", StringComparison.OrdinalIgnoreCase) < 0 &&
                        devLine.IndexOf("Блокировка=", StringComparison.Ordinal) < 0 &&
                        devLine.IndexOf("Причина=", StringComparison.Ordinal) < 0 &&
                        devLine.IndexOf("Метка=Метка_Проверка", StringComparison.Ordinal) >= 0 &&
                        bareLine.IndexOf("Метка=-", StringComparison.Ordinal) >= 0 &&
                        devLine.IndexOf("SN_ПРОВЕРКА", StringComparison.Ordinal) >= 0 &&
                        devLine.IndexOf("Модель_Проверка", StringComparison.Ordinal) >= 0 &&
                        fileLine.IndexOf("Модель", StringComparison.OrdinalIgnoreCase) < 0 &&
                        fileLine.IndexOf("SN_ПРОВЕРКА", StringComparison.Ordinal) >= 0 &&
                        fileLine.IndexOf("Метка", StringComparison.OrdinalIgnoreCase) >= 0;
                    sb.AppendLine("Journal record fields (no VID:PID/Блокировка/Причина, метка есть): " +
                        (fieldsOk ? "OK" : "FAIL") + " [" + devLine + "] [" + bareLine + "] [" + fileLine + "]");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Journal record fields ERROR: " + ex.Message);
                }

                // 4) горячая клавиша: разбор и форматирование
                try
                {
                    uint mods, vk;
                    bool parseMain = HotkeyStore.TryParse("Ctrl+Alt+U", out mods, out vk);
                    bool parseMods = mods == (HotkeyStore.ModControl | HotkeyStore.ModAlt);
                    bool parseVk = vk == (uint)Keys.U;
                    bool format = parseMain && HotkeyStore.Format(mods, vk) == "Ctrl+Alt+U";
                    // без модификатора - нельзя: перехватили бы набор текста
                    uint m2, v2;
                    bool noMod = !HotkeyStore.TryParse("U", out m2, out v2);
                    // сам модификатор основной клавишей быть не может
                    bool bareMod = !HotkeyStore.TryParse("Ctrl+Shift", out m2, out v2);
                    bool badName = !HotkeyStore.TryParse("Ctrl+НетТакой", out m2, out v2);
                    bool empty = !HotkeyStore.TryParse("", out m2, out v2);
                    // Win в новых комбинациях не появляется (окно назначения
                    // его не добавляет), но сохранённое ранее значение
                    // "Win+..." разобрать надо - иначе вышло бы "комбинация
                    // не распознаётся" у работающей машины.
                    uint m3, v3;
                    bool winLegacy = HotkeyStore.TryParse("Win+Ctrl+U", out m3, out v3) &&
                        (m3 & HotkeyStore.ModWin) != 0 && v3 == (uint)Keys.U;
                    bool keyOk = parseMain && parseMods && parseVk && format &&
                        noMod && bareMod && badName && empty && winLegacy;
                    sb.AppendLine("Hotkey parse: " + (keyOk ? "OK" : "FAIL") +
                        " (mods=" + parseMods + " vk=" + parseVk + " fmt=" + format +
                        " без_мод=" + noMod + " мод_как_клавиша=" + bareMod +
                        " плохое_имя=" + badName + " пусто=" + empty +
                        " win_из_старого=" + winLegacy + ")");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Hotkey parse ERROR: " + ex.Message);
                }

                // 5) парність мов: кожен ключ uk-списку має англійський переклад
                try
                {
                    IList<string> mt = Loc.MissingTranslations();
                    bool langOk = mt.Count == 0;
                    string shown = string.Join(", ", mt);
                    if (langOk) sb.AppendLine("Language pair (кожен український ключ має англійський переклад): OK (" +
                        Loc.Current + ")");
                    else
                        sb.AppendLine("Language pair (переклад відсутній): FAIL (" +
                            mt.Count + ": " + shown + ")");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Language pair ERROR: " + ex.Message);
                }

                // 6) XML задач защиты: задача контроля службы (--ensure-service,
                //    от SYSTEM, раз в минуту) и повтор у задачи сповіщувача.
                try
                {
                    string gx = TrayTask.GuardXmlForTest();
                    string nx = TrayTask.NotifyXmlForTest();
                    bool guardOk =
                        gx.IndexOf("S-1-5-18", StringComparison.Ordinal) >= 0 &&
                        gx.IndexOf("--ensure-service", StringComparison.Ordinal) >= 0 &&
                        gx.IndexOf("<Interval>PT1M</Interval>", StringComparison.Ordinal) >= 0;
                    bool notifyOk =
                        nx.IndexOf("<Repetition>", StringComparison.Ordinal) >= 0 &&
                        nx.IndexOf("<Interval>PT1M</Interval>", StringComparison.Ordinal) >= 0;
                    bool guardAll = guardOk && notifyOk;
                    sb.AppendLine("Task guard XML (SYSTEM + --ensure-service + повтор): " +
                        (guardAll ? "OK" : "FAIL") +
                        " (guard=" + guardOk + " notify_repeat=" + notifyOk + ")");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Task guard XML ERROR: " + ex.Message);
                }
            }
            finally
            {
                // Возвращаем журналу настоящую папку и убираем временную.
                UsbJournal.UseDirectoryForTest(null);
                try
                {
                    Directory.Delete(jDir, true);
                }
                catch
                {
                }
            }

            return sb.ToString().Replace(Environment.NewLine, " | ");
        }

        private static UsbJournalMonitor.FileStamp Stamp(long size, long writtenTicks)
        {
            UsbJournalMonitor.FileStamp s;
            s.Size = size;
            s.WrittenTicks = writtenTicks;
            return s;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }

    // =============================================================
    // Локалізація інтерфейсу (українська / англійська).
    // Мова за замовчуванням - українська. Вибір користувача зберігається в
    // реєстрі (HKCU - трей і сповіщення, HKLM - служба і журнал подій) і
    // застосовується після перезапуску процесу. Українські рядки в коді є
    // одночасно ключами перекладу; англійські - у вбудованому ресурсі
    // strings.en.txt (рядки "український<TAB>english"). Якщо ключа немає,
    // повертається український текст.
    // =============================================================
    internal static class LanguageSettings
    {
        public const string Ukrainian = "uk";
        public const string English = "en";

        private const string UserKey = @"Software\USB_Block";
        private const string MachineKey = @"SOFTWARE\USB_Block";
        private const string ValueName = "UiLanguage";

        public static string Normalize(string lang)
        {
            if (lang != null && lang.Length >= 2 &&
                lang.Substring(0, 2).ToLowerInvariant() == "en")
                return English;
            return Ukrainian;
        }

        private static string Read(RegistryKey root, string sub)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(sub, false))
                {
                    if (k == null) return null;
                    object v = k.GetValue(ValueName);
                    return v as string;
                }
            }
            catch { return null; }
        }

        public static string GetUserLanguage()
        {
            return Normalize(Read(Registry.CurrentUser, UserKey));
        }

        public static string GetMachineLanguage()
        {
            return Normalize(Read(Registry.LocalMachine, MachineKey));
        }

        // Поточний процес: спочатку вибір користувача, потім машинний.
        public static string Get()
        {
            string v = Read(Registry.CurrentUser, UserKey);
            if (v == null) v = Read(Registry.LocalMachine, MachineKey);
            return Normalize(v);
        }

        public static bool SetUserLanguage(string lang)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UserKey))
                {
                    if (k == null) return false;
                    k.SetValue(ValueName, Normalize(lang), RegistryValueKind.String);
                }
                return true;
            }
            catch { return false; }
        }

        public static bool SetMachineLanguage(string lang)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(MachineKey))
                {
                    if (k == null) return false;
                    k.SetValue(ValueName, Normalize(lang), RegistryValueKind.String);
                }
                return true;
            }
            catch { return false; }
        }
    }

    internal static class Loc
    {
        private static string _lang;
        private static Dictionary<string, string> _en;
        private static readonly List<string> _missing = new List<string>();

        // Ключі, яких не знайшлося в англійському файлі (для --diag).
        public static IList<string> MissingEnglish { get { return _missing; } }

        public static string Current
        {
            get { Ensure(); return _lang; }
        }

        private static bool IsSelftestRun()
        {
            try
            {
                foreach (string a in Environment.GetCommandLineArgs())
                {
                    if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "-selftest", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static void Ensure()
        {
            if (_lang != null) return;
            // Самоперевірка завжди виконується українською: її зразки
            // порівнюються з українськими рядками інтерфейсу.
            if (IsSelftestRun()) { _lang = LanguageSettings.Ukrainian; return; }
            _lang = LanguageSettings.Get();
            if (_lang == LanguageSettings.English) LoadEnglish();
        }

        // Читає вбудований strings.en.txt (рядки "український<TAB>english").
        // Викликається і при українському інтерфейсі - для перевірки парності мов.
        private static void LoadEnglish()
        {
            if (_en != null) return;
            _en = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream(
                    "UsbBlockTray.strings.en.txt"))
                {
                    if (s == null) return;
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            if (line.Length == 0 || line[0] == '#') continue;
                            int eq = line.IndexOf('\t');
                            if (eq <= 0) continue;
                            string key = Unescape(line.Substring(0, eq));
                            string val = Unescape(line.Substring(eq + 1));
                            _en[key] = val;
                        }
                    }
                }
            }
            catch { }
        }

        // Парність мов: кожен ключ зі strings.uk.txt мусить мати рядок у
        // strings.en.txt. Повертає ключі без перекладу (для --diag та
        // самоперевірки).
        public static IList<string> MissingTranslations()
        {
            LoadEnglish();
            List<string> miss = new List<string>();
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream(
                    "UsbBlockTray.strings.uk.txt"))
                {
                    if (s == null)
                    {
                        miss.Add("<ресурс UsbBlockTray.strings.uk.txt відсутній>");
                        return miss;
                    }
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            if (line.Length == 0 || line[0] == '#') continue;
                            int tab = line.IndexOf('\t');
                            string key = Unescape(tab > 0 ? line.Substring(0, tab) : line);
                            if (!_en.ContainsKey(key)) miss.Add(key);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                miss.Add("<" + ex.Message + ">");
            }
            return miss;
        }

        public static void ForceUkrainian()
        {
            _lang = LanguageSettings.Ukrainian;
            _en = null;
        }

        public static string T(string uk)
        {
            if (uk == null) return null;
            Ensure();
            if (_lang != LanguageSettings.English || _en == null) return uk;
            string v;
            if (_en.TryGetValue(uk, out v)) return v;
            if (!_missing.Contains(uk)) _missing.Add(uk);
            return uk;
        }

        public static string F(string uk, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, T(uk), args);
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == 'r') { sb.Append('\r'); i++; continue; }
                    if (n == 't') { sb.Append('\t'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}