:: Like gh.cmd but lets the script's changes (disassembly, references) be saved into the project.
:: usage: scripts\re\ghidra\gh-write.cmd RefsFast.java refs.txt 0x142154e14
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

call "%GHIDRA%\support\analyzeHeadless.bat" "%PROJ%" Game3293 -process Game.exe -noanalysis -scriptPath "%S%" -postScript %*