#!/usr/bin/env python3
"""Assemble a test kit from checked package and prior evidence outputs."""
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import shutil
import subprocess
import tempfile
from zipfile import ZIP_DEFLATED, ZipFile

NAME = "CoD2-Unbound-Windows-Kit"
FRAME_PYTHON_SHA256 = "9e35319bffedab8b36ad44d8f377cb4f1733bcec257a0b6d04d94f01486ddb38"
HERE = Path(__file__).resolve().parent


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def copy_file(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)


def write_manifest(root, manifest):
    target = root / manifest
    entries = sorted(p for p in root.rglob("*") if p.is_file() and p != target)
    target.write_text("".join(f"{digest(p)}  {p.relative_to(root).as_posix()}\n" for p in entries),
                      encoding="ascii")


def copy_fixtures(args, kit):
    references = kit / "frame-references"
    for edition in ("original", "cod2x"):
        for map_name in ("mp_toujane", "mp_harbor"):
            demo = args.wine / "demos" / edition / f"smoke-{map_name}.dm_1"
            copy_file(demo, references / edition / demo.name)
        for mode in ("dx9", "dx7"):
            for map_name, number in (("mp_toujane", "00"), ("mp_harbor", "00"), ("mp_harbor", "04"), ("mp_harbor", "08")):
                view = f"{map_name}-{number}"
                stem = f"{mode}-{view}"
                source_dir = args.windows_original if edition == "original" else args.windows_cod2x
                source_edition = "" if edition == "original" else "cod2x-"
                windows = source_dir / f"{stem}-{source_edition}windows.png"
                copy_file(windows, references / edition / mode / f"{stem}-windows.png")
                wine = (args.windows_original / f"{stem}-wine.png" if edition == "original"
                        else args.wine / "runs" / f"fixed-after-{edition}-{mode}" / f"{view}.png")
                copy_file(wine, references / edition / mode / f"{stem}-wine.png")


def assemble(args):
    if digest(args.frame_python) != FRAME_PYTHON_SHA256:
        raise ValueError("Expected the pinned Python 3.13.17 x64 embeddable runtime")
    with tempfile.TemporaryDirectory(prefix="kit-stage-", dir=args.output.parent) as temporary:
        kit = Path(temporary) / NAME
        # Reuse the checked, licensed-data-free evidence inputs.
        fixture_sources = {"replays", "probes"}
        if not args.wine:
            fixture_sources.add("frame-references")
        with ZipFile(args.baseline) as baseline:
            for member in baseline.namelist():
                parts = Path(member).parts
                if len(parts) < 3 or parts[0] != NAME or parts[1] not in fixture_sources:
                    continue
                if member.endswith("/") or ".." in parts or "\\" in member:
                    continue
                target = kit.joinpath(*parts[1:])
                target.parent.mkdir(parents=True, exist_ok=True)
                with baseline.open(member) as source, target.open("wb") as output:
                    shutil.copyfileobj(source, output)

        for path in HERE.iterdir():
            if path.suffix in {".ps1", ".md", ".cfg", ".cs", ".py", ".sh", ".applescript"}:
                copy_file(path, kit / path.name)
        source_root = HERE.parents[2]
        copy_file(HERE.parent / "frame-times.py", kit / "frame-times.py")
        with ZipFile(args.frame_python) as runtime:
            runtime.extractall(kit / "frame-analyser")
        copy_file(source_root / "tests/launcher/fixtures/core-parity/directory.json",
                  kit / "launcher-fixtures/directory.json")
        copy_file(args.probe, kit / "probes/media-probe.exe")
        copy_file(args.stack_probe, kit / "probes/stack-probe.exe")
        if args.wine:
            copy_fixtures(args, kit)
        for path in (args.release / "artifacts").iterdir():
            if path.name != "SHA256SUMS":
                copy_file(path, kit / "install" / path.name)
        copy_file(args.previous, kit / "install" / "previous" / args.previous.name)
        layout = args.release / "signed-layout"
        for file in layout.rglob("*"):
            rel = file.relative_to(layout)
            if file.is_file() and rel.parts[0] != "launcher" and rel.name not in {"payload.cat", "SHA256SUMS"}:
                copy_file(file, kit / "portable" / rel)
        write_manifest(kit / "portable", "SHA256SUMS")
        launcher_revision = (args.release / "work/SOURCE_REVISION").read_text().strip()
        kit_revision = subprocess.check_output(["git", "-C", str(source_root), "rev-parse", "HEAD"], text=True).strip()
        revision_time = subprocess.check_output(
            ["git", "-C", str(source_root), "show", "-s", "--format=%ct", kit_revision], text=True)
        build_date = datetime.fromtimestamp(int(revision_time), timezone.utc).date()
        (kit / "BUILD.txt").write_text(
            f"Engine source {args.engine}\nLauncher source {launcher_revision}\n"
            f"Kit scripts and guides {kit_revision}\nKit date {build_date}\n"
            f"Package version {(layout / 'RELEASE_VERSION').read_text().strip()}\n"
            "Upgrade fixture version 0.3.9. Both test packages use the same product source.\n",
            encoding="ascii",
        )
        write_manifest(kit, "SHA256SUMS.txt")
        candidate = args.output.with_name(args.output.name + ".partial")
        with ZipFile(candidate, "w", compression=ZIP_DEFLATED, compresslevel=6) as archive:
            archive.writestr(f"{NAME}/results/", "")
            for path in sorted(kit.rglob("*")):
                if path.is_file():
                    archive.write(path, f"{NAME}/{path.relative_to(kit).as_posix()}")
        with ZipFile(candidate) as archive:
            failure = archive.testzip()
            if failure:
                raise ValueError(f"ZIP CRC failed for {failure}")
            manifest = archive.read(f"{NAME}/SHA256SUMS.txt").decode("ascii").splitlines()
            for line in manifest:
                expected, relative = line.split("  ", 1)
                with archive.open(f"{NAME}/{relative}") as member:
                    if hashlib.file_digest(member, "sha256").hexdigest() != expected:
                        raise ValueError(f"ZIP manifest digest failed for {relative}")
        candidate.replace(args.output)
    print(f"{len(manifest)} entries checked. {args.output.stat().st_size} bytes. SHA-256 {digest(args.output)}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("baseline", "release", "previous", "probe", "stack-probe", "frame-python", "output"):
        parser.add_argument(f"--{argument}", required=True, type=Path)
    for argument in ("wine", "windows-original", "windows-cod2x"):
        parser.add_argument(f"--{argument}", type=Path)
    parser.add_argument("--engine", required=True)
    arguments = parser.parse_args()
    reference_overrides = (arguments.wine, arguments.windows_original, arguments.windows_cod2x)
    if any(reference_overrides) and not all(reference_overrides):
        parser.error("--wine, --windows-original and --windows-cod2x must be supplied together")
    assemble(arguments)
