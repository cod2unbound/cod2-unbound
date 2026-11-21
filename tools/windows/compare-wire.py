#!/usr/bin/env python3
"""Compare loopback receiver logs at identical server tick times."""
import argparse
from collections import Counter
from pathlib import Path
import re
from struct import Struct

MESSAGE = re.compile(r'^\s*\d+:(svc_\w+|END OF MESSAGE)\s*$')
LENGTH = Struct('<I')


def datagram_sizes(path):
    sizes = []
    with path.open('rb') as stream:
        while header := stream.read(LENGTH.size):
            if len(header) != LENGTH.size:
                raise ValueError(f'{path}: truncated length')
            size, = LENGTH.unpack(header)
            if size > 65507 or len(stream.read(size)) != size:
                raise ValueError(f'{path}: truncated or invalid datagram')
            sizes.append(size)
    return sizes


def read_run(path):
    configs, snapshots, commands = {}, {}, []
    sequence_time = {}
    message = []
    snapshot = None
    receipt = None
    with (path / 'decoded.log').open() as stream:
        for line in stream:
            fields = line.rstrip('\n').split()
            if not fields:
                continue
            kind = fields[0]
            if kind == 'message':
                message = []
            elif match := MESSAGE.fullmatch(line):
                message.append(match[1])
            elif kind == 'config':
                _, index, value = line.rstrip('\n').split(' ', 2)
                configs[int(index)] = value
            elif kind == 'command':
                commands.append(line.rstrip('\n').split(' ', 2)[2])
            elif kind == 'snapshot':
                time, size, seq, delta = map(int, fields[1:9:2])
                snapshot = dict(size=size, base=sequence_time.get(delta), player=tuple(fields[10:]),
                                flags=fields[9], messages=tuple(message), entity={}, hud={})
                if time in snapshots:
                    raise ValueError(f'{path}: duplicate server time {time}')
                snapshots[time] = snapshot
                sequence_time[seq] = time
            elif kind in ('entity', 'hud'):
                number = int(fields[1])
                if snapshot is None or number in snapshot[kind]:
                    raise ValueError(f'{path}: invalid {kind} record')
                snapshot[kind][number] = tuple(fields[2:])
            elif fields[:2] == ['received', 'snapshots']:
                receipt = int(fields[2])
    sizes = datagram_sizes(path / 'datagrams.bin')
    if not configs or not snapshots or receipt != len(snapshots) or len(sizes) < receipt:
        raise ValueError(f'{path}: missing or incomplete capture')
    return configs, commands, snapshots, sizes


def compare(left, right, start=None, end=None):
    lc, lcommands, ls, ld = left
    rc, rcommands, rs, rd = right
    common = sorted(t for t in ls.keys() & rs.keys() if (start is None or t >= start) and (end is None or t <= end))
    if not common:
        raise ValueError('no overlapping snapshot times in the requested range')
    lo, hi = common[0], common[-1]
    left_only = sum(lo <= t <= hi for t in ls.keys() - rs.keys())
    right_only = sum(lo <= t <= hi for t in rs.keys() - ls.keys())
    print(f'Datagrams: {len(ld)} / {len(rd)} · decoded snapshots: {len(ls)} / {len(rs)}')
    print(f'Shared snapshot times: {len(common)} ({lo}–{hi} ms); within that span, left-only {left_only}, right-only {right_only}')
    config_diffs = [i for i in sorted(lc.keys() | rc.keys()) if lc.get(i) != rc.get(i)]
    print(f'Initial config strings: {len(lc)} / {len(rc)}; {len(config_diffs)} differ')
    for i in config_diffs:
        print(f'  {i}: {lc.get(i)!r} / {rc.get(i)!r}')
    print(f'Reliable command order: {"matches" if lcommands == rcommands else "differs"} ({len(lcommands)} / {len(rcommands)} commands)')
    counts = Counter()
    first = {}
    for time in common:
        a, b = ls[time], rs[time]
        for field in ('player', 'flags', 'entity', 'hud', 'messages', 'size'):
            if field in ('size', 'messages') and (a['base'] is None or a['base'] != b['base']):
                continue
            counts[f'{field} compared'] += 1
            if a[field] == b[field]:
                continue
            counts[f'{field} differing'] += 1
            if field in ('entity', 'hud'):
                keys = a[field].keys() | b[field].keys()
                items = [i for i in keys if a[field].get(i) != b[field].get(i)]
                counts[f'{field} differing records'] += len(items)
                index = min(items)
                first.setdefault(field, (time, index, a[field].get(index), b[field].get(index)))
            else:
                first.setdefault(field, (time, a[field], b[field]))
    for field in ('player', 'flags', 'entity', 'hud', 'messages', 'size'):
        print(f"{field}: {counts[f'{field} differing']}/{counts[f'{field} compared']} snapshots differ"
              + (f" ({counts[f'{field} differing records']} rows)" if field in ('entity', 'hud') else ''))
        if field in first:
            print(f'  first {first[field]}')
    return bool(config_diffs or lcommands != rcommands or left_only or right_only or first)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('retail', type=Path)
    parser.add_argument('unbound', type=Path)
    parser.add_argument('--start', type=int)
    parser.add_argument('--end', type=int)
    args = parser.parse_args()
    try:
        return compare(read_run(args.retail), read_run(args.unbound), args.start, args.end)
    except (OSError, ValueError) as exc:
        parser.exit(2, f'{exc}\n')


if __name__ == '__main__':
    raise SystemExit(main())
