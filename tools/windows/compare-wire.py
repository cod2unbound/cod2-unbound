#!/usr/bin/env python3
"""Compare loopback receiver logs at identical server tick times."""
import argparse
from collections import Counter
from itertools import zip_longest
from pathlib import Path
import re
from struct import Struct

MESSAGE = re.compile(r'^\s*\d+:(svc_\w+|END OF MESSAGE)\s*$')
COMMAND_TIME_UPDATE = re.compile(r'^\s*\d+: playerstate commandTime:(-?\d+)\b')
LENGTH = Struct('<I')


def read_datagrams(path):
    sizes = []
    by_sequence = {}
    with path.open('rb') as stream:
        while header := stream.read(LENGTH.size):
            if len(header) != LENGTH.size:
                raise ValueError(f'{path}: truncated length')
            size, = LENGTH.unpack(header)
            if size > 65507:
                raise ValueError(f'{path}: invalid datagram size')
            payload = stream.read(size)
            if len(payload) != size:
                raise ValueError(f'{path}: truncated or invalid datagram')
            sizes.append(size)
            if size >= LENGTH.size and payload[:LENGTH.size] != b'\xff' * LENGTH.size:
                sequence, = LENGTH.unpack_from(payload)
                by_sequence.setdefault(sequence & 0x7fffffff, []).append(size)
    return sizes, by_sequence


def read_run(path):
    configs, snapshots, commands = {}, {}, []
    sequence_time = {}
    message = []
    command_time_update = None
    snapshot = None
    receipt = None
    client = None
    gamestate_messages = ()
    with (path / 'decoded.log').open() as stream:
        for line in stream:
            fields = line.rstrip('\n').split()
            if not fields:
                continue
            kind = fields[0]
            if kind == 'message':
                message = []
                command_time_update = None
            elif match := MESSAGE.fullmatch(line):
                message.append(match[1])
            elif match := COMMAND_TIME_UPDATE.match(line):
                command_time_update = int(match[1])
            elif kind == 'config':
                _, index, value = line.rstrip('\n').split(' ', 2)
                configs[int(index)] = value
            elif kind == 'command':
                commands.append(line.rstrip('\n').split(' ', 2)[2])
            elif kind == 'client':
                client = int(fields[1])
                gamestate_messages = tuple(message)
            elif kind == 'snapshot':
                time, size, seq, delta = map(int, fields[1:9:2])
                if delta > 0 and delta not in sequence_time:
                    raise ValueError(f'{path}: snapshot {time} has an unknown delta base')
                snapshot = dict(sequence=seq, size=size, base=sequence_time.get(delta),
                                player=tuple(fields[10:]), commandTime=None, flags=fields[9],
                                command_time_update=command_time_update, messages=tuple(message),
                                entity={}, hud={}, clientstate={})
                if time in snapshots:
                    raise ValueError(f'{path}: duplicate server time {time}')
                snapshots[time] = snapshot
                sequence_time[seq] = time
            elif kind == 'commandTime':
                if snapshot is None or snapshot[kind] is not None:
                    raise ValueError(f'{path}: invalid commandTime record')
                snapshot[kind] = int(fields[1])
            elif kind in ('entity', 'hud', 'clientstate'):
                number = int(fields[1])
                if snapshot is None or number in snapshot[kind]:
                    raise ValueError(f'{path}: invalid {kind} record')
                snapshot[kind][number] = tuple(fields[2:])
            elif fields[:2] == ['received', 'snapshots']:
                receipt = int(fields[2])
    sizes, by_sequence = read_datagrams(path / 'datagrams.bin')
    if not configs or not snapshots or receipt != len(snapshots) or len(sizes) < receipt:
        raise ValueError(f'{path}: missing or incomplete capture')
    for time, row in snapshots.items():
        packets = by_sequence.get(row['sequence'])
        if not packets or packets[-1] != row['size']:
            raise ValueError(f'{path}: datagrams do not match snapshot {time}')
        row['size'] = tuple(packets)
    return configs, commands, snapshots, sizes, client, gamestate_messages


def first_word_difference(left, right):
    if left is None or right is None:
        return f'row presence {left is not None} / {right is not None}'
    for offset, (a, b) in enumerate(zip_longest(left, right)):
        if a != b:
            return f'word {offset}: {a} / {b}'
    raise ValueError('rows have no differing word')


def compare(left, right, start=None, end=None, step=None):
    lc, lcommands, ls, ld, lclient, lgame = left
    rc, rcommands, rs, rd, rclient, rgame = right
    common = sorted(t for t in ls.keys() & rs.keys() if (start is None or t >= start) and (end is None or t <= end))
    if not common:
        raise ValueError('no overlapping snapshot times in the requested range')
    lo = common[0] if start is None else start
    hi = common[-1] if end is None else end
    lt = {t for t in ls if lo <= t <= hi}
    rt = {t for t in rs if lo <= t <= hi}
    left_only, right_only = sorted(lt - rt), sorted(rt - lt)
    print(f'Client slots: {lclient} / {rclient}')
    print(f'Datagrams: {len(ld)} / {len(rd)} ({sum(ld)} / {sum(rd)} bytes)')
    print(f'Decoded snapshots: {len(ls)} / {len(rs)}')
    print(f'Shared snapshot times: {len(common)} ({lo}–{hi} ms)')
    print(f'One-sided times within the comparison window: {len(left_only)} / {len(right_only)}')
    if left_only or right_only:
        print(f'  Left only: {left_only}; right only: {right_only}')
    print(f'Outside the comparison window: left {sorted(ls.keys() - lt)}; right {sorted(rs.keys() - rt)}')
    for time in sorted((ls.keys() & rs.keys()) - set(common)):
        a, b = ls[time], rs[time]
        updates = [str(row['command_time_update']) if row['command_time_update'] is not None else 'inherited'
                   for row in (a, b)]
        print(f"  {time} ms: base {a['base']} / {b['base']}, "
              f"commandTime {a['commandTime']} / {b['commandTime']}, "
              f"commandTime writes {updates[0]} / {updates[1]}, "
              f"packet sizes {a['size']} / {b['size']}")
    grid_error = False
    if step is not None:
        if 'svc_gamestate' not in lgame or 'svc_gamestate' not in rgame or any(
            snapshots[t]['commandTime'] is None for snapshots in (ls, rs) for t in common
        ):
            raise ValueError('clocked capture lacks gamestate order or decoded commandTime')
        expected = set(range(lo, hi + 1, step))
        for name, actual in (('left', lt), ('right', rt)):
            missing, extra = sorted(expected - actual), sorted(actual - expected)
            if missing or extra:
                print(f'  {name} times missing from grid: {missing}; extra: {extra}')
                grid_error = True
    config_diffs = [i for i in sorted(lc.keys() | rc.keys()) if lc.get(i) != rc.get(i)]
    print(f'Initial config strings: {len(lc)} / {len(rc)}; {len(config_diffs)} differ')
    for i in config_diffs:
        print(f'  {i}: {lc.get(i)!r} / {rc.get(i)!r}')
    print(f'Gamestate message order: {"matches" if lgame == rgame else "differs"} ({len(lgame)} / {len(rgame)} messages)')
    if lgame != rgame:
        print(f'  First difference: {first_word_difference(lgame, rgame)}')
    print(f'Reliable command order: {"matches" if lcommands == rcommands else "differs"} ({len(lcommands)} / {len(rcommands)} commands)')
    if lcommands != rcommands:
        print(f'  First difference: {first_word_difference(lcommands, rcommands)}')
    counts = Counter()
    first = {}
    fields = ('player', 'commandTime', 'flags', 'entity', 'hud', 'clientstate', 'messages', 'size', 'base')
    records = ('entity', 'hud', 'clientstate')
    for time in common:
        a, b = ls[time], rs[time]
        for field in fields:
            if field == 'base' and step is None:
                continue
            if field == 'size' and a['base'] != b['base']:
                continue
            if field == 'commandTime' and (a[field] is None and b[field] is None):
                continue
            counts[f'{field} compared'] += 1
            if a[field] == b[field]:
                continue
            counts[f'{field} differing'] += 1
            if field in records:
                keys = a[field].keys() | b[field].keys()
                items = [i for i in keys if a[field].get(i) != b[field].get(i)]
                counts[f'{field} differing records'] += len(items)
                index = min(items)
                first.setdefault(field, (time, index, first_word_difference(a[field].get(index), b[field].get(index))))
            elif field == 'player':
                first.setdefault(field, (time, first_word_difference(a[field], b[field])))
            else:
                first.setdefault(field, (time, a[field], b[field]))
    for field in fields:
        if field == 'base' and step is None:
            continue
        print(f"{field}: {counts[f'{field} differing']}/{counts[f'{field} compared']} snapshots differ"
              + (f" ({counts[f'{field} differing records']} rows)" if field in records else ''))
        if field in first:
            print(f'  first {first[field]}')
    for name, snapshots in (('left', ls), ('right', rs)):
        rows = Counter()
        words = 0
        for time in common:
            row = snapshots[time]
            rows.update({field: len(row[field]) for field in records})
            words += len(row['player']) + (row['commandTime'] is not None)
            words += sum(len(values) for field in records for values in row[field].values())
        print(f"Shared times, {name}: {rows['entity']} entity rows, {rows['hud']} HUD rows, "
              f"{rows['clientstate']} client-state rows, {words} fields, "
              f"{sum(sum(snapshots[time]['size']) for time in common)} snapshot datagram bytes")
    return bool(lclient != rclient or lgame != rgame or config_diffs or lcommands != rcommands or
                left_only or right_only or first or grid_error)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('retail', type=Path)
    parser.add_argument('unbound', type=Path)
    parser.add_argument('--start', type=int)
    parser.add_argument('--end', type=int)
    parser.add_argument('--step', type=int, help='require every time on this grid and matching delta bases')
    args = parser.parse_args()
    if args.step is not None and (args.step <= 0 or args.start is None or args.end is None or
                                  args.start > args.end or (args.end - args.start) % args.step):
        parser.error('--step requires a positive step and aligned --start and --end')
    try:
        return compare(read_run(args.retail), read_run(args.unbound), args.start, args.end, args.step)
    except (OSError, ValueError) as exc:
        parser.exit(2, f'{exc}\n')


if __name__ == '__main__':
    raise SystemExit(main())
