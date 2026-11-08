# 一键开服（局域网 / 内网穿透）。双击不行，在 PowerShell 里运行：
#   cd "F:\local exile"
#   .\scripts\dev\lan.ps1            # 用下面这台穿透的地址开服
#   .\scripts\dev\lan.ps1 -Stop      # 停服
#   .\scripts\dev\lan.ps1 -NoClient  # 只开服务器，不启动本机客户端
#   .\scripts\dev\lan.ps1 -DryRun    # 只打印将要执行的命令
#
# 参数默认值就是这台机器的穿透配置；换隧道就改参数或直接改这里的默认值。
param(
    [string]$Address = 'frp-net.com',
    [int]$PublicLoginPort = 20493,
    [int]$PublicPort = 15633,
    [switch]$NoClient,
    [switch]$Stop,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$live = Join-Path $repo 'scripts\dev\live.ps1'
if (-not (Test-Path $live)) { throw "找不到 $live（这个脚本要放在仓库的 scripts\dev 目录里）" }

# live.ps1 用 `dotnet` 编译服务端。从资源管理器或计划任务启动的进程往往没有 SDK 的路径，
# 那样 dotnet 会落到只有 runtime 的 Program Files 版本，编译第一步就失败。
function Find-DotnetWithSdk {
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    foreach ($exe in $candidates) {
        if (-not (Test-Path $exe)) { continue }
        $sdks = & $exe --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and "$sdks".Trim()) { return (Split-Path $exe -Parent) }
    }
    return $null
}

$root = Find-DotnetWithSdk
if ($root) {
    $env:DOTNET_ROOT = $root
    $env:PATH = $root + [IO.Path]::PathSeparator + $env:PATH
    # The Gateway copies every mod into %TEMP%\localexile-mod-copy before loading it. If that folder is left behind by a
    # process with a narrower token (an agent's sandboxed shell, an elevated run), a normal run cannot write into it and
    # every module fails to load — the Gateway then refuses every instance connection ("the game module is not loaded").
    # Keeping the copies inside the repository avoids sharing that folder with anyone else's run.
    $tmp = Join-Path $repo 'data\tmp'
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $env:TEMP = $tmp
    $env:TMP = $tmp
} else {
    Write-Warning '没有找到带 SDK 的 .NET（通常需要 .NET 10 SDK）：开服会在编译这一步失败。'
}

if ($Stop) {
    Write-Host '停止本 checkout 的服务器与客户端 ...' -ForegroundColor Cyan
    & $live -Stop
    return
}

# live.ps1 的参数按名字传：数组 splat 会按位置绑定，'-NoExtract' 会被当成 -Only 的值而报错。
$liveArgs = @{
    NoExtract = $true
    Lan = $true
    PublicAddress = $Address
    PublicLoginPort = $PublicLoginPort
    PublicPort = $PublicPort
}
if ($NoClient) { $liveArgs.NoClient = $true }
$liveShown = @('live.ps1') + ($liveArgs.GetEnumerator() | Where-Object { $_.Key -ne 'NoExtract' -or $true } | ForEach-Object {
    if ($_.Value -is [bool]) { "-$($_.Key)" } else { "-$($_.Key) $($_.Value)" } })

Write-Host ''
Write-Host '玩家连接信息：' -ForegroundColor Cyan
Write-Host "  登录地址     $Address : $PublicLoginPort"
Write-Host "  实例地址     $Address : $PublicPort"
Write-Host '  补丁         不用穿透：玩家端工具在本机自己回答'
Write-Host "  dotnet       $root"
Write-Host ''

if ($DryRun) {
    Write-Host "将执行： $($liveShown -join ' ')"
    return
}

$log = Join-Path $repo 'logs\lan-start.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
Write-Host "日志同时写入 $log" -ForegroundColor DarkGray
# Tee 让输出既留在窗口里，也落盘（排查时用）。
& $live @liveArgs 2>&1 | Tee-Object -FilePath $log -Append