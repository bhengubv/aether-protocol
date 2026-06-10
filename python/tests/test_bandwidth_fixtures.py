# SPDX-License-Identifier: MIT

"""Cross-language ABMF numeric-conformance driver.

Drives the Python AetherNet SDK through the shared corpus at
``tests/cross-language/bandwidth-fixtures.json``. Every AetherNet SDK drives the
SAME corpus and MUST produce identical results — this is the oracle that proves
numeric parity across all 8 language implementations.

This file is the Python mirror of the C# reference driver
``tests/AetherNet.Core.Tests/Bandwidth/BandwidthFixtureTests.cs``. Integer/string
fields are asserted EXACTLY; floating-point fields (srttMs, rttVarMs, rtPropMs,
lossRate) within ``toleranceAbs`` of the expected value.

A failure here means the Python implementation diverges from the C# reference —
the assertion message names the case, the expected value, and the actual value.
Do NOT loosen tolerances or edit the JSON to make a case pass: a divergence in the
estimator/director numeric path is a real bug to surface.

Run from the ``python/`` directory::

    python -m pytest tests/test_bandwidth_fixtures.py -v
"""

from __future__ import annotations

import json
import time
from datetime import timedelta
from pathlib import Path

import pytest

from aethernet.bandwidth import (
    BandwidthConfidence,
    BandwidthDirector,
    BandwidthEstimator,
    BandwidthGossipPayload,
    BandwidthProbeAck,
    BandwidthSample,
)


# ── Corpus loading ────────────────────────────────────────────────────────────


def _load_corpus() -> dict:
    """Walk up from this test file to find tests/cross-language/bandwidth-fixtures.json."""
    here = Path(__file__).resolve()
    for parent in here.parents:
        candidate = parent / "tests" / "cross-language" / "bandwidth-fixtures.json"
        if candidate.is_file():
            return json.loads(candidate.read_text(encoding="utf-8"))
    raise FileNotFoundError(
        f"bandwidth-fixtures.json not found walking up from {here}"
    )


_CORPUS = _load_corpus()
_TOL = float(_CORPUS["toleranceAbs"])


def _parse_confidence(s: str) -> BandwidthConfidence:
    """Map a corpus confidence string to the Python enum (``None`` → ``None_``)."""
    return {
        "None": BandwidthConfidence.None_,
        "Low": BandwidthConfidence.Low,
        "Medium": BandwidthConfidence.Medium,
        "High": BandwidthConfidence.High,
    }[s]


def _ms(td: timedelta) -> float:
    return td.total_seconds() * 1000.0


def _us(td: timedelta) -> int:
    return round(td.total_seconds() * 1_000_000)


def _ids(section: str):
    """Use the fixture ``name`` field as the pytest parametrize id."""
    return [c["name"] for c in _CORPUS[section]]


# ── probeAck ──────────────────────────────────────────────────────────────────


@pytest.mark.parametrize("f", _CORPUS["probeAck"], ids=_ids("probeAck"))
def test_probe_ack_rtt_and_owd_exact(f: dict) -> None:
    ack = BandwidthProbeAck(
        sequence=1,
        sender_send_us=f["senderSendUs"],
        receiver_receive_us=f["receiverReceiveUs"],
        receiver_send_us=f["receiverSendUs"],
        sender_receive_us=f["senderReceiveUs"],
        probe_bytes=f["probeBytes"],
    )

    rtt_us = _us(ack.rtt)
    owd_us = _us(ack.forward_owd)
    assert rtt_us == f["expectRttUs"], (
        f"probeAck[{f['name']}] rtt: expected {f['expectRttUs']} us, got {rtt_us} us"
    )
    assert owd_us == f["expectForwardOwdUs"], (
        f"probeAck[{f['name']}] forward_owd: "
        f"expected {f['expectForwardOwdUs']} us, got {owd_us} us"
    )


# ── rto ───────────────────────────────────────────────────────────────────────


@pytest.mark.parametrize("f", _CORPUS["rto"], ids=_ids("rto"))
def test_rto_clamped_matches_rfc6298(f: dict) -> None:
    sample = BandwidthSample(
        transport_name="T",
        btlbw_bps=1_000_000,
        available_bps=900_000,
        bdp_bytes=1000,
        srtt=timedelta(milliseconds=f["srttMs"]),
        rtt_var=timedelta(milliseconds=f["rttVarMs"]),
        rt_prop=timedelta(milliseconds=10),
        loss_rate=0.0,
        phy_cap_bps=0,
        confidence=BandwidthConfidence.High,
        measured_at=time.time(),
    )

    rto_ms = _ms(sample.rto)
    assert rto_ms == pytest.approx(f["expectRtoMs"], abs=0.1), (
        f"rto[{f['name']}]: expected {f['expectRtoMs']} ms, got {rto_ms} ms"
    )


# ── phyCap ────────────────────────────────────────────────────────────────────


@pytest.mark.parametrize("f", _CORPUS["phyCap"], ids=_ids("phyCap"))
def test_phy_cap_from_rssi_exact(f: dict) -> None:
    e = BandwidthEstimator("T", max_bandwidth_bps=10_000_000_000)
    e.apply_phy_hint(f["rssiDbm"])
    cap = e.current_sample().phy_cap_bps
    assert cap == f["expectCapBps"], (
        f"phyCap[{f['name']}] rssi={f['rssiDbm']}: "
        f"expected {f['expectCapBps']} bps, got {cap} bps"
    )


# ── estimator ─────────────────────────────────────────────────────────────────


@pytest.mark.parametrize("f", _CORPUS["estimator"], ids=_ids("estimator"))
def test_estimator_drives_to_expected_sample(f: dict) -> None:
    e = BandwidthEstimator(f["transport"], max_bandwidth_bps=f["maxBps"])

    for op in f["ops"]:
        kind = op["op"]
        if kind == "delivery":
            e.record_delivery(op["bytes"], op["sendUs"], op["deliverUs"])
        elif kind == "loss":
            e.record_loss(op["bytes"])
        elif kind == "phyHint":
            e.apply_phy_hint(op["rssiDbm"])
        elif kind == "gossip":
            e.warm_from_gossip(
                op["btlBwBps"],
                timedelta(milliseconds=op["rtPropMs"]),
                _parse_confidence(op["confidence"]),
            )
        else:
            raise AssertionError(f"unknown op {kind!r}")

    s = e.current_sample()
    exp = f["expect"]
    name = f["name"]

    # Integer / enum fields — exact.
    int_fields = [
        ("btlBwBps", s.btlbw_bps),
        ("effectiveBps", s.effective_bps),
        ("availableBps", s.available_bps),
        ("bdpBytes", s.bdp_bytes),
        ("phyCapBps", s.phy_cap_bps),
    ]
    for key, actual in int_fields:
        if key in exp:
            assert actual == exp[key], (
                f"estimator[{name}] {key}: expected {exp[key]}, got {actual}"
            )

    if "confidence" in exp:
        expected_conf = _parse_confidence(exp["confidence"])
        assert s.confidence is expected_conf, (
            f"estimator[{name}] confidence: "
            f"expected {expected_conf.name}, got {s.confidence.name}"
        )

    # Float fields — tolerance.
    float_fields = [
        ("srttMs", _ms(s.srtt)),
        ("rttVarMs", _ms(s.rtt_var)),
        ("rtPropMs", _ms(s.rt_prop)),
        ("lossRate", s.loss_rate),
    ]
    for key, actual in float_fields:
        if key in exp:
            assert actual == pytest.approx(exp[key], abs=_TOL), (
                f"estimator[{name}] {key}: expected {exp[key]}, got {actual}"
            )


# ── director ──────────────────────────────────────────────────────────────────


@pytest.mark.parametrize("f", _CORPUS["director"], ids=_ids("director"))
def test_director_recommends_expected_transport(f: dict) -> None:
    director = BandwidthDirector()

    # Register one estimator per declared transport. Use a generous maxBps so the
    # PHY default does not cap the gossip-seeded values.
    for t in f["register"]:
        director.register(BandwidthEstimator(t, max_bandwidth_bps=10_000_000_000))

    for g in f["gossips"]:
        director.apply_gossip(
            BandwidthGossipPayload(
                peer_uhid=g["peerUhid"],
                transport_name=g["transport"],
                btlbw_bps=g["btlBwBps"],
                rt_prop_us=g["rtPropUs"],
                confidence=_parse_confidence(g["confidence"]),
                measured_at=time.time(),
            )
        )

    rec = f["recommend"]
    result = director.recommend_transport(rec["peerUhid"], rec["payloadBytes"])

    expected = f["expectTransport"]
    if expected is None:
        assert result is None, (
            f"director[{f['name']}]: expected None, got {result!r}"
        )
    else:
        assert result == expected, (
            f"director[{f['name']}]: expected {expected!r}, got {result!r}"
        )
