"""Wait for both bots to spawn before a third client can affect their spawn RNG."""
from pathlib import Path
from struct import Struct
import sys
import time

header = Struct('<4i')
START_TICK = 140


def current_tick(mode):
    if mode == 'unbound':
        lines = Path('/run/statehash.txt').read_text().splitlines()
        return max((int(line.split()[1]) for line in lines if line.startswith('frame ') and len(line.split()) == 10), default=0)
    with Path('/run/capture.bin').open('rb') as stream:
        while row := stream.read(header.size):
            if len(row) != header.size:
                return 0
            frame, _, entities, players = header.unpack(row)
            if frame >= START_TICK:
                return frame
            stream.seek(244 * entities + 308 * players, 1)
    return 0


deadline = time.monotonic() + 60
while time.monotonic() < deadline:
    try:
        tick = current_tick(sys.argv[1])
    except FileNotFoundError:
        tick = 0
    if tick >= START_TICK:
        print(f'Connecting after replay tick {tick}', flush=True)
        sys.exit(0)
    time.sleep(.10)
sys.exit(f'Did not reach tick {START_TICK}')
