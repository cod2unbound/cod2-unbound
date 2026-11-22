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
if ! git -C "$root" diff --quiet "$revision" -- launcher \
    src/PC/qcommon/cod2x_url.c src/PC/qcommon/cod2x_url.h \
    src/platform/cod2x_native_setup.h src/platform/cod2x_native_shaders.m \
    ':(glob)tests/launcher/fixtures/*.bin' tests/launcher/fixtures/releases.json tests/launcher/fixtures/match-freezes.json \
    tests/launcher/fixtures/match-freeze-legacy.json; then
    echo "Swift sources, resources or input fixtures differ from $revision." >&2
    exit 1
fi
scratch=$(mktemp -d "${TMPDIR:-/tmp}/cod2-core-parity.XXXXXX")
cleanup() {
    rm -f "$scratch"/parity "$scratch"/*ParityReference.swift "$scratch"/setup.o "$scratch"/shaders.o "$scratch"/url.o
    if [ -d "$scratch/parity-artwork" ]; then
        rm -f "$scratch/parity-artwork"/*.iwi "$scratch/parity-artwork"/*.iwd "$scratch/parity-artwork/main/iw_09.iwd"
        rmdir "$scratch/parity-artwork/main" "$scratch/parity-artwork"
    fi
    rmdir "$scratch"
}
trap cleanup EXIT
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
# The progress rules have no UI dependencies, but share a file with the Mac process adapter.
awk '
    BEGIN { print "import Foundation" }
    /^enum SteamLanguage: / || /^struct SteamProgress: / { copy = 1 }
    /^struct DepotToolError: / || /^\/\/\/ One run of the download tool/ { copy = 0 }
    copy { print }
' "$root/launcher/SteamDownload.swift" > "$scratch/SteamParityReference.swift"
# Compile the binding editor rules without its AppKit capture and observation layer.
{
    printf '%s\n' 'import Foundation'
    awk '/^struct EngineKey:/ {copy=1} /^enum KeyboardSize:/ {copy=0} copy' "$root/launcher/InputDevices.swift"
    awk '/^enum ConfigBindingGroup:/ {copy=1} /^enum BindingCaptureCancellation:/ {copy=0} copy' "$root/launcher/ConfigBindings.swift"
    awk '/^enum EngineInputMapping/ {copy=1} /^enum KeyboardShape:/ {copy=0} copy' "$root/launcher/InputDevices.swift"
} > "$scratch/BindingParityReference.swift"
{
    printf '%s\n' 'import Foundation' 'import zlib'
    awk '/^enum ArtworkVariation / {copy=1} /^@MainActor final class MapArtworkStore/ {copy=0} copy' "$root/launcher/MapArtwork.swift"
    awk '/^struct ConfigSettingSpec:/ {copy=1} /^\/\/ MARK: - Store/ {copy=0} copy' "$root/launcher/ConfigStudioModel.swift"
    printf '%s\n' 'enum ParityProfileName {'
    awk '/^    static func identifier\(for name:/ {copy=1} copy { print } copy && /^    }/ { exit }' "$root/launcher/ConfigStudioModel.swift"
    printf '%s\n' '}'
    awk '/^enum ServerFilter:/ {print;exit}' "$root/launcher/Model.swift"
    awk '/^enum ServerScope:/ {copy=1} /^\/\/\/ The game version a server runs/ {copy=0} copy' "$root/launcher/ModelServers.swift"
    printf '%s\n' 'enum ParityMatchFacts {'
    awk '/^    static func duration\(_ seconds:/ {copy=1} copy { print } copy && /^    }/ { exit }' "$root/launcher/ModelLibrary.swift"
    printf '%s\n' '}'
    awk '/^enum ServerAddressInput / {copy=1} /^enum ServerPasswords / {copy=0} copy' "$root/launcher/ModelServers.swift"
    awk '/^enum PointerButtonPosition:/ {copy=1} /^\/\/\/ Registry metadata/ {copy=0} copy' "$root/launcher/InputDevices.swift"
} > "$scratch/ExtrasParityReference.swift"
# Hold the ZIP local timestamps fixed. Swift checks the original bytes and its decoded results.
python3 - "$root/tests/launcher/artwork_fixtures.py" "$scratch/parity-artwork" <<'PYTHON'
import runpy, sys, time
script = sys.argv.pop(1)
time.localtime = lambda *_: (2026, 1, 1, 0, 0, 0, 3, 1, 0)
runpy.run_path(script, run_name="__main__")
PYTHON
/usr/bin/xcrun swiftc -j 2 -D CORE_PARITY_TESTS -swift-version 6 -strict-concurrency=complete -warnings-as-errors -O \
    -target "$target" -sdk "$sdk" -import-objc-header "$root/tests/launcher/CoreParityBridge.h" \
    "$root/launcher/ConfigDocument.swift" "$root/launcher/ConfigCatalog.swift" \
    "$root/launcher/ConfigDirectory.swift" "$root/launcher/ConfigImport.swift" "$root/launcher/ConfigImportSafety.swift" \
    "$root/launcher/ConfigImportCatalogAdapter.swift" "$root/launcher/ConfigClean.swift" "$root/launcher/WindowsProfiles.swift" \
    "$root/launcher/ConfigImportSession.swift" "$root/launcher/ConfigProfiles.swift" "$root/launcher/ConfigHistory.swift" \
    "$root/launcher/ConfigStudioSession.swift" "$root/launcher/ConfigShare.swift" \
    "$root/launcher/Core.swift" "$root/launcher/MatchSummaries.swift" "$root/launcher/Presentation.swift" "$root/launcher/Hosting.swift" \
    "$root/tests/launcher/CoreParity.swift" "$root/tests/launcher/CoreParityConfig.swift" "$root/tests/launcher/CoreParityWire.swift" \
    "$root/tests/launcher/CoreParityLaunch.swift" "$root/tests/launcher/CoreParitySettings.swift" \
    "$root/tests/launcher/CoreParityDirectory.swift" "$root/tests/launcher/DirectoryFixture.swift" \
    "$root/tests/launcher/CoreParityImport.swift" "$root/tests/launcher/ConfigImportTests.swift" "$root/tests/launcher/ConfigImportSafetyTests.swift" \
    "$root/tests/launcher/CoreParitySession.swift" "$root/tests/launcher/ConfigImportSessionTests.swift" \
    "$root/tests/launcher/CoreParityProfiles.swift" \
    "$root/tests/launcher/CoreParityShare.swift" "$root/tests/launcher/ConfigShareTests.swift" \
    "$root/tests/launcher/CoreParityHosting.swift" "$root/tests/launcher/CoreParitySetup.swift" "$scratch/SteamParityReference.swift" "$scratch/BindingParityReference.swift" \
    "$root/launcher/ConfigRules.swift" "$root/tests/launcher/CoreParityStudio.swift" \
    "$root/launcher/ConfigChecker.swift" "$root/tests/launcher/CoreParityChecker.swift" \
    "$root/launcher/ConfigReadyCheck.swift" "$root/launcher/ConfigCheckup.swift" "$root/launcher/ConfigChains.swift" \
    "$root/tests/launcher/CoreParityReady.swift" "$root/tests/launcher/CoreParityLibrary.swift" \
    "$root/tests/launcher/CoreParityChains.swift" "$root/tests/launcher/CoreParityCheckup.swift" \
    "$root/tests/launcher/CoreParityExtras.swift" "$scratch/ExtrasParityReference.swift" \
    "$root/tests/launcher/CoreParityLifecycle.swift" \
    "$scratch/setup.o" "$scratch/shaders.o" "$scratch/url.o" -framework AppKit -lz -o "$scratch/parity"
"$scratch/parity" "$mode" "$output" "$revision" "$root" "$scratch"
