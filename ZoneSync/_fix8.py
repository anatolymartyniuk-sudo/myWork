# -*- coding: utf-8 -*-
# Zone Sync: модель пари незалежних сторін (local<->local, flash<->flash,
# local<->flash). Міграція конфіга, універсальні функції, меню, версія.
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')
BS = chr(92)

def rep(old, new, cnt=1):
    global d
    assert old in d, 'NOT FOUND: ' + old[:80]
    d = d.replace(old, new, cnt)

# ---------------------------------------------------------------------
# 1. struct SyncItem.
# ---------------------------------------------------------------------
rep("// Елемент синхронізації: 'F' файл / 'D' папка (Directory).\n"
    "struct SyncItem { wchar_t type; std::wstring localPath; std::wstring flashName; };",
    "// Елемент синхронізації: ПАРА незалежних сторін. Кожна сторона -\n"
    "// абсолютний шлях (фіксований диск) або відносний шлях (знімний\n"
    "// носій - розв'язується на всі підключені флешки під час syncing).\n"
    "// Це дає local<->local, local<->flash і flash<->flash.\n"
    "struct SyncItem { wchar_t type; std::wstring sideA; std::wstring sideB; };")

# ---------------------------------------------------------------------
# 2. LoadItems (повністю).
# ---------------------------------------------------------------------
old_load = '''static void LoadItems()
{
    g_items.clear();
    HANDLE f = CreateFileW(g_configPath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) {
        SyncItem it; it.type = L'F';
        it.localPath = std::wstring(LOG_DIR) + L"\\\\" + DEFAULT_FILE;
        it.flashName = DEFAULT_FILE;
        g_items.push_back(it);
        return;
    }
    std::string data;
    char buf[4096]; DWORD got = 0;
    while (ReadFile(f, buf, sizeof(buf), &got, nullptr) && got > 0)
        data.append(buf, got);
    CloseHandle(f);
    size_t pos = 0;
    while (pos <= data.size()) {
        size_t nl = data.find('\\n', pos);
        std::string line = data.substr(pos, (nl == std::string::npos ? data.size() : nl) - pos);
        std::wstring w = Trim(Utf8ToWide(line));
        if (!w.empty() && w[0] != L'#') {
            SyncItem it;
            // Формат: "F|локальний шлях|ім'я на флешці".
            // Старий формат "F|ім'я" -> шлях з LOG_DIR (міграція).
            size_t p1 = w.find(L'|');
            if (p1 != std::wstring::npos && (w[0] == L'F' || w[0] == L'D')) {
                it.type = w[0];
                size_t p2 = w.find(L'|', p1 + 1);
                if (p2 != std::wstring::npos) {
                    it.localPath = w.substr(p1 + 1, p2 - p1 - 1);
                    it.flashName = w.substr(p2 + 1);
                } else {
                    std::wstring name = w.substr(p1 + 1);
                    it.localPath = std::wstring(LOG_DIR) + L"\\" + name;
                    it.flashName = name;
                }
                if (!it.localPath.empty() && !it.flashName.empty())
                    g_items.push_back(it);
            }
        }
        if (nl == std::string::npos) break;
        pos = nl + 1;
    }
    if (g_items.empty()) {
        SyncItem it; it.type = L'F';
        it.localPath = std::wstring(LOG_DIR) + L"\\" + DEFAULT_FILE;
        it.flashName = DEFAULT_FILE;
        g_items.push_back(it);
    }
}'''
new_load = '''// Розбір рядка конфіга по '|' (шляхи не містять '|').
static std::vector<std::wstring> SplitFields(const std::wstring& w)
{
    std::vector<std::wstring> t;
    size_t pos = 0;
    while (true) {
        size_t p = w.find(L'|', pos);
        if (p == std::wstring::npos) { t.push_back(w.substr(pos)); break; }
        t.push_back(w.substr(pos, p - pos));
        pos = p + 1;
    }
    return t;
}

static void LoadItems()
{
    g_items.clear();
    HANDLE f = CreateFileW(g_configPath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) {
        SyncItem it; it.type = L'F';
        it.sideA = std::wstring(LOG_DIR) + L"\\" + DEFAULT_FILE;
        it.sideB = DEFAULT_FILE;
        g_items.push_back(it);
        return;
    }
    std::string data;
    char buf[4096]; DWORD got = 0;
    while (ReadFile(f, buf, sizeof(buf), &got, nullptr) && got > 0)
        data.append(buf, got);
    CloseHandle(f);
    size_t pos = 0;
    while (pos <= data.size()) {
        size_t nl = data.find('\\n', pos);
        std::string line = data.substr(pos, (nl == std::string::npos ? data.size() : nl) - pos);
        std::wstring w = Trim(Utf8ToWide(line));
        if (!w.empty() && w[0] != L'#') {
            std::vector<std::wstring> t = SplitFields(w);
            SyncItem it;
            // Новий формат: "PF|сторона A|сторона B" / "PD|сторона A|сторона B".
            if (t.size() == 3 && (t[0] == L"PF" || t[0] == L"PD")) {
                it.type = t[0][1];
                it.sideA = t[1]; it.sideB = t[2];
            }
            // Міграція зі старого: "F|локальний шлях|ім'я на флешці"
            // або "F|ім'я" (шлях з LOG_DIR).
            else if (t.size() >= 2 && (t[0] == L"F" || t[0] == L"D")) {
                it.type = t[0][0];
                if (t.size() == 3) {
                    it.sideA = (t[1].find(L':') == 1)
                        ? t[1] : std::wstring(LOG_DIR) + L"\\" + t[1];
                    it.sideB = t[2];
                } else {
                    it.sideA = std::wstring(LOG_DIR) + L"\\" + t[1];
                    it.sideB = t[1];
                }
            }
            if (!it.sideA.empty() && !it.sideB.empty())
                g_items.push_back(it);
        }
        if (nl == std::string::npos) break;
        pos = nl + 1;
    }
    if (g_items.empty()) {
        SyncItem it; it.type = L'F';
        it.sideA = std::wstring(LOG_DIR) + L"\\" + DEFAULT_FILE;
        it.sideB = DEFAULT_FILE;
        g_items.push_back(it);
    }
}'''
rep(old_load, new_load)

# ---------------------------------------------------------------------
# 3. SaveItems.
# ---------------------------------------------------------------------
old_save = '''static void SaveItems()
{
    std::string data = "\\xEF\\xBB\\xBF";
    for (size_t i = 0; i < g_items.size(); i++) {
        data += (g_items[i].type == L'D' ? "D|" : "F|");
        data += WideToUtf8(g_items[i].localPath);
        data += "|";
        data += WideToUtf8(g_items[i].flashName);
        data += "\\r\\n";
    }'''
new_save = '''static void SaveItems()
{
    std::string data = "\\xEF\\xBB\\xBF";
    for (size_t i = 0; i < g_items.size(); i++) {
        data += (g_items[i].type == L'D' ? "PD|" : "PF|");
        data += WideToUtf8(g_items[i].sideA);
        data += "|";
        data += WideToUtf8(g_items[i].sideB);
        data += "\\r\\n";
    }'''
rep(old_save, new_save)

# ---------------------------------------------------------------------
# 4. ItemInList / RemoveItemFromList / AddItemToList.
# ---------------------------------------------------------------------
old_list = '''static bool ItemInList(wchar_t type, const std::wstring& localPath)
{
    for (size_t i = 0; i < g_items.size(); i++)
        if (g_items[i].type == type &&
            _wcsicmp(g_items[i].localPath.c_str(), localPath.c_str()) == 0) return true;
    return false;
}

static void RemoveItemFromList(wchar_t type, const std::wstring& localPath)
{
    std::vector<SyncItem> out;
    for (size_t i = 0; i < g_items.size(); i++) {
        if (g_items[i].type == type &&
            _wcsicmp(g_items[i].localPath.c_str(), localPath.c_str()) == 0) continue;
        out.push_back(g_items[i]);
    }
    g_items = out;
    SaveItems();
}

static void AddItemToList(wchar_t type, const std::wstring& localPath,
                          const std::wstring& flashName)
{
    if (ItemInList(type, localPath)) return;
    SyncItem it; it.type = type; it.localPath = localPath; it.flashName = flashName;
    g_items.push_back(it);
    SaveItems();
}'''
new_list = '''static bool ItemInList(wchar_t type, const std::wstring& sideA,
                       const std::wstring& sideB)
{
    for (size_t i = 0; i < g_items.size(); i++)
        if (g_items[i].type == type &&
            _wcsicmp(g_items[i].sideA.c_str(), sideA.c_str()) == 0 &&
            _wcsicmp(g_items[i].sideB.c_str(), sideB.c_str()) == 0) return true;
    return false;
}

static void RemoveItemFromList(wchar_t type, const std::wstring& sideA,
                               const std::wstring& sideB)
{
    std::vector<SyncItem> out;
    for (size_t i = 0; i < g_items.size(); i++) {
        if (g_items[i].type == type &&
            _wcsicmp(g_items[i].sideA.c_str(), sideA.c_str()) == 0 &&
            _wcsicmp(g_items[i].sideB.c_str(), sideB.c_str()) == 0) continue;
        out.push_back(g_items[i]);
    }
    g_items = out;
    SaveItems();
}

static void AddItemToList(wchar_t type, const std::wstring& sideA,
                          const std::wstring& sideB)
{
    if (ItemInList(type, sideA, sideB)) return;
    SyncItem it; it.type = type; it.sideA = sideA; it.sideB = sideB;
    g_items.push_back(it);
    SaveItems();
}'''
rep(old_list, new_list)

open(p, 'wb').write(d.encode('utf-8'))
print('part 1 done (struct/config)')
