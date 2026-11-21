#!/usr/bin/env python3
"""Check inactive source tokens; real i386 binary parity requires the CI toolchain."""
import argparse
from difflib import SequenceMatcher
from functools import lru_cache
import importlib.util
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
UPSTREAM = '410342a82da1b2e8286c290274aeb0f1731a6859'
BOUNDARY = re.compile(r'''"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[;{}]''')


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


spec = importlib.util.spec_from_file_location('guards', ROOT / 'tools/macos-port/check_legacy_guards.py')
guards = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guards)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--base', default='4802e924f22b15beacc5139a1ac8e239940f7272')
args = parser.parse_args()
paths = subprocess.check_output(['git', 'diff', '--diff-filter=M', '--name-only', args.base,
                                 '--', 'src'], cwd=ROOT, text=True).splitlines()
failures = []
restorations = 0
for path in paths:
    if not path.endswith(('.c', '.h', '.inc')):
        continue
    # The Metal providers and the Direct3D 9 on Metal glue are compiled only into the
    # native client; no 32-bit build reads them.
    if path.startswith(('src/Mac/Metal/', 'src/platform/mac_d3d9')):
        continue
    old = subprocess.check_output(['git', 'show', f'{args.base}:{path}'], cwd=ROOT, text=True)
    new = (ROOT / path).read_text()
    upstream = None
    for name, defines in guards.CONFIGURATIONS.items():
        if name == 'linux-x86_64-port-on':
            continue
        for patch in (0, 1):
            definitions = defines + [f'COD2_IS_PATCH_13={patch}', 'COD2_PORT_DEBUG=1',
                'COD2_DEBUG_ENV(name)=getenv(name)', 'COD2_DEBUG_ONLY(...)=__VA_ARGS__']
            before = guards.body(old, definitions)
            after = guards.body(new, definitions)
            if before == after:
                continue
            if args.base == 'main':
                # A change can undo a leak already in main. Accept only exact
                # upstream replacements at the same positions in main.
                if upstream is None:
                    result = subprocess.run(['git', 'show', f'{UPSTREAM}:{path}'], cwd=ROOT,
                                            capture_output=True, text=True)
                    upstream = result.stdout if result.returncode == 0 else ''
                if upstream:
                    original = guards.body(upstream, definitions)
                    if after == original or edits(before, after) <= edits(before, original):
                        restorations += 1
                        continue
            failures.append((path, name, patch))
print(f'{len(paths)} changed source files; 10 legacy configurations; {len(failures)} mismatches')
if restorations:
    print(f'{restorations} comparisons restore upstream source')
for failure in failures:
    print(*failure)
raise SystemExit(bool(failures))
