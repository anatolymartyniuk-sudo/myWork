# -*- coding: utf-8 -*-
# Bump: cpp + rc + manifest + version.txt -> 1.0.2.2
for p, old, new in [
    (r'D:\Work\ZoneSync\src\zone_sync.cpp', 'APP_VERSION = L"1.0.2.1"', 'APP_VERSION = L"1.0.2.2"'),
    (r'D:\Work\ZoneSync\res\resources.rc', 'FILEVERSION 1,0,2,1', 'FILEVERSION 1,0,2,2'),
    (r'D:\Work\ZoneSync\res\resources.rc', '"FileVersion",      "1.0.2.1"', '"FileVersion",      "1.0.2.2"'),
    (r'D:\Work\ZoneSync\res\resources.rc', '"ProductVersion",   "1.0.2.1"', '"ProductVersion",   "1.0.2.2"'),
    (r'D:\Work\ZoneSync\res\zone_sync.manifest', 'version="1.0.2.1"', 'version="1.0.2.2"'),
    (r'D:\Work\ZoneSync\version.txt', '1.0.2.1', '1.0.2.2'),
]:
    s = open(p, 'rb').read().decode('utf-8-sig')
    assert old in s, (p, old)
    open(p, 'wb').write(s.replace(old, new, 1).encode('utf-8-sig'))
print('bumped to 1.0.2.2')
