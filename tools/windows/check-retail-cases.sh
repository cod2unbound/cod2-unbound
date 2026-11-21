#!/usr/bin/env bash
# Replay the short four-client streams against both Windows Original 1.3 servers.
set -euo pipefail
if [[ $# -lt 2 || $# -gt 3 ]]; then
    echo "Usage: $0 <licensed-game-directory> <retail-1.3-directory> [case]" >&2
    exit 2
fi
root=$(cd "$(dirname "$0")/../.." && pwd)
data=$(cd "$1" && pwd)
retail=$(cd "$2" && pwd)
only=${3:-}
fixture="$root/tools/windows/fixtures/retail-extra"
matrix=${COD2_REPLAY_MATRIX:-${COD2_WINDOWS_OUTPUT:-$root/output/r05/builds}/matrix}
if [[ ! -f "$matrix/ded64-original/cod2_win32_ded.exe" ]]; then
    echo 'Build the Windows matrix before running the extra retail cases.' >&2
    exit 2
fi
output=${COD2_RETAIL_OUTPUT:-$root/output/r05/oracle/extra}
mkdir -p "$output"
output=$(cd "$output" && pwd)
run=$(mktemp -d "$output/cases-XXXXXXXX")
echo "Extra retail replay evidence in $run"
failed=0
matched=0
while read -r name map gametype seed ticks clients <&3; do
    if [[ -n $only && $only != "$name" ]]; then continue; fi
    matched=1
    case_dir="$run/$name"
    mkdir -p "$case_dir/prefix"
    echo "Checking $name ($map, $gametype, seed $seed, $clients clients)"
    if ! docker run --rm --init --pull never --platform linux/amd64 --network none \
        --label cod2.windows.check=retail-case -v "$root:/src:ro" -v "$data:/data:ro" \
        -v "$case_dir:/run" -v "$matrix/ded64-original:/matrix:ro" --tmpfs /run/prefix:mode=0700,size=2g \
        "${COD2_WINDOWS_IMAGE:-sha256:eb9d795192ea5bf14d58d939dd76b2c3e4fe344f2a91b713093ba5a4d728b7e6}" \
        bash -ec '
export HOME=/run/home CFFIXED_USER_HOME=/run/home WINEPREFIX=/run/prefix
export WINEARCH=win64 WINEDEBUG=-all WINEDLLOVERRIDES="mscoree,mshtml="
export COD2_WINE_HEADLESS_CHECK=1 COD2_EXE=/matrix/cod2_win32_ded.exe
export COD2X_TEST_SERVICE_URL=http://127.0.0.1:9 COD2X_TEST_UPDATER_ADDRESS=127.0.0.1:9
export COD2X_TEST_UPDATER_URL=http://127.0.0.1:9/api/updater/check
mkdir -p "$HOME"
wineboot -i > /run/wineboot.log 2>&1
trap "wineserver -k; wineserver -w" EXIT
localdata=$(find "$WINEPREFIX/drive_c/users" -path "*/AppData/Local" -type d -print -quit)
test -n "$localdata"
game_home="$localdata/CoD2 Unbound/replay"
stream=/src/tools/windows/fixtures/retail-extra/$1
script=()
if [[ -f $stream.gsc ]]; then script=(--fixture-script "$stream.gsc"); fi
python3 -B /src/tools/parity/record.py --binary /src/tools/windows/wine-binary.sh \
    --data /data --output /run/replay --home "$game_home" --map "$2" --gametype "$3" --seed "$4" --frames "$5" \
    --dedicated --players --clients "$6" --replay "$stream.txt" "${script[@]}"
if [[ -f "$game_home/main/games_mp.log" ]]; then
    cp "$game_home/main/games_mp.log" /run/replay/games_mp.log
fi
' bash "$name" "$map" "$gametype" "$seed" "$ticks" "$clients"; then
        failed=1
        continue
    fi
    script="$fixture/$name.gsc"
    if [[ ! -f $script ]]; then script=; fi
    if ! COD2_RETAIL_MAP=$map COD2_RETAIL_GAMETYPE=$gametype COD2_RETAIL_SEED=$seed COD2_RETAIL_CLIENTS=$clients \
        COD2_RETAIL_FIXTURE_SCRIPT=$script COD2_RETAIL_OUTPUT=$case_dir \
        "$root/tools/windows/check-retail.sh" "$data" "$retail" "$case_dir/replay" "$fixture/$name.txt"; then
        failed=1
    fi
done 3< "$fixture/cases.tsv"
if (( ! matched )); then
    echo "Unknown replay case: $only" >&2
    exit 2
fi
exit "$failed"
