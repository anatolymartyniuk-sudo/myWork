# -*- coding: utf-8 -*-
# Удалить остатки старого тела CleanupBackups (строки 6-28 после заголовка)
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')
lines = d.split(chr(10))
# Найти "static void CleanupBackups()" и удалить мусор до следующей функции/комментария
start = None
for i, l in enumerate(lines):
    if l.strip() == 'static void CleanupBackups()' and i > 500:
        start = i
        break
assert start is not None
# Найти конец функции: первая строка после start, которая начинается с "// Файл заблоковано"
# или является закрывающей скобкой функции, за которой идёт комментарий
end = None
depth = 0
started = False
for j in range(start, len(lines)):
    l = lines[j]
    depth += l.count('{') - l.count('}')
    if l.strip() == '}' and depth == 0 and started:
        end = j
        break
    if l.strip().startswith('{'):
        started = True
# Удалить строки между end+1 и следующей строкой "// Файл заблоковано" - 1
nxt = None
for j in range(end + 1, len(lines)):
    if lines[j].strip().startswith('// Файл заблоковано'):
        nxt = j
        break
if nxt:
    del lines[end + 1:nxt]
open(p, 'w', encoding='utf-8').write(chr(10).join(lines))
print('cleanup done')
