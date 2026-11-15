# -*- coding: utf-8 -*-
# Cleanup state vars: remove leftover g_backupDir/g_progDataDir/g_logFallback/g_bakFallback
# duplicates and fix InitPaths to set them properly with fallback logic.
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')
BS = chr(92)

# 1. state vars: proper set
old = '''static std::wstring g_logPath;      // LocalDir\\zone_sync.log
static std::wstring g_backupDir;    // Program Files\\Zone_Sync\\BackUP
static std::wstring g_progDataDir;  // %ProgramData%\\ZoneSync
static bool g_logFallback = false;  // true - лог в %ProgramData% (запись в PF не удалась)
static bool g_bakFallback = false;  // true - бекапы в %ProgramData%'''
new = '''static std::wstring g_logPath;      // активный путь лога
static std::wstring g_backupDir;    // активный путь бекапов
static std::wstring g_progDataDir;  // %ProgramData%\\ZoneSync (фолбэк)
static bool g_logFallback = false;  // true - лог в %ProgramData% (запись в PF не удалась)
static bool g_bakFallback = false;  // true - бекапы в %ProgramData%'''
assert old in d, 'state vars block'
d = d.replace(old, new, 1)

# 2. InitPaths: исправить пути
old2 = '''    g_logPath = g_installPath.substr(0, g_installPath.find_last_of(L"\\\\")) + L"\\\\" + LOG_NAME;
    g_backupDir = g_installPath.substr(0, g_installPath.find_last_of(L"\\\\")) + L"\\\\BackUP";
    CreateDirectoryW(g_backupDir.c_str(), nullptr);'''
new2 = '''    g_logPath = g_installPath.substr(0, g_installPath.find_last_of(L"\\\\")) + L"\\\\" + LOG_NAME;
    g_backupDir = g_installPath.substr(0, g_installPath.find_last_of(L"\\\\")) + L"\\\\BackUP";
    CreateDirectoryW(g_backupDir.c_str(), nullptr);
    // Фолбэк: %ProgramData%\\ZoneSync\\ (для не-админ запусков).
    GetEnvironmentVariableW(L"ProgramData", pd, MAX_PATH);
    g_progDataDir = std::wstring(pd) + L"\\\\" + CONFIG_DIR_ENV;
    CreateDirectoryW(g_progDataDir.c_str(), nullptr);'''
assert old2 in d, 'initpaths tail'
d = d.replace(old2, new2, 1)

# 3. pd объявление в InitPaths
old3 = '''static void InitPaths()
{
    // Канонические пути: Program Files\\Zone_Sync\\.'''
new3 = '''static void InitPaths()
{
    wchar_t pd[MAX_PATH] = { 0 };
    GetEnvironmentVariableW(L"ProgramData", pd, MAX_PATH);
    // Канонические пути: Program Files\\Zone_Sync\\.'''
assert old3 in d, 'initpaths head'
d = d.replace(old3, new3, 1)

open(p, 'wb').write(d.encode('utf-8'))
print('initpaths fixed')
