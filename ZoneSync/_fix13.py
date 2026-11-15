# -*- coding: utf-8 -*-
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')
BS = chr(92)

# 1. State vars: убрать дубли, оставить чистый набор
lines = d.split(chr(10))
out = []
seen = set()
for i, l in enumerate(lines):
    st = l.strip()
    key = st.split(' ')[0] if st.startswith('static std::wstring g_') or st.startswith('static bool g_') else None
    if key:
        if key in ('g_logPath;', 'g_backupDir;', 'g_progDataDir;', 'g_logFallback', 'g_bakFallback'):
            if key in seen:
                continue
            seen.add(key)
    out.append(l)
d = chr(10).join(out)

# 2. InitPaths: добавить pd + фолбэк-пути
old = '''    // Канонічна копія: Program Files\\Zone_Sync\\zone_sync.exe.
    wchar_t pf[MAX_PATH] = { 0 };
    SHGetSpecialFolderPathW(nullptr, pf, CSIDL_PROGRAM_FILES, FALSE);
    g_installPath = std::wstring(pf) + L"\\\\" + INSTALL_DIR + L"\\\\zone_sync.exe";
'''
new = '''    // Канонічна копія: Program Files\\Zone_Sync\\zone_sync.exe.
    wchar_t pf[MAX_PATH] = { 0 };
    SHGetSpecialFolderPathW(nullptr, pf, CSIDL_PROGRAM_FILES, FALSE);
    g_installPath = std::wstring(pf) + L"\\\\" + INSTALL_DIR + L"\\\\zone_sync.exe";

    // %ProgramData%\\ZoneSync\\ (фолбэк для не-адмін запусков).
    wchar_t pd[MAX_PATH] = { 0 };
    GetEnvironmentVariableW(L"ProgramData", pd, MAX_PATH);
    g_progDataDir = std::wstring(pd) + L"\\\\" + CONFIG_DIR_ENV;
    CreateDirectoryW(g_progDataDir.c_str(), nullptr);
'''
assert old in d, 'initpaths install block'
d = d.replace(old, new, 1)

open(p, 'wb').write(d.encode('utf-8'))
print('initpaths + vars fixed')
