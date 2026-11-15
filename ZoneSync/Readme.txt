ZONE SYNC — синхронизация файла(ов) между ПК и флешкой
======================================================

Версия: 1.0.0.1
ОС: Windows 10/11

ЧТО ЭТО
-------
Исполняемая утилита (zone_sync.exe) двусторонней синхронизации файлов
между локальной папкой (D:\3336\СЗІ) и съёмными носителями.
Значок в трее, автозапуск при входе, мгновенная реакция на подключение
флешки. Сравнивает дату изменения; более свежая копия заменяет старую.
ПЕРЕД перезаписью старая копия сохраняется как бекап — данные
не теряются никогда. Ничего не удаляет.

УСТАНОВКА
---------
Установка не нужна: скопируйте zone_sync.exe в любую папку
и запустите. В трее появится значок (две стрелки по кругу).

ТРЕЙ-МЕНЮ (правый клик по значку)
---------------------------------
  Синхронізувати зараз      — немедленная синхронизация
  Автозапуск при вході      — (галочка) задача Планировщика:
                              запуск трея при входе в систему
  Файли синхронізації…      — подменю:
      Додати файл…              — выбрать файл (должен существовать
                                  в локальной папке)
      Редагувати список…        — открыть список в Блокноте
      ✕  имя файла              — удалить файл из списка
                                  (сам файл не удаляется)
  Показати лог              — открыть лог в Блокноте
  Мова / Language           — переключение UA/EN (применяется сразу)
  Вихід

КАК РАБОТАЕТ СИНХРОНИЗАЦИЯ
--------------------------
1. Флешки ищутся автоматически (Win32_LogicalDisk DriveType=2) —
   буква не важна (F:, E: и т.д.).
2. Сравнение дат изменения с допуском 3 секунды: FAT хранит время
   с точностью до 2 с — без допуска синхронизация ходила бы по кругу.
3. Свежая копия перезаписывает старую; старая ПЕРЕД этим
   сохраняется как имя.bak_дата.xlsx.
4. Файл открыт в Excel — балун «Файл відкрито в іншій програмі»,
   ничего не портится.
5. При успешной синхронизации — балун «Зміни перенесено»
   с направлением (local→flash / flash→local). Если копии
   идентичны — тихо, только запись в лог.
6. Лог: D:\3336\СЗІ\zone_sync.log (кольцо ~100 КБ), UTF-8.

АВТОЗАПУСК И РЕАКЦИЯ НА ФЛЕШКУ
------------------------------
Меню [Автозапуск при вході] создаёт задачу Планировщика "ZoneSync"
(требует прав администратора — UAC, ретрай автоматический):
запуск трея при входе в систему.

Подключение флешки отслеживается напрямую (WM_DEVICECHANGE):
синхронизация запускается через 1,5 с после подключения, без
Планировщика-событий. Также выполняется начальная синхронизация
при старте трея, если флешка уже подключена.

Контакты: при уже запущенном трее второй экземпляр не стартует
(единственный экземпляр).

СПИСОК ФАЙЛОВ
-------------
Хранится в %ProgramData%\ZoneSync\files.txt (UTF-8, одно имя файла
на строку). Редактируется через меню «Файли синхронізації…»
или любым редактором. Добавляемый файл должен существовать
в локальной папке.

БЕЗ EXECUTION POLICY
--------------------
Это exe — политика выполнения сценариев (Execution Policy)
на него не действует вообще. Никаких .bat-обёрток не нужно.

ДЛЯ РАЗРАБОТЧИКА (сборка)
-------------------------
MSYS2 UCRT64 (g++ + windres), статическая линковка — exe автономен:
  build.bat            — windres res\resources.rc + g++ src\zone_sync.cpp
Версия при каждой сборке меняется в ТРЁХ местах:
  src\zone_sync.cpp      - APP_VERSION
  res\resources.rc       - FILEVERSION + FileVersion/ProductVersion
  res\zone_sync.manifest - assemblyIdentity version
Иконка: res\sync.ico (генерируется make_icon.py).

============================================================

ZONE SYNC — syncing file(s) between PC and flash drive
======================================================

Version: 1.0.0.1
OS: Windows 10/11

WHAT IT IS
----------
An executable utility (zone_sync.exe) for two-way syncing of files
between a local folder (D:\3336\СЗІ) and removable drives.
Tray icon, autostart at logon, instant reaction to flash drive
connection. Compares modification dates; the newer copy replaces
the older one. BEFORE overwriting, the older copy is saved as a
backup — data is never lost. Does not delete anything.

INSTALLATION
------------
No installation needed: copy zone_sync.exe to any folder and run it.
A tray icon appears (two circular arrows).

TRAY MENU (right-click the icon)
--------------------------------
  Синхронізувати зараз      - sync now
  Автозапуск при вході      - (checked) Scheduler task: start the
                              tray at logon
  Файли синхронізації…      - submenu:
      Додати файл…              - pick a file (must exist in the
                                  local folder)
      Редагувати список…        - open the list in Notepad
      ✕  file name              - remove the file from the list
                                  (the file itself is not deleted)
  Показати лог              - open the log in Notepad
  Мова / Language           - switch UA/EN (applied immediately)
  Вихід

HOW SYNCING WORKS
-----------------
1. Flash drives are found automatically (Win32_LogicalDisk
   DriveType=2) - the drive letter does not matter.
2. Date comparison with a 3-second tolerance: FAT stores timestamps
   with 2-second granularity - without the tolerance the sync would
   ping-pong between the copies.
3. The newer copy overwrites the older one; the older one is FIRST
   saved as name.bak_date.xlsx.
4. If the file is open in Excel - a balloon says the file is locked,
   nothing gets corrupted.
5. On a successful sync - a balloon "Changes transferred" with the
   direction (local→flash / flash→local). Identical copies are
   silent (log only).
6. Log: D:\3336\СЗІ\zone_sync.log (ring ~100 KB), UTF-8.

AUTOSTART AND FLASH REACTION
----------------------------
The menu item [Автозапуск при вході] creates the "ZoneSync"
Scheduler task (requires admin - UAC, automatic retry): the tray
starts at logon.

Flash connection is tracked directly (WM_DEVICECHANGE): sync starts
1.5 seconds after the drive appears, no event-based Scheduler
triggers needed. An initial sync also runs at tray start if the
flash is already connected.

A second instance does not start while one is running
(single instance).

FILE LIST
---------
Stored in %ProgramData%\ZoneSync\files.txt (UTF-8, one file name
per line). Edited via the "Файли синхронізації…" menu or any text
editor. The added file must exist in the local folder.

EXECUTION POLICY
----------------
This is an exe - the script execution policy does not apply to it
at all. No .bat wrappers are needed.

FOR DEVELOPERS (build)
----------------------
MSYS2 UCRT64 (g++ + windres), static linking - standalone exe:
  build.bat            - windres res\resources.rc + g++ src\zone_sync.cpp
Version is changed in THREE places on every build:
  src\zone_sync.cpp      - APP_VERSION
  res\resources.rc       - FILEVERSION + FileVersion/ProductVersion
  res\zone_sync.manifest - assemblyIdentity version
Icon: res\sync.ico (generated by make_icon.py).
