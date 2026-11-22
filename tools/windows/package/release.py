"""Stage the native Windows release from the checkout mounted at /src."""

import hashlib
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import struct
import subprocess
import sys
import zipfile


ROOT = Path("/src")
OUT = Path("/out")
WINDOWS = ROOT / "tools/windows"
EDITION_NAMES = ("original", "cod2x")
EXE_NAMES = ("cod2_win32.exe", "cod2_win32_ded.exe")
OBJ = "x86_64-w64-mingw32-"
LAUNCHER_NAME = "CoD2Unbound.Windows.exe"
LAUNCHER_CATALOG = "payload.cat"


def checked(command, log):
    with log.open("w") as stream:
        result = subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode:
        print("\n".join(log.read_text(errors="replace").splitlines()[-40:]), file=sys.stderr)
        result.check_returncode()


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def files(tree):
    return sorted(path for path in tree.rglob("*") if path.is_file())


def write_checksums(tree, paths):
    (tree / "SHA256SUMS").write_text(
        "".join(f"{sha256(path)}  {path.relative_to(tree).as_posix()}\n" for path in paths)
    )


def binary_files(tree):
    return [path for path in files(tree) if path.suffix.lower() in (".exe", ".dll")]


def stage_launcher(bundle, tree, work):
    with zipfile.ZipFile(bundle) as archive:
        entries = {}
        folded = set()
        for member in archive.infolist():
            name = member.filename
            parts = PurePosixPath(name).parts
            if (not parts or "/".join(parts) != name or any(
                    part in (".", "..", "/") or part.endswith((".", " "))
                    or re.search(r'[\\\\:<>"|?*\x00-\x1f]', part) for part in parts)
                    or stat.S_ISLNK(member.external_attr >> 16) or name.casefold() in folded):
                raise ValueError(f"Invalid or duplicate launcher member: {name!r}")
            entries[name] = member
            folded.add(name.casefold())

        required = {LAUNCHER_NAME, "CoD2Unbound.Windows.pri", "Microsoft.ui.xaml.dll",
                    "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "SOURCE_REVISION", "SHA256SUMS"}
        if required - entries.keys():
            raise ValueError(f"Missing launcher files: {sorted(required - entries.keys())}")
        hashes = {}
        for line in archive.read("SHA256SUMS").decode("ascii").splitlines():
            match = re.fullmatch(r"([a-f0-9]{64})  (.+)", line)
            if not match or match[2] in hashes:
                raise ValueError(f"Invalid launcher checksum entry: {line!r}")
            hashes[match[2]] = match[1]
        if hashes.keys() != entries.keys() - {"SHA256SUMS"}:
            raise ValueError("Launcher checksum list does not match the ZIP contents")
        for name, digest in hashes.items():
            with archive.open(name) as source:
                if name == "SOURCE_REVISION":
                    revision = source.read()
                    if (not re.fullmatch(rb"[a-f0-9]{40}\r?\n?", revision)
                            or hashlib.sha256(revision).hexdigest() != digest):
                        raise ValueError("Invalid launcher source revision")
                    (work / name).write_bytes(revision)
                    continue
                destination = tree / "launcher" / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                with destination.open("xb") as output:
                    shutil.copyfileobj(source, output)
                if sha256(destination) != digest:
                    raise ValueError(f"Launcher SHA-256 mismatch for {name}")
        (work / "launcher-SHA256SUMS").write_bytes(archive.read("SHA256SUMS"))
    launcher = tree / "launcher" / LAUNCHER_NAME
    with launcher.open("rb") as image:
        image.seek(0x3C)
        image.seek(int.from_bytes(image.read(4), "little"))
        if image.read(6) != b"PE\0\0\x64\x86":
            raise ValueError(f"{launcher}: expected an x64 PE")
    print(f"Staged launcher from {revision.decode().strip()} (ZIP SHA-256 {sha256(bundle)})",
          flush=True)


def require_public_launcher(bundle):
    with zipfile.ZipFile(bundle) as archive:
        assembly = archive.read("CoD2Unbound.Windows.dll")
    markers = (b"CoD2Unbound.UpdateFixture", b"CoD2Unbound.ReportingFixture")
    fixture_variables = (
        "COD2_UNBOUND_TEST_UPDATE_ORIGIN",
        "COD2_UNBOUND_TEST_RELEASES",
        "COD2_UNBOUND_TEST_MASTER_PORT",
        "COD2_UNBOUND_TEST_HOME",
        "COD2_UNBOUND_TEST_UPDATE_VISUAL",
        "COD2_UNBOUND_TEST_CONFIG_DIRECTORY",
        "COD2_USAGE_URL",
        "COD2_CRASH_URL",
    )
    if (any(marker in assembly for marker in markers) or
            any(name.encode("utf-16le") in assembly for name in fixture_variables)):
        raise ValueError("The production release requires a launcher without updater or reporting fixtures")
    if b"UpdatePublisherRules" not in assembly:
        raise ValueError("The production release requires an updater publisher identity")


def check_pe(exe, allowed_file):
    header = subprocess.check_output([OBJ + "objdump", "-f", exe], text=True)
    if "file format pei-x86-64\n" not in header:
        raise ValueError(f"{exe}: expected an x64 PE")
    details = subprocess.check_output([OBJ + "objdump", "-p", exe], text=True)
    imports = {name.lower() for name in re.findall(r"DLL Name: (\S+)", details)}
    allowed = set(allowed_file.read_text().splitlines())
    if not imports or not imports <= allowed:
        raise ValueError(f"{exe}: unexpected PE imports {sorted(imports - allowed)}")
    print(f"{exe.name} imports {', '.join(sorted(imports))}", flush=True)


def version_string(resources, name):
    key = (name + "\x00").encode("utf-16le")
    start = resources.find(key)
    if start < 6:
        return None
    length = struct.unpack_from("<H", resources, start - 4)[0]
    value = (start + len(key) + 3) & ~3
    return resources[value:value + length * 2].decode("utf-16le").rstrip("\x00 ")


def check_version(exe, version, company):
    image = exe.read_bytes()
    header = struct.unpack_from("<I", image, 0x3C)[0]
    count = struct.unpack_from("<H", image, header + 6)[0]
    optional_size = struct.unpack_from("<H", image, header + 20)[0]
    first = header + 24 + optional_size
    for section in range(first, first + 40 * count, 40):
        if image[section:section + 8].rstrip(b"\x00") != b".rsrc":
            continue
        size, offset = struct.unpack_from("<II", image, section + 16)
        resources = image[offset:offset + size]
        marker = b"\xbd\x04\xef\xfe\x00\x00\x01\x00"
        fixed = resources.find(marker)
        major, minor, patch = map(int, version.split("."))
        product_version = struct.pack("<II", (major << 16) | minor, patch << 16)
        if (fixed >= 0 and resources[fixed + 16:fixed + 24] == product_version
                and version_string(resources, "ProductVersion") == version
                and version_string(resources, "ProductName") == "CoD2 Unbound"
                and version_string(resources, "CompanyName") == company):
            return
    raise ValueError(f"{exe}: Windows version resource does not match {version} and {company}")


def build_engine(edition, is_client, tree, work, version, company, jobs):
    role = "client" if is_client else "server"
    config = work / f"{edition}-{role}"
    config.mkdir(parents=True)
    on_off = "ON" if is_client else "OFF"
    features = "-DCOD2_CODX=1" if edition == "cod2x" else ""
    print(f"Building {edition} {role}", flush=True)
    checked([
        "cmake", "-S", ROOT, "-B", config, "-G", "Ninja",
        "-DCMAKE_TOOLCHAIN_FILE=/src/cmake/toolchain-mingw64.cmake",
        "-DCOD2_WIN32_LEGACY=OFF", f"-DCOD2_WIN32_CLIENT={on_off}",
        f"-DCOD2_WIN32_D3D9={on_off}", f"-DCOD2_FEATURE_CFLAGS={features}",
        f"-DCOD2_GIT_HASH={os.environ['COD2_WINDOWS_COMMIT']}",
        f"-DCOD2_WINDOWS_COMPANY={company}",
        f"-DCOD2_CURL_DIR={work / 'curl'}", f"-DCOD2_SDL2_DIR={work / 'sdl2'}",
    ], config / "configure.log")
    checked(["cmake", "--build", config, "--parallel", jobs], config / "build.log")
    name = EXE_NAMES[0 if is_client else 1]
    allow = "client" if is_client else f"ded64-{edition}"
    exe = config / name
    check_pe(exe, WINDOWS / "pe-imports" / f"{allow}.txt")
    symbols = subprocess.check_output([OBJ + "nm", "--defined-only", exe], text=True)
    has_d3d9 = " Win_D3D9_Direct3DCreate9\n" in symbols
    if has_d3d9 != is_client:
        raise ValueError(f"{exe}: unexpected native D3D9 entry point")
    binary = tree / edition / name
    subprocess.run([OBJ + "strip", "--strip-all", "-o", binary, exe], check=True)
    check_version(binary, version, company)


def stage(tree, work, version, company, jobs, launcher=None):
    work.mkdir(parents=True)
    if launcher is not None:
        stage_launcher(launcher, tree, work)
    for edition in EDITION_NAMES:
        (tree / edition).mkdir(parents=True)
        for is_client in (True, False):
            build_engine(edition, is_client, tree, work, version, company, jobs)

    (tree / "RELEASE_VERSION").write_text(f"{version}\n")
    for name in ("LICENSE", "NOTICE.md", "CREDITS.md"):
        shutil.copyfile(ROOT / name, tree / name)
    shutil.copyfile(ROOT / "scripts/server/server.cfg", tree / "server.cfg")
    shutil.copyfile(WINDOWS / "package/README-server.txt", tree / "README-server.txt")
    shutil.copyfile(WINDOWS / "package/THIRD-PARTY.txt", tree / "THIRD-PARTY.txt")
    licenses = tree / "licenses"
    licenses.mkdir()
    for name, source in {
        "SDL2.txt": work / "sdl2/LICENSE.txt",
        "curl.txt": work / "curl/curl-8.14.1/COPYING",
        "minimp3.txt": ROOT / "third_party/minimp3/LICENSE",
        "mingw-w64.txt": Path("/usr/share/doc/mingw-w64-common/copyright"),
        "zlib.txt": Path("/usr/share/doc/libz-mingw-w64-dev/copyright"),
    }.items():
        shutil.copyfile(source, licenses / name)
    write_checksums(tree, files(tree))
    epoch = int(os.environ["SOURCE_DATE_EPOCH"])
    for path in [tree, *tree.rglob("*")]:
        path.chmod(0o755 if path.is_dir() else 0o644)
        os.utime(path, (epoch, epoch))


def server_zip(tree, archive):
    members = {}
    for path in files(tree):
        relative = path.relative_to(tree)
        if relative.parts[0] == "launcher" or path.name in (
                EXE_NAMES[0], "SHA256SUMS", LAUNCHER_CATALOG):
            continue
        members[relative.as_posix()] = path
    checksums = "".join(f"{sha256(path)}  {name}\n" for name, path in members.items())
    with zipfile.ZipFile(archive, "w") as bundle:
        for name in sorted([*members, "SHA256SUMS"]):
            data = checksums.encode() if name == "SHA256SUMS" else members[name].read_bytes()
            info = zipfile.ZipInfo(name)
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            bundle.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)


def main(mode):
    version = (ROOT / "RELEASE_VERSION").read_text().strip()
    if (not re.fullmatch(r"(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*)){2}", version)
            or any(int(part) > 65535 for part in version.split("."))):
        raise ValueError("RELEASE_VERSION must contain three version fields in 0..65535")
    company = os.environ["COD2_WINDOWS_COMPANY"]
    if not re.fullmatch(r"[A-Za-z0-9][ A-Za-z0-9.,&()_'-]{0,127}", company):
        raise ValueError("COD2_WINDOWS_COMPANY must be a printable publisher name")
    jobs = os.environ["COD2_WINDOWS_JOBS"]
    if not re.fullmatch(r"[1-9][0-9]*", jobs):
        raise ValueError("COD2_WINDOWS_JOBS must be a positive integer")
    os.environ["LC_ALL"] = "C.UTF-8"
    os.environ["TZ"] = "UTC"

    work = OUT / "work"
    tree = OUT / "layout"
    launcher = Path("/launcher-win-x64.zip")
    if mode != "layout" and not launcher.is_file():
        raise ValueError("A verified x64 launcher publish ZIP is required for Setup")
    if mode == "azure":
        require_public_launcher(launcher)
    stage(tree, work, version, company, jobs, launcher if launcher.is_file() else None)
    if mode != "layout":
        from package_installer import release
        release(mode, tree, work, version, company)


if __name__ == "__main__":
    main(sys.argv[1])
