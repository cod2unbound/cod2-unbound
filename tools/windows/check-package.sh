#!/usr/bin/env bash
# Test signed staging artifacts with licensed data under offline Wine.
set -euo pipefail
if [[ $# -lt 2 || $# -gt 3 ]]; then
    echo "Usage: $0 TEST_RELEASE_DIR PREVIOUS_TEST_INSTALLER [LICENSED_DATA_DIR]" >&2
    exit 2
fi

root=$(cd "$(dirname "$0")/../.." && pwd)
package=$(cd "$1" && pwd)
previous=$(cd "$(dirname "$2")" && pwd)
data=$(cd "${3:-$HOME/Games/CoD2}" && pwd)
: "${COD2_WINDOWS_PACKAGE_IMAGE:?Specify the package build image by ID}"
[[ $COD2_WINDOWS_PACKAGE_IMAGE =~ ^sha256:[0-9a-f]{64}$ ]]
mkdir -p "$root/output/r05/package"
run=$(mktemp -d "$root/output/r05/package/check-XXXXXXXX")
echo "Package checks in $run"
exec docker run --rm --init --pull=never --platform linux/amd64 --network none \
    --label cod2.windows.check=package-proof \
    -v "$root:/src:ro" -v "$package:/release:ro" -v "$previous:/previous:ro" \
    -v "$data:/data:ro" -v "$run:/out" \
    "$COD2_WINDOWS_PACKAGE_IMAGE" xvfb-run -a python3 -B /src/tools/windows/package/check_package.py \
    /release/artifacts /release/work "/previous/$(basename "$2")" /out
