# SPDX-License-Identifier: MIT

"""Cross-language rendezvous parity: the Python port must reproduce the C# reference vectors
(fixtures/meeting/meeting_basic.json) byte-for-byte."""

from __future__ import annotations

import json
from pathlib import Path

from aethernet.meeting import Meeting

# fixtures/meeting/meeting_basic.json lives at the repo root: tests/ -> python/ -> repo root.
_FIXTURE = json.loads(
    (Path(__file__).resolve().parents[2] / "fixtures" / "meeting" / "meeting_basic.json").read_text(
        encoding="utf-8"
    )
)


def test_meeting_byte_parity_with_csharp_fixture() -> None:
    assert _FIXTURE["info"] == "aether-meeting-v1"
    assert _FIXTURE["length"] == 25

    for case in _FIXTURE["cases"]:
        meeting = Meeting.with_tags(case["my_tag"], case["their_tag"])
        assert meeting is not None, case["name"]

        assert meeting.rendezvous == case["rendezvous"], case["name"]
        assert meeting.i_start == case["i_start"], case["name"]
        assert meeting.uuid().bytes_le.hex() == case["uuid"], case["name"]
        assert str(meeting.uuid()) == case["uuid_string"], case["name"]
        for bits, expected in case["address"].items():
            assert meeting.address(int(bits)) == expected, f"{case['name']} @ {bits} bits"

        # Shape: a rendezvous is exactly LENGTH characters, all from the Crockford alphabet.
        assert len(meeting.rendezvous) == _FIXTURE["length"], case["name"]
        assert set(meeting.rendezvous) <= set(_FIXTURE["alphabet"]), case["name"]


def test_swapped_pair_invariant() -> None:
    """The same pair, fed either way round, meets at the same place with opposite host roles."""
    a = Meeting.with_tags("BH8CZ-B09CA", "DY5CF-84G9T")
    b = Meeting.with_tags("DY5CF-84G9T", "BH8CZ-B09CA")
    assert a is not None and b is not None
    assert a.rendezvous == b.rendezvous
    assert a.uuid() == b.uuid()
    assert a.i_start != b.i_start


def test_rejects_yield_no_meeting() -> None:
    for reject in _FIXTURE["rejects"]:
        assert Meeting.with_tags(reject["my_tag"], reject["their_tag"]) is None, reject["name"]
