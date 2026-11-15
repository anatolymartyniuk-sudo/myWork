# -*- coding: utf-8 -*-
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')
BS = chr(92)

# 1. Лог: Program Files\Zone_Sync\ (фолбэк - %ProgramData%).
old = '''static std::wstring g_installPath;  // Program Files\\Zone_Sync\\zone_sync.exe
static std::wstring g_backupDir;    // Program Files\\Zone_Sync\\BackUP'''
new = '''static std::wstring g_installPath;  // Program Files\\Zone_Sync\\zone_sync.exe
static std::wstring g_backupDir;    // Program Files\\Zone_Sync\\BackUP
static std::wstring g_logPath;      // Program Files\\Zone_Sync\\zone_sync.log
static std::wstring g_progDataDir;  // %ProgramData%\\ZoneSync
static bool g_logFallback = false;  // true - лог в %ProgramData% (запись в PF не удалась)
static bool g_bakFallback = false;  // true - бекапы в %ProgramData%'''
assert old in d, 'state vars'
d = d.replace(old, new, 1)

# 2. InitPaths: лог + бекап в Program Files (создаем, ACL - в --install-copy).
old = '''    // %ProgramData%\\ZoneSync\\files.txt.
    wchar_t pd[MAX_PATH] = { 0 };
    GetEnvironmentVariableW(L"ProgramData", pd, MAX_PATH);
    std::wstring dir = std::wstring(pd) + L"\\\\" + CONFIG_DIR_ENV;
    CreateDirectoryW(dir.c_str(), nullptr);
    g_configPath = dir + L"\\\\" + CONFIG_NAME;
    g_logPath = std::wstring(LOG_DIR) + L"\\\\" + LOG_NAME;
}'''
new = '''    // Канонические пути: Program Files\\Zone_Sync\\.
    g_configPath = g_installPath.substr(0, g_installPath.find_last_of(L"\\\\")) + L"\\\\" + CONFIG_NAME;
    g_logPath = g_installPath.substr(0, g_installPath.find_last_of(L"\\\\")) + L"\\\\" + LOG_NAME;
    g_backupDir = g_installPath.substr(0, g_installPath.find_last_of(L"\\\\")) + L"\\\\BackUP";
    CreateDirectoryW(g_backupDir.c_str(), nullptr);
}'''
assert old in d, 'initpaths block'
d = d.replace(old, new, 1)

# 3. InitPaths: убрать старые backupDir из констант.
d = d.replace('''    SHGetSpecialFolderPathW(nullptr, pf, CSIDL_PROGRAM_FILES, FALSE);
    g_installPath = std::wstring(pf) + L"\\\\" + INSTALL_DIR + L"\\\\zone_sync.exe";
    g_backupDir = std::wstring(pf) + L"\\\\" + INSTALL_DIR + L"\\\\BackUP";
    CreateDirectoryW(g_backupDir.c_str(), nullptr);
''', '''    SHGetSpecialFolderPathW(nullptr, pf, CSIDL_PROGRAM_FILES, FALSE);
    g_installPath = std::wstring(pf) + L"\\\\" + INSTALL_DIR + L"\\\\zone_sync.exe";
''')

# 4. RunInstallCopy: создать BackUP + icacls ACL для записи не-админом.
old = '''    if (!CopyFileW(self, g_installPath.c_str(), FALSE)) return 2;
    WriteLog(S(S_COPIED_TO_INSTALL));
    StartTarget();
    return 0;
}'''
new = '''    if (!CopyFileW(self, g_installPath.c_str(), FALSE)) return 2;
    // Папка бекапов + право записи для звичайних користувачів
    // (трей працює без підвищення, а бекапи пишуться сюди).
    std::wstring bkDir = g_installPath.substr(0, g_installPath.find_last_of(L"\\\\")) + L"\\\\BackUP";
    CreateDirectoryW(bkDir.c_str(), nullptr);
    std::wstring icacls = std::wstring(L"icacls.exe \\"") + bkDir +
        L"\\" /grant *S-1-5-11:(OI)(CI)M";
    DWORD icaclsCode = 1;
    RunHidden(icacls.c_str(), &icaclsCode);
    WriteLog(S(S_COPIED_TO_INSTALL));
    StartTarget();
    return 0;
}'''
assert old in d, 'install copy backup dir'
d = d.replace(old, new, 1)

open(p, 'wb').write(d.encode('utf-8'))
print('paths restructured')
