$ProgressPreference = 'SilentlyContinue'
function Try-Get($u) {
    for ($i = 0; $i -lt 3; $i++) {
        try { return Invoke-RestMethod -Uri $u -Headers @{ 'User-Agent' = 'dsh' } -TimeoutSec 50 } catch { Start-Sleep 3 }
    }
    return $null
}
foreach ($n in @(713, 714)) {
    $x = Try-Get ("https://api.github.com/repos/LocalExile/LocalExile/pulls/" + $n)
    if (-not $x) { Write-Output ("PR " + $n + ": fetch failed"); continue }
    Write-Output ("PR " + $n + " title=" + $x.title)
    Write-Output ("   base=" + $x.base.ref + " commits=" + $x.commits + " files=" + $x.changed_files + " +" + $x.additions + "/-" + $x.deletions + " mergeable_state=" + $x.mergeable_state)
    $c = Try-Get ("https://api.github.com/repos/LocalExile/LocalExile/commits/" + $x.head.sha + "/check-runs")
    if ($c) { Write-Output ("   check-runs total=" + $c.total_count) } else { Write-Output "   check-runs: none/failed" }
    $f = Try-Get ("https://api.github.com/repos/LocalExile/LocalExile/pulls/" + $n + "/files?per_page=100")
    if ($f) { Write-Output ("   files: " + (($f | ForEach-Object { $_.filename }) -join ', ')) }
}