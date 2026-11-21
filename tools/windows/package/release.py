"""Stage the native Windows release from the checkout mounted at /src."""

import hashlib
import os
from pathlib import Path
import re
import shutil
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


def stage(tree, work, version, company, jobs):
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
        if relative.parts[0] == "launcher" or path.name in (EXE_NAMES[0], "SHA256SUMS"):
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
    stage(tree, work, version, company, jobs)
    if mode != "layout":
        from package_installer import release
        release(mode, tree, work, version, company)


if __name__ == "__main__":
    main(sys.argv[1])
