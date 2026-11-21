"""Build and sign the installer and dedicated-server archive in Docker."""

from contextlib import contextmanager, nullcontext
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from catalog import make_catalog
from release import WINDOWS, check_pe, check_version, files, server_zip, sha256, write_checksums
from signing import Signer, azure_options, test_authority


HERE = WINDOWS / "package"
INNO_VERSION = "7.1.1"
INNO_URL = (
    "https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe"
)
INNO_SHA256 = "8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0"
JSIGN_URL = "https://github.com/ebourg/jsign/releases/download/7.6/jsign-7.6.jar"
JSIGN_SHA256 = "6cf58aa080e7ff82656f320cff8d4a3bff30408df8766806dcba3740dd839fa5"
ROOT_URL = (
    "https://www.microsoft.com/pkiops/certs/"
    "microsoft%20identity%20verification%20root%20certificate%20authority%202020.crt"
)
ROOT_SHA256 = "5367f20c7ade0e2bca790915056d086b720c33c1fa2a2661acf787e3292e1270"


def fetch(url, digest, path):
    subprocess.run(["curl", "--fail", "--silent", "--show-error", "--location",
                    "--connect-timeout", "15", "--max-time", "120", "-o", path, url], check=True)
    if sha256(path) != digest:
        path.unlink()
        raise ValueError(f"Unexpected SHA-256 of {path.name}")


def windows_path(path):
    return "Z:" + str(path).replace("/", "\\")


@contextmanager
def compiler_env(work):
    with tempfile.TemporaryDirectory(prefix="wine-compiler-", dir=work) as location:
        p = Path(location)
        home = p / "home"
        home.mkdir()
        env = {k: v for k, v in os.environ.items() if not k.startswith("COD2_SIGN_")}
        env.update(HOME=str(home), WINEPREFIX=str(p / "prefix"), WINEARCH="win64", WINEDEBUG="-all",
                   WINEDLLOVERRIDES="mscoree,mshtml=")
        try:
            yield env
        finally:
            subprocess.run(["wineserver", "-k"], env=env, timeout=15)
            subprocess.run(["wineserver", "-w"], env=env, timeout=30, check=True)


def compile_setup(tree, artifacts, work, version, company, signer):
    script = work / "inno-script"
    script.mkdir()
    uninstallers = work / "uninstallers"
    uninstallers.mkdir()
    fetch(INNO_URL, INNO_SHA256, work / "inno.exe")
    shutil.copyfile(HERE / "unbound.iss", script / "unbound.iss")
    defines = {
        "Payload": windows_path(tree),
        "Artifacts": windows_path(artifacts),
        "Uninstallers": windows_path(uninstallers),
        "ReleaseVersion": version,
        "Company": company,
    }
    (script / "release.iss").write_text(
        "".join(f'#define {name} "{value}"\n' for name, value in defines.items())
    )

    with compiler_env(work) as env:
        def wine(command, label):
            with (work / f"{label}.log").open("w") as log:
                return subprocess.run(["wine", *command], env=env, timeout=600,
                                      stdout=log, stderr=subprocess.STDOUT)

        wine([work / "inno.exe", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
              "/SP-", "/DIR=C:\\Inno"], "inno-bootstrap").check_returncode()
        iscc = r"C:\Inno\ISCC.exe"
        version_output = subprocess.check_output(["wine", iscc, "--version"], env=env,
                                                 text=True, timeout=30).strip()
        if version_output != INNO_VERSION:
            raise ValueError(f"Expected Inno Setup {INNO_VERSION}, got {version_output}")

        command = [iscc, windows_path(script / "unbound.iss")]
        first = wine(command, "inno-unsigned-uninstaller")
        unsigned = files(uninstallers)
        # This documented compiler pass emits the unsigned uninstaller and exits with an error.
        if first.returncode != 2 or len(unsigned) != 1:
            raise ValueError(f"Expected Inno to emit an unsigned uninstaller, see {work}")
        uninstaller = unsigned[0]
        signer.sign([uninstaller])
        wine(command, "inno-signed-uninstaller").check_returncode()

    installer = artifacts / f"CoD2-Unbound-{version}-windows-x64-setup.exe"
    check_pe(uninstaller, HERE / "inno-uninstall-imports.txt")
    check_pe(installer, HERE / "inno-setup-imports.txt")
    for pe in (uninstaller, installer):
        check_version(pe, version, company)
    signer.sign([installer])


def release(mode, tree, work, version, company):
    fetch(JSIGN_URL, JSIGN_SHA256, work / "jsign.jar")
    if mode == "test":
        options = test_authority(work)
    else:
        fetch(ROOT_URL, ROOT_SHA256, work / "azure-root.crt")
        options = nullcontext(azure_options(work, work / "azure-root.crt"))
    with options as (arguments, code_root, tsa_root):
        signer = Signer(work / "jsign.jar", arguments, code_root, tsa_root,
                        work / "signature-verification.log")
        signed_tree = tree.with_name("signed-layout")
        shutil.copytree(tree, signed_tree)
        signer.sign([path for path in files(signed_tree) if path.suffix.lower() in (".exe", ".dll")])
        write_checksums(signed_tree, [p for p in files(signed_tree) if p.name != "SHA256SUMS"])
        artifacts = work.parent / "artifacts"
        artifacts.mkdir()
        compile_setup(signed_tree, artifacts, work, version, company, signer)
        archive = artifacts / f"CoD2-Unbound-{version}-windows-x64-server.zip"
        server_zip(signed_tree, archive)
        catalog = archive.with_suffix(".cat")
        make_catalog(archive, catalog, work)
        signer.sign([catalog])
        signer.verify(catalog, archive)
        write_checksums(artifacts, files(artifacts))
