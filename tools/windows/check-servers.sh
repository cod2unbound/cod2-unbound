#!/usr/bin/env bash
# Run only the built x64 dedicated servers, in an offline Wine container.
set -euo pipefail

if [[ $# -gt 1 ]]; then
    echo "Usage: $0 [licensed-game-directory]" >&2
    exit 2
fi
root=$(cd "$(dirname "$0")/../.." && pwd)
data=$(cd "${1:-$HOME/Games/CoD2}" && pwd)
output="$root/output/r05/builds"
for edition in original cod2x; do
    if [[ ! -f "$output/matrix/ded64-$edition/cod2_win32_ded.exe" ]]; then
        echo "Missing $edition dedicated server. Run tools/windows/build-matrix.sh first." >&2
        exit 1
    fi
done
mkdir -p "$output/wine"

exec docker run --rm --init --pull=never --platform linux/amd64 --network none \
    --label cod2.windows.check=wine \
    -v "$root:/src:ro" -v "$data:/data:ro" \
    -v "$output/matrix:/matrix:ro" -v "$output/wine:/out" \
    "${COD2_WINDOWS_IMAGE:-cod2-build-win}" python3 -B /src/tools/windows/check-servers.py
