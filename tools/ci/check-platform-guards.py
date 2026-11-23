#!/usr/bin/env python3
"""Reject presence tests for macros that the platform headers always define."""

from bisect import bisect_right
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
CONFIG_HEADERS = {
    "src/headers/cod2_platform.h": {"COD2_MALLOC_HEADER"},
    "src/headers/cod2_feature_config.h": {"COD2_FEATURE_CONFIG_H"},
}
DEBUG_HEADER = "src/PC/qcommon/port_debug.h"
SOURCE_SUFFIXES = {".c", ".cc", ".cpp", ".h", ".hpp", ".inc", ".m", ".mm", ".s"}
SPLICE = re.compile(r"\\\n")
COMMENT_OR_LITERAL = re.compile(r"""/\*[\s\S]*?\*/|//[^\n]*|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'""")
DIRECTIVE = re.compile(r"^[ \t]*#[ \t]*(if|elif|ifdef|ifndef|elifdef|elifndef)\b([^\n]*)", re.M)
DEFINE = re.compile(r"^[ \t]*#[ \t]*define[ \t]+(COD2_\w+)\b", re.M)
PRESENCE_TEST = re.compile(r"\bdefined\b\s*(?:\(\s*)?(COD2_\w+)\b")


def presence_tests(source, known, *, allow_defaults=False):
    # Preprocessing joins physical lines before stripping comments.
    splices = [match.start() - index * 2 for index, match in enumerate(SPLICE.finditer(source))]
    source = SPLICE.sub("", source)
    source = COMMENT_OR_LITERAL.sub(lambda match: re.sub(r"[^\n]", " ", match[0]), source)
    for directive in DIRECTIVE.finditer(source):
        operation, expression = directive.groups()
        if operation in ("if", "elif"):
            names = PRESENCE_TEST.findall(expression)
        else:
            names = expression.split()[:1]
        for name in sorted(known.intersection(names)):
            if allow_defaults and operation == "ifndef" and (
                name == "COD2_PORT_DEBUG" or re.match(
                    rf"\s*#[ \t]*define[ \t]+{name}\b", source[directive.end():]
                )
            ):
                continue
            start = directive.start()
            yield source.count("\n", 0, start) + bisect_right(splices, start) + 1, name


def main():
    known = {"COD2_PORT_DEBUG"}
    for path, optional_names in CONFIG_HEADERS.items():
        known.update(set(DEFINE.findall((ROOT / path).read_text())) - optional_names)
    failures = 0
    for path in sorted((ROOT / "src").rglob("*")):
        if path.suffix.lower() not in SOURCE_SUFFIXES or not path.is_file():
            continue
        relative = path.relative_to(ROOT).as_posix()
        for line, name in presence_tests(path.read_text(errors="replace"), known,
                                         allow_defaults=relative in CONFIG_HEADERS or relative == DEBUG_HEADER):
            print(f"{relative}:{line}: {name} is always defined. Test its value or the platform condition.")
            failures += 1
    if failures:
        print(f"{failures} invalid platform guard tests")
    return bool(failures)


if __name__ == "__main__":
    raise SystemExit(main())
