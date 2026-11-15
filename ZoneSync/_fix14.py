# -*- coding: utf-8 -*-
# Фолбэк для лога и бекапов: запись в Program Files может не удаться
# (не-админ запуск) — тогда переключаемся на %ProgramData%\ZoneSync.
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')
BS = chr(92)

# 1. BackupOldCopy: фолбэк на g_progDataDir если g_backupDir недоступен
old = '''    std::wstring bak = g_backupDir + L"\\\\" +
        SanitizeName(path) + L"_" + TimeStampForFile() + ext;
    if (CopyFileW(path.c_str(), bak.c_str(), TRUE)) return bak;
    return L"";'''
new = '''    std::wstring bakDir = g_backupDir;
    if (g_bakFallback) bakDir = g_progDataDir + L"\\\\BackUP";
    std::wstring bak = bakDir + L"\\\\" +
        SanitizeName(path) + L"_" + TimeStampForFile() + ext;
    if (CopyFileW(path.c_str(), bak.c_str(), TRUE)) return bak;
    // Программа Files недоступна для записи -> фолбэк на %ProgramData%.
    CreateDirectoryW((g_progDataDir + L"\\\\BackUP").c_str(), nullptr);
    bak = g_progDataDir + L"\\\\BackUP\\\\" +
        SanitizeName(path) + L"_" + TimeStampForFile() + ext;
    if (CopyFileW(path.c_str(), bak.c_str(), TRUE)) { g_bakFallback = true; return bak; }
    return L"";'''
assert old in d, 'backupold'
d = d.replace(old, new, 1)

# 2. WriteLog: фолбэк лога
old2 = '''    HANDLE f2 = CreateFileW(g_logPath.c_str(), FILE_APPEND_DATA,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            nullptr, OPEN_ALWAYS, 0, nullptr);
    if (f2 != INVALID_HANDLE_VALUE) {
        SetFilePointer(f2, 0, nullptr, FILE_END);
        DWORD w = 0;
        WriteFile(f2, line.c_str(), (DWORD)line.size(), &w, nullptr);
        CloseHandle(f2);
    }
}'''
new2 = '''    HANDLE f2 = CreateFileW(g_logPath.c_str(), FILE_APPEND_DATA,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            nullptr, OPEN_ALWAYS, 0, nullptr);
    if (f2 != INVALID_HANDLE_VALUE) {
        SetFilePointer(f2, 0, nullptr, FILE_END);
        DWORD w = 0;
        WriteFile(f2, line.c_str(), (DWORD)line.size(), &w, nullptr);
        CloseHandle(f2);
        return;
    }
    // Фолбэк: лог в %ProgramData%\\ZoneSync\\ (запись в Program Files не удалась).
    g_logFallback = true;
    std::wstring altLog = g_progDataDir + L"\\\\zone_sync.log";
    HANDLE f3 = CreateFileW(altLog.c_str(), FILE_APPEND_DATA,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            nullptr, OPEN_ALWAYS, 0, nullptr);
    if (f3 != INVALID_HANDLE_VALUE) {
        SetFilePointer(f3, 0, nullptr, FILE_END);
        DWORD w = 0;
        WriteFile(f3, line.c_str(), (DWORD)line.size(), &w, nullptr);
        CloseHandle(f3);
    }
}'''
assert old2 in d, 'writelog tail'
d = d.replace(old2, new2, 1)

# 3. CleanupBackups: чистить обе папки (активную и фолбэк)
old3 = '''static void CleanupBackups()
{
    if (GetFileAttributesW(g_backupDir.c_str()) == INVALID_FILE_ATTRIBUTES)
        return;'''
new3 = '''static void CleanupBackupsIn(const std::wstring& dir)
{
    if (GetFileAttributesW(dir.c_str()) == INVALID_FILE_ATTRIBUTES)
        return;
    FILETIME ft; GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER cutoff; cutoff.LowPart = ft.dwLowDateTime; cutoff.HighPart = ft.dwHighDateTime;
    cutoff.QuadPart -= 24ULL * 3600ULL * 10000000ULL;   // старше доби
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        ULARGE_INTEGER u; u.LowPart = fd.ftLastWriteTime.dwLowDateTime;
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
    CleanupBackupsIn(g_progDataDir + L"\\\\BackUP");
}'''
assert old3 in d, 'cleanup backups old not found'
d = d.replace(old3, new3, 1)

open(p, 'wb').write(d.encode('utf-8'))
print('fallback + cleanup done')
