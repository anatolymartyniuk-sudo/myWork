# -*- coding: utf-8 -*-
# LoadItems -> new version (generic pairs).
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')
BS = chr(92)

def extract_method(src, header):
    i = src.find(header)
    assert i >= 0, 'NOT FOUND: ' + header
    ls = src.rfind(chr(10), 0, i) + 1
    dep = 0
    started = False
    for j in range(ls, len(src)):
        ch = src[j]
        if ch == '{':
            dep += 1
            started = True
        elif ch == '}':
            dep -= 1
            if dep == 0 and started:
                return (ls, j + 1)
    return None

old_load = '''static void LoadItems()
{
    g_items.clear();
    HANDLE f = CreateFileW(g_configPath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) {
        SyncItem it; it.type = L'F';
        it.localPath = std::wstring(LOG_DIR) + L"''' + BS + BS + '''" + DEFAULT_FILE;
        it.flashName = DEFAULT_FILE;
        g_items.push_back(it);
        return;
    }'''
new_load = '''static void LoadItems()
{
    g_items.clear();
    HANDLE f = CreateFileW(g_configPath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) {
        SyncItem it; it.type = L'F';
        it.sideA = std::wstring(LOG_DIR) + L"''' + BS + '''" + DEFAULT_FILE;
        it.sideB = DEFAULT_FILE;
        g_items.push_back(it);
        return;
    }'''
assert old_load in d, 'load head not found'
d = d.replace(old_load, new_load, 1)

# flashName field -> sideB (миграция старого конфига)
old = '''                it.localPath = w.substr(p1 + 1, p2 - p1 - 1);
                it.flashName = w.substr(p2 + 1);
            } else {
                std::wstring name = w.substr(p1 + 1);
                it.localPath = std::wstring(LOG_DIR) + L"''' + BS + '''" + name;
                it.flashName = name;
            }'''
new = '''                it.localPath = w.substr(p1 + 1, p2 - p1 - 1);
                it.sideB = w.substr(p2 + 1);
            } else {
                std::wstring name = w.substr(p1 + 1);
                it.localPath = std::wstring(LOG_DIR) + L"''' + BS + '''" + name;
                it.sideB = name;
            }'''
assert old in d, 'migrate block not found'
d = d.replace(old, new, 1)

old = '''        it.localPath = std::wstring(LOG_DIR) + L"''' + BS + '''" + DEFAULT_FILE;
        it.flashName = DEFAULT_FILE;
        g_items.push_back(it);
    }
}'''
new = '''        it.sideA = std::wstring(LOG_DIR) + L"''' + BS + '''" + DEFAULT_FILE;
        it.sideB = DEFAULT_FILE;
        g_items.push_back(it);
    }
}'''
assert old in d, 'default block not found'
d = d.replace(old, new, 1)

open(p, 'wb').write(d.encode('utf-8'))
print('LoadItems updated to sideA/sideB')
