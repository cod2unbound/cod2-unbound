#!/usr/bin/env bash
# Build a fresh Windows release under an isolated output directory.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
output="${COD2_WINDOWS_RELEASE_OUTPUT:-$root/output/r05/package}"
base=sha256:eb9d795192ea5bf14d58d939dd76b2c3e4fe344f2a91b713093ba5a4d728b7e6
mode=${1:-layout}
launcher=${2:-}
[[ $# -le 2 ]] && [[ $mode == layout || $mode == test || $mode == azure ]] &&
    [[ $mode == layout || -n $launcher ]] || {
    echo "Usage: $0 layout [LAUNCHER_WIN_X64_ZIP] | {test|azure} LAUNCHER_WIN_X64_ZIP" >&2
    exit 2
}
: "${COD2_WINDOWS_COMPANY:?Specify the Windows publisher/company name}"
export COD2_WINDOWS_COMPANY COD2_WINDOWS_JOBS="${COD2_WINDOWS_JOBS:-3}"
export COD2_WINDOWS_COMMIT
COD2_WINDOWS_COMMIT=$(git -C "$root" rev-parse --short=12 HEAD)
export SOURCE_DATE_EPOCH
SOURCE_DATE_EPOCH=$(git -C "$root" log -1 --format=%ct)
image=$base
runner=()
handoff=()
if [[ -n $launcher ]]; then
    launcher="$(cd "$(dirname "$launcher")" && pwd)/$(basename "$launcher")"
    [[ -f $launcher ]] || { echo "Launcher publish ZIP not found: $launcher" >&2; exit 2; }
    handoff=(--mount "type=bind,source=$launcher,target=/launcher-win-x64.zip,readonly")
fi
if [[ $mode != layout ]]; then
    : "${COD2_WINDOWS_PACKAGE_IMAGE:?Specify the package build image by ID (see tools/windows/README.md)}"
    image=$COD2_WINDOWS_PACKAGE_IMAGE
    runner=(xvfb-run -a)
    if [[ ! $image =~ ^sha256:[0-9a-f]{64}$ ]]; then
        echo "COD2_WINDOWS_PACKAGE_IMAGE must be a full local image ID" >&2
        exit 2
    fi
fi

mkdir -p "$output"
run=$(mktemp -d "$output/release-XXXXXXXX")
echo "Windows $mode output in $run"
exec docker run --rm --init --pull=never --platform linux/amd64 \
    --label cod2.windows.check=package \
    -v "$root:/src:ro" -v "$run:/out" \
    ${handoff[@]+"${handoff[@]}"} \
    -e COD2_WINDOWS_COMPANY -e COD2_WINDOWS_JOBS -e COD2_WINDOWS_COMMIT -e SOURCE_DATE_EPOCH \
    -e COD2_SIGN_ENDPOINT -e COD2_SIGN_ACCOUNT -e COD2_SIGN_PROFILE -e COD2_SIGN_TOKEN \
    "$image" ${runner[@]+"${runner[@]}"} python3 -B /src/tools/windows/package/release.py "$mode"
