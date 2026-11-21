#!/usr/bin/env python3
"""Compare ELF32 build outputs against a baseline and, when supplied, reviewed differences."""
import argparse
import hashlib
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory

BINARIES = ('cod2_lnxded', 'cod2_linux')


def artifacts(root, *, require_binaries=True):
    # Compiler probes are outside the three engine targets.
    objects = {p.relative_to(root).as_posix(): p for p in root.rglob('*.o')
               if any(part in ('cod2_objs.dir', 'cod2_linux.dir', 'cod2_lnxded.dir')
                      for part in p.relative_to(root).parts)}
    if not objects:
        raise ValueError(f'No game objects in {root}')
    for name in BINARIES:
        path = root / name
        if not require_binaries and not path.exists():
            continue
        with path.open('rb') as file:
            if file.read(5) != b'\x7fELF\x01':
                raise ValueError(f'Not a 32-bit ELF binary: {path}')
        objects[name] = path
    return objects


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def without_debug(path, tmp, *, linked=False):
    flags = ['--strip-debug']
    if linked:
        flags.append('--remove-section=.note.gnu.build-id')
    subprocess.run(['objcopy', *flags, str(path), str(tmp)], check=True)
    return digest(tmp)


def compare_reviewed(left, right, reviewed, allowed, tmp):
    failures = []
    debug_only = intended = identical = 0
    for name in sorted(left.keys() | right.keys() | allowed.keys()):
        linked = name in BINARIES
        if name not in left or name not in right:
            failures.append((name, 'missing from one or both builds'))
            continue
        if digest(left[name]) == digest(right[name]):
            if name in allowed:
                failures.append((name, f'listed difference absent ({allowed[name]})'))
            elif not linked:
                identical += 1
            continue

        base_hash = without_debug(left[name], tmp, linked=linked)
        head_hash = without_debug(right[name], tmp, linked=linked)
        if name in allowed:
            control = reviewed / name
            if base_hash == head_hash:
                failures.append((name, f'listed difference absent ({allowed[name]})'))
            elif not control.is_file() or head_hash != without_debug(control, tmp):
                failures.append((name, f'differs from reviewed object ({allowed[name]})'))
            else:
                print(f'intended {allowed[name]}: {name}')
                intended += 1
        elif base_hash == head_hash:
            if not linked:
                debug_only += 1
        elif linked and allowed and (reviewed / name).is_file() and head_hash == without_debug(
                reviewed / name, tmp, linked=True):
            print(f'intended linked result: {name}')
        else:
            failures.append((name, 'unlisted code or data difference'))
    print(f'{identical} identical objects; {debug_only} debug-only differences; '
          f'{intended} intended object differences; {len(failures)} mismatches')
    for name, reason in failures:
        print(f'FAIL: {name}: {reason}')
    return bool(failures)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('base', type=Path)
    parser.add_argument('head', type=Path)
    parser.add_argument('--intended', nargs=2, type=Path, metavar=('CATALOG', 'REVIEWED_BUILD'))
    args = parser.parse_args()

    left, right = (artifacts(path, require_binaries=not args.intended)
                   for path in (args.base, args.head))
    if args.intended:
        from upstream_differences import read_catalog

        catalog, reviewed = args.intended
        _, _, allowed = read_catalog(catalog)
        with TemporaryDirectory(dir=args.base.parent, prefix='compare-') as temp:
            return compare_reviewed(left, right, reviewed, allowed, Path(temp) / 'object')

    changed = [key for key in sorted(left.keys() | right.keys())
               if key not in left or key not in right or digest(left[key]) != digest(right[key])]
    if changed:
        print('FAIL: differing or missing artifacts:\n' + '\n'.join(changed))
        return 1
    print(f'PASS: {len(left) - 2} objects and both unstripped 32-bit binaries are byte-identical')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (KeyError, OSError, ValueError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
