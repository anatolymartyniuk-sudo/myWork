# -*- coding: utf-8 -*-
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')
lines = d.split(chr(10))
out = []
seen_g_logPath = 0
for i, l in enumerate(lines):
    if l.strip().startswith('static std::wstring g_logPath;'):
        seen_g_logPath += 1
        if seen_g_logPath > 1:
            continue  # удалить дубликат
    out.append(l)
open(p, 'wb').write(chr(10).join(out).encode('utf-8'))
print('dup g_logPath removed, count was', seen_g_logPath)
