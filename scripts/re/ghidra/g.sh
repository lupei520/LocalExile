#!/bin/bash
# usage: g.sh <Script.java> [args...]  — runs a script against the saved Game3293 project (read-only).
# GLINES=<n> shows more than the first 8 matching log lines.
#
# The checkout's path is turned into its 8.3 form (F:\LOCALE~1) first: cmd cannot be handed a path with a space
# through analyzeHeadless.bat, and Ghidra's own directories go under the checkout because the sandbox denies %APPDATA%
# and %TEMP% to this process.
R=$(cd "$(dirname "$0")/../../.." && pwd)
RW=$(cd "$R" && pwd -W)
W=$(powershell -NoProfile -Command "(New-Object -ComObject Scripting.FileSystemObject).GetFolder('$RW').ShortPath" 2>/dev/null | tr -d '\r\n')
[ -n "$W" ] || W="$RW"
S="$W\\scripts\\re\\ghidra"
export JAVA_HOME="$W\\Tools\\jdk25\\jdk-25.0.5+8"
export TMP="$W\\data\\tmp\\gtmp" TEMP="$W\\data\\tmp\\gtmp" APPDATA="$W\\data\\tmp\\gapp" USERPROFILE="$W\\data\\tmp\\ghome"
mkdir -p "$R/data/tmp/gtmp" "$R/data/tmp/gapp" "$R/data/tmp/ghome"
cd "$R" && cmd //c "Tools\\ghidra\\ghidra_12.2_PUBLIC\\support\\analyzeHeadless.bat Tools\\ghidra-projects Game3293 -process Game.exe -noanalysis -readOnly -scriptPath $S -postScript $*" 2>&1 | grep -E "ERROR|Exception|SCRIPT" | head -${GLINES:-8}
