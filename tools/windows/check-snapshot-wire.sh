#!/usr/bin/env bash
# Receive the Toujane replay over protocol 118 on isolated loopback.
set -euo pipefail
if [[ $# != 4 ]]; then
    echo "Usage: $0 <licensed-game-directory> <retail-1.3-directory> <passed-retail-capture> <new-output-directory>" >&2
    exit 2
fi
root=$(cd "$(dirname "$0")/../.." && pwd)
data=$(cd "$1" && pwd)
retail=$(cd "$2" && pwd)
capture=$(cd "$3" && pwd)
matrix=${COD2_WINDOWS_OUTPUT:-$root/output/r05/builds}/matrix
for input in "$capture/game/mss32.dll" "$capture/retail.sha256" "$capture/game/main/maps/mp/gametypes/dm.gsc" \
    "$matrix/ded64-original/cod2_win32_ded.exe"; do
    test -f "$input" || { echo "Missing $input" >&2; exit 2; }
done
cmp "$capture/usercmds.txt" "$root/tools/windows/fixtures/two-bot/usercmds.txt"
mkdir "$4"
output=$(cd "$4" && pwd)
"$root/tools/windows/build-snapshot-receiver.sh" "$output"
for side in retail unbound; do
    mkdir "$output/$side"
    platform=linux/amd64
    image=${COD2_WINDOWS_IMAGE:-sha256:eb9d795192ea5bf14d58d939dd76b2c3e4fe344f2a91b713093ba5a4d728b7e6}
    if [[ $side == retail ]]; then
        platform=linux/386
        image=${COD2_WINDOWS_RETAIL_IMAGE:-sha256:759dc298078a9637ce371a1cf9b8551a20aea985c1dee7c2766972c59162fbee}
    fi
    docker run --rm --init -i --pull never --platform "$platform" --network none \
        --label cod2.windows.check=snapshot-wire --tmpfs /run/prefix:mode=0700,size=2g \
        -e QEMU_RESERVED_VA=0x80000000 -v "$root:/src:ro" -v "$data:/data:ro" -v "$retail:/retail:ro" \
        -v "$matrix/ded64-original:/matrix:ro" -v "$capture:/capture:ro" \
        -v "$output/snapshot-receiver:/receiver:ro" -v "$output/$side:/run" \
        "$image" bash -s -- "$side" <<'BASH'
set -euo pipefail
side=$1
export HOME=/run/home CFFIXED_USER_HOME=/run/home WINEPREFIX=/run/prefix WINEDEBUG=-all
export WINEDLLOVERRIDES='winemenubuilder.exe,mscoree,mshtml=d'
export COD2X_TEST_SERVICE_URL=http://127.0.0.1:9 COD2X_TEST_UPDATER_ADDRESS=127.0.0.1:9
export COD2X_TEST_UPDATER_URL=http://127.0.0.1:9/api/updater/check
mkdir "$HOME" /run/game
unset DISPLAY WAYLAND_DISPLAY
xvfb= server=
trap 'if [[ -n $server ]]; then kill "$server" 2>/dev/null || true; wait "$server" || true; fi
      wineserver -k || true; wineserver -w || true
      if [[ -n $xvfb ]]; then kill "$xvfb" 2>/dev/null || true; fi' EXIT
cd /run/game
args=(+set dedicated 1 +set net_ip 127.0.0.1 +set net_port 29791 +set developer 0 +set logfile 2
      +set g_gametype dm +set sv_pure 0 +set sv_fps 20 +set scr_testclients 2 +map mp_toujane)
cp /capture/usercmds.txt /run/usercmds.txt
if [[ $side == retail ]]; then
    sha256sum -c /capture/retail.sha256
    export WINEARCH=win32 DISPLAY=:99
    Xvfb "$DISPLAY" -screen 0 640x480x24 -nolisten tcp > /run/xvfb.log 2>&1 &
    xvfb=$!
    engine=CoD2MP_s.exe
    game_home=/run/game
    for f in CoD2MP_s.exe gfx_d3d_mp_x86_s.dll; do ln -s "/retail/$f" "$f"; done
    for f in localization.txt codlogo.bmp cod.bmp; do ln -s "/data/$f" "$f"; done
    ln -s /capture/game/mss32.dll mss32.dll
    ln -s /data/mss32.dll miles_retail.dll
    export COD2_RETAIL_REPLAY=1 COD2_RETAIL_TICKS=600 COD2_RETAIL_COMMANDS=1004
    export COD2_RETAIL_CLIENTS=3 COD2_RETAIL_SEED=12345
else
    export WINEARCH=win64
    export SYSDIFF_STATEHASH=/run/statehash.txt SYSDIFF_MAXFRAMES=600 SYSDIFF_SEED=12345
    export SYSDIFF_REPLAY=/run/usercmds.txt
fi
timeout 90 wineboot -i > /run/wineboot.log 2>&1
if [[ $side == unbound ]]; then
    engine=/matrix/cod2_win32_ded.exe
    localdata=$(find "$WINEPREFIX/drive_c/users" -path "*/AppData/Local" -type d -print -quit)
    test -n "$localdata"
    game_home="$localdata/CoD2 Unbound/wire"
fi
mkdir -p "$game_home/main/players/reference"
printf '%s\n' reference > "$game_home/main/players/active.txt"
printf '%s\n' 000000000000000086D3 > "$game_home/main/cod2key"
cp -R /capture/game/main/maps "$game_home/main/maps"
wine_home=$(winepath -w "$game_home")
timeout --kill-after=8 110 wine "$engine" +set fs_basepath 'Z:\data' +set fs_homepath "$wine_home" \
    +set sv_punkbuster 0 "${args[@]}" > /run/server.log 2>&1 &
server=$!
python3 -B /src/tools/windows/wait-wire-replay.py "$side" > /run/sync.log
/receiver 29791 43 /run/datagrams.bin > /run/decoded.log 2> /run/receiver.log
BASH
done
python3 -B "$root/tools/windows/compare-wire.py" "$output/retail" "$output/unbound" | tee "$output/comparison.log"
