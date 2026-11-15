# -*- coding: utf-8 -*-
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')

# 1. wchar[] concat fix
old1 = 'WriteLog(L"Бекап видалено (доба минула): " + fd.cFileName);'
new1 = 'WriteLog(std::wstring(L"Бекап видалено (доба минула): ") + fd.cFileName);'
assert old1 in d
d = d.replace(old1, new1, 1)

# 2. RunHidden forward declaration (RunInstallCopy uses it before definition)
anchor = 'static int RunInstallCopy()'
assert anchor in d
fwd = ('// Попереднє оголошення (використовується раніше за визначення).\n'
       'static bool RunHidden(const wchar_t* cmdline, DWORD* exitCode = nullptr);\n\n')
i = d.find(anchor)
d = d[:i] + fwd + d[i:]

open(p, 'wb').write(d.encode('utf-8'))
print('fixed')
