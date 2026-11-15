# -*- coding: utf-8 -*-
d = open(r'D:\Work\ZoneSync\src\zone_sync.cpp', 'rb').read().decode('utf-8')
i = d.find('static int RunInstallCopy')
j = d.find('static bool StartTarget')
print('RunInstallCopy at', i, 'StartTarget at', j)
print(d[i:j])
