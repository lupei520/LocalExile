$env:DOTNET_ROOT = 'C:\Users\DRLUPEI\.dotnet'
$env:PATH = 'C:\Users\DRLUPEI\.dotnet;' + $env:PATH
$log = 'F:\local exile\logs\lan-run3.log'
$errLog = $log + '.err'
if (Test-Path $log) { Remove-Item $log -Force }
if (Test-Path $errLog) { Remove-Item $errLog -Force }
$argLine = '-NoProfile -ExecutionPolicy Bypass -File "F:\local exile\scripts\dev\lan.ps1" -NoClient'
$p = Start-Process powershell -ArgumentList $argLine -WorkingDirectory 'F:\local exile' -NoNewWindow -PassThru `
    -RedirectStandardOutput $log -RedirectStandardError $errLog
if ($p.WaitForExit(220000)) { Write-Output "script exited code=$($p.ExitCode)" } else { Write-Output 'script still running after 220s (good: servers up)'; Stop-Process -Id $p.Id -Force }
Write-Output '--- stdout tail:'
if (Test-Path $log) { Get-Content $log -Tail 14 | Out-String }
Write-Output '--- stderr (red) tail:'
if (Test-Path $errLog) { Get-Content $errLog -Tail 20 | Out-String }
Write-Output '--- ports:'
foreach ($port in 20493, 6112, 12995) {
    $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { Write-Output ("$port on " + $c.LocalAddress) } else { Write-Output "$port down" }
}