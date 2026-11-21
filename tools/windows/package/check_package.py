"""Exercise the portable servers and the Inno Setup lifecycle in offline Wine."""

import os
from pathlib import Path
import runpy
import shutil
import socket
import subprocess
import sys
import zipfile

from package_installer import windows_path
from release import EXE_NAMES, WINDOWS, files, sha256
from signing import Signer


helpers = runpy.run_path(str(WINDOWS / "check-servers.py"))
ENV = dict(os.environ, WINEARCH="win64", WINEDEBUG="-all", WINEDLLOVERRIDES="mscoree,mshtml=",
           COD2_WINE_HEADLESS_CHECK="1", COD2_CRASH_NOPOPUP="1",
           COD2X_TEST_SERVICE_URL="http://127.0.0.1:9",
           COD2X_TEST_UPDATER_ADDRESS="127.0.0.1:9",
           COD2X_TEST_UPDATER_URL="http://127.0.0.1:9/api/updater/check")
UNINSTALL = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\CoD2Unbound_is1"
RETAIL = r"HKLM\Software\Activision\Call of Duty 2"
SILENT = ("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART")


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
    args = [WINDOWS / "wine-binary.sh", "+set", "fs_basepath", "/data",
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
                    helpers["query_server"](sock, process, b"getstatus", b"statusResponse",
                                            b"g_gametype", b"118" if edition == "original" else b"120")
                if process.wait(timeout=90):
                    raise ValueError(f"Server exited with {process.returncode}")
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait()
    if "execing server.cfg" not in (run / "console.log").read_text(errors="replace"):
        raise ValueError("The dedicated server did not read the sample config")
    print(f"PASS {edition} started from {exe.parent} and exited cleanly", flush=True)


def verify_portable(artifacts, work, sample):
    archive = next(artifacts.glob("*-server.zip"))
    signer = Signer(None, None, work / "test-code.pem", work / "test-tsa.pem",
                    sample / "signature-verification.log")
    signer.verify(archive.with_suffix(".cat"), archive)
    tampered = sample / "tampered.zip"
    shutil.copyfile(archive, tampered)
    with tampered.open("ab") as stream:
        stream.write(b"changed byte sequence")
    try:
        signer.verify(archive.with_suffix(".cat"), tampered)
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
    verify_files(expanded)
    for edition in ("original", "cod2x"):
        exe = expanded / edition / EXE_NAMES[1]
        signer.verify(exe)
        with helpers["private_wine"](ENV, sample / f"{edition}-wine") as env:
            run_server(exe, expanded / "server.cfg", edition, env, sample / edition)


def install(env, installer, log, *args):
    result = wine(env, installer, *SILENT, "/SP-", f"/LOG={windows_path(log)}", *args)
    print(f"{installer.name} with {' '.join(args) or 'default scope'} exited {result.returncode}",
          flush=True)


def assert_version(env, root, scope, version):
    key = scope + "\\" + UNINSTALL
    result = wine(env, "reg", "query", key, "/v", "DisplayVersion", "/reg:64")
    if result.stdout.split()[-1] != version or (root / "RELEASE_VERSION").read_text().strip() != version:
        raise ValueError(f"Expected {version} in both installed version locations")
    verify_files(root)


def verify_installer(artifacts, work, previous, run):
    setup = next(artifacts.glob("*-setup.exe"))
    old_version = previous.name.split("-")[2]
    version = setup.name.split("-")[2]
    helper = run / "check-key.exe"
    subprocess.run(["x86_64-w64-mingw32-gcc", "-I/src/src", WINDOWS / "path-test-key.c",
                    "/src/src/win32/win_key.c", "-ladvapi32", "-o", helper], check=True)
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
        if len(list(user_start.glob("*.lnk"))) != 3:
            raise ValueError("Missing per-user Start menu entries")

        install(env, setup, run / "upgrade.log", "/CURRENTUSER")
        assert_version(env, root, "HKCU", version)
        if len(list(root.glob("unins*.exe"))) != 1:
            raise ValueError("Upgrade did not reuse the single installed uninstaller")
        uninstaller = next(root.glob("unins*.exe"))
        signer.verify(uninstaller)
        for ext in ("cod2", "cod2x", "CoD2Unbound.Demo", "CoD2Unbound.Config"):
            if not wine(env, "reg", "query", "HKCR\\" + ext, check=False).returncode:
                raise ValueError(f"Unexpected association {ext} without the launcher")

        run_server(root / "cod2x" / EXE_NAMES[1], root / "server.cfg", "cod2x", env,
                   run / "installed-server")
        removed = root / "cod2x" / EXE_NAMES[1]
        expected_hash = sha256(removed)
        removed.unlink()
        install(env, setup, run / "repair.log", "/CURRENTUSER")
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
