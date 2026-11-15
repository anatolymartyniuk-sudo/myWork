# -*- coding: utf-8 -*-
p = r'D:\Work\ZoneSync\src\zone_sync.cpp'
d = open(p, 'rb').read().decode('utf-8')
BS = chr(92)

# 1. КРИТИЧЕСКИЙ ФИКС: notify передавался как dry - все проходы были холостыми.
d = d.replace('static void DoSyncOnce(bool notify, std::wstring* balloonOut, bool* isError)',
              'static void DoSyncOnce(bool dry, std::wstring* balloonOut, bool* isError)')
d = d.replace('SyncFileItem(g_items[i], notify, &done, &firstError, &identical);',
              'SyncFileItem(g_items[i], dry, &done, &firstError, &identical);')
d = d.replace('SyncFolderPair(g_items[i], notify, &done, &firstError, &identical);',
              'SyncFolderPair(g_items[i], dry, &done, &firstError, &identical);')
# вызывающие стороны: в трее и тихом режиме синхронизация РЕАЛЬНАЯ (не dry)
d = d.replace('''            std::wstring text; bool err = false;
            DoSyncOnce(true, &text, &err);''',
              '''            std::wstring text; bool err = false;
            DoSyncOnce(false, &text, &err);''')
d = d.replace('''            std::wstring text; bool err = false;
            DoSyncOnce(true, &text, &err);
            if (!text.empty()) ShowBalloon(text, err);
        }
        else if (wp == TIMER_ID + 1) {''',
              '''            std::wstring text; bool err = false;
            DoSyncOnce(false, &text, &err);
            if (!text.empty()) ShowBalloon(text, err);
        }
        else if (wp == TIMER_ID + 1) {''')
d = d.replace('''    std::wstring text; bool err = false;
    DoSyncOnce(true, &text, &err);
    // Балун у тихому режимі неможливий (трея немає) - усе в логу.''',
              '''    std::wstring text; bool err = false;
    DoSyncOnce(false, &text, &err);
    // Балун у тихому режимі неможливий (трея немає) - усе в логу.''')

open(p, 'wb').write(d.encode('utf-8'))
print('dry/notify fix done')
