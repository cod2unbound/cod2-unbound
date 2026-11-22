#!/usr/bin/env python3
"""Check inactive source tokens; real i386 binary parity requires the CI toolchain."""
import argparse
from collections import Counter
from difflib import SequenceMatcher
from functools import lru_cache
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'tools' / directory) for directory in ('macos-port', 'ci')]
import check_legacy_guards as guards
from upstream_differences import CATALOG, UPSTREAM, read_catalog

BOUNDARY = re.compile(guards.QUOTED + r'|[;{}]')


@lru_cache(maxsize=128)
def fragments(body):
    parts = []
    start = 0
    for match in BOUNDARY.finditer(body):
        if match[0] in (';', '{', '}'):
            parts.append(body[start:match.end()].strip())
            start = match.end()
    if start < len(body):
        parts.append(body[start:].strip())
    return parts


@lru_cache(maxsize=128)
def edits(before, after):
    original, revised = fragments(before), fragments(after)
    return {(i, j, tuple(revised[k:l]))
            for tag, i, j, k, l in SequenceMatcher(None, original, revised).get_opcodes()
            if tag != 'equal'}


def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True)


def show_if_present(ref, path):
    result = subprocess.run(['git', 'show', f'{ref}:{path}'], cwd=ROOT,
                            capture_output=True, text=True)
    return result.stdout if result.returncode == 0 else None


def projections(sources, preserve_literals):
    for name, defines in guards.CONFIGURATIONS.items():
        if name == 'linux-x86_64-port-on':
            continue
        for patch in (0, 1):
            definitions = defines + [f'COD2_IS_PATCH_13={patch}', 'COD2_PORT_DEBUG=1',
                'COD2_DEBUG_ENV(name)=getenv(name)', 'COD2_DEBUG_ONLY(...)=__VA_ARGS__']
            bodies = {source: guards.body(source, definitions, preserve_literals=preserve_literals)
                      for source in dict.fromkeys(sources) if source is not None}
            yield name, patch, [bodies.get(source) for source in sources]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', default='4802e924f22b15beacc5139a1ac8e239940f7272')
    args = parser.parse_args()
    upstream_mode = git('rev-parse', args.base + '^{commit}').strip() == UPSTREAM
    release_gate = args.base == 'main'
    options = ['--no-renames', '--diff-filter=DM'] if upstream_mode else ['--diff-filter=M']
    paths = git('diff', *options, '--name-only', args.base, '--', 'src').splitlines()
    reviewed, listed, _ = read_catalog(ROOT / CATALOG) if upstream_mode or release_gate else (None, {}, {})
    failures = []
    intended = Counter()
    restorations = 0
    for path in sorted(set(paths) | (listed.keys() if upstream_mode else set())):
        if not path.endswith(('.c', '.h', '.inc')):
            continue
        # The Metal providers and the Direct3D 9 on Metal glue are native-only.
        if path.startswith(('src/Mac/Metal/', 'src/platform/mac_d3d9')):
            continue
        old = git('show', f'{args.base}:{path}')
        try:
            new = (ROOT / path).read_text()
        except FileNotFoundError:
            failures.append((path, 'source removed'))
            continue
        source = None
        if upstream_mode and path in listed:
            source = git('show', f'{reviewed}:{path}')
        elif release_gate:
            source = show_if_present(UPSTREAM, path)
        approved = git('show', f'{reviewed}:{path}') if release_gate and path in listed else None
        for name, patch, (before, after, original, recorded) in projections(
                (old, new, source, approved), upstream_mode or release_gate):
            if upstream_mode and original is not None and before != original:
                if after == original:
                    intended[path] += 1
                else:
                    status = 'listed difference absent' if after == before else 'differs from reviewed source'
                    failures.append((path, name, patch, status))
                continue
            if before == after:
                continue
            if release_gate and original is not None:
                # An edit may undo a leak at its position in main.
                if after == original or edits(before, after) <= edits(before, original):
                    restorations += 1
                    continue
            if release_gate and after == recorded:
                intended[path] += 1
                continue
            failures.append((path, name, patch))
    for path, label in listed.items():
        if upstream_mode and not intended[path]:
            failures.append((path, 'stale entry', label))
    print(f'{len(paths)} changed source files; 10 legacy configurations; {len(failures)} mismatches')
    if restorations:
        print(f'{restorations} comparisons restore upstream source')
    for path, count in intended.items():
        print(f'intended {listed[path]}: {path} ({count} comparisons)')
    if intended:
        print(f'{sum(intended.values())} intended comparisons across {len(intended)} files')
    for failure in failures:
        print(*failure)
    return bool(failures)


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (KeyError, OSError, ValueError) as error:
        sys.exit(str(error))
