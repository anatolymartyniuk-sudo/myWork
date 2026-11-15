'--- zone_sync.log tail 12 ---'
Get-Content 'D:\3336\СЗІ\zone_sync.log' -Tail 12 -Encoding UTF8
'--- files.txt ---'
Get-Content 'C:\ProgramData\ZoneSync\files.txt' -Encoding UTF8
'--- file timestamps ---'
foreach ($p in 'D:\3336\СЗІ\!Зона ПК.xlsx','F:\!Зона ПК.xlsx') {
    if (Test-Path -LiteralPath $p) {
        $fi = Get-Item -LiteralPath $p
        '{0}  LastWrite={1}' -f $p, $fi.LastWriteTime
    } else { '{0}  NOT FOUND' -f $p }
}
