"""Generate a PE forwarder definition from the locally installed Miles DLL."""
import argparse
import re
import subprocess
from pathlib import Path


def export_table(dll):
    table = subprocess.check_output(
        ["i686-w64-mingw32-objdump", "-p", dll], text=True,
    )
    header, names = table.split("[Ordinal/Name Pointer] Table", 1)
    base = int(re.search(r"^Ordinal Base\s+(\d+)", header, re.M)[1])
    count = int(re.search(r"^\s*Export Address Table\s+([\da-f]+)$", header, re.M)[1], 16)
    exports = {base + int(index): name for index, name in re.findall(
        r"^\s*\[\s*(\d+)\]\s+([\w@?$]+)$", names.split("\n\n", 1)[0], re.M,
    )}
    if sorted(exports) != list(range(base, base + count)):
        raise ValueError(f"Can't forward every ordinal from {dll}")
    return exports


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("original", type=Path)
    parser.add_argument("--check", type=Path)
    args = parser.parse_args()
    exports = export_table(args.original)
    if args.check:
        if exports != export_table(args.check):
            raise ValueError("Proxy export names or ordinals differ from the licensed DLL")
        table = subprocess.check_output(
            ["i686-w64-mingw32-objdump", "-p", args.check], text=True,
        )
        forwards = {int(ordinal): name for ordinal, name in re.findall(
            r"\+base\[\s*(\d+)\]\s+[\da-f]+ Forwarder RVA -- miles_retail\.(\S+)", table,
        )}
        if forwards != exports:
            raise ValueError("Proxy contains missing or unexpected forwarders")
        print(f"Validated {len(exports)} exports by name, ordinal and target")
        return
    print("LIBRARY mss32\nEXPORTS")
    for ordinal, name in sorted(exports.items()):
        print(f'  "{name}" = "miles_retail.{name}" @{ordinal}')


if __name__ == "__main__":
    main()
