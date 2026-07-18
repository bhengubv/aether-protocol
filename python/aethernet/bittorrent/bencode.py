# SPDX-License-Identifier: MIT
"""Strict BEP-3 bencoding — byte-identical to the C#/Go AetherNet references.

Decoded values: int, bytes (byte string), list, dict (bytes keys). Encoding is
canonical (dictionary keys sorted by raw unsigned byte order).
"""
from __future__ import annotations


class BencodeError(ValueError):
    pass


def decode(data: bytes):
    value, consumed = decode_n(data, 0)
    if consumed != len(data):
        raise BencodeError(f"{len(data) - consumed} trailing byte(s) after value")
    return value


def decode_n(data: bytes, pos: int):
    """Decode one value starting at pos; return (value, next_pos)."""
    if pos >= len(data):
        raise BencodeError("empty input")
    c = data[pos]
    if c == ord("i"):
        return _decode_int(data, pos)
    if c == ord("l"):
        return _decode_list(data, pos)
    if c == ord("d"):
        return _decode_dict(data, pos)
    if ord("0") <= c <= ord("9"):
        return _decode_str(data, pos)
    raise BencodeError(f"unexpected byte 0x{c:02x}")


def _decode_int(data: bytes, pos: int):
    end = data.find(b"e", pos)
    if end < 0:
        raise BencodeError("integer has no terminating 'e'")
    body = data[pos + 1:end]
    if body == b"":
        raise BencodeError("empty integer")
    if body == b"-0":
        raise BencodeError("negative zero is not allowed")
    digits = body[1:] if body[:1] == b"-" else body
    if digits == b"":
        raise BencodeError("bare minus sign")
    if len(digits) > 1 and digits[:1] == b"0":
        raise BencodeError("integer has a leading zero")
    if not digits.isdigit():
        raise BencodeError("integer has a non-digit")
    return int(body), end + 1


def _decode_str(data: bytes, pos: int):
    colon = data.find(b":", pos)
    if colon < 0:
        raise BencodeError("byte string has no ':'")
    len_str = data[pos:colon]
    if len_str == b"":
        raise BencodeError("byte string has an empty length")
    if len(len_str) > 1 and len_str[:1] == b"0":
        raise BencodeError("byte-string length has a leading zero")
    if not len_str.isdigit():
        raise BencodeError("byte-string length has a non-digit")
    n = int(len_str)
    start = colon + 1
    if start + n > len(data):
        raise BencodeError("byte string runs past end of input")
    return data[start:start + n], start + n


def _decode_list(data: bytes, pos: int):
    pos += 1
    out = []
    while True:
        if pos >= len(data):
            raise BencodeError("list has no terminating 'e'")
        if data[pos] == ord("e"):
            return out, pos + 1
        value, pos = decode_n(data, pos)
        out.append(value)


def _decode_dict(data: bytes, pos: int):
    pos += 1
    out: dict[bytes, object] = {}
    prev_key: bytes | None = None
    while True:
        if pos >= len(data):
            raise BencodeError("dictionary has no terminating 'e'")
        if data[pos] == ord("e"):
            return out, pos + 1
        key, pos = _decode_str(data, pos)
        if prev_key is not None:
            if key == prev_key:
                raise BencodeError("duplicate dictionary key")
            if key < prev_key:
                raise BencodeError("dictionary keys are not sorted")
        prev_key = key
        if pos >= len(data):
            raise BencodeError("dictionary key without a value")
        value, pos = decode_n(data, pos)
        out[key] = value


def encode(value) -> bytes:
    out = bytearray()
    _encode_into(value, out)
    return bytes(out)


def _encode_into(value, out: bytearray) -> None:
    if isinstance(value, bool):
        raise BencodeError("bool is not a bencode value")
    if isinstance(value, int):
        out += b"i" + str(value).encode() + b"e"
    elif isinstance(value, (bytes, bytearray)):
        out += str(len(value)).encode() + b":" + bytes(value)
    elif isinstance(value, str):
        b = value.encode("utf-8")
        out += str(len(b)).encode() + b":" + b
    elif isinstance(value, list):
        out += b"l"
        for item in value:
            _encode_into(item, out)
        out += b"e"
    elif isinstance(value, dict):
        out += b"d"
        items = [(_as_key(k), v) for k, v in value.items()]
        items.sort(key=lambda kv: kv[0])
        for kb, v in items:
            _encode_into(kb, out)
            _encode_into(v, out)
        out += b"e"
    else:
        raise BencodeError(f"cannot bencode {type(value).__name__}")


def _as_key(k) -> bytes:
    return k if isinstance(k, bytes) else k.encode("utf-8")
