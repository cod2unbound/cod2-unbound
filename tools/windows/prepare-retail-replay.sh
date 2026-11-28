#!/usr/bin/env bash
# Build the pinned retail recorder and stage the scripts for an offline replay.
set -euo pipefail
if [[ $# != 4 ]]; then
    echo "Usage: $0 <game-directory> <retail-directory> <capture-directory> <gametype>" >&2
    exit 2
fi
root=$(cd "$(dirname "$0")/../.." && pwd)
data=$(cd "$1" && pwd)
retail=$(cd "$2" && pwd)
run=$(cd "$3" && pwd)
export COD2_RETAIL_GAMETYPE=$4
mkdir -p "$run/game"

docker run --rm --init -i --pull=never --platform linux/amd64 --network none \
    --label cod2.windows.check=retail --label cod2.r05=oracle \
    -e COD2_RETAIL_GAMETYPE \
    -v "$root:/src:ro" -v "$run:/capture" -v "$data:/data:ro" -v "$retail:/retail:ro" \
    "${COD2_WINDOWS_IMAGE:-sha256:eb9d795192ea5bf14d58d939dd76b2c3e4fe344f2a91b713093ba5a4d728b7e6}" \
    bash -es <<'BASH'
set -o pipefail
cd /capture
expected=8f482e647b250babe098c6e3deb10129f9a935d013e852c99689cf8dff0d12cc
sha256sum /retail/CoD2MP_s.exe > retail.sha256
if [[ $(cut -d' ' -f1 retail.sha256) != "$expected" ]]; then
    echo "Retail executable SHA-256 differs from the 1.3 recorder anchor" >&2
    exit 2
fi
python3 -B /src/tools/windows/retail-exports.py /data/mss32.dll > miles-forwarders.def
i686-w64-mingw32-gcc -O2 -Wall -Wextra -Werror -shared \
    /src/tools/windows/retail-recorder.c miles-forwarders.def -o game/mss32.dll
python3 -B /src/tools/windows/retail-exports.py /data/mss32.dll --check game/mss32.dll
sha256sum game/mss32.dll /data/mss32.dll /retail/gfx_d3d_mp_x86_s.dll > dlls.sha256
sha256sum usercmds.txt > usercmds.sha256
PYTHONPATH=/src/tools/parity python3 -B - <<'PY'
import os
from pathlib import Path

from record import testclients_bootstrap

fixture = Path("fixture.gsc")
for member, script in testclients_bootstrap(sorted(Path("/data/main").glob("*.iwd")),
                                           os.environ["COD2_RETAIL_GAMETYPE"],
                                           fixture.read_bytes() if fixture.is_file() else None):
    path = Path("game/main") / member
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(script)
PY
BASH
