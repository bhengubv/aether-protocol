# SPDX-License-Identifier: MIT
"""Stub guard, wired into the Python test suite.

Fails if any NON-allowlisted stub marker appears in shipped source anywhere in the
cross-language tree. The scan itself lives in scripts/check_no_stubs.py so it can
also run standalone (`python3 scripts/check_no_stubs.py`). This is the systemic
backstop behind the per-feature regression tests: a stub can never silently pass
again — a new marker fails this test, and every known-but-tracked stub is listed
in that script's ALLOWLIST with a reason.
"""
import importlib.util
import os

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.normpath(os.path.join(_HERE, "..", "..", "scripts", "check_no_stubs.py"))


def _load_guard():
    spec = importlib.util.spec_from_file_location("check_no_stubs", _SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def test_scanner_detects_stubs_selftest():
    """The guard must actually detect a stub and ignore test files — not be a no-op."""
    guard = _load_guard()
    assert guard._selftest() == 0


def test_no_unallowlisted_stub_markers():
    guard = _load_guard()
    violations = guard.scan()
    assert not violations, (
        "New stub marker(s) in shipped source — fix the code, or add a tracked entry "
        "to ALLOWLIST in scripts/check_no_stubs.py:\n"
        + "\n".join(
            f"  {v.path}:{v.line_no}  [{v.marker}]  {v.text.strip()}" for v in violations
        )
    )
