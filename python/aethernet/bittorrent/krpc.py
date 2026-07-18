# SPDX-License-Identifier: MIT
"""KRPC (BEP-5) DHT messages over bencode."""
from __future__ import annotations

from . import bencode

QUERY = "q"
RESPONSE = "r"
ERROR = "e"


def encode_query(tx: bytes, method, args: dict) -> bytes:
    m = method.encode("utf-8") if isinstance(method, str) else method
    return bencode.encode({b"t": tx, b"y": b"q", b"q": m, b"a": args})


def encode_response(tx: bytes, response: dict) -> bytes:
    return bencode.encode({b"t": tx, b"y": b"r", b"r": response})


def encode_error(tx: bytes, code: int, message) -> bytes:
    msg = message.encode("utf-8") if isinstance(message, str) else message
    return bencode.encode({b"t": tx, b"y": b"e", b"e": [code, msg]})


def decode(data: bytes) -> dict:
    d = bencode.decode(data)
    if not isinstance(d, dict):
        raise ValueError("KRPC message is not a dictionary")
    y = d.get(b"y")
    out = {"transaction_id": d.get(b"t"), "type": y.decode("utf-8") if isinstance(y, bytes) else None}
    if y == b"q":
        out["method"] = d.get(b"q", b"").decode("utf-8")
        out["arguments"] = d.get(b"a", {})
    elif y == b"r":
        out["response"] = d.get(b"r", {})
    elif y == b"e":
        e = d.get(b"e", [])
        if len(e) >= 2:
            out["error_code"] = e[0]
            out["error_message"] = e[1].decode("utf-8") if isinstance(e[1], bytes) else e[1]
    return out
