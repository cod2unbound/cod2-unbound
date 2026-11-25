#!/usr/bin/env bash
# Run only the built x64 dedicated servers, in an offline Wine container.
set -euo pipefail

if [[ $# -gt 1 ]]; then
    echo "Usage: $0 [licensed-game-directory]" >&2
    exit 2
fi
root=$(cd "$(dirname "$0")/../.." && pwd)
data=$(cd "${1:-$HOME/Games/CoD2}" && pwd)
output="${COD2_WINDOWS_OUTPUT:-$root/output/r05/builds}"
for edition in original cod2x; do
    if [[ ! -f "$output/matrix/ded64-$edition/cod2_win32_ded.exe" ]]; then
        echo "Missing $edition dedicated server. Run tools/windows/build-matrix.sh first." >&2
        exit 1
    fi
done
mkdir -p "$output/wine"
stamp=$(date -u +%Y%m%dT%H%M%SZ)
run=$(mktemp -d "$output/wine/headless-$stamp-XXXXXXXX")
receipt="$run/receipt.txt"
image=$(docker image inspect --format '{{.Id}}' "${COD2_WINDOWS_IMAGE:-cod2-build-win}")
revision=$(git -C "$root" rev-parse HEAD)
echo "Evidence in $run"
printf 'Started (UTC): %s\nFixture checkout: %s\nDocker image: %s\n' \
    "$stamp" "$revision" "$image" | tee "$receipt"

status=0
docker run --rm --init --pull=never --platform linux/amd64 --network none \
    --label cod2.windows.check=wine \
    -v "$root:/src:ro" -v "$data:/data:ro" \
    -v "$output/matrix:/matrix:ro" -v "$output/wine:/out" \
    "$image" bash -ec '
        sha256sum /matrix/ded64-{original,cod2x}/cod2_win32_ded.exe
        exec python3 -B /src/tools/windows/check-servers.py "$1"
    ' bash "/out/${run##*/}" 2>&1 | tee -a "$receipt" || status=$?
printf 'Finished (UTC): %s\nWine checks exit status: %s\n' \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$status" | tee -a "$receipt"
if (( status != 0 )); then
    exit "$status"
fi

if [[ -n ${COD2_WINDOWS_RETAIL_DIR:-} ]]; then
    exec "$root/tools/windows/check-retail.sh" "$data" "$COD2_WINDOWS_RETAIL_DIR" "$run/original/replay"
fi
