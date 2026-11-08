:: Run one of scripts\re\ghidra\*.java against the imported Game.exe, read-only, without re-running analysis.
:: usage: Tools\re\gh.cmd Decomp 0x1421a18b0
:: Same environment workarounds as import-game3293.cmd (8.3 paths, repository-local TMP/APPDATA/JDK home).
@echo off
setlocal
set "REPO=F:\LOCALE~1"
set "GHIDRA=%REPO%\Tools\ghidra\ghidra_12.2_PUBLIC"
set "PROJ=%REPO%\Tools\ghidra-projects"
set "S=%REPO%\scripts\re\ghidra"
set "JAVA_HOME=%REPO%\Tools\jdk25\JDK-25~1.5_8"
set "GHIDRA_MAXMEM=12G"
set "TMP=%REPO%\data\tmp"
set "TEMP=%REPO%\data\tmp"
set "HOME=%REPO%\Tools\ghidra-home"
set "APPDATA=%HOME%\AppData\Roaming"
set "LOCALAPPDATA=%HOME%\AppData\Local"
set "JAVA_TOOL_OPTIONS=-Duser.home=%HOME%"

call "%GHIDRA%\support\analyzeHeadless.bat" "%PROJ%" Game3293 -process Game.exe -noanalysis -readOnly -scriptPath "%S%" -postScript %*