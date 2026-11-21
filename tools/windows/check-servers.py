"""Run isolated UDP and replay checks inside the Windows build container."""
from contextlib import contextmanager
import gzip
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import tempfile
import time


WINDOWS = Path(__file__).resolve().parent
PARITY = WINDOWS.parent / "parity"
FIXTURE = WINDOWS / "fixtures" / "two-bot"
MAP = "mp_toujane"
FRAMES = 600
HEADER = b"\xff" * 4


def private_home(env, home):
    home.mkdir(mode=0o700)
    return {**env, "HOME": str(home), "CFFIXED_USER_HOME": str(home)}


@contextmanager
def private_wine(env, run):
    run.mkdir()
    with tempfile.TemporaryDirectory(prefix="prefix-", dir=run) as prefix:
        env = private_home({**env, "WINEPREFIX": prefix}, run / "home")
        try:
            with (run / "wineboot.log").open("w") as log:
                subprocess.run(["wineboot", "-i"], env=env, stdout=log, stderr=subprocess.STDOUT,
                               timeout=120, check=True)
            yield env
        finally:
            subprocess.run(["wineserver", "-k"], env=env, timeout=15)
            subprocess.run(["wineserver", "-w"], env=env, timeout=15, check=True)


def query_server(sock, process, query, response, gametype_key, protocol):
    challenge = b"windows-check"
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline and process.poll() is None:
        try:
            sock.send(HEADER + query + b" " + challenge)
            packet = sock.recv(8192)
        except (TimeoutError, ConnectionRefusedError):
            continue
        prefix = HEADER + response + b"\n"
        if not packet.startswith(prefix):
            continue
        fields = packet[len(prefix):].split(b"\n", 1)[0].split(b"\\")[1:]
        info = dict(zip(fields[::2], fields[1::2]))
        if info.get(b"challenge") != challenge:
            continue
        expected = {b"mapname": MAP.encode(), gametype_key: b"dm", b"protocol": protocol}
        for key, value in expected.items():
            if info.get(key) != value:
                raise ValueError(f"{query.decode()}: expected {key}={value}, got {info.get(key)}")
        print(f"{query.decode()} answered for {MAP}, DM, protocol {protocol.decode()}", flush=True)
        return
    raise ValueError(f"No {response.decode()} for {MAP}")


def smoke(run, env, port, status_protocol):
    run.mkdir()
    home = run / "game-home"
    (home / "main").mkdir(parents=True)
    env = {
        **private_home(env, run / "process-home"),
        "SYSDIFF_STATEHASH": str(run / "statehash.txt"),
        "SYSDIFF_MAXFRAMES": "200",
    }
    command = [WINDOWS / "wine-binary.sh", "+set", "fs_basepath", "/data",
               "+set", "fs_homepath", home, "+set", "dedicated", "1",
               "+set", "net_ip", "127.0.0.1", "+set", "net_port", str(port),
               "+set", "sv_punkbuster", "0", "+set", "g_gametype", "dm", "+map", MAP]
    with (run / "console.log").open("w") as log:
        process = subprocess.Popen(command, cwd=run, env=env, stdout=log, stderr=subprocess.STDOUT,
                                   start_new_session=True)
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
                sock.settimeout(0.5)
                sock.connect(("127.0.0.1", port))
                query_server(sock, process, b"getinfo", b"infoResponse", b"gametype", b"118")
                query_server(sock, process, b"getstatus", b"statusResponse", b"g_gametype",
                             status_protocol)
            code = process.wait(timeout=90)
            if code:
                raise ValueError(f"Dedicated server exited {code}")
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()


def replay(run, env, port, edition):
    env = private_home(env, run.with_name("replay-process-home"))
    subprocess.run(
        [sys.executable, PARITY / "record.py", "--binary", WINDOWS / "wine-binary.sh",
         "--data", "/data", "--output", run, "--map", MAP, "--dedicated",
         "--frames", str(FRAMES), "--seed", "12345", "--timeout", "240", "--port", str(port),
         "--players", "--clients", "2", "--replay", FIXTURE / "usercmds.txt"],
        env=env, check=True,
    )
    reference_state = run / "reference-statehash.txt"
    reference_players = run / "reference-players.txt"
    reference_state.write_bytes(gzip.decompress((FIXTURE / f"{edition}-statehash.txt.gz").read_bytes()))
    reference_players.write_bytes(gzip.decompress((FIXTURE / f"{edition}-players.txt.gz").read_bytes()))
    subprocess.run(
        [sys.executable, PARITY / "compare.py", reference_state, run / "statehash.txt",
         "--players", reference_players, run / "players.txt", "--require-clients", "2", "--raw"],
        check=True,
    )


def main():
    output = Path(tempfile.mkdtemp(prefix="headless-", dir="/out"))
    print(f"Evidence in output/r05/builds/wine/{output.name}", flush=True)
    env = dict(
        os.environ, WINEARCH="win64",
        WINEDEBUG="-all", WINEDLLOVERRIDES="mscoree,mshtml=",
        COD2_WINE_HEADLESS_CHECK="1", PYTHONDONTWRITEBYTECODE="1",
        COD2X_TEST_SERVICE_URL="http://127.0.0.1:9",
        COD2X_TEST_UPDATER_ADDRESS="127.0.0.1:9",
        COD2X_TEST_UPDATER_URL="http://127.0.0.1:9/api/updater/check",
    )
    # Keep going after a failed check so both editions have evidence.
    failed = False
    for edition, protocol in (("original", b"118"), ("cod2x", b"120")):
        run = output / edition
        run.mkdir()
        env["COD2_EXE"] = f"/matrix/ded64-{edition}/cod2_win32_ded.exe"
        for check, port, expected in (
            (smoke, 29676, protocol),
            (replay, 29673, edition),
        ):
            label = check.__name__
            print(f"Checking {edition} {label}", flush=True)
            try:
                with private_wine(env, run / f"{label}-wine") as private_env:
                    check(run / label, private_env, port, expected)
            except (OSError, ValueError, subprocess.SubprocessError) as error:
                print(f"FAIL {edition} {label}: {error}", file=sys.stderr)
                failed = True
    return failed


if __name__ == "__main__":
    sys.exit(main())
