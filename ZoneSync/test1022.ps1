# Тест v1.0.2.2: запуск + проверка версии
$p = Start-Process -FilePath 'D:\Work\ZoneSync\zone_sync.exe' -PassThru
Start-Sleep -Seconds 5
$alive = -not $p.HasExited
'version: ' + [System.Diagnostics.FileVersionInfo]::GetVersionInfo('D:\Work\ZoneSync\zone_sync.exe').FileVersion
'ALIVE=' + $alive
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
'--- push to GitHub ---'
Set-Location 'D:\Work'
git add ZoneSync
git commit -m "Zone Sync 1.0.2.2: фолбэк лога/бекапов в %ProgramData% при недоступности Program Files" | Select-Object -First 2
$env:GIT_TERMINAL_PROMPT = '0'
git push origin master 2>&1 | Select-Object -First 4
