#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
AetherNet stub guard — the systemic backstop behind the per-fix regression tests.

Scans the cross-language source tree for stub / placeholder / not-implemented
markers and FAILS (exit 1) if any appear in shipped source outside the explicit
ALLOWLIST below. A green run means no NEW stub slipped in; every KNOWN-but-not-
yet-resolved stub is listed in ALLOWLIST with a reason, so it is visible and
tracked — never silently hidden.

Run directly:   python3 scripts/check_no_stubs.py [--verbose]
Run as a test:  pytest python/tests/test_no_stubs.py   (imports scan() from here)
Self-test:      python3 scripts/check_no_stubs.py --selftest

When you fix a stub: delete the marker comment AND its ALLOWLIST entry. When the
allowlist is empty and the scan is clean, the protocol source is stub-free.
"""
from __future__ import annotations

import argparse
import os
import re
import sys
from collections import namedtuple

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_SELF_PATH = os.path.abspath(__file__)  # the guard defines marker strings — never scan itself

# Only these source kinds are scanned (docs/markdown are excluded — prose may
# legitimately discuss "stubs").
SOURCE_EXTS = {
    ".cs", ".go", ".py", ".rs", ".ts", ".tsx", ".js", ".jsx", ".mjs",
    ".kt", ".kts", ".swift", ".c", ".h", ".cpp", ".hpp", ".java", ".ets",
}

# Directory names never scanned: build output, dependencies, generated artifacts.
EXCLUDE_DIRS = {
    "build", "target", "node_modules", "dist", "_site", "artifacts",
    "artifacts-nuget", "bin", "obj", ".git", ".gradle", "Pods", "vendor",
    ".build", "__pycache__", "bench", "benches", "packages", ".loki",
}

# High-signal markers. Each almost always means incomplete code in a shipped
# source file. Test doubles and docs are excluded separately (is_test_or_doc).
MARKERS = [
    ("stub-returns",       re.compile(r"Stub:\s*returns", re.I)),
    ("placeholder-see",    re.compile(r"placeholder, see below", re.I)),
    ("rust-todo",          re.compile(r"\btodo!\s*\(")),
    ("rust-unimplemented", re.compile(r"\bunimplemented!\s*\(")),
    ("kotlin-todo",        re.compile(r"\bTODO\s*\(\s*\)")),
    ("py-notimpl",         re.compile(r"raise\s+NotImplementedError")),
    ("left-as-todo",       re.compile(r"left as a TODO", re.I)),
    ("not-decoded-json",   re.compile(r"not decoded from JSON", re.I)),
    ("metadata-only",      re.compile(r"surface .*metadata only", re.I)),
    ("not-impl-in-lang",   re.compile(r"not implemented in \w+ version", re.I)),
    ("sha-tbd",            re.compile(r"sha256:TBD")),
    ("fixme",              re.compile(r"\bFIXME\b")),
]

# KNOWN stubs awaiting a decision / feature build / release-time action. Each is a
# real, tracked gap that is intentionally NOT yet fixed. Format: substrings that
# must BOTH appear (path, and text on the matched line) plus the reason. Remove an
# entry the moment its stub is resolved.
Allow = namedtuple("Allow", "path_sub text_sub reason")
# Empty: the legacy P-256 verify fallback is now a REAL implementation in all 8 SDKs
# (C#/Go/Python/Rust/TS/Kotlin/Swift/C), each driven by tests/cross-language/
# p256-fixtures.json. No tracked stub remains — a non-empty scan is a true regression.
ALLOWLIST = []

Violation = namedtuple("Violation", "path line_no marker text")


def _iter_source_files(root):
    for dirpath, dirnames, filenames in os.walk(root):
        # Prune build/dep dirs AND git submodule / nested-repo boundaries: a
        # subdirectory that itself contains a .git entry is a SEPARATE repo (e.g.
        # the `circleai` submodule -> github.com/bhengubv/CircleAI) and is not this
        # repo's source to police. Stay inside aether-protocol only.
        dirnames[:] = [
            d for d in dirnames
            if d not in EXCLUDE_DIRS
            and not os.path.exists(os.path.join(dirpath, d, ".git"))
        ]
        for name in filenames:
            ext = os.path.splitext(name)[1].lower()
            if ext in SOURCE_EXTS:
                yield os.path.join(dirpath, name)


def is_test_or_doc(rel):
    """True for test files / test doubles — legitimate homes for the word 'stub'."""
    p = rel.replace("\\", "/")
    low = p.lower()
    if "/test/" in low or "/tests/" in low or low.startswith("tests/") or low.startswith("test/"):
        return True
    if "/Tests/" in p:  # Swift convention
        return True
    base = os.path.basename(p)
    if base.startswith("test_") or base.endswith("_test.go") or base.endswith(".test.ts"):
        return True
    if re.search(r"(Test|Tests|Spec)\.(kt|kts|cs|java|swift|ts)$", base):
        return True
    if base in ("fakes.py", "conftest.py"):
        return True
    return False


def _allowed(rel, line):
    norm = rel.replace("\\", "/")
    for a in ALLOWLIST:
        if a.path_sub in norm and a.text_sub in line:
            return True
    return False


def scan(root=None):
    """Return the list of NON-allowlisted stub-marker Violations in shipped source."""
    root = root or REPO_ROOT
    violations = []
    for path in _iter_source_files(root):
        rel = os.path.relpath(path, root)
        if os.path.abspath(path) == _SELF_PATH or is_test_or_doc(rel):
            continue
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as fh:
                lines = fh.readlines()
        except OSError:
            continue
        for i, line in enumerate(lines, start=1):
            for marker_name, rx in MARKERS:
                if rx.search(line):
                    if not _allowed(rel, line):
                        violations.append(Violation(rel, i, marker_name, line.rstrip("\n")))
    return violations


def _selftest():
    """Prove the scanner actually detects a stub (so the guard itself isn't a no-op)."""
    import tempfile
    failures = []
    with tempfile.TemporaryDirectory() as td:
        bad = os.path.join(td, "thing.c")
        with open(bad, "w", encoding="utf-8") as fh:
            fh.write("int now(void){\n    /* Stub: returns 0 */\n    return 0;\n}\n")
        hits = scan(td)
        if not any(v.marker == "stub-returns" for v in hits):
            failures.append("scanner FAILED to detect a 'Stub: returns 0' marker")
        # A test file with the same marker must be ignored.
        tdir = os.path.join(td, "tests")
        os.makedirs(tdir)
        with open(os.path.join(tdir, "test_thing.c"), "w", encoding="utf-8") as fh:
            fh.write("/* Stub: returns 0 */\n")
        hits2 = scan(td)
        if any("test_thing.c" in v.path for v in hits2):
            failures.append("scanner wrongly flagged a marker inside a test file")
    if failures:
        for f in failures:
            print("SELFTEST FAIL:", f)
        return 1
    print("SELFTEST OK: scanner detects stubs and ignores test files.")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description="Fail if any non-allowlisted stub marker is in shipped source.")
    ap.add_argument("--verbose", action="store_true", help="print the allowlist and scan stats")
    ap.add_argument("--selftest", action="store_true", help="verify the scanner detects stubs")
    args = ap.parse_args(argv)

    if args.selftest:
        return _selftest()

    violations = scan()

    if args.verbose:
        print(f"Allowlisted (known, tracked) stubs: {len(ALLOWLIST)}")
        for a in ALLOWLIST:
            print(f"  - {a.path_sub}  [{a.text_sub}]\n      {a.reason}")
        print()

    if violations:
        print(f"STUB GUARD FAILED — {len(violations)} non-allowlisted stub marker(s) in shipped source:")
        for v in violations:
            print(f"  {v.path}:{v.line_no}  [{v.marker}]  {v.text.strip()}")
        print("\nFix the code (preferred) or, if it is a tracked decision/feature/release item,")
        print("add an entry to ALLOWLIST in scripts/check_no_stubs.py with a reason.")
        return 1

    print(f"STUB GUARD OK — 0 new stub markers; {len(ALLOWLIST)} known stub(s) tracked in the allowlist.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
