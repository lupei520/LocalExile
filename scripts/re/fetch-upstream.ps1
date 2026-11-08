$ProgressPreference = 'SilentlyContinue'
$ErrorActionPreference = 'Continue'
$root = 'F:\local exile\Tools\download'
function Get-Text($path) {
    $url = 'https://api.github.com/repos/LocalExile/LocalExile/contents/' + $path
    for ($i = 0; $i -lt 3; $i++) {
        try {
            $j = Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = 'dsh' } -TimeoutSec 60
            $b64 = [string]::Join('', $j.content).Replace("`n", '').Replace("`r", '').Replace(' ', '')
            return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64))
        } catch { Start-Sleep 3 }
    }
    return $null
}
foreach ($f in @('Agents/docs/protocol/c2s.md', 'Agents/status/world.md', 'Agents/roadmap/world.md')) {
    $t = Get-Text $f
    $name = ($f -split '/')[-1]
    if ($t) {
        [IO.File]::WriteAllText((Join-Path $root ('upstream-' + ($f -replace '/', '-'))), $t, (New-Object Text.UTF8Encoding($false)))
        Write-Output ("OK {0} {1} chars" -f $f, $t.Length)
    } else {
        Write-Output ("FAIL {0}" -f $f)
    }
}