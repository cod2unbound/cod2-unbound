"""Convert the retail recorder's binary trace to the existing parity formats."""
import argparse
from pathlib import Path
from struct import Struct, pack, unpack, unpack_from
from zlib import crc32

HEADER = Struct("<4i")
ENTITY_BYTES = 240
PLAYER_BYTES = 304


def read_exact(stream, size):
    data = stream.read(size)
    if len(data) != size:
        raise ValueError("truncated capture")
    return data


def records(directory, ticks=600):
    with (directory / "capture.bin").open("rb") as stream:
        for tick in range(4, 4 + ticks):
            frame, time, entities, players = HEADER.unpack(read_exact(stream, HEADER.size))
            if frame != tick or not 0 < entities <= 1024 or not 0 <= players <= 64:
                raise ValueError(f"incomplete or malformed capture at tick {tick}")
            entity_data = [(unpack("<i", read_exact(stream, 4))[0],
                            read_exact(stream, ENTITY_BYTES)) for _ in range(entities)]
            player_data = [(unpack("<i", read_exact(stream, 4))[0],
                            read_exact(stream, PLAYER_BYTES)) for _ in range(players)]
            yield frame, time, entity_data, player_data
        if stream.read(1):
            raise ValueError("capture contains extra data")


def f32(value):
    return unpack("<f", pack("<f", value))[0]


def entity_fields(state):
    return (unpack_from("<3i", state, 0), (unpack_from("<i", state, 12)[0],
                                          unpack_from("<i", state, 48)[0]),
            unpack_from("<6f", state, 24), unpack_from("<6f", state, 60))


def quantized(state, crc=0):
    identity, types, pos, apos = entity_fields(state)
    fields = identity + types + tuple(int(f32(f32(f * 64) + (0.5 if f >= 0 else -0.5)))
                                     for f in pos + apos)
    return crc32(pack("<17i", *fields), crc)


def entity_line(index, state):
    (number, kind, flags), (tr, ap), pos, apos = entity_fields(state)
    def vector(values, specifier):
        return ",".join(format(value, specifier) for value in values)
    return (f"e{index} eq={quantized(state):08x} num={number} type={kind} fl={flags & 0xffffffff:08x} "
            f"org={vector(pos[:3], '.9g')} ang={vector(apos[:3], '.3f')} "
            f"vel={vector(pos[3:], '.3f')} avel={vector(apos[3:], '.3f')} trT={tr} apT={ap}\n")


def player_line(frame, client, state):
    def vector(offset):
        return ",".join(f"{value:08x}" for value in unpack_from("<3I", state, offset))
    def i32(offset):
        return unpack_from("<i", state, offset)[0]
    return (f"frame {frame} client {client} origin {vector(20)} velocity {vector(32)} "
            f"viewangles {vector(232)} pm_type {i32(4)} pm_flags {i32(12) & 0xffffffff:08x} "
            f"pm_time {i32(16)} weapon {i32(212)} weaponstate {i32(216)} health {i32(300)}\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--ticks", type=int, default=600)
    args = parser.parse_args()
    if args.ticks < 1:
        parser.error("--ticks must be positive")
    path = args.directory
    with (path / "statehash.txt").open("w") as states, (path / "players.txt").open("w") as players:
        for frame, time, entity_data, player_data in records(path, args.ticks):
            raw = normalized = 0
            for _, state in entity_data:
                raw = crc32(state, raw)
                normalized = quantized(state, normalized)
            states.write(f"frame {frame} simt {time} ents {len(entity_data)} raw {raw:08x} q {normalized:08x}\n")
            players.write(f"frame {frame} clients {len(player_data)}\n")
            players.writelines(player_line(frame, index, state) for index, state in player_data)
    (path / "entities.txt").write_text("".join(entity_line(index, state) for index, state in entity_data))


if __name__ == "__main__":
    main()
