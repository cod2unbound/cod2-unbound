#!/bin/sh
# Usage data with real Swift models and a loopback-only batch receiver. No windows.
set -eu
root=$(CDPATH='' cd -- "$(dirname "$0")/../.." && pwd)
out="${COD2_USAGE_TEST_OUTPUT:-$root/output/r042/usage/tests.noindex}"
mkdir -p "$out/tmp"
out=$(CDPATH='' cd -- "$out" && pwd)
export TMPDIR="$out/tmp"
export COD2X_TEST_SERVICE_URL=http://127.0.0.1:9 COD2X_TEST_UPDATER_ADDRESS=127.0.0.1:9
export COD2X_TEST_UPDATER_URL=http://127.0.0.1:9/api/updater/check
sdk=$(/usr/bin/xcrun --sdk macosx --show-sdk-path)
for object in "launcher/NativeSetup.m:NativeSetup" "src/platform/cod2x_native_shaders.m:NativeShaders" "src/PC/qcommon/cod2x_url.c:URL"; do
    /usr/bin/xcrun clang -target arm64-apple-macos26 -isysroot "$sdk" -DCOD2_X64=1 -DCOD2_CODX=1 -fobjc-arc \
        -c "$root/${object%%:*}" -o "$out/${object##*:}.o"
done
set --
for file in "$root"/launcher/*.swift; do
    if [ "${file##*/}" != Main.swift ]; then set -- "$@" "$file"; fi
done
/usr/bin/xcrun swiftc -j 1 -swift-version 6 -strict-concurrency=complete -warnings-as-errors \
    -target arm64-apple-macos26 -sdk "$sdk" -Onone -whole-module-optimization -import-objc-header "$root/launcher/NativeSetup.h" \
    "$@" "$root/tests/launcher/DirectoryFixture.swift" "$root/tests/launcher/UsageDataTests.swift" \
    "$out/NativeSetup.o" "$out/NativeShaders.o" "$out/URL.o" \
    -framework AppKit -framework SwiftUI -framework Network -lz -o "$out/UsageDataTests"
cp "$root/launcher/Resources/ConfigCatalog.json" "$root/launcher/Resources/ConfigRulesets.json" "$root/launcher/Resources/ConfigOptimizer.json" "$out/"
run=$(mktemp -d "$out/run.XXXXXX")
mkdir -p "$run/home"
python3 "$root/tools/usage/usage_standin.py" --port 0 --record "$run/requests.jsonl" >"$run/address" 2>"$run/standin.log" &
standin=$!
trap 'kill "$standin" 2>/dev/null || :; wait "$standin" 2>/dev/null || :' EXIT
trap 'exit 1' HUP INT TERM
address=""
for _ in $(seq 1 100); do
    address=$(sed -n 's/^usage stand-in listening on //p' "$run/address")
    [ -z "$address" ] || break
    sleep 0.1
done
[ -n "$address" ] || { cat "$run/standin.log" >&2; exit 1; }
HOME="$run/home" CFFIXED_USER_HOME="$run/home" COD2_USAGE_URL="$address" \
    "$out/UsageDataTests" "$run"
