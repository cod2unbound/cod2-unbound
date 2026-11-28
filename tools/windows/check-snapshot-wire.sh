#!/usr/bin/env bash
# Receive a Windows 1.3 replay over protocol 118 on isolated loopback.
set -euo pipefail
if [[ $# != 4 ]]; then
    echo "Usage: $0 <licensed-game-directory> <retail-1.3-directory> <passed-Toujane-capture-or-case> <new-output-directory>" >&2
    exit 2
fi
root=$(cd "$(dirname "$0")/../.." && pwd)
data=$(cd "$1" && pwd)
retail=$(cd "$2" && pwd)
matrix=${COD2_WINDOWS_OUTPUT:-$root/output/r05/builds}/matrix
if [[ ! -f "$matrix/ded64-original/cod2_win32_ded.exe" ]]; then
    echo 'Build the Windows Original dedicated server before running the wire check.' >&2
    exit 2
fi
fixture="$root/tools/windows/fixtures"
fixture_script=
if [[ -d $3 ]]; then
    capture=$(cd "$3" && pwd)
    input="$fixture/two-bot/usercmds.txt"
    cmp "$capture/usercmds.txt" "$input"
    map=mp_toujane gametype=dm seed=12345 ticks=600 clients=2 connect_tick=140
else
    row=$(awk -v name="$3" '$1 == name {print}' "$fixture/retail-extra/cases.tsv")
    if [[ -z $row ]]; then
        echo "Unknown replay case or missing Toujane capture: $3" >&2
        exit 2
    fi
    read -r name map gametype seed ticks clients <<< "$row"
    input="$fixture/retail-extra/$name.txt"
    fixture_script="$fixture/retail-extra/$name.gsc"
    connect_tick=120
fi
commands=$(awk 'END { print NR - 2 }' "$input")
# Replay starts at frame 4 with 350 ms of server time. Both engine recorders
# stop after the last simulation tick, before that tick's snapshot is sent.
# Allow ten ticks for the handshake and fragmented gamestate. Start on the
# next tick, whose delta can use a post-handshake snapshot as its base.
first_time=$(((connect_tick + 11) * 50 + 150))
last_time=$(((ticks + 5) * 50))
mkdir "$4"
output=$(cd "$4" && pwd)
capture="$output/staged-retail"
mkdir "$capture"
trap 'rm -r "$capture"; if [[ -f "$output/snapshot-receiver" ]]; then rm "$output/snapshot-receiver"; fi' EXIT
cp "$input" "$capture/usercmds.txt"
if [[ -f $fixture_script ]]; then cp "$fixture_script" "$capture/fixture.gsc"; fi
"$root/tools/windows/prepare-retail-replay.sh" "$data" "$retail" "$capture" "$gametype"
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
        -v "$output/snapshot-receiver:/receiver:ro" -v "$output/$side:/run" -v "$input:/run/usercmds.txt:ro" \
        "$image" bash -s -- "$side" "$map" "$gametype" "$seed" "$ticks" "$clients" "$commands" "$connect_tick" "$last_time" <<'BASH'
set -euo pipefail
side=$1 map=$2 gametype=$3 seed=$4 ticks=$5 clients=$6 commands=$7 connect_tick=$8 last_time=$9
export HOME=/run/home CFFIXED_USER_HOME=/run/home WINEPREFIX=/run/prefix WINEDEBUG=-all
export WINEDLLOVERRIDES='winemenubuilder.exe,mscoree,mshtml=d'
export COD2X_TEST_SERVICE_URL=http://127.0.0.1:9 COD2X_TEST_UPDATER_ADDRESS=127.0.0.1:9
export COD2X_TEST_UPDATER_URL=http://127.0.0.1:9/api/updater/check
mkdir "$HOME" /run/game
unset DISPLAY WAYLAND_DISPLAY
xvfb= server= receiver=
trap 'if [[ -n $server ]]; then kill "$server" 2>/dev/null || true; wait "$server" || true; fi
      if [[ -n $receiver ]]; then kill "$receiver" 2>/dev/null || true; wait "$receiver" || true; fi
      wineserver -k || true; wineserver -w || true
      if [[ -n $xvfb ]]; then kill "$xvfb" 2>/dev/null || true; fi
      rm -r "$HOME" /run/game' EXIT
cd /run/game
args=(+set dedicated 1 +set net_ip 127.0.0.1 +set net_port 29791 +set developer 0 +set logfile 2
      +set g_gametype "$gametype" +set sv_pure 0 +set sv_fps 20 +set scr_testclients "$clients")
if [[ -f /capture/fixture.gsc ]]; then args+=(+set g_logSync 1); fi
# A fixed 5 ms increment, smaller than the 50 ms simulation step, prevents
# wall-clock catch-up and gives loopback time to acknowledge each snapshot.
args+=(+devmap "$map" +set fixedtime 5)
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
    export COD2_RETAIL_REPLAY=1 COD2_RETAIL_TICKS="$ticks" COD2_RETAIL_COMMANDS="$commands"
    export COD2_RETAIL_CLIENTS="$((clients + 1))" COD2_RETAIL_SEED="$seed"
else
    export WINEARCH=win64
    # With a sub-tick clock, Unbound's outer trace also records the map's
    # initialization frame (3) before the replay frames start at 4.
    export SYSDIFF_STATEHASH=/run/statehash.txt SYSDIFF_MAXFRAMES="$((ticks + 1))" SYSDIFF_SEED="$seed"
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
python3 -B /src/tools/windows/wait-wire-replay.py "$side" "$connect_tick" > /run/sync.log
/receiver 29791 120 /run/datagrams.bin "$last_time" > /run/decoded.log 2> /run/receiver.log &
receiver=$!
wait "$server"
server=
wait "$receiver"
receiver=
grep -Fx "client $clients" /run/decoded.log
if [[ -f "$game_home/main/console_mp.log" ]]; then
    cp "$game_home/main/console_mp.log" /run/console.log
fi
BASH
done
python3 -B "$root/tools/windows/compare-wire.py" "$output/retail" "$output/unbound" --start "$first_time" --end "$last_time" --step 50 | tee "$output/comparison.log"
