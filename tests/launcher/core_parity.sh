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
if ! git -C "$root" diff --quiet "$revision" -- launcher; then
    echo "Swift sources or resources differ from $revision." >&2
    exit 1
fi
scratch=$(mktemp -d "${TMPDIR:-/tmp}/cod2-core-parity.XXXXXX")
trap 'rm -f "$scratch"/parity; rmdir "$scratch"' EXIT
export HOME="$scratch"
export CFFIXED_USER_HOME="$scratch"
export LANG=en_US.UTF-8
/usr/bin/xcrun swiftc -j 2 -swift-version 6 -strict-concurrency=complete -warnings-as-errors -O \
    "$root/launcher/ConfigDocument.swift" "$root/launcher/ConfigCatalog.swift" \
    "$root/tests/launcher/CoreParity.swift" "$root/tests/launcher/CoreParityConfig.swift" \
    -o "$scratch/parity"
"$scratch/parity" "$mode" "$output" "$revision" "$root"
