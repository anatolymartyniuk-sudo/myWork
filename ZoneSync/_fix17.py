# -*- coding: utf-8 -*-
# Вставить пустую строку между функциями для читаемости
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')
d = d.replace('}\n// Файл заблоковано іншою програмою', '}\n\n// Файл заблоковано іншою програмою', 1)
open(p, 'wb').write(d.encode('utf-8'))
print('done')
