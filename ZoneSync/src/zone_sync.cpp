// =====================================================================
// Zone Sync - двостороння синхронізація файлів і папок між локальною
// папкою та знімними носіями. Трей + автозапуск + миттєва реакція.
//
// Захищена копія: при запуску з будь-якого місця програма копіюється
// в C:\Program Files\Zone_Sync\ (якщо немає/версія старіша) і далі
// працює звідти; задача автозапуску вказує на канонічну копію.
//
// Збірка: build.bat (windres + g++ -static, MSYS2 UCRT64).
// Версія: APP_VERSION (тут) і VERSIONINFO (res/resources.rc) -
//         при кожній збірці змінюються ОБИДВА місця.
// =====================================================================

#define UNICODE
#define _UNICODE
#include <windows.h>
#include <shellapi.h>
#include <commdlg.h>
#include <shlobj.h>
#include <dbt.h>
#include <tlhelp32.h>
#include <string>
#include <vector>
#include <map>
#include <algorithm>

static const wchar_t* APP_VERSION = L"1.0.4.1";

// ---------------------------------------------------------------------
// Константи.
// ---------------------------------------------------------------------
static const wchar_t* APP_TITLE      = L"Zone Sync";
static const wchar_t* LOG_DIR        = L"D:\\3336\\СЗІ";   // розташування лога
static const wchar_t* LOG_NAME       = L"zone_sync.log";
static const wchar_t* CONFIG_DIR_ENV = L"ZoneSync";
static const wchar_t* CONFIG_NAME    = L"files.txt";
static const wchar_t* DEFAULT_FILE   = L"!Зона ПК.xlsx";
static const wchar_t* MUTEX_NAME     = L"Local\\ZoneSync_SingleInstance";
static const wchar_t* REG_KEY        = L"Software\\ZoneSync";
static const wchar_t* TASK_NAME      = L"ZoneSync";
static const wchar_t* INSTALL_DIR    = L"Zone_Sync";   // у Program Files

// GitHub: перевірка оновлень (version.txt у репозиторії).
static const wchar_t* UPDATE_BASE    =
    L"https://raw.githubusercontent.com/anatolymartyniuk-sudo/myWork/master/ZoneSync/";

static const UINT  WM_TRAYICON     = WM_APP + 1;
static const UINT  TIMER_DEBOUNCE  = 1;
static const int   LOG_MAX_BYTES   = 100 * 1024;
static const int   LOG_KEEP_LINES  = 2000;
static const double TIME_TOLERANCE = 3.0;    // FAT: 2 с гранулярність
static const int   MAX_TREE_DEPTH  = 32;

// Ідентифікатори меню.
enum {
    IDM_SYNC_NOW    = 1001,
    IDM_AUTOSTART   = 1002,
    IDM_FILES       = 1003,
    IDM_ADD_FILE    = 1010,
    IDM_ADD_FOLDER  = 1011,
    IDM_EDIT_LIST   = 1012,
    IDM_UPDATE      = 1013,
    IDM_REMOVE_BASE = 2000,   // + індекс у списку
    IDM_SHOW_LOG    = 1004,
    IDM_LANGUAGE    = 1005,
    IDM_EXIT        = 1006,
};
static const UINT TIMER_ID = 1;

// ---------------------------------------------------------------------
// Мова: UA / EN. Зберігається в HKCU\Software\ZoneSync.
// ---------------------------------------------------------------------
enum Lang { LANG_UA = 0, LANG_EN = 1 };
static Lang g_lang = LANG_UA;

enum StrId {
    S_TITLE, S_SYNC_NOW, S_AUTOSTART, S_FILES, S_ADD_FILE, S_ADD_FOLDER,
    S_EDIT_LIST, S_SHOW_LOG, S_LANGUAGE, S_EXIT, S_SYNC_DONE,
    S_NOTHING_CHANGED, S_FILE_LOCKED, S_BACKUP_FAILED, S_COPY_FAILED,
    S_TASK_INSTALLED, S_TASK_INSTALL_FAIL, S_TASK_REMOVED,
    S_TASK_REMOVE_FAIL, S_REMOVE_CONFIRM, S_ADDED, S_ALREADY_IN_LIST,
    S_REMOVED, S_IDENTICAL, S_COPIED, S_BACKUP, S_MODIFIED,
    S_DIRECTION_LF, S_DIRECTION_FL, S_LOCAL_MISSING, S_EDIT_LIST_HINT,
    S_CONFIG_EMPTY, S_ADD_FILE_TITLE, S_ADD_FOLDER_TITLE,
    S_REMOVE_FILE_TITLE, S_MUST_BE_IN_LOCAL, S_TYPE_FOLDER,
    S_ERROR, S_LANG_APPLIED, S_COPIED_TO_INSTALL,
    S_UPDATE_AVAILABLE, S_UPDATE_INSTALL, S_UPDATE_FAIL,
    S_CHOOSE_FLASH_PLACE, S_NO_FLASH_PLACE, S_STORED_UNTIL, S_BACKUP_CREATED,
    S_DELETED_SYNC, S_DELETE_FAILED, S_CLOSE, S_CLOSE_IN,
    S_CONFLICT, S_SIDE_PC, S_SIDE_FLASH
};

struct Str { const wchar_t* ua; const wchar_t* en; };

static const Str STRINGS[] = {
    { L"Zone Sync",                       L"Zone Sync" },
    { L"Синхронізувати зараз",            L"Sync now" },
    { L"Автозапуск при вході",            L"Start with Windows" },
    { L"Файли і папки синхронізації…",    L"Sync files and folders…" },
    { L"Додати файл…",                    L"Add file…" },
    { L"Додати папку…",                   L"Add folder…" },
    { L"Редагувати список…",              L"Edit list…" },
    { L"Показати лог",                    L"Show log" },
    { L"Мова / Language",                 L"Мова / Language" },
    { L"Вихід",                           L"Exit" },
    { L"Зміни перенесено",                L"Changes transferred" },
    { L"Змін немає",                      L"No changes" },
    { L"Файл відкрито в іншій програмі",  L"File is locked by another program" },
    { L"Не вдалося зробити бекап",        L"Backup failed" },
    { L"Помилка копіювання",              L"Copy failed" },
    { L"Задачу автозапуску встановлено",  L"Autostart task installed" },
    { L"Не вдалося встановити задачу",    L"Failed to install task" },
    { L"Задачу автозапуску видалено",     L"Autostart task removed" },
    { L"Не вдалося видалити задачу",      L"Failed to remove task" },
    { L"Видалити зі списку синхронізації?\n(Сам файл/папку не видаляється.)",
                                          L"Remove from the sync list?\n(The file/folder itself is not deleted.)" },
    { L"Додано до синхронізації",         L"Added to sync list" },
    { L"Файл уже у списку",               L"Already in the list" },
    { L"Видалено зі списку",              L"Removed from list" },
    { L"копії ідентичні",                 L"copies identical" },
    { L"скопійовано",                     L"copied" },
    { L"бекап",                           L"backup" },
    { L"змінено",                         L"modified" },
    { L"local → flash",                   L"local → flash" },
    { L"flash → local",                   L"flash → local" },
    { L"flash → local (локальної копії не існує)",
                                          L"flash → local (local copy missing)" },
    { L"Редагувати список можна і в блокноті:\n{}",
                                          L"You can also edit the list in Notepad:\n{}" },
    { L"Список файлів синхронізації порожній.",
                                          L"The sync file list is empty." },
    { L"Додати файл синхронізації",       L"Add sync file" },
    { L"Додати папку синхронізації",      L"Add sync folder" },
    { L"Видалення зі списку синхронізації", L"Remove from sync list" },
    { L"Файл/папку має існувати в локальній папці:\n{}",
                                          L"The file/folder must exist in the local folder:\n{}" },
    { L"(папка)",                         L"(folder)" },
    { L"Помилка",                         L"Error" },
    { L"Мову змінено. Вступає в силу одразу.",
                                          L"Language changed. Applied immediately." },
    { L"Скопійовано в Program Files\\Zone_Sync\\ і запущено звідти.",
                                          L"Copied to Program Files\\Zone_Sync\\ and started from there." },
    { L"Доступна нова версія: {}",       L"New version available: {}" },
    { L"Встановити оновлення до ",       L"Install update to " },
    { L"Не вдалося встановити оновлення.", L"Failed to install the update." },
    { L"Виберіть друге розташування для '{}' (флешка або папка на диску)",
                                          L"Choose the second location for '{}' (flash drive or any folder)" },
    { L"Флешка не підключена - розташування на флешці: корінь (змінюється у files.txt)",
                                          L"Flash drive not connected - flash location: root (edit in files.txt)" },
    { L"зберігається до",                 L"stored until" },
    { L"Створено бекап",                  L"Backup created" },
    { L"Видалено за синхронізацією",      L"Deleted by sync" },
    { L"Не вдалося видалити",             L"Failed to delete" },
    { L"Закрити",                         L"Close" },
    { L"Закрити (через {} с)",            L"Close (in {} s)" },
    { L"Конфлікт файлів",                 L"File conflict" },
    { L"ПК",                              L"PC" },
    { L"флешка",                          L"flash drive" },
};
static const int STR_COUNT = (int)(sizeof(STRINGS) / sizeof(STRINGS[0]));

static std::wstring S(StrId id)
{
    int i = (int)id;
    if (i < 0 || i >= STR_COUNT) return L"?";
    return g_lang == LANG_EN ? STRINGS[i].en : STRINGS[i].ua;
}

// ---------------------------------------------------------------------
// Рядкові утиліти.
// ---------------------------------------------------------------------
static std::wstring Trim(const std::wstring& s)
{
    size_t a = s.find_first_not_of(L" \t\r\n");
    if (a == std::wstring::npos) return L"";
    size_t b = s.find_last_not_of(L" \t\r\n");
    return s.substr(a, b - a + 1);
}

static std::wstring Format1(const std::wstring& fmt, const std::wstring& a)
{
    size_t p = fmt.find(L"{}");
    if (p == std::wstring::npos) return fmt;
    return fmt.substr(0, p) + a + fmt.substr(p + 2);
}

// Абсолютний шлях (диск ":" + "\") у другій стороні пари.
static bool IsAbsolutePath(const std::wstring& p)
{
    return p.size() >= 3 && p[1] == L':' && (p[2] == L'\\' || p[2] == L'/');
}

static std::wstring Utf8ToWide(std::string s)
{
    if (s.size() >= 3 && (unsigned char)s[0] == 0xEF &&
        (unsigned char)s[1] == 0xBB && (unsigned char)s[2] == 0xBF)
        s = s.substr(3);
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}

static std::string WideToUtf8(const std::wstring& w)
{
    if (w.empty()) return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}

static std::wstring NowStamp()
{
    SYSTEMTIME t; GetLocalTime(&t);
    wchar_t b[32];
    wsprintfW(b, L"%04u-%02u-%02u %02u:%02u:%02u",
              t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return b;
}

static std::wstring TimeStampForFile()
{
    SYSTEMTIME t; GetLocalTime(&t);
    wchar_t b[32];
    wsprintfW(b, L"%04u-%02u-%02u_%02u-%02u-%02u",
              t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return b;
}

// Позначка для конфліктної копії: "ДД-ММ_ГГ-ХХ-СС" (унікальність).
static std::wstring ConflictStamp()
{
    SYSTEMTIME t; GetLocalTime(&t);
    wchar_t b[32];
    wsprintfW(b, L"%02u-%02u_%02u-%02u-%02u",
              t.wDay, t.wMonth, t.wHour, t.wMinute, t.wSecond);
    return b;
}

// ---------------------------------------------------------------------
// Стан.
// ---------------------------------------------------------------------
static std::wstring g_configPath;   // %APPDATA%\ZoneSync\files.txt
static std::wstring g_logPath;      // %APPDATA%\ZoneSync\zone_sync.log
static std::wstring g_installPath;  // Program Files\Zone_Sync\zone_sync.exe
static std::wstring g_backupDir;    // Program Files\Zone_Sync\BackUP
static std::wstring g_progDataDir;  // %ProgramData%\ZoneSync
static bool g_logFallback = false;  // true - лог в %ProgramData% (запись в PF не удалась)
static bool g_bakFallback = false;  // true - бекапы в %ProgramData%
static HWND  g_hwnd = nullptr;
static bool  g_trayAdded = false;

// Елемент синхронізації: 'F' файл / 'D' папка (Directory).
struct SyncItem { wchar_t type; std::wstring localPath; std::wstring flashName; };
static std::vector<SyncItem> g_items;

// ---------------------------------------------------------------------
// Шляхи, мова.
// ---------------------------------------------------------------------
static void InitPaths()
{
    // Канонічна копія: Program Files\Zone_Sync\zone_sync.exe.
    wchar_t pf[MAX_PATH] = { 0 };
    SHGetSpecialFolderPathW(nullptr, pf, CSIDL_PROGRAM_FILES, FALSE);
    g_installPath = std::wstring(pf) + L"\\" + INSTALL_DIR + L"\\zone_sync.exe";

    // %ProgramData%\ZoneSync\ (фолбэк для не-адмін запусков).
    wchar_t pd[MAX_PATH] = { 0 };
    GetEnvironmentVariableW(L"ProgramData", pd, MAX_PATH);
    g_progDataDir = std::wstring(pd) + L"\\" + CONFIG_DIR_ENV;
    CreateDirectoryW(g_progDataDir.c_str(), nullptr);

    // Конфіг: %APPDATA%\ZoneSync\files.txt - запис можливий завжди
    // (Program Files\ доступний користувачам лише на читання, через що
    // видалення зі списку мовчки губилося в несо-адмін екземплярі).
    // Разова міграція зі старого місця (Program Files), якщо файл ще там.
    wchar_t ad[MAX_PATH] = { 0 };
    GetEnvironmentVariableW(L"APPDATA", ad, MAX_PATH);
    std::wstring cfgDir = std::wstring(ad) + L"\\ZoneSync";
    CreateDirectoryW(cfgDir.c_str(), nullptr);
    CreateDirectoryW((cfgDir + L"\\state").c_str(), nullptr);   // знімки пар
    g_configPath = cfgDir + L"\\" + CONFIG_NAME;
    // Міграція зі старих місць (Program Files / ProgramData) - ОДНОРАЗОВА,
    // під прапорцем у реєстрі: якщо користувач колись прибере APPDATA-конфіг,
    // старий файл (з видаленими елементами) не повинен воскресати знову.
    DWORD mig = 0, msz = sizeof(mig);
    bool migrated = RegGetValueW(HKEY_CURRENT_USER, REG_KEY, L"CfgMigrated",
                                 RRF_RT_REG_DWORD, nullptr, &mig, &msz) == ERROR_SUCCESS
                    && mig == 1;
    if (!migrated && GetFileAttributesW(g_configPath.c_str()) == INVALID_FILE_ATTRIBUTES) {
        std::wstring pfDir = g_installPath.substr(0, g_installPath.find_last_of(L"\\"));
        std::wstring oldCfg = pfDir + L"\\" + CONFIG_NAME;
        if (GetFileAttributesW(oldCfg.c_str()) == INVALID_FILE_ATTRIBUTES)
            oldCfg = g_progDataDir + L"\\" + CONFIG_NAME;
        if (GetFileAttributesW(oldCfg.c_str()) != INVALID_FILE_ATTRIBUTES)
            CopyFileW(oldCfg.c_str(), g_configPath.c_str(), TRUE);
    }
    HKEY mk;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, REG_KEY, 0, nullptr,
                        0, KEY_SET_VALUE, nullptr, &mk, nullptr) == ERROR_SUCCESS) {
        DWORD one = 1;
        RegSetValueExW(mk, L"CfgMigrated", 0, REG_DWORD,
                       (const BYTE*)&one, sizeof(one));
        RegCloseKey(mk);
    }

    // Лог: теж у %APPDATA%\ZoneSync\ - запис можливий завжди, для будь-якого
    // екземпляра (лог у Program Files писали лише адмін-екземпляри, звичайний
    // трей писав у фолбэк %ProgramData% - звідси плутанина "де лог").
    g_logPath = cfgDir + L"\\" + LOG_NAME;
    g_backupDir = g_installPath.substr(0, g_installPath.find_last_of(L"\\")) + L"\\BackUP";
    CreateDirectoryW(g_backupDir.c_str(), nullptr);
}

static void LoadLang()
{
    wchar_t buf[8] = { 0 };
    DWORD size = sizeof(buf);
    if (RegGetValueW(HKEY_CURRENT_USER, REG_KEY, L"Lang",
                     RRF_RT_REG_SZ, nullptr, buf, &size) == ERROR_SUCCESS) {
        g_lang = _wcsicmp(buf, L"en") == 0 ? LANG_EN : LANG_UA;
    }
}

static void SaveLang()
{
    HKEY k;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, REG_KEY, 0, nullptr,
                        0, KEY_SET_VALUE, nullptr, &k, nullptr) == ERROR_SUCCESS) {
        const wchar_t* v = (g_lang == LANG_EN) ? L"en" : L"ua";
        RegSetValueExW(k, L"Lang", 0, REG_SZ, (const BYTE*)v,
                       (DWORD)((lstrlenW(v) + 1) * sizeof(wchar_t)));
        RegCloseKey(k);
    }
}

// ---------------------------------------------------------------------
// Список елементів (конфіг UTF-8 BOM; формат: "F|ім'я" або "D|ім'я";
// рядок без префікса = файл - сумісність зі старим конфігом).
// ---------------------------------------------------------------------
static void LoadItems()
{
    g_items.clear();
    HANDLE f = CreateFileW(g_configPath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;   // конфіга немає -> порожній список
    std::string data;
    char buf[4096]; DWORD got = 0;
    while (ReadFile(f, buf, sizeof(buf), &got, nullptr) && got > 0)
        data.append(buf, got);
    CloseHandle(f);
    size_t pos = 0;
    while (pos <= data.size()) {
        size_t nl = data.find('\n', pos);
        std::string line = data.substr(pos, (nl == std::string::npos ? data.size() : nl) - pos);
        std::wstring w = Trim(Utf8ToWide(line));
        if (!w.empty() && w[0] != L'#') {
            SyncItem it;
            // Формат: "F|локальний шлях|ім'я на флешці".
            // Старий формат "F|ім'я" -> шлях з LOG_DIR (міграція).
            size_t p1 = w.find(L'|');
            if (p1 != std::wstring::npos && (w[0] == L'F' || w[0] == L'D')) {
                it.type = w[0];
                size_t p2 = w.find(L'|', p1 + 1);
                if (p2 != std::wstring::npos) {
                    it.localPath = w.substr(p1 + 1, p2 - p1 - 1);
                    it.flashName = w.substr(p2 + 1);
                } else {
                    std::wstring name = w.substr(p1 + 1);
                    it.localPath = std::wstring(LOG_DIR) + L"\\" + name;
                    it.flashName = name;
                }
                if (!it.localPath.empty() && !it.flashName.empty())
                    g_items.push_back(it);
            }
        }
        if (nl == std::string::npos) break;
        pos = nl + 1;
    }
    // Порожній список = порожній список: нічого не вигадуємо. Раніше тут
    // примусово додавався DEFAULT_FILE ("!Зона ПК.xlsx") - через це видалений
    // елемент "сам повертався" при кожному відкритті меню/синхронізації.
}

static void WriteLog(const std::wstring& msg);   // форвард (визначена нижче)

static void SaveItems()
{
    std::string data = "\xEF\xBB\xBF";
    for (size_t i = 0; i < g_items.size(); i++) {
        data += (g_items[i].type == L'D' ? "D|" : "F|");
        data += WideToUtf8(g_items[i].localPath);
        data += "|";
        data += WideToUtf8(g_items[i].flashName);
        data += "\r\n";
    }
    HANDLE f = CreateFileW(g_configPath.c_str(), GENERIC_WRITE, 0,
                           nullptr, CREATE_ALWAYS, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) {
        // Мовчазна втрата списку була причиною "видалення не спрацьовує".
        WriteLog(S(S_ERROR) + L": " + g_configPath + L" (save)");
        return;
    }
    DWORD written = 0;
    WriteFile(f, data.c_str(), (DWORD)data.size(), &written, nullptr);
    CloseHandle(f);
}

static bool ItemInList(wchar_t type, const std::wstring& localPath)
{
    for (size_t i = 0; i < g_items.size(); i++)
        if (g_items[i].type == type &&
            _wcsicmp(g_items[i].localPath.c_str(), localPath.c_str()) == 0) return true;
    return false;
}

static void RemoveItemFromList(wchar_t type, const std::wstring& localPath)
{
    std::vector<SyncItem> out;
    for (size_t i = 0; i < g_items.size(); i++) {
        if (g_items[i].type == type &&
            _wcsicmp(g_items[i].localPath.c_str(), localPath.c_str()) == 0) continue;
        out.push_back(g_items[i]);
    }
    g_items = out;
    SaveItems();
}

static void AddItemToList(wchar_t type, const std::wstring& localPath,
                          const std::wstring& flashName)
{
    if (ItemInList(type, localPath)) return;
    SyncItem it; it.type = type; it.localPath = localPath; it.flashName = flashName;
    g_items.push_back(it);
    SaveItems();
}

// ---------------------------------------------------------------------
// Лог (UTF-8, кільце ~100 КБ).
// ---------------------------------------------------------------------
static void WriteLog(const std::wstring& msg)
{
    std::string line = WideToUtf8(NowStamp() + L"  " + msg) + "\r\n";
    HANDLE f = CreateFileW(g_logPath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, 0, nullptr);
    if (f != INVALID_HANDLE_VALUE) {
        DWORD hi = 0; DWORD lo = GetFileSize(f, &hi);
        CloseHandle(f);
        if (lo > (DWORD)LOG_MAX_BYTES) {
            HANDLE rf = CreateFileW(g_logPath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                                    nullptr, OPEN_EXISTING, 0, nullptr);
            if (rf != INVALID_HANDLE_VALUE) {
                std::string all;
                char buf[8192]; DWORD got = 0;
                while (ReadFile(rf, buf, sizeof(buf), &got, nullptr) && got > 0)
                    all.append(buf, got);
                CloseHandle(rf);
                size_t pos = all.size(); int lines = 0;
                while (pos > 0 && lines <= LOG_KEEP_LINES) {
                    size_t nl = all.rfind('\n', pos - 1);
                    if (nl == std::string::npos) { pos = 0; break; }
                    pos = nl; lines++;
                }
                std::string tail = all.substr(pos == 0 ? 0 : pos + 1);
                HANDLE wf = CreateFileW(g_logPath.c_str(), GENERIC_WRITE, 0,
                                        nullptr, CREATE_ALWAYS, 0, nullptr);
                if (wf != INVALID_HANDLE_VALUE) {
                    DWORD w = 0;
                    WriteFile(wf, tail.c_str(), (DWORD)tail.size(), &w, nullptr);
                    CloseHandle(wf);
                }
            }
        }
    }
    HANDLE f2 = CreateFileW(g_logPath.c_str(), FILE_APPEND_DATA,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            nullptr, OPEN_ALWAYS, 0, nullptr);
    if (f2 != INVALID_HANDLE_VALUE) {
        SetFilePointer(f2, 0, nullptr, FILE_END);
        DWORD w = 0;
        WriteFile(f2, line.c_str(), (DWORD)line.size(), &w, nullptr);
        CloseHandle(f2);
        return;
    }
    // Фолбэк: лог в %ProgramData%\ZoneSync\ (запись в Program Files не удалась).
    g_logFallback = true;
    std::wstring altLog = g_progDataDir + L"\\zone_sync.log";
    HANDLE f3 = CreateFileW(altLog.c_str(), FILE_APPEND_DATA,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            nullptr, OPEN_ALWAYS, 0, nullptr);
    if (f3 != INVALID_HANDLE_VALUE) {
        SetFilePointer(f3, 0, nullptr, FILE_END);
        DWORD w = 0;
        WriteFile(f3, line.c_str(), (DWORD)line.size(), &w, nullptr);
        CloseHandle(f3);
    }
}

// ---------------------------------------------------------------------
// Знімні носії.
// ---------------------------------------------------------------------
static std::vector<std::wstring> GetRemovableRoots()
{
    std::vector<std::wstring> out;
    DWORD bits = GetLogicalDrives();
    for (int i = 0; i < 26; i++) {
        if (!(bits & (1u << i))) continue;
        wchar_t root[4] = { wchar_t(L'A' + i), L':', L'\\', 0 };
        if (GetDriveTypeW(root) == DRIVE_REMOVABLE)
            out.push_back(root);
    }
    return out;
}

// ---------------------------------------------------------------------
// Час файлу (LastWriteTime) як 100ns; файл відсутній - QuadPart=0.
// ---------------------------------------------------------------------
static ULARGE_INTEGER FileTimeOf(const std::wstring& path, bool* exists)
{
    WIN32_FILE_ATTRIBUTE_DATA fa;
    ULARGE_INTEGER u; u.QuadPart = 0;
    *exists = GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fa) != 0;
    if (*exists) { u.LowPart = fa.ftLastWriteTime.dwLowDateTime;
                   u.HighPart = fa.ftLastWriteTime.dwHighDateTime; }
    return u;
}

// ---------------------------------------------------------------------
// Бекап: НЕ рядом с файлом, а в Program Files\Zone_Sync\BackUP\
// (унікальне ім'я = санітизований повний шлях + штамп). Зберігається
// добу, потім видаляється (CleanupBackups).
// ---------------------------------------------------------------------
static std::wstring SanitizeName(const std::wstring& path)
{
    std::wstring s = path;
    for (size_t i = 0; i < s.size(); i++) {
        wchar_t c = s[i];
        if (c == L'\\' || c == L'/' || c == L':') s[i] = L'_';
    }
    return s;
}

static std::wstring DeletionTimeText()
{
    SYSTEMTIME t; GetLocalTime(&t);
    FILETIME ft;
    SYSTEMTIME st = t;
    SystemTimeToFileTime(&st, &ft);
    ULARGE_INTEGER u; u.LowPart = ft.dwLowDateTime; u.HighPart = ft.dwHighDateTime;
    u.QuadPart += 24ULL * 3600ULL * 10000000ULL;   // +24 год
    ft.dwLowDateTime = u.LowPart; ft.dwHighDateTime = u.HighPart;
    SYSTEMTIME out; FileTimeToSystemTime(&ft, &out);
    wchar_t b[32];
    wsprintfW(b, L"%04u-%02u-%02u %02u:%02u",
              out.wYear, out.wMonth, out.wDay, out.wHour, out.wMinute);
    return b;
}

// ---------------------------------------------------------------------
// Знімки стану пар (для поширення видалень). %APPDATA%\ZoneSync\state\.
// Файл на пару: 1-й рядок "T|тип|локальний шлях|друга сторона",
// далі секції сторін: "S|шлях сторони" + "F|відносний шлях|lo|hi".
// Для файлової пари відносний шлях - порожній ("").
// ---------------------------------------------------------------------
struct SideState { std::wstring path; std::map<std::wstring, ULARGE_INTEGER> files; };
struct PairState {
    wchar_t type = 0; std::wstring localPath, flashName;
    std::vector<SideState> sides;
};

static unsigned long long FnvHash(const std::string& s)
{
    unsigned long long h = 1469598103934665603ULL;
    for (size_t i = 0; i < s.size(); i++) {
        h ^= (unsigned char)s[i];
        h *= 1099511628211ULL;
    }
    return h;
}

static std::wstring PairStatePath(const SyncItem& it)
{
    std::string key = std::string(1, it.type == L'D' ? 'D' : 'F') + "|" +
        WideToUtf8(it.localPath) + "|" + WideToUtf8(it.flashName);
    wchar_t hex[32];
    wsprintfW(hex, L"%016I64x", FnvHash(key));
    return g_configPath.substr(0, g_configPath.find_last_of(L"\\")) +
           L"\\state\\" + hex + L".txt";
}

static bool TimeClose(ULARGE_INTEGER a, ULARGE_INTEGER b)
{
    long long d = (long long)a.QuadPart - (long long)b.QuadPart;
    if (d < 0) d = -d;
    return d <= (long long)(TIME_TOLERANCE * 10000000.0);
}

static int FindSideIdx(PairState& ps, const std::wstring& path)
{
    for (size_t i = 0; i < ps.sides.size(); i++)
        if (_wcsicmp(ps.sides[i].path.c_str(), path.c_str()) == 0) return (int)i;
    return -1;
}

static int GetSideIdx(PairState& ps, const std::wstring& path)
{
    int i = FindSideIdx(ps, path);
    if (i >= 0) return i;
    SideState n; n.path = path;
    ps.sides.push_back(n);
    return (int)(ps.sides.size() - 1);
}

static bool LoadPairState(const SyncItem& it, PairState& ps)
{
    ps.type = it.type; ps.localPath = it.localPath; ps.flashName = it.flashName;
    HANDLE f = CreateFileW(PairStatePath(it).c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;
    std::string data;
    char buf[4096]; DWORD got = 0;
    while (ReadFile(f, buf, sizeof(buf), &got, nullptr) && got > 0)
        data.append(buf, got);
    CloseHandle(f);
    size_t pos = 0;
    int cur = -1;
    while (pos <= data.size()) {
        size_t nl = data.find('\n', pos);
        std::string line = data.substr(pos, (nl == std::string::npos ? data.size() : nl) - pos);
        std::wstring w = Trim(Utf8ToWide(line));
        if (!w.empty()) {
            if (w.rfind(L"T|", 0) == 0) {
                // Заголовок: перевірка відповідності пари.
                size_t p1 = w.find(L'|', 2);
                size_t p2 = (p1 == std::wstring::npos) ? std::wstring::npos
                            : w.find(L'|', p1 + 1);
                if (p1 == std::wstring::npos || p2 == std::wstring::npos) { return false; }
                std::wstring lp = w.substr(p1 + 1, p2 - p1 - 1);
                std::wstring fn = w.substr(p2 + 1);
                if (w[2] != it.type ||
                    _wcsicmp(lp.c_str(), it.localPath.c_str()) != 0 ||
                    _wcsicmp(fn.c_str(), it.flashName.c_str()) != 0)
                    return false;
            } else if (w.rfind(L"S|", 0) == 0) {
                cur = GetSideIdx(ps, w.substr(2));
            } else if (w.rfind(L"F|", 0) == 0 && cur >= 0) {
                // Формат: "F|відносний шлях| час (100ns)". Для файлової
                // пари відносний шлях порожній: "F||час".
                std::wstring body = w.substr(2);
                size_t q1 = body.find(L'|');
                if (q1 != std::wstring::npos) {
                    ULARGE_INTEGER t;
                    t.QuadPart = _wcstoui64(body.substr(q1 + 1).c_str(), nullptr, 10);
                    ps.sides[cur].files[body.substr(0, q1)] = t;
                }
            }
        }
        if (nl == std::string::npos) break;
        pos = nl + 1;
    }
    return true;
}

static void SavePairState(const SyncItem& it, const PairState& ps)
{
    std::string data = "\xEF\xBB\xBF";
    data += "T|" + std::string(1, it.type == L'D' ? 'D' : 'F') + "|" +
            WideToUtf8(it.localPath) + "|" + WideToUtf8(it.flashName) + "\r\n";
    for (size_t i = 0; i < ps.sides.size(); i++) {
        data += "S|" + WideToUtf8(ps.sides[i].path) + "\r\n";
        for (std::map<std::wstring, ULARGE_INTEGER>::const_iterator k = ps.sides[i].files.begin();
             k != ps.sides[i].files.end(); ++k) {
            char num[24];
            sprintf(num, "%I64u", (unsigned long long)k->second.QuadPart);
            data += "F|" + WideToUtf8(k->first) + "|" + num + "\r\n";
        }
    }
    HANDLE f = CreateFileW(PairStatePath(it).c_str(), GENERIC_WRITE, 0,
                           nullptr, CREATE_ALWAYS, 0, nullptr);
    if (f != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(f, data.c_str(), (DWORD)data.size(), &written, nullptr);
        CloseHandle(f);
    }
}

// Видалення файлу з бекапом: спочатку копія в BackUP, потім видалення.
static std::wstring BackupOldCopy(const std::wstring& path);   // форвард

static bool DeleteWithBackup(const std::wstring& path)
{
    std::wstring bak = BackupOldCopy(path);
    if (bak.empty()) return false;
    return DeleteFileW(path.c_str()) != 0;
}

// Після поширених видалень: прибрати папки, що стали ПОРОЖНІМИ
// (від глибини вгору до кореня пари; сам корінь пари не чіпаємо).
// Порожня = нікого немає: якщо користувач поклав туди новий файл -
// папка лишається, і файл повернеться звичайною синхронізацією.
static void RemoveEmptyDirsUnder(const std::wstring& pairRoot,
                                 const std::wstring& startDir,
                                 std::vector<std::wstring>* removed)
{
    std::wstring dir = startDir;
    for (;;) {
        if (dir.size() <= pairRoot.size()) break;
        if (_wcsnicmp(dir.c_str(), pairRoot.c_str(), pairRoot.size()) != 0) break;
        if (GetFileAttributesW(dir.c_str()) == INVALID_FILE_ATTRIBUTES) break;
        bool empty = true;
        WIN32_FIND_DATAW fd;
        HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
        if (h != INVALID_HANDLE_VALUE) {
            do {
                if (wcscmp(fd.cFileName, L".") != 0 &&
                    wcscmp(fd.cFileName, L"..") != 0) { empty = false; break; }
            } while (FindNextFileW(h, &fd));
            FindClose(h);
        }
        if (!empty) break;
        if (!RemoveDirectoryW(dir.c_str())) break;
        WriteLog(S(S_DELETED_SYNC) + L": " + dir);
        removed->push_back(dir);
        size_t slash = dir.find_last_of(L'\\');
        if (slash == std::wstring::npos) break;
        dir = dir.substr(0, slash);
    }
}

// Розв'язання конфлікту (файл змінено на ОБОХ сторонах): переможець
// (новіший) лишає собі оригінальне ім'я, переможений перейменовується
// на "<ім'я> (конфлікт від <сторона> <дата>)" і копіюється на сторону
// переможця. Жодна версія не втрачається.
static bool ResolveConflict(const std::wstring& rel, bool winnerLocal,
                            const std::wstring& localRoot,
                            const std::wstring& flashRoot,
                            std::vector<std::wstring>* conflicts,
                            std::wstring* firstError)
{
    std::wstring stem = rel, ext = L"";
    size_t dot = rel.find_last_of(L'.');
    size_t bsl = rel.find_last_of(L'\\');
    if (dot != std::wstring::npos && (bsl == std::wstring::npos || dot > bsl)) {
        stem = rel.substr(0, dot);
        ext = rel.substr(dot);
    }
    std::wstring loserSide = winnerLocal ? S(S_SIDE_FLASH) : S(S_SIDE_PC);
    std::wstring conflictRel = stem + L" (конфлікт від " + loserSide +
                               L" " + ConflictStamp() + L")" + ext;

    std::wstring loserPath = winnerLocal ? flashRoot + L"\\" + rel
                                         : localRoot + L"\\" + rel;
    std::wstring movedTo   = winnerLocal ? flashRoot + L"\\" + conflictRel
                                         : localRoot + L"\\" + conflictRel;
    std::wstring copyDst   = winnerLocal ? localRoot + L"\\" + conflictRel
                                         : flashRoot + L"\\" + conflictRel;

    WriteLog(S(S_CONFLICT) + L": " + rel);
    if (!MoveFileExW(loserPath.c_str(), movedTo.c_str(),
                     MOVEFILE_REPLACE_EXISTING)) {
        WriteLog(L"  " + S(S_ERROR) + L": " + S(S_COPY_FAILED));
        if (firstError->empty()) *firstError = S(S_COPY_FAILED);
        return false;
    }
    if (!CopyFileW(movedTo.c_str(), copyDst.c_str(), TRUE)) {
        WriteLog(L"  " + S(S_ERROR) + L": " + S(S_COPY_FAILED));
        if (firstError->empty()) *firstError = S(S_COPY_FAILED);
        return false;
    }
    WriteLog(L"  " + conflictRel);
    conflicts->push_back(rel + L"  →  " + conflictRel);
    return true;
}

static std::wstring BackupOldCopy(const std::wstring& path)
{
    size_t dot = path.find_last_of(L'.');
    size_t slash = path.find_last_of(L'\\');
    std::wstring base = (slash == std::wstring::npos) ? path : path.substr(slash + 1);
    std::wstring ext, stem = base;
    if (dot != std::wstring::npos && (slash == std::wstring::npos || dot > slash)) {
        stem = base.substr(0, dot - slash - 1);
        ext  = base.substr(dot - slash - 1);
    }
    std::wstring bakDir = g_backupDir;
    if (g_bakFallback) bakDir = g_progDataDir + L"\\BackUP";
    std::wstring bak = bakDir + L"\\" +
        SanitizeName(path) + L"_" + TimeStampForFile() + ext;
    if (CopyFileW(path.c_str(), bak.c_str(), TRUE)) return bak;
    // Программа Files недоступна для записи -> фолбэк на %ProgramData%.
    CreateDirectoryW((g_progDataDir + L"\\BackUP").c_str(), nullptr);
    bak = g_progDataDir + L"\\BackUP\\" +
        SanitizeName(path) + L"_" + TimeStampForFile() + ext;
    if (CopyFileW(path.c_str(), bak.c_str(), TRUE)) { g_bakFallback = true; return bak; }
    return L"";
}

// Видалення бекапів старших доби.
static void CleanupBackupsIn(const std::wstring& dir)
{
    if (GetFileAttributesW(dir.c_str()) == INVALID_FILE_ATTRIBUTES)
        return;
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER cutoff;
    cutoff.LowPart = ft.dwLowDateTime;
    cutoff.HighPart = ft.dwHighDateTime;
    cutoff.QuadPart -= 24ULL * 3600ULL * 10000000ULL;   // старше доби
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        ULARGE_INTEGER u;
        u.LowPart = fd.ftLastWriteTime.dwLowDateTime;
        u.HighPart = fd.ftLastWriteTime.dwHighDateTime;
        if (u.QuadPart < cutoff.QuadPart) {
            std::wstring p = dir + L"\\" + fd.cFileName;
            if (DeleteFileW(p.c_str()))
                WriteLog(std::wstring(L"Бекап видалено (доба минула): ") + fd.cFileName);
        }
    } while (FindNextFileW(h, &fd));
    FindClose(h);
}

static void CleanupBackups()
{
    CleanupBackupsIn(g_backupDir);
    CleanupBackupsIn(g_progDataDir + L"\\BackUP");
}
// Файл заблоковано іншою програмою (БУДЬ-ЯКИЙ тип файлу, не лише Excel):
// спроба відкрити з потрібним доступом без розділення. wantWrite -
// потрібен запис (перезаписуваний бік); інакше лише читання.
static bool IsFileLocked(const std::wstring& path, bool wantWrite)
{
    DWORD attrs = GetFileAttributesW(path.c_str());
    if (attrs == INVALID_FILE_ATTRIBUTES) return false;   // файлу немає
    DWORD access = wantWrite ? (GENERIC_READ | GENERIC_WRITE) : GENERIC_READ;
    HANDLE h = CreateFileW(path.c_str(), access, 0, nullptr,
                           OPEN_EXISTING, 0, nullptr);
    if (h != INVALID_HANDLE_VALUE) { CloseHandle(h); return false; }
    DWORD e = GetLastError();
    if (e == ERROR_SHARING_VIOLATION || e == ERROR_LOCK_VIOLATION) return true;
    if (e == ERROR_ACCESS_DENIED) return true;   // теж не відкрити
    return false;
}

// Створює батьківські папки для шляху (якщо не існують).
static void CreateParentDirs(const std::wstring& path)
{
    size_t slash = path.find_last_of(L'\\');
    if (slash == std::wstring::npos) return;
    std::wstring dir = path.substr(0, slash);
    // Зверху вниз: кожен рівень.
    for (size_t i = 3; i <= dir.size(); i++) {
        if (i == dir.size() || dir[i] == L'\\') {
            std::wstring part = dir.substr(0, i);
            DWORD attrs = GetFileAttributesW(part.c_str());
            if (attrs == INVALID_FILE_ATTRIBUTES ||
                !(attrs & FILE_ATTRIBUTE_DIRECTORY))
                CreateDirectoryW(part.c_str(), nullptr);
        }
    }
}

// ---------------------------------------------------------------------
// Обхід дерева папки: відносні шляхи -> час зміни. .bak_* пропускаються.
// ---------------------------------------------------------------------
struct TreeEntry { std::wstring rel; ULARGE_INTEGER t; };

static void WalkTree(const std::wstring& root, const std::wstring& prefix,
                     int depth, std::vector<TreeEntry>& out)
{
    if (depth > MAX_TREE_DEPTH) return;
    std::wstring pat = root + (prefix.empty() ? L"" : L"\\") + prefix + L"\\*";
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW(pat.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0)
            continue;
        std::wstring rel = prefix.empty() ? std::wstring(fd.cFileName)
                                          : prefix + L"\\" + fd.cFileName;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            WalkTree(root, rel, depth + 1, out);
        } else {
            // Наші власні бекапи в папках не синхронізуємо (інакше
            // вони розмножувалися б на обидва боки).
            if (rel.find(L".bak_") != std::wstring::npos) continue;
            TreeEntry e; e.rel = rel;
            e.t.LowPart = fd.ftLastWriteTime.dwLowDateTime;
            e.t.HighPart = fd.ftLastWriteTime.dwHighDateTime;
            out.push_back(e);
        }
    } while (FindNextFileW(h, &fd));
    FindClose(h);
}

// Чи існує диск, на якому лежить шлях ("F:\..." -> диск F:).
static bool DriveExists(const std::wstring& path)
{
    if (path.size() < 2 || path[1] != L':') return true;   // не диск-шлях
    wchar_t root[3] = { path[0], L':', 0 };
    UINT t = GetDriveTypeW(root);
    return t != DRIVE_UNKNOWN && t != DRIVE_NO_ROOT_DIR;
}

// ---------------------------------------------------------------------
// Синхронізація ОДНОГО файлу (не папка): локальний <-> кожна флешка.
// ---------------------------------------------------------------------
static void SyncFileItem(const SyncItem& item, bool dry,
                         std::vector<std::wstring>* done,
                         std::vector<std::wstring>* backups,
                         std::wstring* firstError, int* identicalCount,
                         std::vector<std::wstring>* deletedOut,
                         std::vector<std::wstring>* conflictsOut,
                         PairState* st)
{
    std::wstring local = item.localPath;
    if (!DriveExists(local)) return;   // диска локальної сторони немає - тихо
    bool localExists = false;
    ULARGE_INTEGER lt = FileTimeOf(local, &localExists);

    // Друга сторона: абсолютний шлях -> одна ціль; відносний -> кожна
    // підключена флешка.
    std::vector<std::wstring> targets;
    if (IsAbsolutePath(item.flashName))
        targets.push_back(item.flashName);
    else {
        std::vector<std::wstring> roots = GetRemovableRoots();
        for (size_t ri = 0; ri < roots.size(); ri++)
            targets.push_back(roots[ri] + item.flashName);
    }

    // Попередній стан сторін (для виявлення видалень). Локальний стан
    // захоплюємо КОПІЄЮ до циклу: після першої цілі він оновиться, а
    // детекція для інших цілей має йти за початковим знімком.
    ULARGE_INTEGER prevLocalT; bool hadPrevLocal = false;
    if (!dry) {
        int i = FindSideIdx(*st, local);
        if (i >= 0) {
            std::map<std::wstring, ULARGE_INTEGER>::iterator f = st->sides[i].files.find(L"");
            if (f != st->sides[i].files.end()) { prevLocalT = f->second; hadPrevLocal = true; }
        }
    }

    for (size_t ti = 0; ti < targets.size(); ti++) {
        std::wstring flash = targets[ti];
        bool flashExists = false;
        ULARGE_INTEGER ft = FileTimeOf(flash, &flashExists);

        // Попередній стан цілі (для видалень і конфліктів).
        ULARGE_INTEGER prevTargetT; bool hadPrevTarget = false;
        if (!dry) {
            int gi = FindSideIdx(*st, flash);
            if (gi >= 0) {
                std::map<std::wstring, ULARGE_INTEGER>::iterator f = st->sides[gi].files.find(L"");
                if (f != st->sides[gi].files.end()) { prevTargetT = f->second; hadPrevTarget = true; }
            }
        }

        // Виявлення видалення: файл був у знімку ОБОХ сторін; тепер зник
        // з одного боку, на іншому досі є з тим самим часом -> користувач
        // видалив його на першому -> поширюємо на другий (з бекапом).
        if (!dry) {
            if (hadPrevLocal && !localExists && flashExists &&
                TimeClose(prevLocalT, ft)) {
                WriteLog(S(S_DELETED_SYNC) + L": " + flash);
                if (DeleteWithBackup(flash)) {
                    deletedOut->push_back(flash);
                } else if (firstError->empty()) {
                    *firstError = S(S_DELETE_FAILED);
                }
                continue;   // стан цілі оновимо в кінці (вже без файлу)
            }
            if (hadPrevTarget && !flashExists && localExists &&
                TimeClose(prevTargetT, lt)) {
                WriteLog(S(S_DELETED_SYNC) + L": " + local);
                if (DeleteWithBackup(local)) {
                    deletedOut->push_back(local);
                    localExists = false;
                } else if (firstError->empty()) {
                    *firstError = S(S_DELETE_FAILED);
                }
                continue;
            }
        }

        if (!flashExists) {
            // Файлу на флешці немає: розміщуємо локальний (вибране місце
            // на флешці може містити підтапки).
            if (!localExists) continue;
            WriteLog(item.flashName + L": " + S(S_DIRECTION_LF));
            WriteLog(L"  " + S(S_MODIFIED) + L": " + local);
            if (dry) continue;
            if (IsFileLocked(local, false)) {
                WriteLog(L"  " + S(S_FILE_LOCKED));
                if (firstError->empty()) *firstError = S(S_FILE_LOCKED);
                continue;
            }
            CreateParentDirs(flash);
            if (CopyFileW(local.c_str(), flash.c_str(), FALSE)) {
                WriteLog(L"  " + S(S_COPIED));
                done->push_back(S(S_DIRECTION_LF));
            } else if (firstError->empty()) {
                *firstError = S(S_COPY_FAILED);
            }
            continue;
        }

        double diff = (double)((long long)ft.QuadPart - (long long)lt.QuadPart) / 10000000.0;
        if (diff < 0) diff = -diff;

        if (localExists && diff <= TIME_TOLERANCE) {
            WriteLog(item.flashName + L": " + S(S_IDENTICAL));
            (*identicalCount)++;
            continue;
        }

        // Конфлікт: файл змінено на обох сторонах після останнього проходу.
        if (!dry && hadPrevLocal && hadPrevTarget) {
            bool chA = !TimeClose(prevLocalT, lt);
            bool chB = !TimeClose(prevTargetT, ft);
            if (chA && chB) {
                ResolveConflict(L"", lt.QuadPart > ft.QuadPart,
                                local, flash, conflictsOut, firstError);
                continue;
            }
        }

        std::wstring src, dst, dirText;
        bool backupDst = true;
        if (!localExists) {
            src = flash; dst = local; dirText = S(S_DIRECTION_FL);
            backupDst = false;
        } else if (lt.QuadPart > ft.QuadPart) {
            src = local; dst = flash; dirText = S(S_DIRECTION_LF);
        } else {
            src = flash; dst = local; dirText = S(S_DIRECTION_FL);
        }

        WriteLog(item.flashName + L": " + dirText);
        WriteLog(L"  " + S(S_MODIFIED) + L": " + src);

        if (dry) continue;

        // Блокування будь-якого файлу (не лише Excel): перевірка ДО бекапу.
        if (IsFileLocked(src, false) || IsFileLocked(dst, true)) {
            WriteLog(L"  " + S(S_FILE_LOCKED));
            if (firstError->empty()) *firstError = S(S_FILE_LOCKED);
            continue;
        }

        if (backupDst) {
            std::wstring bak = BackupOldCopy(dst);
            if (bak.empty()) {
                WriteLog(L"  " + S(S_BACKUP_FAILED));
                if (firstError->empty()) *firstError = S(S_BACKUP_FAILED);
                continue;
            }
            WriteLog(L"  " + S(S_BACKUP) + L": " + bak);
            backups->push_back(S(S_BACKUP) + L": " + item.flashName +
                L" — " + S(S_STORED_UNTIL) + L" " + DeletionTimeText());
        }

        CreateParentDirs(dst);
        if (CopyFileW(src.c_str(), dst.c_str(), FALSE)) {
            WriteLog(L"  " + S(S_COPIED));
            done->push_back(dirText);
        } else {
            DWORD e = GetLastError();
            WriteLog(L"  " + S(S_COPY_FAILED) + L" (GLE=" +
                     std::to_wstring((long long)e) + L")");
            if (firstError->empty())
                *firstError = (e == ERROR_SHARING_VIOLATION || e == ERROR_LOCK_VIOLATION)
                              ? S(S_FILE_LOCKED) : S(S_COPY_FAILED);
        }
    }

    // Оновлення знімка стану за фактичним вмістом сторін.
    if (!dry) {
        bool le = false;
        ULARGE_INTEGER t = FileTimeOf(local, &le);
        int li = GetSideIdx(*st, local);
        if (le) st->sides[li].files[L""] = t; else st->sides[li].files.erase(L"");
        for (size_t ti = 0; ti < targets.size(); ti++) {
            if (!DriveExists(targets[ti])) continue;   // диска немає - стан лишається
            bool te = false;
            ULARGE_INTEGER tt = FileTimeOf(targets[ti], &te);
            int gi = GetSideIdx(*st, targets[ti]);
            if (te) st->sides[gi].files[L""] = tt; else st->sides[gi].files.erase(L"");
        }
    }
}

// ---------------------------------------------------------------------
// Синхронізація ПАПКИ: файлова структура знову стає однаковою.
// Напрямок для кожного файлу - за датою; нові файли копіюються
// в обидва боки. Видалення: файл, який був у знімку сторони X і зник,
// але з тим самим часом існує на стороні Y -> видаляється і на Y
// (з бекапом). Якщо цей файл на Y змінився - видалення не поширюється
// (модифікація перемагає, файл повернеться на X звичайною синхронізацією).
// ---------------------------------------------------------------------
static void SyncFolderPair(const SyncItem& item, bool dry,
                           std::vector<std::wstring>* done,
                           std::vector<std::wstring>* backups,
                           std::wstring* firstError, int* identicalCount,
                           std::vector<std::wstring>* deletedOut,
                           std::vector<std::wstring>* conflictsOut,
                           PairState* st)
{
    std::wstring localRoot = item.localPath;
    if (!DriveExists(localRoot)) return;   // диска локальної сторони немає - тихо

    // Друга сторона: абсолютний шлях -> одна ціль; відносний -> кожна
    // підключена флешка.
    std::vector<std::wstring> targets;
    if (IsAbsolutePath(item.flashName))
        targets.push_back(item.flashName);
    else {
        std::vector<std::wstring> roots = GetRemovableRoots();
        for (size_t ri = 0; ri < roots.size(); ri++)
            targets.push_back(roots[ri] + item.flashName);
    }

    // Знімок: попередній вміст локальної сторони (копія до циклу - після
    // першої цілі знімок оновиться, а детекція інших цілей має йти за
    // початковим станом).
    std::map<std::wstring, ULARGE_INTEGER> prevLocalFiles;
    bool hasPrevLocal = false;
    if (!dry) {
        int i = FindSideIdx(*st, localRoot);
        if (i >= 0) { prevLocalFiles = st->sides[i].files; hasPrevLocal = true; }
    }

    for (size_t ti = 0; ti < targets.size(); ti++) {
        std::wstring flashRoot = targets[ti];

        // Попередній стан цілі (потрібен і детекції видалень, і конфліктам).
        std::map<std::wstring, ULARGE_INTEGER> prevTargetFiles;
        bool hasPrevTarget = false;
        if (!dry) {
            int gi = FindSideIdx(*st, flashRoot);
            if (gi >= 0) { prevTargetFiles = st->sides[gi].files; hasPrevTarget = true; }
        }

        std::vector<TreeEntry> localTree, flashTree;
        WalkTree(localRoot, L"", 0, localTree);
        WalkTree(flashRoot, L"", 0, flashTree);

        // Виявлення видалень (тільки якщо знімки обох боків відомі).
        if (!dry) {
            if (hasPrevLocal && hasPrevTarget) {
                std::map<std::wstring, ULARGE_INTEGER> curA, curB;
                for (size_t i = 0; i < localTree.size(); i++)
                    curA[localTree[i].rel] = localTree[i].t;
                for (size_t i = 0; i < flashTree.size(); i++)
                    curB[flashTree[i].rel] = flashTree[i].t;
                std::vector<std::wstring> delDirsA, delDirsB;   // батьківські папки видалених
                // Видалено на локальній стороні -> видалити на другій.
                for (std::map<std::wstring, ULARGE_INTEGER>::iterator k = prevLocalFiles.begin();
                     k != prevLocalFiles.end(); ++k) {
                    if (k->first.empty()) continue;
                    if (curA.find(k->first) != curA.end()) continue;
                    std::map<std::wstring, ULARGE_INTEGER>::iterator ib = curB.find(k->first);
                    if (ib == curB.end() || !TimeClose(k->second, ib->second)) continue;
                    std::wstring delPath = flashRoot + L"\\" + k->first;
                    WriteLog(S(S_DELETED_SYNC) + L": " + delPath);
                    if (DeleteWithBackup(delPath)) {
                        deletedOut->push_back(delPath);
                        size_t dsl = delPath.find_last_of(L'\\');
                        if (dsl != std::wstring::npos && dsl > flashRoot.size()) {
                            std::wstring d = delPath.substr(0, dsl);
                            if (std::find(delDirsB.begin(), delDirsB.end(), d) == delDirsB.end())
                                delDirsB.push_back(d);
                        }
                    } else if (firstError->empty()) {
                        *firstError = S(S_DELETE_FAILED);
                    }
                }
                // Видалено на другій стороні -> видалити на локальній.
                for (std::map<std::wstring, ULARGE_INTEGER>::iterator k = prevTargetFiles.begin();
                     k != prevTargetFiles.end(); ++k) {
                    if (k->first.empty()) continue;
                    if (curB.find(k->first) != curB.end()) continue;
                    std::map<std::wstring, ULARGE_INTEGER>::iterator ia = curA.find(k->first);
                    if (ia == curA.end() || !TimeClose(k->second, ia->second)) continue;
                    std::wstring delPath = localRoot + L"\\" + k->first;
                    WriteLog(S(S_DELETED_SYNC) + L": " + delPath);
                    if (DeleteWithBackup(delPath)) {
                        deletedOut->push_back(delPath);
                        size_t dsl = delPath.find_last_of(L'\\');
                        if (dsl != std::wstring::npos && dsl > localRoot.size()) {
                            std::wstring d = delPath.substr(0, dsl);
                            if (std::find(delDirsA.begin(), delDirsA.end(), d) == delDirsA.end())
                                delDirsA.push_back(d);
                        }
                    } else if (firstError->empty()) {
                        *firstError = S(S_DELETE_FAILED);
                    }
                }
                // Папки, що спорожніли після видалення файлів, теж прибрати.
                for (size_t d = 0; d < delDirsB.size(); d++)
                    RemoveEmptyDirsUnder(flashRoot, delDirsB[d], deletedOut);
                for (size_t d = 0; d < delDirsA.size(); d++)
                    RemoveEmptyDirsUnder(localRoot, delDirsA[d], deletedOut);
                // Після видалень переходимо на свіжі дерева.
                localTree.clear(); flashTree.clear();
                WalkTree(localRoot, L"", 0, localTree);
                WalkTree(flashRoot, L"", 0, flashTree);
            }
        }

        int locIdentical = 0, locCopied = 0;

        // Індексуємо флешкове дерево за відносним шляхом.
        std::map<std::wstring, ULARGE_INTEGER> flashMap;
        for (size_t i = 0; i < flashTree.size(); i++)
            flashMap[flashTree[i].rel] = flashTree[i].t;

        for (size_t i = 0; i < localTree.size(); i++) {
            const std::wstring& rel = localTree[i].rel;
            std::map<std::wstring, ULARGE_INTEGER>::iterator it = flashMap.find(rel);
            if (it == flashMap.end()) {
                // Є тільки локально -> на флешку.
                WriteLog(item.flashName + L"\\" + rel + L": " + S(S_DIRECTION_LF));
                if (dry) continue;
                std::wstring src = localRoot + L"\\" + rel;
                std::wstring dst = flashRoot + L"\\" + rel;
                CreateParentDirs(dst);
                if (CopyFileW(src.c_str(), dst.c_str(), FALSE)) {
                    WriteLog(L"  " + S(S_COPIED));
                    done->push_back(S(S_DIRECTION_LF));
                    locCopied++;
                } else if (firstError->empty()) {
                    *firstError = S(S_COPY_FAILED);
                }
                continue;
            }
            double diff = (double)((long long)localTree[i].t.QuadPart -
                                   (long long)it->second.QuadPart) / 10000000.0;
            if (diff < 0) diff = -diff;
            if (diff <= TIME_TOLERANCE) { (*identicalCount)++; locIdentical++; continue; }   // однакові

            // Конфлікт: файл змінено на ОБОХ сторонах після останнього
            // проходу (жодна не збігається зі своїм знімком). Без знімків
            // конфлікт не визначити -> звичайне правило "новіший перемагає".
            if (!dry && hasPrevLocal && hasPrevTarget) {
                bool chA = false, chB = false;
                std::map<std::wstring, ULARGE_INTEGER>::iterator pa =
                    prevLocalFiles.find(rel);
                if (pa != prevLocalFiles.end())
                    chA = !TimeClose(pa->second, localTree[i].t);
                std::map<std::wstring, ULARGE_INTEGER>::iterator pb =
                    prevTargetFiles.find(rel);
                if (pb != prevTargetFiles.end())
                    chB = !TimeClose(pb->second, it->second);
                if (chA && chB) {
                    ResolveConflict(rel, localTree[i].t.QuadPart > it->second.QuadPart,
                                    localRoot, flashRoot, conflictsOut, firstError);
                    continue;
                }
            }

            bool newerLocal = localTree[i].t.QuadPart > it->second.QuadPart;
            std::wstring src = newerLocal ? localRoot + L"\\" + rel
                                          : flashRoot + L"\\" + rel;
            std::wstring dst = newerLocal ? flashRoot + L"\\" + rel
                                          : localRoot + L"\\" + rel;
            std::wstring dirText = newerLocal ? S(S_DIRECTION_LF) : S(S_DIRECTION_FL);

            WriteLog(item.flashName + L"\\" + rel + L": " + dirText);
            if (dry) continue;

            // Блокування будь-якого файлу.
            if (IsFileLocked(src, false) || IsFileLocked(dst, true)) {
                WriteLog(L"  " + S(S_FILE_LOCKED));
                if (firstError->empty()) *firstError = S(S_FILE_LOCKED);
                continue;
            }

            std::wstring bak = BackupOldCopy(dst);
            if (bak.empty()) {
                WriteLog(L"  " + S(S_BACKUP_FAILED));
                if (firstError->empty()) *firstError = S(S_BACKUP_FAILED);
                continue;
            }
            WriteLog(L"  " + S(S_BACKUP) + L": " + bak);
            backups->push_back(S(S_BACKUP) + L": " + item.flashName + L"\\" + rel +
                L" — " + S(S_STORED_UNTIL) + L" " + DeletionTimeText());
            if (CopyFileW(src.c_str(), dst.c_str(), FALSE)) {
                WriteLog(L"  " + S(S_COPIED));
                done->push_back(dirText);
                locCopied++;
            } else if (firstError->empty()) {
                *firstError = S(S_COPY_FAILED);
            }
        }

        // Файли, що є тільки на флешці -> на локальний бік.
        for (size_t i = 0; i < flashTree.size(); i++) {
            const std::wstring& rel = flashTree[i].rel;
            bool inLocal = false;
            for (size_t j = 0; j < localTree.size(); j++)
                if (_wcsicmp(localTree[j].rel.c_str(), rel.c_str()) == 0) { inLocal = true; break; }
            if (inLocal) continue;

            std::wstring src = flashRoot + L"\\" + rel;
            std::wstring dst = localRoot + L"\\" + rel;
            WriteLog(item.flashName + L"\\" + rel + L": " + S(S_DIRECTION_FL));
            if (dry) continue;
            CreateParentDirs(dst);
            if (CopyFileW(src.c_str(), dst.c_str(), FALSE)) {
                WriteLog(L"  " + S(S_COPIED));
                done->push_back(S(S_DIRECTION_FL));
                locCopied++;
            } else if (firstError->empty()) {
                *firstError = S(S_COPY_FAILED);
            }
        }

        // Підсумок пари, якщо нічого не копіювалось: у лог було "тихо"
        // (запис з'являвся лише для файлових пар).
        if (locCopied == 0 && firstError->empty()) {
            if (locIdentical > 0)
                WriteLog(item.flashName + L": " + S(S_IDENTICAL) + L" (" +
                         std::to_wstring((long long)locIdentical) + L")");
            else if (!localTree.empty() || !flashTree.empty())
                WriteLog(item.flashName + L": " + S(S_NOTHING_CHANGED));
        }

        // Оновлення знімка стану за фінальним вмістом сторін.
        if (!dry) {
            std::vector<TreeEntry> a2, b2;
            WalkTree(localRoot, L"", 0, a2);
            WalkTree(flashRoot, L"", 0, b2);
            std::map<std::wstring, ULARGE_INTEGER> mA, mB;
            for (size_t i = 0; i < a2.size(); i++) mA[a2[i].rel] = a2[i].t;
            for (size_t i = 0; i < b2.size(); i++) mB[b2[i].rel] = b2[i].t;
            int li = GetSideIdx(*st, localRoot);
            st->sides[li].files = mA;
            int gi = GetSideIdx(*st, flashRoot);
            st->sides[gi].files = mB;
        }
    }
}

// ---------------------------------------------------------------------
// Один прохід синхронізації. balloonOut - текст для баллуна
// (порожній - нічого не змінено, silent); isError - балун-помилка.
// ---------------------------------------------------------------------
static void DoSyncOnce(bool dry, std::wstring* balloonOut, bool* isError,
                       std::vector<std::wstring>* deletedOut,
                       std::vector<std::wstring>* conflictsOut)
{
    std::vector<std::wstring> done;    // напрямки скопійованих
    std::vector<std::wstring> backups; // створені бекапи
    std::wstring firstError;
    int identical = 0;                 // пар "копії ідентичні"
    std::vector<std::wstring> delLocal, conLocal;
    if (!deletedOut) deletedOut = &delLocal;
    if (!conflictsOut) conflictsOut = &conLocal;

    LoadItems();   // щоразу свіжо (редагування списку).

    if (g_items.empty()) {
        WriteLog(S(S_CONFIG_EMPTY));
        return;
    }

    for (size_t i = 0; i < g_items.size(); i++) {
        PairState st;
        bool haveState = dry ? false : LoadPairState(g_items[i], st);
        if (g_items[i].type == L'F')
            SyncFileItem(g_items[i], dry, &done, &backups, &firstError, &identical,
                         deletedOut, conflictsOut, &st);
        else
            SyncFolderPair(g_items[i], dry, &done, &backups, &firstError, &identical,
                           deletedOut, conflictsOut, &st);
        if (haveState || !dry) SavePairState(g_items[i], st);
    }

    if (balloonOut) {
        if (!firstError.empty()) {
            *balloonOut = firstError; *isError = true;
        } else if (!done.empty() || !backups.empty()) {
            std::wstring lines;
            size_t shown = 0;
            for (size_t i = 0; i < done.size() && shown < 4; i++, shown++) {
                if (shown) lines += L"\n";
                lines += done[i];
            }
            for (size_t i = 0; i < backups.size() && shown < 4; i++, shown++) {
                if (shown) lines += L"\n";
                lines += backups[i];
            }
            if (done.size() + backups.size() > 4) lines += L"  …";
            std::wstring head = done.empty() ? S(S_BACKUP_CREATED) : S(S_SYNC_DONE);
            *balloonOut = head + L"\n" + lines;
            *isError = false;
        } else if (identical > 0) {
            // Копії ідентичні - теж повідомлення (з кількістю пар).
            *balloonOut = S(S_IDENTICAL) + L" (" +
                std::to_wstring((long long)identical) + L")";
            *isError = false;
        } else {
            // Нічого не знайдено (немає флешки/пар) - тихо, лише лог.
            *balloonOut = L""; *isError = false;
        }
    }
}

// ---------------------------------------------------------------------
// Вікно видалень: ПОСЕРЕДИНІ монітора, поверх усього, висить поки
// користувач не закриє. Кнопка "Закрити" активується через 15 с.
// ---------------------------------------------------------------------
static HWND g_delWnd = nullptr;
static int  g_delCountdown = 0;

static LRESULT CALLBACK DelWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg) {
    case WM_TIMER:
        if (wp == 1 && g_delCountdown > 0) {
            g_delCountdown--;
            HWND btn = GetDlgItem(hwnd, 100);
            if (g_delCountdown > 0)
                SetWindowTextW(btn, Format1(S(S_CLOSE_IN),
                    std::to_wstring((long long)g_delCountdown)).c_str());
            else {
                SetWindowTextW(btn, S(S_CLOSE).c_str());
                EnableWindow(btn, TRUE);
                SetForegroundWindow(hwnd);
                KillTimer(hwnd, 1);
            }
        }
        return 0;
    case WM_COMMAND:
        if (LOWORD(wp) == 100 && g_delCountdown == 0) DestroyWindow(hwnd);
        return 0;
    case WM_KEYDOWN:
        if (wp == VK_ESCAPE && g_delCountdown == 0) DestroyWindow(hwnd);
        return 0;
    case WM_DESTROY:
        KillTimer(hwnd, 1);
        g_delWnd = nullptr;
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

// Текст центрального звіту: видалення + конфлікти одним вікном.
static std::wstring SideReportText(const std::vector<std::wstring>& deleted,
                                   const std::vector<std::wstring>& conflicts)
{
    std::wstring t;
    if (!deleted.empty()) {
        t += S(S_DELETED_SYNC) + L":";
        for (size_t i = 0; i < deleted.size(); i++) t += L"\n" + deleted[i];
    }
    if (!conflicts.empty()) {
        if (!t.empty()) t += L"\n";
        t += S(S_CONFLICT) + L":";
        for (size_t i = 0; i < conflicts.size(); i++) t += L"\n" + conflicts[i];
    }
    return t;
}

static void ShowDeletionsWindow(const std::wstring& text)
{
    if (g_delWnd) DestroyWindow(g_delWnd);   // новий прохід - нове вікно

    static HFONT g_delFont = nullptr;
    if (!g_delFont) {
        // Системний шрифт повідомлень (масштабується під DPI).
        NONCLIENTMETRICSW ncm = { sizeof(ncm) };
        SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof(ncm), &ncm, 0);
        g_delFont = CreateFontIndirectW(&ncm.lfMessageFont);
    }

    WNDCLASSEXW wc = { 0 };
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DelWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hIcon = LoadIconW(wc.hInstance, MAKEINTRESOURCEW(1));
    wc.hIconSm = (HICON)LoadImageW(wc.hInstance, MAKEINTRESOURCEW(1), IMAGE_ICON,
                                   GetSystemMetrics(SM_CXSMICON),
                                   GetSystemMetrics(SM_CYSMICON), LR_DEFAULTCOLOR);
    wc.lpszClassName = L"ZoneSyncDelWnd";
    RegisterClassExW(&wc);

    int lines = 1;
    for (size_t i = 0; i < text.size(); i++)
        if (text[i] == L'\n') lines++;

    const int BH = 30;               // кнопка
    int textH = lines * 24 + 10;
    int contentH = 14 + textH + 14 + BH + 16;   // весь клієнтський вміст
    int contentW = 620;
    // Висота ВІКНА = вміст + заголовок/рамки (інакше кнопка обрізалась).
    DWORD wstyle = WS_POPUP | WS_CAPTION | WS_SYSMENU;
    RECT rc = { 0, 0, contentW, contentH };
    AdjustWindowRect(&rc, wstyle, FALSE);
    int ww = rc.right - rc.left, wh = rc.bottom - rc.top;

    int sw = GetSystemMetrics(SM_CXSCREEN);
    int sh = GetSystemMetrics(SM_CYSCREEN);
    if (wh > sh - 60) { contentH -= (wh - (sh - 60)); wh = sh - 60; }
    if (ww > sw - 40) { contentW -= (ww - (sw - 40)); ww = sw - 40; }
    int x = (sw - ww) / 2, y = (sh - wh) / 2;

    g_delWnd = CreateWindowExW(WS_EX_TOPMOST, wc.lpszClassName, APP_TITLE,
                               wstyle, x, y, ww, wh,
                               nullptr, nullptr, wc.hInstance, nullptr);

    HWND stTxt = CreateWindowExW(0, L"STATIC", text.c_str(),
                                 WS_CHILD | WS_VISIBLE | SS_LEFT,
                                 14, 14, contentW - 28, textH,
                                 g_delWnd, nullptr, wc.hInstance, nullptr);
    HWND btn = CreateWindowExW(0, L"BUTTON", Format1(S(S_CLOSE_IN), L"15").c_str(),
                               WS_CHILD | WS_VISIBLE | WS_DISABLED | BS_DEFPUSHBUTTON,
                               (contentW - 180) / 2, contentH - BH - 16,
                               180, BH,
                               g_delWnd, (HMENU)100, wc.hInstance, nullptr);
    SendMessageW(stTxt, WM_SETFONT, (WPARAM)g_delFont, TRUE);
    SendMessageW(btn, WM_SETFONT, (WPARAM)g_delFont, TRUE);

    g_delCountdown = 15;
    SetTimer(g_delWnd, 1, 1000, nullptr);
    ShowWindow(g_delWnd, SW_SHOWNORMAL);
    SetForegroundWindow(g_delWnd);
}

// ---------------------------------------------------------------------
// Балун.
// ---------------------------------------------------------------------
static void ShowBalloon(const std::wstring& text, bool error)
{
    if (!g_trayAdded || !g_hwnd) {
        WriteLog(L"balloon: пропущено (трея немає)  " + text);
        return;
    }
    NOTIFYICONDATAW ni = { sizeof(ni) };
    ni.hWnd = g_hwnd; ni.uID = 1;
    ni.uFlags = NIF_INFO;
    ni.dwInfoFlags = error ? NIIF_ERROR : NIIF_INFO;
    lstrcpynW(ni.szInfoTitle, APP_TITLE, 64);
    lstrcpynW(ni.szInfo, text.c_str(), 256);
    BOOL ok = Shell_NotifyIconW(NIM_MODIFY, &ni);
    // Діагностика: якщо в лог є "balloon: ok", а користувач нічого не бачив -
    // справа в налаштуваннях сповіщень Windows, а не в програмі.
    WriteLog(std::wstring(L"balloon: ") + (ok ? L"ok" : L"FAIL") +
             L"  " + text);
}

// ---------------------------------------------------------------------
// Перевірка оновлень з GitHub: раз на добу (остання - в реєстрі).
// version.txt у репозиторії містить номер останньої версії; якщо вона
// новіша за нашу - завантажуємо zone_sync.exe і пропонуємо оновитись.
// ---------------------------------------------------------------------
static std::wstring g_updateVersion;   // доступна нова версія (порожньо - немає)
static std::wstring g_updateExePath;   // завантажений новий exe
static std::vector<std::wstring> g_lastRemovable;   // останній знімний набір (для опитування)

static std::wstring UpdateCacheDir()
{
    wchar_t pd[MAX_PATH] = { 0 };
    GetEnvironmentVariableW(L"ProgramData", pd, MAX_PATH);
    std::wstring dir = std::wstring(pd) + L"\\" + CONFIG_DIR_ENV + L"\\update";
    CreateDirectoryW(dir.c_str(), nullptr);
    return dir;
}

static bool DownloadToFile(const wchar_t* url, const std::wstring& path)
{
    HRESULT hr = URLDownloadToFileW(nullptr, url, path.c_str(), 0, nullptr);
    return hr == S_OK;
}

struct Ver4 { int a, b, c, d; };

static Ver4 ParseVer4(const std::wstring& s)
{
    Ver4 v = { 0, 0, 0, 0 };
    int parts[4] = { 0, 0, 0, 0 };
    int n = 0, val = 0; bool any = false;
    for (size_t i = 0; i <= s.size(); i++) {
        if (i == s.size() || s[i] == L'.') {
            if (n < 4) parts[n] = val;
            n++; val = 0; any = false;
            if (n >= 4) break;
        } else if (s[i] >= L'0' && s[i] <= L'9') {
            val = val * 10 + (s[i] - L'0'); any = true;
        }
    }
    v.a = parts[0]; v.b = parts[1]; v.c = parts[2]; v.d = parts[3];
    return v;
}

static bool VerGreater(const Ver4& x, const Ver4& y)
{
    if (x.a != y.a) return x.a > y.a;
    if (x.b != y.b) return x.b > y.b;
    if (x.c != y.c) return x.c > y.c;
    return x.d > y.d;
}

// Версія exe з ресурсу VERSIONINFO; файл відсутній - 0.0.0.0.
static Ver4 FileVersionOf(const std::wstring& path)
{
    Ver4 v = { 0, 0, 0, 0 };
    DWORD handle = 0;
    DWORD sz = GetFileVersionInfoSizeW(path.c_str(), &handle);
    if (sz == 0) return v;
    std::vector<BYTE> data(sz);
    if (!GetFileVersionInfoW(path.c_str(), 0, sz, &data[0])) return v;
    VS_FIXEDFILEINFO* fi = nullptr; UINT len = 0;
    if (VerQueryValueW(&data[0], L"\\", (LPVOID*)&fi, &len) && fi) {
        v.a = HIWORD(fi->dwFileVersionMS);
        v.b = LOWORD(fi->dwFileVersionMS);
        v.c = HIWORD(fi->dwFileVersionLS);
        v.d = LOWORD(fi->dwFileVersionLS);
    }
    return v;
}

static bool IsUpdateDue()
{
    ULARGE_INTEGER now;
    FILETIME ft; GetSystemTimeAsFileTime(&ft);
    now.LowPart = ft.dwLowDateTime; now.HighPart = ft.dwHighDateTime;
    ULARGE_INTEGER last; last.QuadPart = 0;
    DWORD size = sizeof(last);
    if (RegGetValueW(HKEY_CURRENT_USER, REG_KEY, L"LastUpdateCheck",
                     RRF_RT_REG_QWORD, nullptr, &last, &size) != ERROR_SUCCESS)
        return true;   // ще не перевіряли ніколи
    unsigned long long day = 24ULL * 3600ULL * 10000000ULL;
    return (now.QuadPart - last.QuadPart) >= day;
}

static void MarkUpdateChecked()
{
    FILETIME ft; GetSystemTimeAsFileTime(&ft);
    HKEY k;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, REG_KEY, 0, nullptr,
                        0, KEY_SET_VALUE, nullptr, &k, nullptr) == ERROR_SUCCESS) {
        RegSetValueExW(k, L"LastUpdateCheck", 0, REG_QWORD,
                       (const BYTE*)&ft, sizeof(ft));
        RegCloseKey(k);
    }
}

// Фон-потік: перевірка версії + завантаження нового exe.
static DWORD WINAPI UpdateCheckThread(LPVOID)
{
    MarkUpdateChecked();
    std::wstring dir = UpdateCacheDir();
    std::wstring verFile = dir + L"\\version.txt";
    DeleteFileW(verFile.c_str());
    if (!DownloadToFile((std::wstring(UPDATE_BASE) + L"version.txt").c_str(), verFile)) {
        WriteLog(L"Оновлення: сервер недоступний");
        return 0;
    }
    HANDLE f = CreateFileW(verFile.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return 0;
    std::string data; char buf[256]; DWORD got = 0;
    while (ReadFile(f, buf, sizeof(buf), &got, nullptr) && got > 0)
        data.append(buf, got);
    CloseHandle(f);
    std::wstring online = Trim(Utf8ToWide(data));
    if (online.empty()) return 0;

    Ver4 ov = ParseVer4(online);
    Ver4 cv = ParseVer4(APP_VERSION);
    if (!VerGreater(ov, cv)) {
        WriteLog(L"Оновлення: версія актуальна (" + std::wstring(APP_VERSION) + L")");
        return 0;
    }

    // Нова версія - завантажуємо exe.
    std::wstring exeFile = dir + L"\\zone_sync.exe";
    DeleteFileW(exeFile.c_str());
    if (!DownloadToFile((std::wstring(UPDATE_BASE) + L"zone_sync.exe").c_str(), exeFile)) {
        WriteLog(L"Оновлення: не вдалося завантажити exe");
        return 0;
    }
    // Мінімальна перевірка цілісності: не порожній файл.
    HANDLE f2 = CreateFileW(exeFile.c_str(), GENERIC_READ, FILE_SHARE_READ,
                            nullptr, OPEN_EXISTING, 0, nullptr);
    if (f2 == INVALID_HANDLE_VALUE) return 0;
    DWORD hi2 = 0; DWORD lo2 = GetFileSize(f2, &hi2);
    CloseHandle(f2);
    if (lo2 < 10000) { DeleteFileW(exeFile.c_str()); return 0; }

    g_updateVersion = online;
    g_updateExePath = exeFile;
    WriteLog(L"Оновлення: доступна версія " + online + L", exe завантажено");
    if (g_hwnd) PostMessageW(g_hwnd, WM_APP + 3, 0, 0);
    return 0;
}

static void StartUpdateCheck()
{
    if (!IsUpdateDue()) return;
    HANDLE t = CreateThread(nullptr, 0, UpdateCheckThread, nullptr, 0, nullptr);
    if (t) CloseHandle(t);   // потік живе самостійно
}

// Установка оновлення: підвищений cmd чекає 2 с (поки ми вийдемо),
// перезаписує exe в Program Files і стартує новий трей.
static void InstallUpdate()
{
    if (g_updateExePath.empty() ||
        GetFileAttributesW(g_updateExePath.c_str()) == INVALID_FILE_ATTRIBUTES) return;
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring params = std::wstring(
        L"/c timeout /t 2 /nobreak >nul & copy /y \"") + g_updateExePath +
        L"\" \"" + exe + L"\" & start \"\" \"" + exe + L"\"";
    SHELLEXECUTEINFOW se = { sizeof(se) };
    se.fMask = SEE_MASK_NOCLOSEPROCESS;
    se.lpVerb = L"runas";
    se.lpFile = L"cmd.exe";
    se.lpParameters = params.c_str();
    se.nShow = SW_SHOWNORMAL;
    if (ShellExecuteExW(&se)) {
        CloseHandle(se.hProcess);
        // Виходимо: cmd перезапише файл за 2 с і стартує новий трей.
        if (g_hwnd) PostMessageW(g_hwnd, WM_CLOSE, 0, 0);
    } else {
        ShowBalloon(S(S_UPDATE_FAIL), true);
    }
}

// ---------------------------------------------------------------------
// Захищена копія: Program Files\Zone_Sync\.
// ---------------------------------------------------------------------
static bool StartTarget()
{
    // Запуск через explorer.exe -> канонічний екземпляр НЕ успадковує
    // підвищення установника. З підвищеного процесу Windows ховає
    // повідомлення-балони, плюс плутанина з правами запису.
    SHELLEXECUTEINFOW se = { sizeof(se) };
    se.fMask = SEE_MASK_NOCLOSEPROCESS;
    se.lpFile = L"explorer.exe";
    se.lpParameters = g_installPath.c_str();
    se.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&se)) return false;
    CloseHandle(se.hProcess);
    return true;
}

// Підвищений ретрай: копіює себе в Program Files і запускає звідти.
// Попереднє оголошення (використовується раніше за визначення).
static bool RunHidden(const wchar_t* cmdline, DWORD* exitCode = nullptr);

// Завершує всі інші екземпляри zone_sync.exe (крім себе): без цього
// CopyFileW не може перезаписати exe, який зараз ВИКОНУЄТЬСЯ (файл зайнятий).
static void KillOtherInstances()
{
    DWORD myPid = GetCurrentProcessId();
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    PROCESSENTRY32W pe = { sizeof(pe) };
    if (Process32FirstW(snap, &pe)) {
        do {
            if (_wcsicmp(pe.szExeFile, L"zone_sync.exe") == 0 &&
                pe.th32ProcessID != myPid) {
                HANDLE p = OpenProcess(PROCESS_TERMINATE, FALSE, pe.th32ProcessID);
                if (p) { TerminateProcess(p, 0); CloseHandle(p); }
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    Sleep(500);   // дати системі звільнити файл
}

static int RunInstallCopy()
{
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    size_t slash = g_installPath.find_last_of(L'\\');
    if (slash != std::wstring::npos)
        CreateDirectoryW(g_installPath.substr(0, slash).c_str(), nullptr);
    KillOtherInstances();
    // Папка бекапів + право запису для звичайних користувачів
    // (трей працює без підвищення, а бекапи пишуться сюди).
    std::wstring bkDir = g_installPath.substr(0, slash) + L"\\BackUP";
    CreateDirectoryW(bkDir.c_str(), nullptr);
    std::wstring icacls = std::wstring(L"icacls.exe \"") + bkDir +
        L"\" /grant *S-1-5-11:(OI)(CI)M";
    DWORD icaclsCode = 1;
    RunHidden(icacls.c_str(), &icaclsCode);
    if (!CopyFileW(self, g_installPath.c_str(), FALSE)) return 2;
    WriteLog(S(S_COPIED_TO_INSTALL));
    StartTarget();
    return 0;
}

// Нормалізація розташування: копіювання/оновлення/запуск з Program Files.
// Повертає: 0 - канонічний екземпляр запущено; 1 - продовжити звідси.
static int EnsureInstalledCopy()
{
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    if (_wcsicmp(self, g_installPath.c_str()) == 0) return 1;   // вже звідси

    Ver4 tv = FileVersionOf(g_installPath);
    Ver4 sv = ParseVer4(APP_VERSION);

    if (tv.a == 0 && tv.b == 0) {
        // Немає в Program Files -> установка (адмін). При скасуванні UAC
        // продовжуємо з поточного місця.
    } else if (VerGreater(sv, tv)) {
        // Встановлена версія старіша -> оновлення (адмін).
    } else if (VerGreater(tv, sv) || (tv.a == sv.a && tv.b == sv.b && tv.c == sv.c && tv.d == sv.d)) {
        // Встановлена новіша або та сама версія -> запустити звідти.
        if (StartTarget()) return 0;
        // Не запустилась - працюємо звідси.
        return 1;
    } else {
        // Встановлена новіша або та сама версія -> запустити звідти.
        if (StartTarget()) return 0;
        return 1;
    }

    // Копіювання потребує прав адміністратора -> UAC.
    SHELLEXECUTEINFOW se = { sizeof(se) };
    se.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_FLAG_NO_UI;
    se.lpVerb = L"runas";
    se.lpFile = self;
    se.lpParameters = L"--install-copy";
    se.nShow = SW_SHOWNORMAL;
    if (ShellExecuteExW(&se)) {
        WaitForSingleObject(se.hProcess, 30000);
        CloseHandle(se.hProcess);
        return 0;   // канонічний екземпляр запущено
    }
    // Скасовано/не вдалося -> продовжуємо з поточного місця.
    WriteLog(S(S_ERROR) + L": UAC");
    return 1;
}

// ---------------------------------------------------------------------
// Задача автозапуску: schtasks.
// ---------------------------------------------------------------------
static bool RunHidden(const wchar_t* cmdline, DWORD* exitCode)
{
    STARTUPINFOW si = { sizeof(si) };
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi = { 0 };
    std::wstring cmd = cmdline;
    std::vector<wchar_t> buf(cmd.begin(), cmd.end());
    buf.push_back(0);
    if (!CreateProcessW(nullptr, &buf[0], nullptr, nullptr, FALSE,
                        CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
        return false;
    WaitForSingleObject(pi.hProcess, 8000);
    if (exitCode) GetExitCodeProcess(pi.hProcess, exitCode);
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    return true;
}

static bool AutostartInstalled()
{
    DWORD code = 1;
    if (!RunHidden(L"schtasks.exe /Query /TN \"ZoneSync\"", &code))
        return false;
    return code == 0;
}

static void InstallAutostartTask()
{
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring cmd = std::wstring(L"schtasks.exe /Create /F /TN \"") + TASK_NAME +
        L"\" /TR \"\\\"" + exe + L"\\\"\" /SC ONLOGON /RL LIMITED";
    DWORD code = 1;
    RunHidden(cmd.c_str(), &code);
    // Повторний запуск з правами адміністратора, якщо перший відмовив.
    if (code != 0) {
        ShellExecuteW(nullptr, L"runas", exe, L"--task-install", nullptr, SW_SHOWNORMAL);
        Sleep(1500);
    }
}

// Підвищений ретрай: окремий процес виконує schtasks від адміністратора.
static void ElevatedTaskAction(bool install)
{
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    if (install) {
        std::wstring cmd = std::wstring(L"schtasks.exe /Create /F /TN \"") + TASK_NAME +
            L"\" /TR \"\\\"" + exe + L"\\\"\" /SC ONLOGON /RL LIMITED";
        DWORD code = 1;
        RunHidden(cmd.c_str(), &code);
    } else {
        DWORD code = 1;
        RunHidden((std::wstring(L"schtasks.exe /Delete /TN \"") + TASK_NAME + L"\" /F").c_str(), &code);
    }
}

// ---------------------------------------------------------------------
// Трей: додавання/видалення іконки, меню.
// ---------------------------------------------------------------------
static void TrayAdd()
{
    NOTIFYICONDATAW ni = { sizeof(ni) };
    ni.hWnd = g_hwnd; ni.uID = 1;
    ni.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    ni.uCallbackMessage = WM_TRAYICON;
    ni.hIcon = LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(1));
    lstrcpynW(ni.szTip, APP_TITLE, 128);
    Shell_NotifyIconW(NIM_ADD, &ni);
    ni.uVersion = 4;
    Shell_NotifyIconW(NIM_SETVERSION, &ni);
    g_trayAdded = true;
}

static void TrayRemove()
{
    if (!g_trayAdded) return;
    NOTIFYICONDATAW ni = { sizeof(ni) };
    ni.hWnd = g_hwnd; ni.uID = 1;
    Shell_NotifyIconW(NIM_DELETE, &ni);
    g_trayAdded = false;
}

// ---------------------------------------------------------------------
// Меню трея з власним малюванням: світлий фон, синя підсвітка,
// кольорові піктограми. Пункти описуються MenuDef (індекс у g_menuDefs
// передається як itemData).
// ---------------------------------------------------------------------
enum MenuIcon {
    MI_NONE = 0, MI_SYNC, MI_AUTOSTART, MI_FILES, MI_ADDFILE, MI_ADDFOLDER,
    MI_EDIT, MI_LOG, MI_LANG, MI_EXIT, MI_UPDATE, MI_REMOVE
};

struct MenuDef {
    UINT id;
    int  strId;              // >= 0 -> S((StrId)id); < 0 -> text
    std::wstring text;
    int  icon;
    bool checked;
};
static std::vector<MenuDef> g_menuDefs;
static HFONT g_menuFont = nullptr;

static HFONT MenuFont()
{
    if (!g_menuFont) {
        NONCLIENTMETRICSW ncm = { sizeof(ncm) };
        SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof(ncm), &ncm, 0);
        g_menuFont = CreateFontIndirectW(&ncm.lfMenuFont);
    }
    return g_menuFont;
}

static std::wstring MenuDefText(size_t idx)
{
    if (idx >= g_menuDefs.size()) return L"";
    const MenuDef& d = g_menuDefs[idx];
    return d.strId >= 0 ? S((StrId)d.strId) : d.text;
}

static void AddOwnerItem(HMENU menu, UINT id, int strId, int icon, bool checked)
{
    MenuDef d;
    d.id = id; d.strId = strId; d.icon = icon; d.checked = checked;
    g_menuDefs.push_back(d);
    AppendMenuW(menu, MF_OWNERDRAW, (UINT_PTR)id,
                (LPCWSTR)(UINT_PTR)(g_menuDefs.size() - 1));
}

static void AddOwnerText(HMENU menu, UINT id, const std::wstring& text, int icon)
{
    MenuDef d;
    d.id = id; d.strId = -1; d.text = text; d.icon = icon; d.checked = false;
    g_menuDefs.push_back(d);
    AppendMenuW(menu, MF_OWNERDRAW, (UINT_PTR)id,
                (LPCWSTR)(UINT_PTR)(g_menuDefs.size() - 1));
}

// Малювання піктограм пункту (GDI, ~16px навколо cx,cy).
static void DrawMenuIcon(HDC hdc, int icon, int cx, int cy, bool sel)
{
    COLORREF blue  = sel ? RGB(255, 255, 255) : RGB(0, 110, 200);
    COLORREF red   = sel ? RGB(255, 190, 190) : RGB(200, 45, 45);
    COLORREF green = sel ? RGB(190, 255, 190) : RGB(20, 140, 80);
    HGDIOBJ op = SelectObject(hdc, CreatePen(PS_SOLID, 2,
        (icon == MI_EXIT || icon == MI_REMOVE) ? red : blue));
    HGDIOBJ ob = SelectObject(hdc, GetStockObject(NULL_BRUSH));
    switch (icon) {
    case MI_SYNC: {          // дві дуги-стрілки по колу
        Arc(hdc, cx - 7, cy - 7, cx + 7, cy + 7, cx + 5, cy - 5, cx - 5, cy - 5);
        Arc(hdc, cx - 7, cy - 7, cx + 7, cy + 7, cx - 5, cy + 5, cx + 5, cy + 5);
        HGDIOBJ ob2 = SelectObject(hdc, CreateSolidBrush(blue));
        POINT tri1[3] = { { cx - 8, cy - 6 }, { cx - 2, cy - 8 }, { cx - 4, cy - 2 } };
        POINT tri2[3] = { { cx + 8, cy + 6 }, { cx + 2, cy + 8 }, { cx + 4, cy + 2 } };
        Polygon(hdc, tri1, 3);
        Polygon(hdc, tri2, 3);
        SelectObject(hdc, ob2);
        break;
    }
    case MI_AUTOSTART: {     // галочка
        MoveToEx(hdc, cx - 5, cy, nullptr);
        LineTo(hdc, cx - 1, cy + 4);
        LineTo(hdc, cx + 6, cy - 5);
        break;
    }
    case MI_FILES: case MI_ADDFOLDER: {   // папка
        MoveToEx(hdc, cx - 7, cy + 5, nullptr);
        LineTo(hdc, cx - 7, cy - 3); LineTo(hdc, cx - 2, cy - 3);
        LineTo(hdc, cx, cy - 5);     LineTo(hdc, cx + 7, cy - 5);
        LineTo(hdc, cx + 7, cy + 5); LineTo(hdc, cx - 7, cy + 5);
        if (icon == MI_ADDFOLDER) {   // "+" у папці
            MoveToEx(hdc, cx + 1, cy - 1, nullptr);
            LineTo(hdc, cx + 1, cy + 3);
            MoveToEx(hdc, cx - 1, cy + 1, nullptr);
            LineTo(hdc, cx + 3, cy + 1);
        }
        break;
    }
    case MI_ADDFILE: {       // аркуш з "+"
        MoveToEx(hdc, cx - 6, cy + 6, nullptr);
        LineTo(hdc, cx - 6, cy - 6); LineTo(hdc, cx + 2, cy - 6);
        LineTo(hdc, cx + 6, cy - 2); LineTo(hdc, cx + 6, cy + 6);
        LineTo(hdc, cx - 6, cy + 6);
        MoveToEx(hdc, cx + 3, cy, nullptr);
        LineTo(hdc, cx - 3, cy);
        MoveToEx(hdc, cx, cy + 3, nullptr);
        LineTo(hdc, cx, cy - 3);
        break;
    }
    case MI_EDIT: {          // олівець
        MoveToEx(hdc, cx - 6, cy + 6, nullptr);
        LineTo(hdc, cx + 5, cy - 5);
        MoveToEx(hdc, cx + 3, cy - 7, nullptr);
        LineTo(hdc, cx + 7, cy - 3);
        MoveToEx(hdc, cx - 6, cy + 6, nullptr);
        LineTo(hdc, cx - 7, cy + 8);
        break;
    }
    case MI_LOG: {           // рядки лога
        for (int i = -1; i <= 1; i++) {
            MoveToEx(hdc, cx - 6, cy + i * 5, nullptr);
            LineTo(hdc, cx + 6, cy + i * 5);
        }
        break;
    }
    case MI_LANG: {          // "глобус": коло + меридіани
        Arc(hdc, cx - 7, cy - 7, cx + 7, cy + 7, cx - 7, cy, cx + 7, cy);
        Arc(hdc, cx - 7, cy - 7, cx + 7, cy + 7, cx + 7, cy, cx - 7, cy);
        MoveToEx(hdc, cx - 7, cy, nullptr);
        LineTo(hdc, cx + 7, cy);
        MoveToEx(hdc, cx, cy - 7, nullptr);
        LineTo(hdc, cx, cy + 7);
        break;
    }
    case MI_EXIT: {          // хрестик
        MoveToEx(hdc, cx - 5, cy - 5, nullptr);
        LineTo(hdc, cx + 5, cy + 5);
        MoveToEx(hdc, cx + 5, cy - 5, nullptr);
        LineTo(hdc, cx - 5, cy + 5);
        break;
    }
    case MI_UPDATE: {        // стрілка вниз
        MoveToEx(hdc, cx, cy - 7, nullptr);
        LineTo(hdc, cx, cy + 4);
        HGDIOBJ ob2 = SelectObject(hdc, CreateSolidBrush(blue));
        POINT tri[3] = { { cx - 5, cy + 3 }, { cx + 5, cy + 3 }, { cx, cy + 8 } };
        Polygon(hdc, tri, 3);
        SelectObject(hdc, ob2);
        break;
    }
    case MI_REMOVE: {        // маленький хрестик
        MoveToEx(hdc, cx - 4, cy - 4, nullptr);
        LineTo(hdc, cx + 4, cy + 4);
        MoveToEx(hdc, cx + 4, cy - 4, nullptr);
        LineTo(hdc, cx - 4, cy + 4);
        break;
    }
    default: break;
    }
    DeleteObject(SelectObject(hdc, op));
    SelectObject(hdc, ob);
}

static void BuildTrayMenu(HMENU menu)
{
    g_menuDefs.clear();
    while (DeleteMenu(menu, 0, MF_BYPOSITION)) {}
    if (!g_updateVersion.empty()) {
        AddOwnerText(menu, IDM_UPDATE, S(S_UPDATE_INSTALL) + g_updateVersion,
                     MI_UPDATE);
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    }
    AddOwnerItem(menu, IDM_SYNC_NOW, S_SYNC_NOW, MI_SYNC, false);
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AddOwnerItem(menu, IDM_AUTOSTART, S_AUTOSTART, MI_AUTOSTART,
                 AutostartInstalled());
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);

    HMENU files = CreatePopupMenu();
    AddOwnerItem(files, IDM_ADD_FILE, S_ADD_FILE, MI_ADDFILE, false);
    AddOwnerItem(files, IDM_ADD_FOLDER, S_ADD_FOLDER, MI_ADDFOLDER, false);
    AddOwnerItem(files, IDM_EDIT_LIST, S_EDIT_LIST, MI_EDIT, false);
    AppendMenuW(files, MF_SEPARATOR, 0, nullptr);
    for (size_t i = 0; i < g_items.size(); i++) {
        std::wstring label = g_items[i].flashName +
            (g_items[i].type == L'D' ? L" " + S(S_TYPE_FOLDER) : L"");
        AddOwnerText(files, IDM_REMOVE_BASE + (UINT)i, label, MI_REMOVE);
    }
    AddOwnerItem(menu, IDM_FILES, S_FILES, MI_FILES, false);
    AppendMenuW(menu, MF_POPUP | MF_OWNERDRAW, (UINT_PTR)files,
                (LPCWSTR)(UINT_PTR)(g_menuDefs.size() - 1));

    AddOwnerItem(menu, IDM_SHOW_LOG, S_SHOW_LOG, MI_LOG, false);
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AddOwnerItem(menu, IDM_LANGUAGE, S_LANGUAGE, MI_LANG, false);
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AddOwnerItem(menu, IDM_EXIT, S_EXIT, MI_EXIT, false);
}

// ---------------------------------------------------------------------
// Вибір папки (SHBrowseForFolder - shell32, без COM-ініціалізації).
// ---------------------------------------------------------------------
static bool PickFolder(HWND owner, std::wstring& out)
{
    std::wstring title = S(S_ADD_FOLDER_TITLE);
    BROWSEINFOW bi = { 0 };
    bi.hwndOwner = owner;
    bi.ulFlags = BIF_RETURNONLYFSDIRS;
    bi.lpszTitle = title.c_str();
    LPITEMIDLIST pidl = SHBrowseForFolderW(&bi);
    if (!pidl) return false;
    wchar_t path[MAX_PATH] = { 0 };
    bool ok = SHGetPathFromIDListW(pidl, path) != 0;
    CoTaskMemFree(pidl);
    if (ok) out = path;
    return ok;
}

// Вибір другого розташування: БУДЬ-ЯКА папка на будь-якому диску
// (огляд починається зі "Робочий стіл" -> "Комп'ютер").
// Якщо вибрано знімний носій - повертаємо ВІДНОСНИЙ шлях (без диска),
// він діє на кожній підключеній флешці. Інакше - абсолютний шлях.
// "" = корінь знімного диска.
static bool PickSecondSide(HWND owner, const std::wstring& itemName,
                           std::wstring& placeOut)
{
    std::wstring title = Format1(S(S_CHOOSE_FLASH_PLACE), itemName);
    BROWSEINFOW bi = { 0 };
    bi.hwndOwner = owner;
    bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_EDITBOX;
    bi.lpszTitle = title.c_str();
    LPITEMIDLIST pidl = SHBrowseForFolderW(&bi);
    if (!pidl) return false;   // скасовано
    wchar_t path[MAX_PATH] = { 0 };
    bool ok = SHGetPathFromIDListW(pidl, path) != 0;
    CoTaskMemFree(pidl);
    if (!ok) return false;

    std::wstring p = path;
    while (!p.empty() && p[p.size() - 1] == L'\\') p = p.substr(0, p.size() - 1);
    // Знімний носій -> зберігаємо шлях відносно кореня диска.
    wchar_t drv[4] = { p[0], L':', L'\\', 0 };
    if (GetDriveTypeW(drv) == DRIVE_REMOVABLE) {
        if (_wcsnicmp(p.c_str(), drv, 3) == 0) p = p.substr(3);
        while (!p.empty() && p[0] == L'\\') p = p.substr(1);
    }
    placeOut = p;
    return true;
}

// ---------------------------------------------------------------------
// Вікно.
// ---------------------------------------------------------------------
static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_TRAYICON) {
        if (LOWORD(lp) == WM_CONTEXTMENU || LOWORD(lp) == WM_RBUTTONUP) {
            LoadItems();
            HMENU menu = CreatePopupMenu();
            BuildTrayMenu(menu);
            POINT pt; GetCursorPos(&pt);
            SetForegroundWindow(hwnd);
            TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN,
                           pt.x, pt.y, 0, hwnd, nullptr);
            PostMessageW(hwnd, WM_NULL, 0, 0);
            DestroyMenu(menu);
        } else if (LOWORD(lp) == WM_LBUTTONDBLCLK) {
            PostMessageW(hwnd, WM_COMMAND, IDM_SYNC_NOW, 0);
        }
        return 0;
    }

    switch (msg) {
    case WM_MEASUREITEM: {
        MEASUREITEMSTRUCT* mi = (MEASUREITEMSTRUCT*)lp;
        if (mi->CtlType != ODT_MENU) break;
        size_t idx = (size_t)mi->itemData;
        HDC hdc = GetDC(hwnd);
        HGDIOBJ of = SelectObject(hdc, MenuFont());
        SIZE sz = { 0, 0 };
        std::wstring t = MenuDefText(idx);
        if (!t.empty())
            GetTextExtentPoint32W(hdc, t.c_str(), (int)t.size(), &sz);
        SelectObject(hdc, of);
        ReleaseDC(hwnd, hdc);
        mi->itemHeight = 26;
        mi->itemWidth = 42 + sz.cx + 24;
        return TRUE;
    }
    case WM_DRAWITEM: {
        DRAWITEMSTRUCT* di = (DRAWITEMSTRUCT*)lp;
        if (di->CtlType != ODT_MENU) break;
        size_t idx = (size_t)di->itemData;
        bool sel = (di->itemState & ODS_SELECTED) != 0;
        bool gray = (di->itemState & (ODS_GRAYED | ODS_DISABLED)) != 0;
        RECT rc = di->rcItem;
        HBRUSH b = CreateSolidBrush(sel ? RGB(0, 118, 215) : RGB(250, 250, 252));
        FillRect(di->hDC, &rc, b);
        DeleteObject(b);
        int cx = rc.left + 20;
        int cy = (rc.top + rc.bottom) / 2;
        DrawMenuIcon(di->hDC, idx < g_menuDefs.size() ? g_menuDefs[idx].icon : MI_NONE,
                     cx, cy, sel);
        SetBkMode(di->hDC, TRANSPARENT);
        SetTextColor(di->hDC, gray ? RGB(160, 160, 160)
                                   : (sel ? RGB(255, 255, 255) : RGB(35, 35, 40)));
        HGDIOBJ of = SelectObject(di->hDC, MenuFont());
        std::wstring t = MenuDefText(idx);
        RECT tr = { rc.left + 40, rc.top, rc.right - 10, rc.bottom };
        DrawTextW(di->hDC, t.c_str(), -1, &tr,
                  DT_SINGLELINE | DT_VCENTER | DT_LEFT | DT_END_ELLIPSIS);
        SelectObject(di->hDC, of);
        return TRUE;
    }

    case WM_COMMAND: {
        WORD id = LOWORD(wp);
        if (id == IDM_SYNC_NOW) {
            std::wstring text; bool err = false;
            std::vector<std::wstring> deleted, conflicts;
            DoSyncOnce(false, &text, &err, &deleted, &conflicts);
            if (!text.empty()) ShowBalloon(text, err);
            std::wstring t = SideReportText(deleted, conflicts);
            if (!t.empty()) ShowDeletionsWindow(t);
        } else if (id == IDM_AUTOSTART) {
            bool installed = AutostartInstalled();
            if (!installed) {
                InstallAutostartTask();
                Sleep(1200);
                installed = AutostartInstalled();
                ShowBalloon(installed ? S(S_TASK_INSTALLED) : S(S_TASK_INSTALL_FAIL),
                            !installed);
            } else {
                DWORD code = 1;
                RunHidden((std::wstring(L"schtasks.exe /Delete /TN \"") + TASK_NAME + L"\" /F").c_str(), &code);
                if (code != 0) {
                    wchar_t exe[MAX_PATH];
                    GetModuleFileNameW(nullptr, exe, MAX_PATH);
                    ShellExecuteW(hwnd, L"runas", exe, L"--task-remove", nullptr, SW_SHOWNORMAL);
                    Sleep(1200);
                }
                bool still = AutostartInstalled();
                ShowBalloon(still ? S(S_TASK_REMOVE_FAIL) : S(S_TASK_REMOVED), still);
            }
        } else if (id == IDM_ADD_FILE) {
            wchar_t file[MAX_PATH] = { 0 };
            OPENFILENAMEW of = { sizeof(of) };
            of.hwndOwner = hwnd;
            of.lpstrFilter = L"Excel\0*.xlsx;*.xls\0Документи\0*.docx;*.doc\0PDF\0*.pdf\0Архіви\0*.zip;*.rar;*.7z\0Усі файли\0*.*\0";
            of.lpstrFile = file;
            of.nMaxFile = MAX_PATH;
            of.lpstrInitialDir = LOG_DIR;
            of.Flags = OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR | OFN_HIDEREADONLY;
            std::wstring titleDlg = S(S_ADD_FILE_TITLE);
            of.lpstrTitle = titleDlg.c_str();
            if (GetOpenFileNameW(&of)) {
                std::wstring localPath = file;
                std::wstring leaf = localPath;
                size_t slash = leaf.find_last_of(L'\\');
                if (slash != std::wstring::npos) leaf = leaf.substr(slash + 1);
                if (ItemInList(L'F', localPath)) {
                    ShowBalloon(S(S_ALREADY_IN_LIST), true);
                    return 0;
                }
                // Друге розташування: будь-яка папка (флешка або диск).
                // Скасовано - нічого не додаємо.
                std::wstring place;
                if (!PickSecondSide(hwnd, leaf, place)) return 0;
                std::wstring sideB = place.empty() ? leaf : place + L"\\" + leaf;
                AddItemToList(L'F', localPath, sideB);
                WriteLog(S(S_ADDED) + L": " + localPath + L"  →  " + sideB);
                ShowBalloon(S(S_ADDED) + L": " + sideB, false);
            }
        } else if (id == IDM_ADD_FOLDER) {
            std::wstring localPath;
            if (!PickFolder(hwnd, localPath)) return 0;
            std::wstring leaf = localPath;
            size_t slash = leaf.find_last_of(L'\\');
            if (slash != std::wstring::npos) leaf = leaf.substr(slash + 1);
            if (leaf.empty()) return 0;
            if (ItemInList(L'D', localPath)) {
                ShowBalloon(S(S_ALREADY_IN_LIST), true);
                return 0;
            }
            // Друге розташування: будь-яка папка (флешка або диск).
            // Скасовано - нічого не додаємо.
            std::wstring place;
            if (!PickSecondSide(hwnd, leaf, place)) return 0;
            std::wstring sideB = place.empty() ? leaf : place + L"\\" + leaf;
            AddItemToList(L'D', localPath, sideB);
            WriteLog(S(S_ADDED) + L": [папка] " + localPath + L"  →  " + sideB);
            ShowBalloon(S(S_ADDED) + L": " + sideB, false);
        } else if (id == IDM_EDIT_LIST) {
            std::wstring hint = Format1(S(S_EDIT_LIST_HINT), g_configPath);
            MessageBoxW(hwnd, hint.c_str(), APP_TITLE, MB_OK | MB_ICONINFORMATION);
            ShellExecuteW(hwnd, L"open", L"notepad.exe",
                          g_configPath.c_str(), nullptr, SW_SHOWNORMAL);
        } else if (id >= IDM_REMOVE_BASE && id < IDM_REMOVE_BASE + 1000) {
            UINT idx = id - IDM_REMOVE_BASE;
            if (idx < g_items.size()) {
                SyncItem it = g_items[idx];
                std::wstring label = it.flashName +
                    (it.type == L'D' ? L" " + S(S_TYPE_FOLDER) : L"");
                std::wstring msg = Format1(S(S_REMOVE_CONFIRM), label);
                if (MessageBoxW(hwnd, msg.c_str(), S(S_REMOVE_FILE_TITLE).c_str(),
                                MB_YESNO | MB_ICONQUESTION) == IDYES) {
                    RemoveItemFromList(it.type, it.localPath);
                    WriteLog(S(S_REMOVED) + L": " + label);
                    ShowBalloon(S(S_REMOVED) + L": " + label, false);
                }
            }
        } else if (id == IDM_SHOW_LOG) {
            if (GetFileAttributesW(g_logPath.c_str()) != INVALID_FILE_ATTRIBUTES)
                ShellExecuteW(hwnd, L"open", L"notepad.exe",
                              g_logPath.c_str(), nullptr, SW_SHOWNORMAL);
        } else if (id == IDM_UPDATE) {
            InstallUpdate();
        } else if (id == IDM_LANGUAGE) {
            g_lang = (g_lang == LANG_UA) ? LANG_EN : LANG_UA;
            SaveLang();
            ShowBalloon(S(S_LANG_APPLIED), false);
        } else if (id == IDM_EXIT) {
            DestroyWindow(hwnd);
        }
        return 0;
    }

    case WM_DEVICECHANGE: {
        // DBT_DEVICEARRIVAL(7)/DBT_DEVICEREMOVECOMPLETE(4), тип VOLUME(2).
        if (wp == 7 || wp == 4) {
            DEV_BROADCAST_HDR* h = (DEV_BROADCAST_HDR*)lp;
            if (h && h->dbch_devicetype == DBT_DEVTYP_VOLUME) {
                // Debounce: флешка дає кілька подій підряд.
                SetTimer(hwnd, TIMER_ID, 1500, nullptr);
            }
        }
        return 0;
    }

    case WM_TIMER:
        if (wp == TIMER_ID) {
            KillTimer(hwnd, TIMER_ID);
            std::wstring text; bool err = false;
            std::vector<std::wstring> deleted, conflicts;
            DoSyncOnce(false, &text, &err, &deleted, &conflicts);
            if (!text.empty()) ShowBalloon(text, err);
            std::wstring t = SideReportText(deleted, conflicts);
            if (!t.empty()) ShowDeletionsWindow(t);
        }
        else if (wp == TIMER_ID + 2) {
            // Опитування знімних дисків кожні 2 с - надійніше за
            // WM_DEVICECHANGE (той не завжди доходить до вікна).
            std::vector<std::wstring> cur = GetRemovableRoots();
            bool changed = cur.size() != g_lastRemovable.size();
            for (size_t i = 0; i < cur.size() && !changed; i++)
                if (_wcsicmp(cur[i].c_str(), g_lastRemovable[i].c_str()) != 0) changed = true;
            if (changed) {
                g_lastRemovable = cur;
                SetTimer(hwnd, TIMER_ID, 1500, nullptr);   // debounce -> sync
            }
        }
        else if (wp == TIMER_ID + 1) {
            // Щоденний тик: перевірка оновлень + чистка бекапів старших доби.
            StartUpdateCheck();
            CleanupBackups();
            // Переозброюємо на наступну добу (якщо це був стартовий тик).
            SetTimer(hwnd, TIMER_ID + 1, 24ULL * 3600ULL * 1000ULL, nullptr);
        }
        return 0;

    case WM_APP + 3: {
        // Фон-потік повідомив про доступне оновлення.
        std::wstring msg = Format1(S(S_UPDATE_AVAILABLE), g_updateVersion);
        ShowBalloon(msg, false);
        return 0;
    }

    case WM_DESTROY:
        KillTimer(hwnd, TIMER_ID);
        KillTimer(hwnd, TIMER_ID + 1);
        KillTimer(hwnd, TIMER_ID + 2);
        TrayRemove();
        CoUninitialize();
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

// ---------------------------------------------------------------------
// Тихий режим (--sync): один прохід без трея.
// ---------------------------------------------------------------------
static int RunSilentSync()
{
    std::wstring text; bool err = false;
    std::vector<std::wstring> deleted, conflicts;
    DoSyncOnce(false, &text, &err, &deleted, &conflicts);
    // Балун у тихому режимі неможливий (трея немає) - усе в логу.
    return err ? 2 : 0;
}

// ---------------------------------------------------------------------
// Підвищений ретрай задачі (--task-install / --task-remove).
// ---------------------------------------------------------------------
static int RunTaskAction(bool install)
{
    ElevatedTaskAction(install);
    return 0;
}

// ---------------------------------------------------------------------
// Трей-режим.
// ---------------------------------------------------------------------
// Чи живий ще якийсь інший екземпляр zone_sync.exe (крім нашого PID).
static bool AnyOtherInstanceRunning()
{
    DWORD myPid = GetCurrentProcessId();
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return true;   // не впевнені - вважаємо живим
    PROCESSENTRY32W pe = { sizeof(pe) };
    bool found = false;
    if (Process32FirstW(snap, &pe)) {
        do {
            if (_wcsicmp(pe.szExeFile, L"zone_sync.exe") == 0 &&
                pe.th32ProcessID != myPid) { found = true; break; }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    return found;
}

// Захоплюємо м'ютекс єдиного екземпляра з чеканням: канонічний екземпляр
// стартує ПОКИ старий ще живий і тримає м'ютекс - без чекання канонічний
// виходив одразу ("трей пропав"). Мертвий володар звільняє м'ютекс
// автоматично - тоді захоплюємо.
static HANDLE AcquireSingleInstanceMutex()
{
    for (int attempt = 0; attempt < 20; attempt++) {   // ~4 с
        HANDLE m = CreateMutexW(nullptr, FALSE, MUTEX_NAME);
        if (GetLastError() != ERROR_ALREADY_EXISTS) return m;   // перший
        CloseHandle(m);
        if (!AnyOtherInstanceRunning()) continue;   // володар помер - м'ютекс звільниться
        Sleep(200);
    }
    return nullptr;   // інший живий екземпляр
}

static int RunTray(HINSTANCE hInst)
{
    LoadLang();
    InitPaths();
    LoadItems();

    // Mutex єдиного екземпляра (лише для трей-режиму) - з чеканням.
    HANDLE mutex = AcquireSingleInstanceMutex();
    if (!mutex) return 0;

    WNDCLASSW wc = { 0 };
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInst;
    wc.lpszClassName = L"ZoneSyncTrayWnd";
    RegisterClassW(&wc);

    g_hwnd = CreateWindowExW(0, wc.lpszClassName, APP_TITLE,
                             WS_POPUP, 0, 0, 0, 0,
                             nullptr, nullptr, hInst, nullptr);

    TrayAdd();
    // URLDownloadToFile (urlmon) працює коректно з COM-ініціалізацією.
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    // Без стартової синхронізації: перший прохід - при підключенні флешки
    // (поллінг) або вручну. Одразу запускаємо опитування знімних дисків.
    g_lastRemovable = GetRemovableRoots();
    SetTimer(g_hwnd, TIMER_ID + 2, 2000, nullptr);
    // Перевірка оновлень (раз на добу, фон-потік).
    SetTimer(g_hwnd, TIMER_ID + 1, 4000, nullptr);

    MSG msg;
    for (;;) {
        int r = (int)GetMessageW(&msg, nullptr, 0, 0);
        if (r == 0) break;        // WM_QUIT - штатний вихід
        if (r == -1) continue;    // помилка GetMessage - раніше процес
                                  // мовчки завершувався через "> 0"
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    CloseHandle(mutex);
    return 0;
}

int APIENTRY wWinMain(HINSTANCE hInst, HINSTANCE, PWSTR cmdLine, int)
{
    LoadLang();
    InitPaths();

    // Режим командного рядка: задачі (підвищений ретрай), тихий прогін,
    // копіювання в Program Files.
    if (wcsstr(cmdLine, L"--task-install"))  return RunTaskAction(true);
    if (wcsstr(cmdLine, L"--task-remove"))   return RunTaskAction(false);
    if (wcsstr(cmdLine, L"--install-copy"))  return RunInstallCopy();
    if (wcsstr(cmdLine, L"--sync"))          return RunSilentSync();

    // Нормалізація розташування: Program Files\Zone_Sync\.
    int canon = EnsureInstalledCopy();
    if (canon == 0) return 0;   // канонічний екземпляр запущено

    return RunTray(hInst);
}
