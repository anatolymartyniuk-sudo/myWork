# -*- coding: utf-8 -*-
# Fix CleanupBackupsIn: заменить сломанную функцию на корректную.
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')
BS = chr(92)

# Найти сломанный CleanupBackupsIn и заменить целиком
start = d.find('static void CleanupBackupsIn(')
assert start >= 0, 'CleanupBackupsIn not found'
# Найти конец функции: закрывающая скобка функции (следующая пустая строка + })
depth = 0
started = False
i = start
end = -1
while i < len(d):
    ch = d[i]
    if ch == '{':
        depth += 1
        started = True
    elif ch == '}':
        depth -= 1
        if depth == 0 and started:
            end = i + 1
            break
    i += 1
assert end >= 0

BS2 = BS + BS
new_fn = ('static void CleanupBackupsIn(const std::wstring& dir)\n'
          '{\n'
          '    if (GetFileAttributesW(dir.c_str()) == INVALID_FILE_ATTRIBUTES)\n'
          '        return;\n'
          '    FILETIME ft;\n'
          '    GetSystemTimeAsFileTime(&ft);\n'
          '    ULARGE_INTEGER cutoff;\n'
          '    cutoff.LowPart = ft.dwLowDateTime;\n'
          '    cutoff.HighPart = ft.dwHighDateTime;\n'
          '    cutoff.QuadPart -= 24ULL * 3600ULL * 10000000ULL;   // старше доби\n'
          '    WIN32_FIND_DATAW fd;\n'
          '    HANDLE h = FindFirstFileW((dir + L"' + BS + BS + '*").c_str(), &fd);\n'
          '    if (h == INVALID_HANDLE_VALUE) return;\n'
          '    do {\n'
          '        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;\n'
          '        ULARGE_INTEGER u;\n'
          '        u.LowPart = fd.ftLastWriteTime.dwLowDateTime;\n'
          '        u.HighPart = fd.ftLastWriteTime.dwHighDateTime;\n'
          '        if (u.QuadPart < cutoff.QuadPart) {\n'
          '            std::wstring p = dir + L"' + BS + BS + '" + fd.cFileName;\n'
          '            if (DeleteFileW(p.c_str()))\n'
          '                WriteLog(std::wstring(L"Бекап видалено (доба минула): ") + fd.cFileName);\n'
          '        }\n'
          '    } while (FindNextFileW(h, &fd));\n'
          '    FindClose(h);\n'
          '}')
d = d[:start] + new_fn + d[end:]
open(p, 'wb').write(d.encode('utf-8'))
print('CleanupBackupsIn fixed')
