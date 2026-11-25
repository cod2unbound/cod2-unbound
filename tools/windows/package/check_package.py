"""Exercise the portable servers and the Inno Setup lifecycle in offline Wine."""

import os
from pathlib import Path
import re
import runpy
import shutil
import socket
import struct
import subprocess
import sys
import time
import zipfile

from package_installer import windows_path
from release import (EXE_NAMES, LAUNCHER_CATALOG, LAUNCHER_NAME, WINDOWS, binary_files,
                     files, sha256)
from signing import Signer


helpers = runpy.run_path(str(WINDOWS / "check-servers.py"))
ENV = dict(os.environ, WINEARCH="win64", WINEDEBUG="-all", WINEDLLOVERRIDES="mscoree,mshtml=",
           COD2_WINE_HEADLESS_CHECK="1", COD2_CRASH_NOPOPUP="1",
           COD2X_TEST_SERVICE_URL="http://127.0.0.1:9",
           COD2X_TEST_UPDATER_ADDRESS="127.0.0.1:9",
           COD2X_TEST_UPDATER_URL="http://127.0.0.1:9/api/updater/check")
UNINSTALL = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\CoD2Unbound_is1"
USER_CLASSES = r"HKCU\Software\Classes"
RETAIL = r"HKLM\Software\Activision\Call of Duty 2"
SILENT = ("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART")
ARM_NOTICE = ("CoD2 Unbound has not been tested on Arm PCs. It runs here through "
              "Windows' x64 emulation and may not work as well as on an x64 PC.")


def wine(env, *args, check=True):
    result = subprocess.run(["wine", *args], env=env, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, timeout=180)
    if check and result.returncode:
        print(result.stdout, file=sys.stderr)
        result.check_returncode()
    return result


def verify_files(tree):
    for line in (tree / "SHA256SUMS").read_text().splitlines():
        digest, name = line.split("  ", 1)
        if sha256(tree / name) != digest:
            raise ValueError(f"Checksum mismatch for {tree / name}")


def run_server(exe, cfg, edition, env, run):
    run.mkdir()
    local = helpers["wine_local_app_data"](env)
    instance = local / "CoD2 Unbound/servers/Package Test"
    config = instance / "main/server.cfg"
    config.parent.mkdir(parents=True)
    shutil.copyfile(cfg, config)
    env = {
        **helpers["private_home"](env, run / "process-home"),
        "COD2_EXE": str(exe),
        "SYSDIFF_STATEHASH": str(run / "statehash.txt"),
        "SYSDIFF_MAXFRAMES": "200",
    }
    port = 29676
    args = [WINDOWS / "wine-binary.sh", "+set", "fs_basepath", windows_path("/data"),
            "+set", "fs_homepath", str(instance), "+set", "dedicated", "1",
            "+set", "net_ip", "127.0.0.1", "+set", "net_port", str(port),
            "+exec", "server.cfg", "+map", helpers["MAP"]]
    with (run / "console.log").open("w") as log:
        with subprocess.Popen(args, cwd=exe.parent, env=env, stdout=log,
                              stderr=subprocess.STDOUT) as process:
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
                    sock.settimeout(0.5)
                    sock.connect(("127.0.0.1", port))
                    status = helpers["query_server"](
                        sock, process, b"getstatus", b"statusResponse", b"g_gametype",
                        b"118" if edition == "original" else b"120",
                    )
                    hostname = status.get(b"sv_hostname")
                    if hostname != b"CoD2 Unbound LAN":
                        raise ValueError(f"Expected the sample server hostname, got {hostname!r}")
                if process.wait(timeout=90):
                    raise ValueError(f"Server exited with {process.returncode}")
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait()
    print(f"PASS {edition} used the sample config from {exe.parent} and exited cleanly", flush=True)


def verify_portable(artifacts, work, sample):
    archive = next(artifacts.glob("*-server.zip"))
    signer = Signer(None, None, work / "test-code.pem", work / "test-tsa.pem",
                    sample / "signature-verification.log")
    signer.verify_catalog(archive.with_suffix(".cat"), [archive], artifacts)
    tampered = sample / archive.name
    shutil.copyfile(archive, tampered)
    with tampered.open("ab") as stream:
        stream.write(b"changed byte sequence")
    try:
        signer.verify_catalog(archive.with_suffix(".cat"), [tampered], sample)
    except ValueError as error:
        if "does not bind" not in str(error):
            raise
    else:
        raise ValueError("Catalog incorrectly accepted a modified ZIP")
    finally:
        tampered.unlink()

    expanded = sample / "Portable Server Release"
    with zipfile.ZipFile(archive) as bundle:
        bundle.extractall(expanded)
    if (expanded / "launcher").exists() or (expanded / LAUNCHER_CATALOG).exists():
        raise ValueError("Launcher files are present in the dedicated server ZIP")
    verify_files(expanded)
    for edition in ("original", "cod2x"):
        exe = expanded / edition / EXE_NAMES[1]
        signer.verify(exe)
        with helpers["private_wine"](ENV, sample / f"{edition}-wine") as env:
            run_server(exe, expanded / "server.cfg", edition, env, sample / edition)


def install(env, installer, log, *args):
    result = wine(env, installer, *SILENT, "/SP-", f"/LOG={windows_path(log)}", *args, check=False)
    print(f"{installer.name} with {' '.join(args) or 'default scope'} exited {result.returncode}",
          flush=True)
    result.check_returncode()
    if ARM_NOTICE in log.read_text(encoding="utf-8-sig"):
        raise ValueError("Setup showed the Arm warning on x64")


def install_with_folder_lock(env, setup, root, holder, run, release_after=None):
    run.mkdir()
    ready = run / "ready"
    release = run / "release"
    log = run / "setup.log"
    lock = re.compile(r"MoveFileExW failed .*Win32 error (?:5|32), elapsed (\d+) ms")
    args = ("/CURRENTUSER", "/TASKS=links")
    with subprocess.Popen(["wine", holder, windows_path(root), windows_path(ready), windows_path(release)],
                          env=env) as control:
        try:
            deadline = time.monotonic() + 15
            while not ready.exists():
                if control.poll() is not None or time.monotonic() >= deadline:
                    raise ValueError("The directory lock helper did not start")
                time.sleep(0.05)
            with subprocess.Popen(["wine", setup, *SILENT, "/SP-", f"/LOG={windows_path(log)}", *args],
                                  env=env) as process:
                try:
                    if release_after is not None:
                        deadline = time.monotonic() + 60
                        while process.poll() is None and time.monotonic() < deadline:
                            if log.exists() and lock.search(log.read_text(encoding="utf-8-sig", errors="replace")):
                                time.sleep(release_after)
                                break
                            time.sleep(0.05)
                        release.touch()
                    status = process.wait(timeout=120)
                finally:
                    if process.poll() is None:
                        process.kill()
                        process.wait()
        finally:
            release.touch()
            try:
                control.wait(timeout=10)
            except subprocess.TimeoutExpired:
                control.kill()
                control.wait()
                raise
        if control.returncode:
            raise ValueError(f"The directory lock helper exited {control.returncode}")
    receipt = log.read_text(encoding="utf-8-sig")
    return status, receipt, tuple(int(ms) for ms in lock.findall(receipt))


def assert_version(env, root, scope, version):
    key = scope + "\\" + UNINSTALL
    result = wine(env, "reg", "query", key, "/v", "DisplayVersion", "/reg:64")
    if result.stdout.split()[-1] != version or (root / "RELEASE_VERSION").read_text().strip() != version:
        raise ValueError(f"Expected {version} in both installed version locations")
    verify_files(root)


def verify_shortcut_working_directory(shortcut, expected):
    # MS-SHLLINK: StringData follows the optional target ID list and LinkInfo.
    data = shortcut.read_bytes()
    offset, = struct.unpack_from("<I", data)
    flags, = struct.unpack_from("<I", data, 0x14)
    if flags & 0x01:
        size, = struct.unpack_from("<H", data, offset)
        offset += 2 + size
    if flags & 0x02:
        size, = struct.unpack_from("<I", data, offset)
        offset += size
    unicode = bool(flags & 0x80)
    width = 2 if unicode else 1
    working_directory = ""
    for flag in (0x04, 0x08, 0x10):  # name, relative path, working directory
        if flags & flag:
            count, = struct.unpack_from("<H", data, offset)
            offset += 2
            end = offset + count * width
            if flag == 0x10:
                working_directory = data[offset:end].decode("utf-16-le" if unicode else "ascii")
            offset = end
    # Wine's shell includes a terminator in the counted string it writes.
    if working_directory.rstrip("\x00").rstrip("\\").casefold() != expected.rstrip("\\").casefold():
        raise ValueError(f"{shortcut.name} starts in {working_directory!r}, expected {expected!r}")


def verify_launcher(env, root, start, uninstaller, signer):
    shortcuts = list(start.glob("*.lnk"))
    if {path.name for path in shortcuts} != {
            "CoD2 Unbound.lnk", "Dedicated Server Guide.lnk"}:
        raise ValueError("Expected a launcher shortcut and a dedicated server guide")
    for shortcut in shortcuts:
        verify_shortcut_working_directory(shortcut, r"C:\windows\system32")
    binaries = [path for path in binary_files(root) if path != uninstaller]
    signer.verify_catalog(root / LAUNCHER_CATALOG, binaries, root)
    for path in binaries:
        signer.verify(path)

    for key in ("CoD2Unbound.Demo", "CoD2Unbound.Config", "cod2x", "cod2unbound"):
        command = wine(env, "reg", "query", f"{USER_CLASSES}\\{key}\\shell\\open\\command",
                       "/ve", "/reg:64").stdout
        if f'\\launcher\\{LAUNCHER_NAME}" "%1"' not in command:
            raise ValueError(f"{key} does not open the installed launcher")
    wine(env, "reg", "query", f"{USER_CLASSES}\\cod2unbound", "/v", "URL Protocol", "/reg:64")
    for ext, handler in ((".cfg", "CoD2Unbound.Config"), (".dm_1", "CoD2Unbound.Demo")):
        wine(env, "reg", "query", f"{USER_CLASSES}\\{ext}\\OpenWithProgids", "/v", handler,
             "/reg:64")
    print(f"PASS launcher shortcut, associations, signed payload catalog and {len(binaries)} PEs",
          flush=True)


def verify_installer(artifacts, work, previous, run):
    setup = next(artifacts.glob("*-setup.exe"))
    old_version = previous.name.split("-")[2]
    version = setup.name.split("-")[2]
    helper = run / "check-key.exe"
    subprocess.run(["x86_64-w64-mingw32-gcc", "-DCOD2_WINDOWS_NATIVE=1", "-I/src/src",
                    "-I/src/src/headers", WINDOWS / "path-test-key.c", "/src/src/win32/win_key.c",
                    "-ladvapi32", "-o", helper], check=True)
    holder = run / "hold-install-directory.exe"
    subprocess.run(["x86_64-w64-mingw32-gcc", "-municode", WINDOWS / "package/check-folder-lock.c",
                    "-o", holder], check=True)
    signer = Signer(None, None, work / "test-code.pem", work / "test-tsa.pem",
                    run / "signature-verification.log")
    signer.verify(setup)

    with helpers["private_wine"](ENV, run / "installer-wine") as env:
        wine(env, "winecfg.exe", "-v", "win11")
        local = helpers["wine_local_app_data"](env)
        root = local / "Programs/CoD2 Unbound"
        user_start = local.parent / "Roaming/Microsoft/Windows/Start Menu/Programs/CoD2 Unbound"
        data = local / "CoD2 Unbound"
        retail = run / "Retail Data Control"
        retail.mkdir()
        (retail / "retail.txt").write_text("licensed data sentinel")
        wine(env, "reg", "add", RETAIL, "/reg:32", "/v", "InstallPath",
             "/d", windows_path(retail), "/f")
        wine(env, helper)
        preserved = [
            data / "main/players/test/config_mp.cfg",
            data / "main/screenshots/test.jpg",
            data / "main/demos/test.dm_1",
            retail / "retail.txt",
        ]
        for path in preserved[:-1]:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f"User-owned {path.name}\n")
        before = {path: sha256(path) for path in preserved}

        install(env, previous, run / "previous.log")
        assert_version(env, root, "HKCU", old_version)
        old_launcher = root / "launcher" / LAUNCHER_NAME
        old_shortcuts = 2 if old_launcher.is_file() else 3
        if len(list(user_start.glob("*.lnk"))) != old_shortcuts:
            raise ValueError("Missing per-user Start menu entries")

        # Wine 9's Windows 10 preset reports 19043. Keep the upgrade checks on
        # its supported Windows 11 preset and check 22H2 on native Windows.

        existing_scheme = USER_CLASSES + r"\cod2\shell\open\command"
        wine(env, "reg", "add", existing_scheme, "/ve", "/d", "Third-party link handler",
             "/f", "/reg:64")
        status, receipt, refusals = install_with_folder_lock(env, setup, root, holder, run / "persistent-lock")
        if status == 0 and not refusals and "Completed the CoD2 Unbound installation." in receipt:
            print("SKIP Wine allowed the rename with an open directory handle; use the Windows lock checks", flush=True)
        else:
            expired = re.search(r"MoveFileExW retry window expired after (\d+) ms", receipt)
            if (status == 0 or len(refusals) < 2 or not expired or int(expired[1]) < 15000
                    or "Close CoD2 Unbound and any programs using its installation folder" not in receipt):
                raise ValueError("Setup did not exhaust its rename retry window and report the previous-install failure")
            assert_version(env, root, "HKCU", old_version)
            if root.with_name(f"{root.name}.previous-install").exists():
                raise ValueError("The failed rename left a previous-install tree")
            print(f"PASS persistent directory lock left {old_version} intact after {expired[1]} ms", flush=True)

            status, receipt, refusals = install_with_folder_lock(env, setup, root, holder, run / "passing-lock",
                                                                 release_after=2)
            moved = re.search(r"MoveFileExW succeeded .* after (\d+) ms", receipt)
            if (status != 0 or len(refusals) < 2 or not moved or int(moved[1]) < 2000
                    or "Completed the CoD2 Unbound installation." not in receipt):
                raise ValueError("Setup did not complete after the directory lock was released")
            print(f"PASS passing directory lock cleared after {moved[1]} ms, {len(refusals)} refusals", flush=True)
        assert_version(env, root, "HKCU", version)
        if ARM_NOTICE in receipt:
            raise ValueError("Setup showed the Arm warning on x64")
        if ARM_NOTICE not in (root / "README-server.txt").read_text(encoding="utf-8"):
            raise ValueError("The installed Windows README is missing the Arm notice")
        print("PASS x64 Setup omits the Arm page and the installed README includes the notice", flush=True)
        if len(list(root.glob("unins*.exe"))) != 1:
            raise ValueError("Upgrade did not reuse the single installed uninstaller")
        uninstaller = next(root.glob("unins*.exe"))
        signer.verify(uninstaller)
        verify_launcher(env, root, user_start, uninstaller, signer)
        if "Third-party link handler" not in wine(env, "reg", "query", existing_scheme, "/ve").stdout:
            raise ValueError("Upgrade replaced an existing link handler")

        run_server(root / "cod2x" / EXE_NAMES[1], root / "server.cfg", "cod2x", env,
                   run / "installed-server")
        removed = root / "launcher" / "CoD2Unbound.Windows.pri"
        expected_hash = sha256(removed)
        removed.unlink()
        install(env, setup, run / "repair.log", "/CURRENTUSER", "/TASKS=links")
        assert_version(env, root, "HKCU", version)
        if sha256(removed) != expected_hash:
            raise ValueError("Repair did not restore the signed file")

        extra = root / "user-file.txt"
        extra.write_text("keep")
        wine(env, uninstaller, *SILENT, f"/LOG={windows_path(run / 'uninstall.log')}")
        subprocess.run(["wineserver", "-w"], env=env, check=True, timeout=45)
        if files(root) != [extra] or list(user_start.glob("*.lnk")):
            raise ValueError("Uninstall left installed files or removed the user's own file")
        if not wine(env, "reg", "query", "HKCU\\" + UNINSTALL, check=False).returncode:
            raise ValueError("Uninstall key still exists")
        for key in ("CoD2Unbound.Demo", "CoD2Unbound.Config", "cod2x", "cod2unbound"):
            if not wine(env, "reg", "query", USER_CLASSES + "\\" + key, "/reg:64",
                        check=False).returncode:
                raise ValueError(f"Uninstall left the {key} association")
        if "Third-party link handler" not in wine(env, "reg", "query", existing_scheme, "/ve").stdout:
            raise ValueError("Uninstall removed an existing link handler")
        retail_value = wine(env, "reg", "query", RETAIL, "/reg:32", "/v", "InstallPath").stdout
        if windows_path(retail) not in retail_value or before != {p: sha256(p) for p in preserved}:
            raise ValueError("An installer operation changed retail or player files")
        print(wine(env, helper, "verify").stdout, end="")
        print("PASS default install, HKCU and Start menu, in-place upgrade, repair, uninstall "
              "and user files preserved", flush=True)


def main(artifacts, work, previous, run):
    verify_files(artifacts)
    verify_portable(artifacts, work, run)
    verify_installer(artifacts, work, previous, run)


if __name__ == "__main__":
    main(*map(Path, sys.argv[1:]))
