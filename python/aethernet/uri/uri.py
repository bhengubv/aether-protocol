# SPDX-License-Identifier: MIT

"""Aether URI — the canonical addressing format for resources on the Aether mesh.

Grammar (ABNF, RFC 5234)
------------------------
::

    aether-uri   = "aether://" authority [ "/" path ] [ "?" query ] [ "#" fragment ]

    authority    = aether-tag / uhid
    aether-tag   = 5(crockford) [ "-" ] 5(crockford)        ; case-insensitive
    uhid         = 64(HEXDIG)                                ; SHA-256 hex of public key

    path         = path-segment *( "/" path-segment )
    path-segment = 1*( unreserved / pct-encoded / sub-delims / ":" / "@" )

    query        = query-param *( "&" query-param )
    query-param  = key [ "=" value ]
    key          = 1*( unreserved / pct-encoded )
    value        = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )

    fragment     = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )

    crockford    = %x30-39 / %x41-48 / %x4A / %x4B / %x4D / %x4E
                 / %x50-54 / %x56-5A
                 ; 0-9 A-H J K M N P-T V-Z (no I L O U)

    unreserved   = ALPHA / DIGIT / "-" / "." / "_" / "~"
    pct-encoded  = "%" HEXDIG HEXDIG
    sub-delims   = "!" / "$" / "&" / "'" / "(" / ")" / "*" / "+" / "," / ";" / "="

The scheme is always ``aether``. Case-insensitive on parse; emitted lower-case.
The authority is the destination — either an :class:`AetherNetTag` (10 Crockford
characters, dash optional) or a UHID (64 hex characters). Case-insensitive on
parse; canonicalised to upper-case on emit.

The path is opaque to the protocol — it names a handler within the destination
(``/profile``, ``/content/<hash>``, ``/watch/<id>/join``). Segments are
case-sensitive. Consecutive slashes are illegal.

Query keys are case-insensitive; values are case-sensitive. An empty value is
permitted: ``?flag`` is equivalent to ``?flag=``.

The fragment is a client-side hint and is never transmitted over the wire.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Iterator, Mapping, Optional

from aethernet.identity import AetherNetTag


SCHEME: str = "aether"
_SCHEME_PREFIX: str = "aether://"


# ---------------------------------------------------------------------------
# Case-insensitive query map
# ---------------------------------------------------------------------------


class _CaseInsensitiveQuery(Mapping[str, str]):
    """Read-only mapping with case-insensitive key lookup.

    Iteration order is preserved (matching the C# ``Dictionary`` insertion
    order used to emit the canonical form). Keys returned from iteration are
    the lower-cased keys as stored.
    """

    __slots__ = ("_data",)

    def __init__(self, data: Optional[Mapping[str, str]] = None) -> None:
        if data is None:
            self._data: dict[str, str] = {}
        else:
            # Lower-case keys on insert, preserving insertion order.
            self._data = {k.lower(): v for k, v in data.items()}

    def __getitem__(self, key: str) -> str:
        return self._data[key.lower()]

    def __iter__(self) -> Iterator[str]:
        return iter(self._data)

    def __len__(self) -> int:
        return len(self._data)

    def __contains__(self, key: object) -> bool:
        return isinstance(key, str) and key.lower() in self._data

    def __eq__(self, other: object) -> bool:
        if isinstance(other, _CaseInsensitiveQuery):
            return self._data == other._data
        if isinstance(other, Mapping):
            # Compare against any mapping by lower-casing its keys.
            try:
                normalised = {k.lower(): v for k, v in other.items()}
            except AttributeError:
                return NotImplemented
            return self._data == normalised
        return NotImplemented

    def __hash__(self) -> int:
        return hash(frozenset(self._data.items()))

    def __repr__(self) -> str:
        return f"{type(self).__name__}({self._data!r})"


# ---------------------------------------------------------------------------
# Exceptions
# ---------------------------------------------------------------------------


class AetherURIError(ValueError):
    """Raised when an ``aether://`` URI fails to parse, build, or dispatch."""


# ---------------------------------------------------------------------------
# Encoder character tables — MUST match the C# reference EncodeKind table.
# ---------------------------------------------------------------------------


def _is_unreserved(c: str) -> bool:
    """RFC 3986 unreserved set: ALPHA / DIGIT / "-" / "." / "_" / "~"."""
    return c.isascii() and (c.isalnum() or c in "-._~")


def _is_sub_delim(c: str) -> bool:
    """RFC 3986 sub-delims: ! $ & ' ( ) * + , ; ="""
    return c in "!$&'()*+,;="


def _is_hex(c: str) -> bool:
    return c in "0123456789abcdefABCDEF"


# Per-kind allowed-unencoded predicates (mirrors the C# EncodeKind table).
def _allowed_path_segment(c: str) -> bool:
    # pchar = unreserved / pct-encoded / sub-delims / ":" / "@"
    return _is_unreserved(c) or _is_sub_delim(c) or c in (":", "@")


def _allowed_query_key(c: str) -> bool:
    # Encode '&' and '=' in keys; allow ':' '@' and the non-colliding sub-delims.
    if _is_unreserved(c):
        return True
    return c in (":", "@", "!", "$", "'", "(", ")", "*", "+", ",", ";")


def _allowed_query_value(c: str) -> bool:
    # Allow sub-delims except '&' (separator); '=' is fine inside a value.
    if _is_unreserved(c):
        return True
    return c in (
        ":", "@", "/", "?",
        "!", "$", "'", "(", ")", "*", "+", ",", ";", "=",
    )


def _allowed_fragment(c: str) -> bool:
    # fragment = *( pchar / "/" / "?" )  ; pchar incl. ':' '@' sub-delims
    return _is_unreserved(c) or _is_sub_delim(c) or c in (":", "@", "/", "?")


# ---------------------------------------------------------------------------
# Percent encode / decode
# ---------------------------------------------------------------------------


def _percent_encode(value: str, allowed: "callable[[str], bool]") -> str:
    """Percent-encode *value* using the given allowed-character predicate.

    Characters that fail the predicate are encoded as the upper-case
    percent-escapes of their UTF-8 bytes.
    """
    out: list[str] = []
    for ch in value:
        if allowed(ch):
            out.append(ch)
            continue
        for byte in ch.encode("utf-8"):
            out.append(f"%{byte:02X}")
    return "".join(out)


def _percent_decode(value: str) -> str:
    """Decode percent-escapes in *value*, treating decoded bytes as UTF-8."""
    if "%" not in value:
        return value

    buf = bytearray()
    i = 0
    n = len(value)
    while i < n:
        c = value[i]
        if c == "%" and i + 2 < n and _is_hex(value[i + 1]) and _is_hex(value[i + 2]):
            buf.append(int(value[i + 1:i + 3], 16))
            i += 3
        else:
            buf.extend(c.encode("utf-8"))
            i += 1
    return buf.decode("utf-8")


def _validate_path(path: str) -> Optional[str]:
    """Return None if *path* is valid; else an error message.

    Walks each segment, allowing unreserved + pct-encoded + sub-delims + ':' + '@'.
    Empty segments (consecutive slashes) are rejected. Malformed percent-encodings
    are rejected.
    """
    if not path:
        return None

    for segment in path.split("/"):
        if len(segment) == 0:
            return "Empty path segment (consecutive slashes)."
        i = 0
        while i < len(segment):
            c = segment[i]
            if _is_unreserved(c) or _is_sub_delim(c) or c in (":", "@"):
                i += 1
                continue
            if c == "%":
                if i + 2 >= len(segment) or not _is_hex(segment[i + 1]) or not _is_hex(segment[i + 2]):
                    return f"Malformed percent-encoding at position {i} of segment {segment!r}."
                i += 3
                continue
            return f"Illegal character {c!r} in path segment {segment!r}."
    return None


def _percent_decode_path(path: str) -> str:
    """Decode percent-escapes per segment, preserving '/' separators."""
    if not path:
        return path
    return "/".join(_percent_decode(seg) for seg in path.split("/"))


# ---------------------------------------------------------------------------
# Authority canonicalisation
# ---------------------------------------------------------------------------


def _canonicalise_authority(raw: str) -> tuple[Optional[str], Optional[str]]:
    """Canonicalise an authority component to upper-case.

    Returns ``(value, None)`` on success or ``(None, error)`` on failure.
    """
    # UHID: 64 hex chars.
    if len(raw) == 64 and all(_is_hex(c) for c in raw):
        return raw.upper(), None

    # AetherTag: 10 Crockford chars with optional dash.
    tag = AetherNetTag.try_parse(raw)
    if tag is not None:
        return tag.value, None

    return None, f"Authority {raw!r} is neither a valid AetherTag nor a 64-char hex UHID."


# ---------------------------------------------------------------------------
# AetherURI value type
# ---------------------------------------------------------------------------


def _empty_query() -> "_CaseInsensitiveQuery":
    return _CaseInsensitiveQuery()


@dataclass(frozen=True)
class AetherURI:
    """The canonical, immutable representation of an ``aether://`` URI.

    Attributes
    ----------
    authority:
        The destination — an AetherTag (``"XXXXX-XXXXX"`` upper-case) or a 64-char
        upper-case UHID. Always canonicalised.
    path:
        The handler path, **without** the leading slash. Empty string means root.
        Percent-escapes have been decoded.
    query:
        Decoded query parameters. Keys are stored lower-case for case-insensitive
        lookup; the ``Mapping`` is read-only.
    fragment:
        The fragment, **without** the leading ``#``. Empty string if none.
        Percent-escapes have been decoded.
    """

    authority: str
    path: str = ""
    query: Mapping[str, str] = field(default_factory=_empty_query)
    fragment: str = ""

    def __post_init__(self) -> None:
        # Normalise the query to a case-insensitive view, preserving order.
        if not isinstance(self.query, _CaseInsensitiveQuery):
            object.__setattr__(self, "query", _CaseInsensitiveQuery(self.query))

    # ------------------------------------------------------------------
    # Derived views
    # ------------------------------------------------------------------

    @property
    def handler_name(self) -> str:
        """First path segment, or empty string for root."""
        if not self.path:
            return ""
        slash = self.path.find("/")
        return self.path if slash < 0 else self.path[:slash]

    @property
    def path_segments(self) -> list[str]:
        """The path split into already-decoded segments. Empty list for root."""
        if not self.path:
            return []
        return self.path.split("/")

    # ------------------------------------------------------------------
    # Canonical string form
    # ------------------------------------------------------------------

    def canonical(self) -> str:
        """Return the canonical RFC-safe string form of this URI."""
        if not self.authority:
            return ""

        parts: list[str] = [_SCHEME_PREFIX, self.authority]
        if self.path:
            parts.append("/")
            # Re-encode each segment.
            first = True
            for segment in self.path.split("/"):
                if not first:
                    parts.append("/")
                first = False
                parts.append(_percent_encode(segment, _allowed_path_segment))
        if self.query:
            parts.append("?")
            first = True
            for key, value in self.query.items():
                if not first:
                    parts.append("&")
                first = False
                parts.append(_percent_encode(key, _allowed_query_key))
                if value:
                    parts.append("=")
                    parts.append(_percent_encode(value, _allowed_query_value))
        if self.fragment:
            parts.append("#")
            parts.append(_percent_encode(self.fragment, _allowed_fragment))
        return "".join(parts)

    def __str__(self) -> str:
        return self.canonical()

    def __repr__(self) -> str:
        return f"AetherURI({self.canonical()!r})"

    # ------------------------------------------------------------------
    # Equality — query order-insensitive, key case-insensitive (already
    # canonicalised by the parser).
    # ------------------------------------------------------------------

    def __eq__(self, other: object) -> bool:
        if not isinstance(other, AetherURI):
            return NotImplemented
        if self.authority != other.authority:
            return False
        if self.path != other.path:
            return False
        if self.fragment != other.fragment:
            return False
        if len(self.query) != len(other.query):
            return False
        for k, v in self.query.items():
            if other.query.get(k) != v:
                return False
        return True

    def __hash__(self) -> int:
        # Hash is order-insensitive across the query dict.
        return hash((self.authority, self.path, self.fragment, frozenset(self.query.items())))


# ---------------------------------------------------------------------------
# Parser
# ---------------------------------------------------------------------------


def parse(s: str) -> AetherURI:
    """Parse an ``aether://`` URI.

    Raises
    ------
    AetherURIError
        On any syntactic violation. Use :func:`try_parse` for a non-throwing
        alternative.
    """
    uri, error = try_parse(s)
    if uri is None:
        raise AetherURIError(error or "Invalid aether URI.")
    return uri


def try_parse(s: str) -> tuple[Optional[AetherURI], Optional[str]]:
    """Attempt to parse an ``aether://`` URI.

    Returns
    -------
    (AetherURI, None)
        On success.
    (None, error_message)
        On failure.
    """
    if s is None:
        return None, "Input is null."
    if not isinstance(s, str):
        return None, "Input is not a string."
    if len(s) == 0:
        return None, "Input is null or empty."

    if len(s) < len(_SCHEME_PREFIX) or s[: len(_SCHEME_PREFIX)].lower() != _SCHEME_PREFIX:
        return None, f"Scheme must be '{SCHEME}://'."

    rest = s[len(_SCHEME_PREFIX):]

    # 1. Fragment (only one '#' allowed; first wins, rest treated as fragment body).
    fragment = ""
    hash_idx = rest.find("#")
    if hash_idx >= 0:
        fragment = _percent_decode(rest[hash_idx + 1:])
        rest = rest[:hash_idx]

    # 2. Query.
    query: dict[str, str] = {}
    q_idx = rest.find("?")
    if q_idx >= 0:
        query_raw = rest[q_idx + 1:]
        rest = rest[:q_idx]
        for pair in query_raw.split("&"):
            if pair == "":
                continue  # tolerate trailing/duplicate '&'
            eq = pair.find("=")
            if eq >= 0:
                key_raw = pair[:eq]
                value_raw = pair[eq + 1:]
            else:
                key_raw = pair
                value_raw = ""
            decoded_key = _percent_decode(key_raw)
            if len(decoded_key) == 0:
                return None, "Empty query parameter key."
            # Lower-case key for case-insensitive lookup.
            query[decoded_key.lower()] = _percent_decode(value_raw)

    # 3. Authority + path.
    slash_idx = rest.find("/")
    if slash_idx >= 0:
        authority_raw = rest[:slash_idx]
        path_raw = rest[slash_idx + 1:]
    else:
        authority_raw = rest
        path_raw = ""

    if len(authority_raw) == 0:
        return None, "Authority is missing."

    authority, auth_err = _canonicalise_authority(authority_raw)
    if authority is None:
        return None, auth_err

    path_err = _validate_path(path_raw)
    if path_err is not None:
        return None, path_err

    decoded_path = _percent_decode_path(path_raw)
    return (
        AetherURI(
            authority=authority,
            path=decoded_path,
            query=_CaseInsensitiveQuery(query),
            fragment=fragment,
        ),
        None,
    )
