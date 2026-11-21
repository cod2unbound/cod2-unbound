#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
mode=${1:---check}
output="$root/tests/launcher/fixtures/core-parity"
case "$mode" in
    --check) revision=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["swiftRevision"])' "$output/config.json");;
    --write) revision=${2:-$(git -C "$root" rev-parse HEAD)};;
    *) echo 'Usage: core_parity.sh [--check | --write [swift-revision]]' >&2; exit 2;;
esac
if ! git -C "$root" diff --quiet "$revision" -- launcher src/PC/qcommon/cod2x_url.c src/PC/qcommon/cod2x_url.h src/platform/cod2x_native_setup.h src/platform/cod2x_native_shaders.m; then
    echo "Swift sources or resources differ from $revision." >&2
    exit 1
fi
scratch=$(mktemp -d "${TMPDIR:-/tmp}/cod2-core-parity.XXXXXX")
trap 'rm -f "$scratch"/parity "$scratch"/setup.o "$scratch"/shaders.o "$scratch"/url.o; rmdir "$scratch"' EXIT
export HOME="$scratch"
export CFFIXED_USER_HOME="$scratch"
export LANG=en_US.UTF-8
sdk=$(/usr/bin/xcrun --sdk macosx --show-sdk-path)
target=arm64-apple-macos26
/usr/bin/xcrun clang -target "$target" -isysroot "$sdk" -fobjc-arc -DCOD2_X64=1 -DCOD2_CODX=1 \
    -c "$root/launcher/NativeSetup.m" -o "$scratch/setup.o"
/usr/bin/xcrun clang -target "$target" -isysroot "$sdk" -fobjc-arc -DCOD2_X64=1 -DCOD2_CODX=1 \
    -c "$root/src/platform/cod2x_native_shaders.m" -o "$scratch/shaders.o"
/usr/bin/xcrun clang -target "$target" -isysroot "$sdk" -DCOD2_X64=1 -DCOD2_CODX=1 \
    -c "$root/src/PC/qcommon/cod2x_url.c" -o "$scratch/url.o"
/usr/bin/xcrun swiftc -j 2 -swift-version 6 -strict-concurrency=complete -warnings-as-errors -O \
    -target "$target" -sdk "$sdk" -import-objc-header "$root/launcher/NativeSetup.h" \
    "$root/launcher/ConfigDocument.swift" "$root/launcher/ConfigCatalog.swift" \
    "$root/launcher/Core.swift" "$root/launcher/Presentation.swift" \
    "$root/tests/launcher/CoreParity.swift" "$root/tests/launcher/CoreParityConfig.swift" "$root/tests/launcher/CoreParityWire.swift" \
    "$scratch/setup.o" "$scratch/shaders.o" "$scratch/url.o" -framework AppKit -lz -o "$scratch/parity"
"$scratch/parity" "$mode" "$output" "$revision" "$root"
