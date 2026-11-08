:: Import Client/Game.exe into a Ghidra project and run the full analysis (Ghidra 12.2, JDK 25).
:: Long: an hour or more for a 79 MB image. Run it as a background job and poll logs\ghidra-import.log.
::
:: Two environment quirks this script works around:
::  * the repository path contains a space ("local exile"), which the Ghidra launcher splits when JAVA_HOME is
::    unquoted — everything below uses the 8.3 short path (F:\LOCALE~1);
::  * the agent's shell runs with a restricted token, so %TEMP%, %APPDATA% and the JDK's user.home all point inside
::    the repository.
@echo off
setlocal
set "REPO=F:\LOCALE~1"
set "GHIDRA=%REPO%\Tools\ghidra\ghidra_12.2_PUBLIC"
set "PROJ=%REPO%\Tools\ghidra-projects"
set "EXE=%REPO%\Client\Game.exe"
set "LOG=%REPO%\logs\ghidra-import.log"
set "JAVA_HOME=%REPO%\Tools\jdk25\JDK-25~1.5_8"

set "GHIDRA_MAXMEM=8G"
set "TMP=%REPO%\data\tmp"
set "TEMP=%REPO%\data\tmp"
set "HOME=%REPO%\Tools\ghidra-home"
set "APPDATA=%HOME%\AppData\Roaming"
set "LOCALAPPDATA=%HOME%\AppData\Local"
set "JAVA_TOOL_OPTIONS=-Duser.home=%HOME%"

if not exist "%PROJ%" mkdir "%PROJ%"
if not exist "%TEMP%" mkdir "%TEMP%"
if not exist "%APPDATA%" mkdir "%APPDATA%"
if not exist "%LOCALAPPDATA%" mkdir "%LOCALAPPDATA%"
if not exist "%HOME%" mkdir "%HOME%"

echo === importing %EXE% at %DATE% %TIME% > "%LOG%"
call "%GHIDRA%\support\analyzeHeadless.bat" "%PROJ%" Game3293 -import "%EXE%" -overwrite -analysisTimeoutPerFile 1800 >> "%LOG%" 2>&1
echo === done (exit %ERRORLEVEL%) at %DATE% %TIME% >> "%LOG%"