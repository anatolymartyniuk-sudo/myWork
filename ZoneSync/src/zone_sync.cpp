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

static const wchar_t* APP_VERSION = L"1.0.2.1";

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
    S_CHOOSE_FLASH_PLACE, S_NO_FLASH_PLACE
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
    { L"Виберіть місце на флешці для '{}'\n(Enter/ОК - корінь флешки)",
                                          L"Choose the flash location for '{}'\n(OK - flash root)" },
    { L"Флешка не підключена - розташування на флешці: корінь (змінюється у files.txt)",
                                          L"Flash drive not connected - flash location: root (edit in files.txt)" },
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

// ---------------------------------------------------------------------
// Стан.
// ---------------------------------------------------------------------
static std::wstring g_configPath;   // %ProgramData%\ZoneSync\files.txt
static std::wstring g_logPath;      // LocalDir\zone_sync.log
static std::wstring g_installPath;  // Program Files\Zone_Sync\zone_sync.exe
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

    // %ProgramData%\ZoneSync\files.txt.
    wchar_t pd[MAX_PATH] = { 0 };
    GetEnvironmentVariableW(L"ProgramData", pd, MAX_PATH);
    std::wstring dir = std::wstring(pd) + L"\\" + CONFIG_DIR_ENV;
    CreateDirectoryW(dir.c_str(), nullptr);
    g_configPath = dir + L"\\" + CONFIG_NAME;
    g_logPath = std::wstring(LOG_DIR) + L"\\" + LOG_NAME;
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
    if (f == INVALID_HANDLE_VALUE) {
        SyncItem it; it.type = L'F';
        it.localPath = std::wstring(LOG_DIR) + L"\\" + DEFAULT_FILE;
        it.flashName = DEFAULT_FILE;
        g_items.push_back(it);
        return;
    }
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
    if (g_items.empty()) {
        SyncItem it; it.type = L'F';
        it.localPath = std::wstring(LOG_DIR) + L"\\" + DEFAULT_FILE;
        it.flashName = DEFAULT_FILE;
        g_items.push_back(it);
    }
}

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
    if (f != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(f, data.c_str(), (DWORD)data.size(), &written, nullptr);
        CloseHandle(f);
    }
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
// Бекап: name.ext -> name.bak_<штамп>.ext
// ---------------------------------------------------------------------
static std::wstring BackupOldCopy(const std::wstring& path)
{
    size_t slash = path.find_last_of(L'\\');
    size_t dot = path.find_last_of(L'.');
    std::wstring dir = (slash == std::wstring::npos) ? L"." : path.substr(0, slash);
    std::wstring base = (slash == std::wstring::npos) ? path : path.substr(slash + 1);
    std::wstring ext, stem = base;
    if (dot != std::wstring::npos && (slash == std::wstring::npos || dot > slash)) {
        stem = base.substr(0, dot - slash - 1);
        ext  = base.substr(dot - slash - 1);
    }
    std::wstring bak = dir + L"\\" + stem + L".bak_" + TimeStampForFile() + ext;
    if (CopyFileW(path.c_str(), bak.c_str(), TRUE)) return bak;
    return L"";
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

// ---------------------------------------------------------------------
// Синхронізація ОДНОГО файлу (не папка): локальний <-> кожна флешка.
// ---------------------------------------------------------------------
static void SyncFileItem(const SyncItem& item, bool dry,
                         std::vector<std::wstring>* done,
                         std::wstring* firstError, int* identicalCount)
{
    std::wstring local = item.localPath;
    bool localExists = false;
    ULARGE_INTEGER lt = FileTimeOf(local, &localExists);

    std::vector<std::wstring> roots = GetRemovableRoots();
    for (size_t ri = 0; ri < roots.size(); ri++) {
        std::wstring flash = roots[ri] + item.flashName;
        bool flashExists = false;
        ULARGE_INTEGER ft = FileTimeOf(flash, &flashExists);
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
}

// ---------------------------------------------------------------------
// Синхронізація ПАПКИ: файлова структура знову стає однаковою.
// Напрямок для кожного файлу - за датою; нові файли копіюються
// в обидва боки; видалення НЕ поширюються (безпечний дефолт).
// ---------------------------------------------------------------------
static void SyncFolderPair(const SyncItem& item, bool dry,
                           std::vector<std::wstring>* done,
                           std::wstring* firstError, int* identicalCount)
{
    std::wstring localRoot = item.localPath;

    std::vector<std::wstring> roots = GetRemovableRoots();
    for (size_t ri = 0; ri < roots.size(); ri++) {
        std::wstring flashRoot = roots[ri] + item.flashName;

        std::vector<TreeEntry> localTree, flashTree;
        WalkTree(localRoot, L"", 0, localTree);
        WalkTree(flashRoot, L"", 0, flashTree);

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
                } else if (firstError->empty()) {
                    *firstError = S(S_COPY_FAILED);
                }
                continue;
            }
            double diff = (double)((long long)localTree[i].t.QuadPart -
                                   (long long)it->second.QuadPart) / 10000000.0;
            if (diff < 0) diff = -diff;
            if (diff <= TIME_TOLERANCE) { (*identicalCount)++; continue; }   // однакові

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
            if (CopyFileW(src.c_str(), dst.c_str(), FALSE)) {
                WriteLog(L"  " + S(S_COPIED));
                done->push_back(dirText);
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
            } else if (firstError->empty()) {
                *firstError = S(S_COPY_FAILED);
            }
        }
    }
}

// ---------------------------------------------------------------------
// Один прохід синхронізації. balloonOut - текст для баллуна
// (порожній - нічого не змінено, silent); isError - балун-помилка.
// ---------------------------------------------------------------------
static void DoSyncOnce(bool notify, std::wstring* balloonOut, bool* isError)
{
    std::vector<std::wstring> done;   // напрямки скопійованих
    std::wstring firstError;
    int identical = 0;                // пар "копії ідентичні"

    LoadItems();   // щоразу свіжо (редагування списку).

    if (g_items.empty()) {
        WriteLog(S(S_CONFIG_EMPTY));
        return;
    }

    for (size_t i = 0; i < g_items.size(); i++) {
        if (g_items[i].type == L'F')
            SyncFileItem(g_items[i], notify, &done, &firstError, &identical);
        else
            SyncFolderPair(g_items[i], notify, &done, &firstError, &identical);
    }

    if (balloonOut) {
        if (!firstError.empty()) {
            *balloonOut = firstError; *isError = true;
        } else if (!done.empty()) {
            std::wstring dirs;
            for (size_t i = 0; i < done.size() && i < 4; i++) {
                if (i) dirs += L"\n";
                dirs += done[i];
            }
            if (done.size() > 4) dirs += L"  …";
            *balloonOut = S(S_SYNC_DONE) + L"\n" + dirs;
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
// Балун.
// ---------------------------------------------------------------------
static void ShowBalloon(const std::wstring& text, bool error)
{
    if (!g_trayAdded || !g_hwnd) return;
    NOTIFYICONDATAW ni = { sizeof(ni) };
    ni.hWnd = g_hwnd; ni.uID = 1;
    ni.uFlags = NIF_INFO;
    ni.dwInfoFlags = error ? NIIF_ERROR : NIIF_INFO;
    lstrcpynW(ni.szInfoTitle, APP_TITLE, 64);
    lstrcpynW(ni.szInfo, text.c_str(), 256);
    Shell_NotifyIconW(NIM_MODIFY, &ni);
}

// ---------------------------------------------------------------------
// Перевірка оновлень з GitHub: раз на добу (остання - в реєстрі).
// version.txt у репозиторії містить номер останньої версії; якщо вона
// новіша за нашу - завантажуємо zone_sync.exe і пропонуємо оновитись.
// ---------------------------------------------------------------------
static std::wstring g_updateVersion;   // доступна нова версія (порожньо - немає)
static std::wstring g_updateExePath;   // завантажений новий exe

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
    STARTUPINFOW si = { sizeof(si) };
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_SHOWNORMAL;
    PROCESS_INFORMATION pi = { 0 };
    if (!CreateProcessW(g_installPath.c_str(), nullptr, nullptr, nullptr,
                        FALSE, 0, nullptr, nullptr, &si, &pi))
        return false;
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    return true;
}

// Підвищений ретрай: копіює себе в Program Files і запускає звідти.
static int RunInstallCopy()
{
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    size_t slash = g_installPath.find_last_of(L'\\');
    if (slash != std::wstring::npos)
        CreateDirectoryW(g_installPath.substr(0, slash).c_str(), nullptr);
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

static void BuildTrayMenu(HMENU menu)
{
    while (DeleteMenu(menu, 0, MF_BYPOSITION)) {}
    if (!g_updateVersion.empty()) {
        std::wstring lbl = S(S_UPDATE_INSTALL) + g_updateVersion;
        AppendMenuW(menu, MF_STRING, IDM_UPDATE, lbl.c_str());
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    }
    AppendMenuW(menu, MF_STRING, IDM_SYNC_NOW, S(S_SYNC_NOW).c_str());
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING | (AutostartInstalled() ? MF_CHECKED : 0),
                IDM_AUTOSTART, S(S_AUTOSTART).c_str());
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);

    HMENU files = CreatePopupMenu();
    AppendMenuW(files, MF_STRING, IDM_ADD_FILE, S(S_ADD_FILE).c_str());
    AppendMenuW(files, MF_STRING, IDM_ADD_FOLDER, S(S_ADD_FOLDER).c_str());
    AppendMenuW(files, MF_STRING, IDM_EDIT_LIST, S(S_EDIT_LIST).c_str());
    AppendMenuW(files, MF_SEPARATOR, 0, nullptr);
    for (size_t i = 0; i < g_items.size(); i++) {
        std::wstring label = L"✕  " + g_items[i].flashName +
            (g_items[i].type == L'D' ? L" " + S(S_TYPE_FOLDER) : L"");
        AppendMenuW(files, MF_STRING, IDM_REMOVE_BASE + (UINT)i, label.c_str());
    }
    AppendMenuW(menu, MF_POPUP, (UINT_PTR)files, S(S_FILES).c_str());

    AppendMenuW(menu, MF_STRING, IDM_SHOW_LOG, S(S_SHOW_LOG).c_str());
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, IDM_LANGUAGE, S(S_LANGUAGE).c_str());
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, IDM_EXIT, S(S_EXIT).c_str());
}

// ---------------------------------------------------------------------
// Вибір папки (SHBrowseForFolder - shell32, без COM-ініціалізації).
// ---------------------------------------------------------------------
static bool PickFolder(HWND owner, std::wstring& out)
{
    BROWSEINFOW bi = { 0 };
    bi.hwndOwner = owner;
    bi.ulFlags = BIF_RETURNONLYFSDIRS;
    bi.lpszTitle = S(S_ADD_FOLDER_TITLE).c_str();
    LPITEMIDLIST pidl = SHBrowseForFolderW(&bi);
    if (!pidl) return false;
    wchar_t path[MAX_PATH] = { 0 };
    bool ok = SHGetPathFromIDListW(pidl, path) != 0;
    CoTaskMemFree(pidl);
    if (ok) out = path;
    return ok;
}

// Вибір місця НА флешці: огляд починається з кореня першої знімної
// флешки. Повертає ВІДНОСНИЙ шлях (без диска); "" = корінь флешки.
static bool PickFlashRelative(HWND owner, const std::wstring& itemName,
                              std::wstring& relOut)
{
    std::vector<std::wstring> roots = GetRemovableRoots();
    if (roots.empty()) return false;   // флешка не підключена
    wchar_t root[4] = { roots[0][0], L':', L'\\', 0 };

    PIDLIST_ABSOLUTE rootPidl = nullptr;
    if (SHParseDisplayName(root, nullptr, &rootPidl, 0, nullptr) != S_OK || !rootPidl)
        return false;

    BROWSEINFOW bi = { 0 };
    bi.hwndOwner = owner;
    bi.pidlRoot = rootPidl;
    bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_EDITBOX;
    bi.lpszTitle = Format1(S(S_CHOOSE_FLASH_PLACE), itemName).c_str();
    LPITEMIDLIST pidl = SHBrowseForFolderW(&bi);
    CoTaskMemFree(rootPidl);
    if (!pidl) return false;
    wchar_t path[MAX_PATH] = { 0 };
    bool ok = SHGetPathFromIDListW(pidl, path) != 0;
    CoTaskMemFree(pidl);
    if (!ok) return false;

    // Обрізаємо корінь диска: "F:\backup" -> "backup"; корінь -> "".
    std::wstring p = path;
    if (_wcsnicmp(p.c_str(), root, 3) == 0) p = p.substr(3);
    while (!p.empty() && p[0] == L'\\') p = p.substr(1);
    while (!p.empty() && p[p.size() - 1] == L'\\') p = p.substr(0, p.size() - 1);
    relOut = p;
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
    case WM_COMMAND: {
        WORD id = LOWORD(wp);
        if (id == IDM_SYNC_NOW) {
            std::wstring text; bool err = false;
            DoSyncOnce(true, &text, &err);
            if (!text.empty()) ShowBalloon(text, err);
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
            of.lpstrTitle = S(S_ADD_FILE_TITLE).c_str();
            if (GetOpenFileNameW(&of)) {
                std::wstring localPath = file;
                std::wstring leaf = localPath;
                size_t slash = leaf.find_last_of(L'\\');
                if (slash != std::wstring::npos) leaf = leaf.substr(slash + 1);
                if (ItemInList(L'F', localPath)) {
                    ShowBalloon(S(S_ALREADY_IN_LIST), true);
                    return 0;
                }
                // Місце на флешці: браузер з кореня флешки (якщо підключена).
                std::wstring relDir;
                bool placePicked = PickFlashRelative(hwnd, leaf, relDir);
                std::wstring flashName = relDir.empty()
                    ? leaf : relDir + L"\\" + leaf;
                AddItemToList(L'F', localPath, flashName);
                std::wstring note = placePicked
                    ? L"" : L"\n" + S(S_NO_FLASH_PLACE);
                WriteLog(S(S_ADDED) + L": " + localPath + L"  →  " + flashName);
                ShowBalloon(S(S_ADDED) + L": " + flashName, false);
                if (!placePicked) ShowBalloon(S(S_NO_FLASH_PLACE), true);
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
            // Місце на флешці для папки.
            std::wstring relDir;
            bool placePicked = PickFlashRelative(hwnd, leaf, relDir);
            std::wstring flashName = relDir.empty()
                ? leaf : relDir + L"\\" + leaf;
            AddItemToList(L'D', localPath, flashName);
            std::wstring note = placePicked ? L"" : L"\n" + S(S_NO_FLASH_PLACE);
            WriteLog(S(S_ADDED) + L": [папка] " + localPath + L"  →  " + flashName);
            ShowBalloon(S(S_ADDED) + L": " + flashName, false);
            if (!placePicked) ShowBalloon(S(S_NO_FLASH_PLACE), true);
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
            DoSyncOnce(true, &text, &err);
            if (!text.empty()) ShowBalloon(text, err);
        }
        else if (wp == TIMER_ID + 1) {
            KillTimer(hwnd, TIMER_ID + 1);
            StartUpdateCheck();
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
    DoSyncOnce(true, &text, &err);
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

    // Початкова синхронізація (якщо флешка вже підключена) - з паузою.
    SetTimer(g_hwnd, TIMER_ID, 1200, nullptr);
    // Перевірка оновлень (раз на добу, фон-потік).
    SetTimer(g_hwnd, TIMER_ID + 1, 4000, nullptr);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
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
