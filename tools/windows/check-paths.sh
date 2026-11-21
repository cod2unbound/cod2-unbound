#!/usr/bin/env bash
# Check Windows discovery and per-user storage without a desktop or network.
set -euo pipefail
if [[ $# -gt 1 ]]; then
    echo "Usage: $0 [licensed-game-directory]" >&2
    exit 2
fi
root=$(cd "$(dirname "$0")/../.." && pwd)
data=$(cd "${1:-$HOME/Games/CoD2}" && pwd)
output="${COD2_WINDOWS_OUTPUT:-$root/output/r05/builds}"
for edition in original cod2x; do
    test -f "$output/matrix/ded64-$edition/cod2_win32_ded.exe" || {
        echo "Missing $edition server. Run tools/windows/build-matrix.sh first." >&2
        exit 1
    }
done
mkdir -p "$output/path-checks"
run=$(mktemp -d "$output/path-checks/run-XXXXXXXX")
exec docker run --rm --init --pull=never --platform linux/amd64 --network none \
    --label cod2.windows.check=paths \
    -v "$root:/src:ro" -v "$data:/data:ro" -v "$output/matrix:/matrix:ro" -v "$run:/out" \
    "${COD2_WINDOWS_IMAGE:-cod2-build-win}" python3 -B /src/tools/windows/check-paths.py
