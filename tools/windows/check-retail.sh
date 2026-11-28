#!/usr/bin/env bash
# Capture the hash-pinned, caller-supplied retail Windows game in offline Wine.
set -euo pipefail

if [[ $# -lt 3 || $# -gt 4 ]]; then
    echo "Usage: $0 <licensed-game-directory> <retail-1.3-directory> <Unbound-original-replay-directory> [usercmds.txt]" >&2
    exit 2
fi
root=$(cd "$(dirname "$0")/../.." && pwd)
data=$(cd "$1" && pwd)
retail=$(cd "$2" && pwd)
trace=$(cd "$3" && pwd)
input=${4:-$root/tools/windows/fixtures/two-bot/usercmds.txt}
export COD2_RETAIL_MAP=${COD2_RETAIL_MAP:-mp_toujane}
export COD2_RETAIL_GAMETYPE=${COD2_RETAIL_GAMETYPE:-dm}
export COD2_RETAIL_SEED=${COD2_RETAIL_SEED:-12345}
export COD2_RETAIL_CLIENTS=${COD2_RETAIL_CLIENTS:-2}
if [[ ! $COD2_RETAIL_MAP =~ ^mp_[a-zA-Z0-9_]+$ ||
      ! $COD2_RETAIL_GAMETYPE =~ ^(dm|tdm|sd|ctf|hq)$ ||
      ! $COD2_RETAIL_SEED =~ ^[0-9]+$ || ! $COD2_RETAIL_CLIENTS =~ ^[1-9][0-9]?$ ]]; then
    echo 'Invalid retail replay map, gametype, seed or client count' >&2
    exit 2
fi
for file in "$retail/CoD2MP_s.exe" "$retail/gfx_d3d_mp_x86_s.dll" \
    "$data/mss32.dll" "$trace/statehash.txt" "$trace/players.txt" "$input"; do
    if [[ ! -f "$file" ]]; then
        echo "Missing $file" >&2
        exit 2
    fi
done

output=${COD2_RETAIL_OUTPUT:-$root/output/r05/oracle}
mkdir -p "$output"
output=$(cd "$output" && pwd)
run=$(mktemp -d "$output/retail-XXXXXXXX")
mkdir -p "$run/game" "$run/prefix"
echo "Retail replay evidence in $run"
cp "$input" "$run/usercmds.txt"
if [[ -n ${COD2_RETAIL_FIXTURE_SCRIPT:-} ]]; then
    cp "$COD2_RETAIL_FIXTURE_SCRIPT" "$run/fixture.gsc"
fi
if ! replay_counts=$(python3 -B - "$run/usercmds.txt" <<'PY'
import re
import sys
from pathlib import Path

lines = Path(sys.argv[1]).read_text(encoding="ascii").splitlines()
if not lines or lines[0] != "cod2-replay 1":
    sys.exit(2)
end = re.fullmatch(r"end ([0-9]+)", lines[-1])
commands = len(lines) - 2
if not end or not 4 <= int(end[1]) <= 2147483647 or commands < 1:
    sys.exit(2)
print(int(end[1]) - 3, commands)
PY
); then
    echo 'Retail replay needs commands and a valid end tick' >&2
    exit 2
fi
read -r COD2_RETAIL_TICKS COD2_RETAIL_COMMANDS <<< "$replay_counts"
export COD2_RETAIL_TICKS COD2_RETAIL_COMMANDS

"$root/tools/windows/prepare-retail-replay.sh" "$data" "$retail" "$run" "$COD2_RETAIL_GAMETYPE"

docker run --rm --init -i --pull=never --platform linux/386 --network none \
    --label cod2.windows.check=retail --label cod2.r05=oracle \
    -e COD2_RETAIL_MAP -e COD2_RETAIL_GAMETYPE -e COD2_RETAIL_SEED \
    -e COD2_RETAIL_TICKS -e COD2_RETAIL_COMMANDS -e COD2_RETAIL_CLIENTS \
    --tmpfs /capture/prefix:mode=0700,size=1g \
    -v "$run:/capture" -v "$data:/data:ro" -v "$retail:/retail:ro" \
    -e QEMU_RESERVED_VA=0x80000000 \
    "${COD2_WINDOWS_RETAIL_IMAGE:-sha256:759dc298078a9637ce371a1cf9b8551a20aea985c1dee7c2766972c59162fbee}" \
    sh -es <<'SH_INNER'
export HOME=/capture/home CFFIXED_USER_HOME=/capture/home WINEPREFIX=/capture/prefix
export WINEARCH=win32 WINEDEBUG=-all COD2_RETAIL_REPLAY=1
export WINEDLLOVERRIDES='winemenubuilder.exe,mscoree,mshtml=d'
export COD2X_TEST_SERVICE_URL=http://127.0.0.1:9
export COD2X_TEST_UPDATER_ADDRESS=127.0.0.1:9
export COD2X_TEST_UPDATER_URL=http://127.0.0.1:9/api/updater/check
unset WAYLAND_DISPLAY
export DISPLAY=:99
cd /capture/game
mkdir -p "$HOME" main/players/reference
printf '%s\n' reference > main/players/active.txt
printf '%s\n' 000000000000000086D3 > main/cod2key
for file in CoD2MP_s.exe gfx_d3d_mp_x86_s.dll localization.txt codlogo.bmp cod.bmp; do
    if [ -f "/retail/$file" ]; then
        ln -s "/retail/$file" "$file"
    elif [ -f "/data/$file" ]; then
        ln -s "/data/$file" "$file"
    fi
done
ln -s /data/mss32.dll miles_retail.dll
wine --version > ../wine-version.txt
sha256sum -c ../retail.sha256
Xvfb "$DISPLAY" -screen 0 640x480x24 -nolisten tcp > ../xvfb.log 2>&1 &
xvfb=$!
trap 'wineserver -k; kill "$xvfb"' EXIT
timeout 60 wineboot -i > ../wineboot.log 2>&1
set --
if [ -f ../fixture.gsc ]; then set -- +set g_logSync 1; fi
status=0
timeout --kill-after=10 110 wine CoD2MP_s.exe \
    +set dedicated 1 +set fs_basepath 'Z:\data' +set fs_homepath 'Z:\capture\game' \
    +set net_ip 127.0.0.1 +set net_port 29791 +set sv_punkbuster 0 \
    +set developer 0 +set logfile 2 +set g_gametype "$COD2_RETAIL_GAMETYPE" +set sv_pure 0 \
    +set sv_fps 20 +set scr_testclients "$COD2_RETAIL_CLIENTS" "$@" +map "$COD2_RETAIL_MAP" > ../wine.log 2>&1 || status=$?
printf '%s\n' "$status" > ../rc.txt
if [ -f main/console_mp.log ]; then cp main/console_mp.log ../console.log; fi
exit "$status"
SH_INNER

python3 -B "$root/tools/windows/retail-trace.py" "$run" --ticks "$COD2_RETAIL_TICKS"
python3 -B "$root/tools/parity/compare.py" "$run/statehash.txt" "$trace/statehash.txt" \
    --raw --players "$run/players.txt" "$trace/players.txt" --require-clients "$COD2_RETAIL_CLIENTS" |
    tee "$run/comparison.txt"
