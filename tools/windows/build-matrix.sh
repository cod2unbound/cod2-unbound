#!/usr/bin/env bash
# Cross-compile the release matrix and check the resulting PE files.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
output="${COD2_WINDOWS_OUTPUT:-$root/output/r05/builds}/matrix"
mkdir -p "$output"

exec docker run --rm --init -i --pull=never --platform linux/amd64 \
    --label cod2.windows.check=matrix \
    -v "$root:/src:ro" -v "$output:/out" \
    "${COD2_WINDOWS_IMAGE:-cod2-build-win}" bash -s -- "${COD2_WINDOWS_JOBS:-3}" <<'BASH'
set -euo pipefail
export LC_ALL=C
jobs=$1
if [[ ! $jobs =~ ^[1-9][0-9]*$ ]]; then
    echo "COD2_WINDOWS_JOBS must be a positive integer" >&2
    exit 2
fi

build() {
    local name=$1 arch=$2 edition=$3 renderer=$4
    local build_dir="/out/$name" triple=x86_64-w64-mingw32 format=pei-x86-64
    local client=OFF d3d9=OFF features= exe=cod2_win32_ded.exe imports="$name" added
    local deps=()
    if [[ $arch == 32 ]]; then
        triple=i686-w64-mingw32
        format=pei-i386
    fi
    if [[ $edition == cod2x ]]; then
        features=-DCOD2_CODX=1
    fi
    if [[ $renderer != dedicated || $edition == cod2x ]]; then
        deps+=("-DCOD2_CURL_DIR=/out/deps/curl$arch")
    fi
    if [[ $renderer != dedicated ]]; then
        client=ON
        exe=cod2_win32.exe
        if [[ $arch == 64 ]]; then
            imports=client
        fi
        deps+=(-DCOD2_SDL2_DIR=/out/deps/sdl2)
    fi
    if [[ $renderer == d3d9 ]]; then
        d3d9=ON
    fi

    mkdir -p "$build_dir"
    echo "Building $name"
    if ! cmake -S /src -B "$build_dir" -G Ninja \
        -DCMAKE_TOOLCHAIN_FILE="/src/cmake/toolchain-mingw$arch.cmake" \
        -DCOD2_WIN32_LEGACY=OFF -DCOD2_WIN32_CLIENT="$client" -DCOD2_WIN32_D3D9="$d3d9" \
        -DCOD2_FEATURE_CFLAGS="$features" "${deps[@]}" > "$build_dir/configure.log" 2>&1; then
        tail -40 "$build_dir/configure.log" >&2
        return 1
    fi
    if ! cmake --build "$build_dir" --parallel "$jobs" > "$build_dir/build.log" 2>&1; then
        tail -40 "$build_dir/build.log" >&2
        return 1
    fi

    "$triple-objdump" -f "$build_dir/$exe" > "$build_dir/pe-header.txt"
    grep -q "file format $format$" "$build_dir/pe-header.txt"
    "$triple-objdump" -p "$build_dir/$exe" |
        awk '/DLL Name:/ {print tolower($3)}' | sort -u > "$build_dir/imports.txt"
    if [[ ! -s "$build_dir/imports.txt" ]]; then
        echo "$name has no readable PE imports" >&2
        return 1
    fi
    added=$(comm -23 "$build_dir/imports.txt" "/src/tools/windows/pe-imports/$imports.txt")
    if [[ -n $added ]]; then
        printf '%s has new DLL imports:\n%s\n' "$name" "$added" >&2
        return 1
    fi

    # Both renderers can import OPENGL32.dll. Check the actual system D3D9 seam.
    local has_d3d9
    has_d3d9=$("$triple-nm" --defined-only "$build_dir/$exe" |
        awk '$NF ~ /^_?Win_D3D9_Direct3DCreate9$/ {found = 1} END { print found ? "ON" : "OFF" }')
    if [[ $has_d3d9 != "$d3d9" ]]; then
        echo "$name: expected system D3D9 seam $d3d9, found $has_d3d9" >&2
        return 1
    fi
    echo "PASS $name ($format, PE imports checked)"
}

for edition in original cod2x; do
    build "ded64-$edition" 64 "$edition" dedicated
    build "d3d64-$edition" 64 "$edition" d3d9
    build "gl64-$edition" 64 "$edition" gl
done
build d3d32-probe 32 original d3d9
BASH
