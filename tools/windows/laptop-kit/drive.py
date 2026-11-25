#!/usr/bin/env python3
"""Drive the same scheduled Windows step via OpenSSH or the scratch UTM guest."""
import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import plistlib
import re
import secrets
import shutil
import subprocess
import sys
import time
import zipfile

HERE = Path(__file__).resolve().parent
SCRATCH = "50586ED7-B2DA-47BC-BED5-F7D3919DE097"
UTM = "/Applications/UTM.app/Contents/MacOS/utmctl"
BUNDLE = Path.home() / "Library/Containers/com.utmapp.UTM/Data/Documents/cod2-scratch.utm"
GAME_STEPS = re.compile(r"^(server-|direct-|installed-(?:original|cod2x)-|demo-|full-|borderless-|match-|open-demo-|firewall-first-start$|launcher-hosting$|identity-cod2x$|faults$)")


def powershell_literal(value):
    return "'" + value.replace("'", "''") + "'"


def ssh_option(key, value):
    # OpenSSH parses an -o value as a config line even when argv preserves spaces.
    if any(ord(char) < 32 or char == '"' for char in value):
        raise ValueError(f"Invalid characters in SSH option {key}.")
    return f'{key}="{value.replace("%", "%%")}"'


def encode(script):
    script = ("$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';"
              "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);") + script
    return base64.b64encode(script.encode("utf-16-le")).decode("ascii")


def capture(args, timeout):
    return subprocess.check_output(args, timeout=timeout, text=True,
                                   encoding="utf-8", errors="replace", stdin=subprocess.DEVNULL).strip()


class SshTransport:
    def __init__(self, host, user):
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.:-]*", host) or not user or any(ord(c) < 32 for c in user):
            raise ValueError("Supply an SSH host and Windows account name.")
        self.host = host
        self.options = [
            "-F", "/dev/null",
            "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "PreferredAuthentications=publickey", "-o", "PasswordAuthentication=no",
            "-o", "KbdInteractiveAuthentication=no", "-o", "NumberOfPasswordPrompts=0",
            "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=5",
            "-o", "ServerAliveCountMax=2", "-o", "ClearAllForwardings=yes",
            "-o", "ForwardAgent=no", "-o", "ForwardX11=no", "-o", ssh_option("User", user),
            "-o", "ControlMaster=no", "-o", "ControlPath=none",
            "-o", "Port=" + os.environ.get("COD2_KIT_SSH_PORT", "22"),
        ]
        if identity := os.environ.get("COD2_KIT_SSH_IDENTITY"):
            self.options += ["-i", identity, "-o", "IdentitiesOnly=yes"]
        if known_hosts := os.environ.get("COD2_KIT_SSH_KNOWN_HOSTS"):
            self.options += ["-o", ssh_option("UserKnownHostsFile", known_hosts)]
        self.remote = f"[{host}]" if ":" in host else host

    def exec(self, script, timeout=45):
        command = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encode(script)
        return capture(["ssh", *self.options, "-T", self.host, command], timeout)

    def push(self, local, remote):
        subprocess.run(["scp", *self.options, str(local), f"{self.remote}:{remote}"],
                       check=True, timeout=1200, stdin=subprocess.DEVNULL)

    def pull(self, remote, local):
        subprocess.run(["scp", *self.options, f"{self.remote}:{remote}", str(local)],
                       check=True, timeout=240, stdin=subprocess.DEVNULL)


class UtmTransport:
    def __init__(self, sid):
        if not re.fullmatch(r"S-1-5-21-(?:[0-9]+-){3}[0-9]+", sid):
            raise ValueError("Supply the scratch console account SID with --utm-sid.")
        self.sid = sid

    def check(self):
        path = BUNDLE / "config.plist"
        if BUNDLE.is_symlink() or path.is_symlink():
            raise ValueError("The scratch VM path is a link.")
        with path.open("rb") as stream:
            identity = plistlib.load(stream)["Information"]
        if identity["Name"] != "cod2-scratch" or identity["UUID"] != SCRATCH:
            raise ValueError("The UTM scratch VM identity does not match.")

    def exec(self, script, timeout=45):
        self.check()
        receipt = rf"C:\Windows\Temp\cod2-kit-{secrets.token_hex(12)}.json"
        path = powershell_literal(receipt)
        program = [UTM, "exec", SCRATCH, "--cmd",
                   r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                   "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand"]
        wrapper = ("try { $out=[string](& {\n" + script +
                   "\n} | Out-String -Width 6000); $errorText=$null }"
                   " catch { $out=$null; $errorText=$_.Exception.Message };"
                   f"$p={path}; $text=@{{Output=$out; Error=$errorText}}|ConvertTo-Json -Compress;"
                   "[IO.File]::WriteAllText(($p+'.tmp'),$text,[Text.UTF8Encoding]::new($false));"
                   "[IO.File]::Move(($p+'.tmp'),$p);")
        subprocess.run([*program, encode(wrapper)], check=True, timeout=20)
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            result = subprocess.run([UTM, "file", "pull", SCRATCH, receipt],
                                    capture_output=True, timeout=20)
            if result.returncode == 0 and result.stdout:
                data = json.loads(result.stdout)
                subprocess.run([*program, encode(f"[IO.File]::Delete({path})")], check=True, timeout=20)
                if data["Error"]:
                    raise ValueError(data["Error"])
                return data["Output"].strip()
            time.sleep(0.5)
        raise subprocess.TimeoutExpired(program[:3], timeout)

    def push(self, local, remote):
        self.check()
        print(f"Target cod2-scratch {SCRATCH} (file push)", flush=True)
        with local.open("rb") as stream:
            subprocess.run([UTM, "file", "push", SCRATCH, remote], stdin=stream, check=True, timeout=1200)

    def pull(self, remote, local):
        self.check()
        with local.open("wb") as stream:
            subprocess.run([UTM, "file", "pull", SCRATCH, remote], stdout=stream, check=True, timeout=240)


class Kit:
    def __init__(self, transport, name, output, game_path, arm_rehearsal, online=False, graphics_label="Default",
                 internet_play=False, stage_parent=None, profile_name=None, cool_down=False):
        self.transport = transport
        self.name = name
        self.output = output
        self.game_path = game_path or ""
        self.utm = isinstance(transport, UtmTransport)
        self.arm_rehearsal = self.utm or arm_rehearsal
        self.online = online
        self.graphics_label = graphics_label
        self.internet_play = internet_play
        self.stage_parent = stage_parent
        self.profile_name = profile_name
        self.cool_down = cool_down
        if internet_play and (not online or self.arm_rehearsal or
                              getattr(transport, "host", "") in ("localhost", "127.0.0.1", "::1")):
            raise ValueError("Internet play requires an online physical laptop run.")
        if self.utm:
            sid = powershell_literal(transport.sid)
            self.prefix = f"$profile=(Get-ItemProperty ('HKLM:\\Software\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList\\'+{sid})).ProfileImagePath;"
        else:
            self.prefix = "$profile=[Environment]::GetFolderPath('UserProfile');"
        parent = powershell_literal(stage_parent) if stage_parent else "$profile"
        self.prefix += f"$parent={parent};$stage=Join-Path $parent {powershell_literal(name)};"
        self.ssh_stage = ("/" + stage_parent.replace("\\", "/") + "/" if stage_parent else "") + name
        if self.utm:
            # utmctl transfers use an absolute guest path. Quote only for PowerShell,
            # never for the guest agent's binary file interface.
            self.absolute_stage = self.transport.exec(self.prefix + "$stage")

    def exec(self, script, timeout=45):
        return self.transport.exec(self.prefix + script, timeout)

    def remote_path(self, file):
        return self.absolute_stage + "\\" + file if self.utm else f"{self.ssh_stage}/{file}"

    def deploy(self, archive, digest):
        archive_name = "kit-upload.zip"
        if self.stage_parent:
            self.exec(
                "if(-not (Test-Path -LiteralPath $parent -PathType Container) -or "
                "((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint))"
                "{throw 'The stage parent must be an existing regular folder.'};")
        installed = self.exec(
            "if((Test-Path -LiteralPath $stage) -and "
            "((Get-Item -LiteralPath $stage -Force).Attributes -band [IO.FileAttributes]::ReparsePoint))"
            "{throw 'The stage must be a regular folder.'};"
            "$digest=Join-Path $stage 'kit.sha256';"
            "if(Test-Path -LiteralPath $digest){[IO.File]::ReadAllText($digest)}")
        if installed:
            if installed.lower() != digest:
                raise ValueError("That run already holds a different kit ZIP. Use a new --run name.")
            return
        self.exec(
            "if(-not (Test-Path -LiteralPath $stage)){[void][IO.Directory]::CreateDirectory($stage)};")
        if self.utm:
            # A stage created as SYSTEM must belong to the console user's writable area.
            sid = powershell_literal(self.transport.sid)
            self.exec(f"$grant='*'+{sid}+':(OI)(CI)M'; & icacls.exe $stage /grant $grant | Out-Null;"
                      "if($LASTEXITCODE){throw 'Could not grant scratch account access to the stage.'}")
        self.transport.push(archive, self.remote_path(archive_name))
        self.exec(
            "$inputZip=Join-Path $stage 'kit-upload.zip';"
            f"if((Get-FileHash -LiteralPath $inputZip).Hash -ine '{digest}'){{throw 'Kit transfer hash mismatch.'}};"
            "$kit=Join-Path $stage 'CoD2-Unbound-Windows-Kit';"
            "if(Test-Path -LiteralPath $kit){Remove-Item -LiteralPath $kit -Recurse};"
            "Expand-Archive -LiteralPath $inputZip -DestinationPath $stage;"
            f"[IO.File]::WriteAllText((Join-Path $stage 'kit.sha256'),'{digest}');"
            "Remove-Item -LiteralPath $inputZip", 180)

    def call(self, operation, step="", answer=""):
        args = (
            f" -Operation {powershell_literal(operation)} -Step {powershell_literal(step)}"
            f" -Answer {powershell_literal(answer)} -GamePath {powershell_literal(self.game_path)}"
        )
        if self.utm:
            args += f" -Sid {powershell_literal(self.transport.sid)}"
        if self.arm_rehearsal:
            args += " -ArmRehearsal"
        if self.online:
            args += " -Online"
        if self.internet_play:
            args += " -InternetPlay"
        if self.profile_name:
            args += f" -ProfileName {powershell_literal(self.profile_name)}"
        if operation == "queue":
            if self.cool_down:
                args += " -CoolDown"
            args += f" -GraphicsLabel {powershell_literal(self.graphics_label)}"
            args += f" -DriverUtc {powershell_literal(datetime.now(timezone.utc).isoformat())}"
        return self.exec("& (Join-Path $stage 'CoD2-Unbound-Windows-Kit\\Remote-Kit.ps1') -Stage $stage" + args)

    def status(self):
        return json.loads(self.call("status"))

    def recover(self, step):
        receipt = json.loads(self.call("archive", step).lstrip("\ufeff"))
        pending = self.output / f"{step}.partial"
        self.transport.pull(self.remote_path(f"{step}.zip"), pending)
        with zipfile.ZipFile(pending) as payload:
            member = f"{step}/result.json"
            if payload.testzip() is not None or json.loads(payload.read(member).decode("utf-8-sig")) != receipt:
                raise ValueError("Windows evidence archive does not match its receipt.")
        pending.replace(self.output / f"{step}.zip")
        return receipt

    def run(self, limit):
        collected = 0
        restart_recovery_attempted = False
        marker = self.output / "pending-step"
        while True:
            status = self.status()
            next_step = status["Next"]
            pending = marker.read_text().strip() if marker.exists() else None
            receipts = status.get("Receipts", [])
            for step in receipts:
                if re.fullmatch(r"[a-z0-9-]+", step) is None:
                    raise ValueError("Unexpected Windows step name.")
                if not (self.output / f"{step}.zip").exists():
                    receipt = self.recover(step)
                    print(f"{receipt['Line']} ({receipt['Seconds']} s)", flush=True)
                    collected += 1
                if pending == step and status["Task"] != "Running":
                    marker.unlink()
                    pending = None
                if limit and collected >= limit:
                    return
            if status["Task"] == "Running":
                if not pending:
                    raise ValueError("A task is active. Resume from the same Mac output directory.")
                self.wait(pending)
                continue
            if next_step["Done"]:
                print(self.call("finish"))
                (self.output / "completed.txt").write_text("Windows steps finished.\n")
                print(f"Results pulled to {self.output}.")
                pack(self.output)
                return
            step = next_step["Id"]
            if self.cool_down and GAME_STEPS.match(step):
                last_game = max((path.stat().st_mtime for path in self.output.glob("*.zip")
                                 if GAME_STEPS.match(path.stem)), default=0)
                remaining = 120 - (time.time() - last_game)
                if remaining > 0:
                    print(f"Cooling pause before {next_step['Title']} ({remaining:.0f} s)", flush=True)
                    time.sleep(remaining)
            response = ""
            restart_recovery = pending == step == "interrupt" and next_step["Prompt"]
            if restart_recovery:
                if restart_recovery_attempted:
                    raise ValueError("The restart has no receipt. Sign back in on the laptop and resume the same run.")
                restart_recovery_attempted = True
            elif next_step["Prompt"]:
                response = input(next_step["Prompt"] + "\n> ").strip()
            pending_file = marker.with_suffix(".tmp")
            pending_file.write_text(step + "\n")
            pending_file.replace(marker)
            print(f"Running {next_step['Title']}. Handle any Windows prompt at the laptop.", flush=True)
            self.call("queue", step, response)
            self.wait(step)

    def wait(self, step):
        deadline = time.monotonic() + 1100
        idle = 0
        disconnected = False
        instruction = ""
        while time.monotonic() < deadline:
            time.sleep(2)
            try:
                status = self.status()
            except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
                if step != "interrupt":
                    raise
                if not disconnected:
                    print("The connection dropped during the restart check. Sign in at the laptop when Windows returns.", flush=True)
                    disconnected = True
                time.sleep(8)
                continue
            if status["Next"].get("Id") != step:
                return
            prompt = (status.get("Attention") or "").strip()
            if prompt and prompt != instruction:
                print(prompt + " Then let the kit continue. Keep the wording and your answer for the notes step.", flush=True)
            instruction = prompt
            idle = idle + 1 if status["Task"] != "Running" else 0
            if idle >= 3:
                if step == "interrupt":
                    # The next loop queues only bookkeeping, without a new restart approval.
                    return
                raise ValueError("The desktop task returned without a receipt. Check that the same Windows user is signed in and inspect worker-error.txt in the run folder.")
        raise TimeoutError("The Windows step exceeded its time limit. Re-run this command with the same --run and --output to retrieve its receipt.")


def pack(folder):
    paths = sorted(folder.glob("*.zip"))
    paths = [path for path in paths if path.name != "CoD2-Unbound-results.zip"]
    if not paths:
        raise ValueError("No step archives are available to pack.")
    def shareable(item):
        return not any(part.startswith("private-map-stall-") for part in PurePosixPath(item.filename).parts)

    for path in paths:
        with zipfile.ZipFile(path) as packet:
            for item in packet.infolist():
                if shareable(item):
                    print(f"{item.filename} ({item.file_size} bytes)")
    if input("Review the pictures, sound and test demos. Type PACK to make one ZIP.\n> ").strip() != "PACK":
        return
    result = folder / "CoD2-Unbound-results.zip"
    pending = result.with_suffix(".partial")
    with zipfile.ZipFile(pending, "w", zipfile.ZIP_DEFLATED) as combined:
        for path in paths:
            with zipfile.ZipFile(path) as packet:
                for item in packet.infolist():
                    if not item.is_dir() and shareable(item):
                        with packet.open(item) as contents, combined.open(item.filename, "w", force_zip64=True) as dest:
                            shutil.copyfileobj(contents, dest)
    pending.replace(result)
    print(f"Results ZIP {result}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("host", nargs="?")
    parser.add_argument("user", nargs="?")
    parser.add_argument("--utm-sid")
    parser.add_argument("--arm-rehearsal", action="store_true")
    parser.add_argument("--online", action="store_true",
                        help="allow a PC that still has an internet route; fixture tests stay on loopback")
    parser.add_argument("--internet-play", action="store_true",
                        help="run only the separate consented play checks with the installed Public build")
    parser.add_argument("--kit", type=Path, default=os.environ.get("COD2_KIT_ZIP"))
    parser.add_argument("--run", default="laptop")
    parser.add_argument("--stage-parent", help="existing absolute Windows directory for a private SSH stage")
    parser.add_argument("--profile-name", help="fresh private directory directly under LocalAppData\\CoD2 Unbound")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--game-path")
    parser.add_argument("--graphics-label", choices=("Default", "Intel", "Radeon"), default="Default")
    parser.add_argument("--cool-down", action="store_true", help="allow two idle minutes between local engine runs")
    parser.add_argument("--pack-only", action="store_true")
    parser.add_argument("--steps", type=int, default=0, help="pause after pulling this many steps")
    args = parser.parse_args()
    if args.internet_play and (not args.online or args.utm_sid or args.arm_rehearsal):
        parser.error("--internet-play requires --online on the physical laptop")
    if args.cool_down and (args.internet_play or args.utm_sid or args.arm_rehearsal):
        parser.error("--cool-down is for local Fixture checks on a physical laptop")
    if args.pack_only or (args.output / "completed.txt").exists():
        pack(args.output)
        return
    if not args.kit:
        parser.error("Supply the kit archive with --kit.")
    if not re.fullmatch(r"[a-zA-Z0-9_-]{1,36}", args.run):
        parser.error("--run must use up to 36 letters, numbers, underscores or hyphens")
    if args.stage_parent and (args.utm_sid or not re.fullmatch(r"[a-zA-Z]:\\(?:[a-zA-Z0-9_-]+\\)*[a-zA-Z0-9_-]+", args.stage_parent)):
        parser.error("--stage-parent requires SSH and a qualified Windows path such as C:\\kit-work")
    if args.profile_name and (args.internet_play or not re.fullmatch(r"[a-zA-Z0-9_-]{1,48}", args.profile_name)):
        parser.error("--profile-name requires the Fixture route and up to 48 letters, numbers, underscores or hyphens")
    if args.utm_sid:
        if args.host or args.user:
            parser.error("Use either SSH or --utm-sid.")
        transport = UtmTransport(args.utm_sid)
    else:
        host = args.host or os.environ.get("COD2_KIT_SSH_HOST")
        user = args.user or os.environ.get("COD2_KIT_SSH_USER")
        if not host or not user:
            parser.error("Supply the Windows host and user.")
        if args.arm_rehearsal and host != "127.0.0.1":
            parser.error("--arm-rehearsal requires a loopback SSH host.")
        transport = SshTransport(host, user)

    with args.kit.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    args.output.mkdir(parents=True, exist_ok=True)
    driver = Kit(transport, f"CoD2-Kit-{args.run}", args.output.resolve(), args.game_path,
                 args.arm_rehearsal, args.online, args.graphics_label, args.internet_play,
                 args.stage_parent, args.profile_name, cool_down=args.cool_down)
    driver.deploy(args.kit.resolve(), digest)
    driver.run(args.steps)


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as error:
        print(f"Driver stopped with transport exit code {error.returncode}. Windows progress stays in its run folder.", file=sys.stderr)
        sys.exit(1)
    except subprocess.TimeoutExpired:
        print("A transport call timed out. Windows progress stays in its run folder.", file=sys.stderr)
        sys.exit(1)
    except (ValueError, OSError, EOFError,
            zipfile.BadZipFile, KeyboardInterrupt) as error:
        print(f"Driver stopped. Windows progress stays in its run folder. {error}", file=sys.stderr)
        sys.exit(1)
