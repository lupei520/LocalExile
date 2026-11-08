# usage: decomp.ps1 <outfile> <addr[:depth]>...
param([string]$Out, [Parameter(ValueFromRemainingArguments=$true)][string[]]$Addrs)
$r = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$env:JAVA_HOME = "$r\Tools\jdk25\jdk-25.0.5+8"
& "$r\Tools\ghidra\ghidra_12.2_PUBLIC\support\analyzeHeadless.bat" "$r\Tools\ghidra-projects" Game3293 -process Game.exe -noanalysis -readOnly -scriptPath "$r\scripts\re\ghidra" -postScript Decomp.java $Out @Addrs 2>&1 | Select-String -Pattern "ERROR|Exception|REPORT" | Select-Object -First 10
