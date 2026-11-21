#!/bin/bash
# Stands in for the game binary when tools/parity/record.py runs a Windows build under Wine:
#   COD2_EXE=/path/to/cod2_win32_ded.exe tools/parity/record.py --binary tools/windows/wine-binary.sh ...
# record.py passes literal quotes around paths, which Wine would escape. Drop them.
export WINEDEBUG="${WINEDEBUG:--all}" WINEDLLOVERRIDES="mscoree,mshtml="
args=()
for arg in "$@"; do
    arg="${arg#\"}"
    args+=("${arg%\"}")
done
if [[ ${COD2_WINE_HEADLESS_CHECK:-0} == 1 ]]; then
    args=(+set developer 0 "${args[@]}")
fi
exec wine "$COD2_EXE" "${args[@]}"
