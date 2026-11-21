#!/usr/bin/env bash
# Build clean git snapshots with identical metadata.
set -euo pipefail
if [[ $(uname -s) != Linux || $(uname -m) != x86_64 ]]; then
    echo 'Requires x86_64 Linux with the README multilib dependencies.' >&2
    exit 2
fi
compiler=${COD2_CC:-gcc-14}
command -v "$compiler" >/dev/null || { echo "Missing GCC >=14 compiler: $compiler" >&2; exit 2; }
repo=$(git rev-parse --show-toplevel)
upstream=410342a82da1b2e8286c290274aeb0f1731a6859
base=$(git rev-parse "${1:-$upstream}^{commit}")
head=$(git rev-parse "${2:-HEAD}^{commit}")
out=${3:-"$repo/output/x86-reference"}
mkdir -p "$out"
out=$(cd "$out" && pwd)
# Refuse reuse: stale objects would weaken the comparison.
mkdir "$out/base" "$out/head"
export LC_ALL=C TZ=UTC
export SOURCE_DATE_EPOCH
SOURCE_DATE_EPOCH=$(git show -s --format=%ct "$base")
# Archives have no .git. Stop discovery before the enclosing worktree so CMake
# uses the existing COD2_GIT_HASH="unknown" fallback for each snapshot.
export GIT_CEILING_DIRECTORIES="$out"
unset GIT_DIR GIT_WORK_TREE
umask 022
printf 'base=%s\nhead=%s\nepoch=%s\n' "$base" "$head" "$SOURCE_DATE_EPOCH" > "$out/inputs.txt"
"$compiler" --version >> "$out/inputs.txt"
cmake --version >> "$out/inputs.txt"

catalog=docs/macos-port/intended-differences.md
intended=()
reviewed=
if [[ $base == "$upstream" ]] && git cat-file -e "$head:$catalog" 2>/dev/null; then
    git show "$head:$catalog" > "$out/intended-differences.md"
    python3 -B "$repo/tools/ci/upstream_differences.py" "$out/intended-differences.md" > "$out/intended-inputs.txt"
    mapfile -t manifest < "$out/intended-inputs.txt"
    reviewed=$(git rev-parse "${manifest[0]}^{commit}")
    objects=("${manifest[@]:1}")
    mkdir "$out/reviewed"
    intended=(--intended "$out/intended-differences.md" "$out/reviewed/build-verify")
fi

configure() {
    local name=$1 rev=$2 root map
    root="$out/$name"
    git -C "$repo" archive "$rev" | tar -x -C "$root"
    find "$root" -type f -exec touch -d "@$SOURCE_DATE_EPOCH" {} +
    map="-ffile-prefix-map=$root=/cod2-reference -fdebug-prefix-map=$root=/cod2-reference"
    # GCC on Linux cannot parse upstream's MSVC calling-convention keywords.
    # The defines leave __attribute__((stdcall)) spellings untouched.
    cmake -S "$root" -B "$root/build-verify" -G 'Unix Makefiles' \
        -DCOD2_X64=OFF -DCMAKE_C_COMPILER="$compiler" -DCMAKE_ASM_COMPILER="$compiler" \
        -DCMAKE_C_FLAGS="-m32 -D__cdecl= -D__stdcall= $map" -DCMAKE_ASM_FLAGS="-m32 $map" \
        2>&1 | tee "$out/$name-configure.log"
}

build() {
    local name=$1
    shift
    cmake --build "$out/$name/build-verify" --parallel "${JOBS:-2}" \
        --target cod2_lnxded cod2_linux "$@" 2>&1 | tee "$out/$name-build.log"
}

build_missing_recorded_objects() {
    local name=$1 obj dir rules
    dir="$out/$name/build-verify"
    for obj in "${objects[@]}"; do
        if [[ ! -f "$dir/$obj" ]]; then
            # Target-specific objects can be unreachable when cod2_objs fails.
            # Use CMake's own recipe and let the comparator diagnose absent objects.
            rules="${obj%%.dir/*}.dir/build.make"
            make -C "$dir" -f "$rules" "$obj" 2>&1 | tee -a "$out/$name-objects.log" || continue
        fi
    done
}

incomplete=0
for name in base head; do
    rev=$base
    [[ $name != head ]] || rev=$head
    configure "$name" "$rev"
    if [[ -z $reviewed ]]; then
        build "$name"
    else
        build "$name" -- -k || incomplete=1
        build_missing_recorded_objects "$name"
    fi
done
if [[ -n $reviewed ]]; then
    configure reviewed "$reviewed"
    if (( ! incomplete )); then
        build reviewed -- -k || incomplete=1
    fi
    build_missing_recorded_objects reviewed
fi

comparison_status=0
python3 -B "$repo/tools/ci/compare-artifacts.py" "$out/base/build-verify" "$out/head/build-verify" \
    "${intended[@]}" | tee "$out/comparison.txt" || comparison_status=$?
if (( incomplete )); then
    echo 'Incomplete x86 build: see the build logs for compiler/linker errors.' | tee -a "$out/comparison.txt"
fi
if (( comparison_status )); then
    exit "$comparison_status"
fi
exit "$((incomplete * 2))"
