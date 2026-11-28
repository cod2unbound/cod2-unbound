"""Wait for the replay's bots to spawn before connecting a spectator."""
from pathlib import Path
from struct import Struct
import sys
import time

header = Struct('<4i')
mode = sys.argv[1]
start_tick = int(sys.argv[2]) if len(sys.argv) > 2 else 140


def current_tick(mode):
    if mode == 'unbound':
        lines = Path('/run/statehash.txt').read_text().splitlines()
        return max((int(line.split()[1]) for line in lines if line.startswith('frame ') and len(line.split()) == 10), default=0)
    with Path('/run/capture.bin').open('rb') as stream:
        while row := stream.read(header.size):
            if len(row) != header.size:
                return 0
            frame, _, entities, players = header.unpack(row)
            if frame >= start_tick:
                return frame
            stream.seek(244 * entities + 308 * players, 1)
    return 0


deadline = time.monotonic() + 60
while time.monotonic() < deadline:
    try:
        tick = current_tick(mode)
    except FileNotFoundError:
        tick = 0
    if tick >= start_tick:
        print(f'Connecting after replay tick {tick}', flush=True)
        sys.exit(0)
    time.sleep(.01 if len(sys.argv) > 2 else .10)
sys.exit(f'Did not reach tick {start_tick}')
