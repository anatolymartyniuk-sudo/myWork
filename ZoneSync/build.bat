@echo off
rem =====================================================================
rem Zone Sync - сборка (MSYS2 UCRT64: windres + g++, статическая линковка).
rem Версия: при каждой сборке меняется в ТРЁХ местах:
rem   src\zone_sync.cpp      - APP_VERSION
rem   res\resources.rc       - FILEVERSION + FileVersion/ProductVersion
rem   res\zone_sync.manifest - assemblyIdentity version
rem =====================================================================
cd /d %~dp0
set PATH=C:\msys64\ucrt64\bin;%PATH%

windres.exe res\resources.rc -O coff -o res\resources.o
if errorlevel 1 goto :err

g++.exe src\zone_sync.cpp res\resources.o -o zone_sync.exe ^
    -municode -mwindows -static -s ^
    -lshell32 -luser32 -ladvapi32 -lcomdlg32 -lversion -lole32 -luuid -lurlmon
if errorlevel 1 goto :err

echo BUILD OK
exit /b 0
:err
echo BUILD FAILED
exit /b 1
