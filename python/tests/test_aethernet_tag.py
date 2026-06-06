# SPDX-License-Identifier: MIT

"""Tests for AetherNetTag identity primitive.

Run with:
    python -m pytest tests/test_aethernet_tag.py -v
"""

from __future__ import annotations

import hashlib

import pytest

from aethernet.identity import AetherNetTag
from aethernet.identity.aethernet_tag import _ALPHABET, _encode


# ---------------------------------------------------------------------------
# Helpers / fixtures
# ---------------------------------------------------------------------------

# A deterministic 32-byte "public key" used as the known-vector anchor.
KNOWN_KEY: bytes = bytes(range(32))  # 0x00 0x01 … 0x1F

# Precompute what the tag *must* be so tests are self-consistent.
_KNOWN_RAW = _encode(KNOWN_KEY)
KNOWN_TAG_VALUE = f"{_KNOWN_RAW[:5]}-{_KNOWN_RAW[5:]}"

# A second distinct key
ALT_KEY: bytes = bytes(range(1, 33))  # 0x01 0x02 … 0x20


# ---------------------------------------------------------------------------
# 1. Known vector — fixed key → verify XXXXX-XXXXX format
# ---------------------------------------------------------------------------

class TestKnownVector:
    def test_format_is_xxxxx_dash_xxxxx(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        parts = tag.value.split("-")
        assert len(parts) == 2, "Expected exactly one '-' separator"
        assert len(parts[0]) == 5
        assert len(parts[1]) == 5

    def test_all_chars_in_crockford_alphabet(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        raw = tag.value.replace("-", "")
        for ch in raw:
            assert ch in _ALPHABET, f"Character {ch!r} not in Crockford alphabet"

    def test_known_vector_deterministic_value(self) -> None:
        """The tag for KNOWN_KEY must always equal KNOWN_TAG_VALUE."""
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        assert tag.value == KNOWN_TAG_VALUE

    def test_bit_packing_matches_spec(self) -> None:
        """Manually verify the bit-packing formula against hashlib."""
        digest = hashlib.sha256(KNOWN_KEY).digest()
        bits = (
            (digest[0] << 42)
            | (digest[1] << 34)
            | (digest[2] << 26)
            | (digest[3] << 18)
            | (digest[4] << 10)
            | (digest[5] << 2)
            | ((digest[6] >> 6) & 0x3)
        )
        expected_chars = "".join(
            _ALPHABET[(bits >> shift) & 0x1F] for shift in range(45, -5, -5)
        )
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        assert tag.value.replace("-", "") == expected_chars


# ---------------------------------------------------------------------------
# 2. Round-trip: from_public_key → str → parse → compare
# ---------------------------------------------------------------------------

class TestRoundTrip:
    def test_str_parse_roundtrip(self) -> None:
        original = AetherNetTag.from_public_key(KNOWN_KEY)
        parsed = AetherNetTag.parse(str(original))
        assert parsed == original

    def test_str_is_value(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        assert str(tag) == tag.value

    def test_parse_then_from_key_equal(self) -> None:
        tag1 = AetherNetTag.from_public_key(KNOWN_KEY)
        tag2 = AetherNetTag.parse(tag1.value)
        assert tag1 == tag2

    def test_roundtrip_alt_key(self) -> None:
        original = AetherNetTag.from_public_key(ALT_KEY)
        parsed = AetherNetTag.parse(str(original))
        assert parsed == original


# ---------------------------------------------------------------------------
# 3. verify() — correct key = True, wrong key = False
# ---------------------------------------------------------------------------

class TestVerify:
    def test_correct_key_returns_true(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        assert AetherNetTag.verify(tag.value, KNOWN_KEY) is True

    def test_wrong_key_returns_false(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        assert AetherNetTag.verify(tag.value, ALT_KEY) is False

    def test_wrong_tag_returns_false(self) -> None:
        alt_tag = AetherNetTag.from_public_key(ALT_KEY)
        assert AetherNetTag.verify(alt_tag.value, KNOWN_KEY) is False

    def test_invalid_tag_string_returns_false(self) -> None:
        assert AetherNetTag.verify("not-valid-tag!!", KNOWN_KEY) is False

    def test_verify_case_insensitive(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        assert AetherNetTag.verify(tag.value.lower(), KNOWN_KEY) is True


# ---------------------------------------------------------------------------
# 4. parse() — accepts valid forms
# ---------------------------------------------------------------------------

class TestParseAccepts:
    def test_canonical_form_with_separator(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        parsed = AetherNetTag.parse(tag.value)
        assert parsed.value == tag.value

    def test_without_separator(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        raw10 = tag.value.replace("-", "")
        parsed = AetherNetTag.parse(raw10)
        assert parsed.value == tag.value

    def test_lowercase_input(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        parsed = AetherNetTag.parse(tag.value.lower())
        assert parsed.value == tag.value  # stored canonical = uppercase

    def test_mixed_case_input(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        raw = tag.value
        # Alternate upper/lower
        mixed = "".join(c.lower() if i % 2 else c.upper() for i, c in enumerate(raw))
        parsed = AetherNetTag.parse(mixed)
        assert parsed.value == tag.value

    def test_leading_trailing_whitespace_stripped(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        parsed = AetherNetTag.parse(f"  {tag.value}  ")
        assert parsed.value == tag.value


# ---------------------------------------------------------------------------
# 5. parse() — rejects invalid input
# ---------------------------------------------------------------------------

class TestParseRejects:
    def test_empty_string(self) -> None:
        with pytest.raises(ValueError):
            AetherNetTag.parse("")

    def test_wrong_length_short(self) -> None:
        with pytest.raises(ValueError):
            AetherNetTag.parse("ABCDE")

    def test_wrong_length_long(self) -> None:
        # 11 raw chars after stripping the single separator → too long
        with pytest.raises(ValueError):
            AetherNetTag.parse("ABCDE-FGHJ2K")

    def test_invalid_char_I(self) -> None:
        # 'I' is excluded from Crockford alphabet
        with pytest.raises(ValueError):
            AetherNetTag.parse("IABCD-EFGH2")

    def test_invalid_char_L(self) -> None:
        with pytest.raises(ValueError):
            AetherNetTag.parse("LABCD-EFGH2")

    def test_invalid_char_O(self) -> None:
        with pytest.raises(ValueError):
            AetherNetTag.parse("OABCD-EFGH2")

    def test_invalid_char_U(self) -> None:
        with pytest.raises(ValueError):
            AetherNetTag.parse("UABCD-EFGH2")

    def test_invalid_special_char(self) -> None:
        with pytest.raises(ValueError):
            AetherNetTag.parse("AB@DE-FGHJ2")

    def test_none_raises(self) -> None:
        with pytest.raises((ValueError, AttributeError)):
            AetherNetTag.parse(None)  # type: ignore[arg-type]

    def test_extra_separator_wrong_length(self) -> None:
        # Two hyphens → stripped raw = 10 but wait, test the case of
        # "ABCDE--FGHJ2" which strips to 10 chars and should parse if valid
        # Actually this *is* 10 valid chars after stripping; make sure it does
        # NOT raise. We just verify the contract: strip ALL hyphens, then
        # validate length == 10 and valid chars.
        # Use genuinely wrong length instead:
        with pytest.raises(ValueError):
            AetherNetTag.parse("ABCDE-FG")


# ---------------------------------------------------------------------------
# 6. try_parse() — returns None on failure, AetherNetTag on success
# ---------------------------------------------------------------------------

class TestTryParse:
    def test_valid_returns_tag(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        result = AetherNetTag.try_parse(tag.value)
        assert result is not None
        assert result == tag

    def test_invalid_returns_none(self) -> None:
        assert AetherNetTag.try_parse("") is None
        assert AetherNetTag.try_parse("bad") is None
        assert AetherNetTag.try_parse("IABCD-EFGH2") is None

    def test_none_input_returns_none(self) -> None:
        assert AetherNetTag.try_parse(None) is None  # type: ignore[arg-type]


# ---------------------------------------------------------------------------
# 7. Different keys → different tags
# ---------------------------------------------------------------------------

class TestDistinctKeys:
    def test_different_keys_produce_different_tags(self) -> None:
        tag1 = AetherNetTag.from_public_key(KNOWN_KEY)
        tag2 = AetherNetTag.from_public_key(ALT_KEY)
        assert tag1 != tag2

    def test_many_distinct_keys_produce_distinct_tags(self) -> None:
        keys = [bytes([i] * 32) for i in range(16)]
        tags = [AetherNetTag.from_public_key(k).value for k in keys]
        assert len(set(tags)) == len(tags), "Collision among 16 distinct keys"


# ---------------------------------------------------------------------------
# 8. Determinism — same key → same tag
# ---------------------------------------------------------------------------

class TestDeterminism:
    def test_same_key_same_tag(self) -> None:
        tag1 = AetherNetTag.from_public_key(KNOWN_KEY)
        tag2 = AetherNetTag.from_public_key(KNOWN_KEY)
        assert tag1 == tag2
        assert tag1.value == tag2.value

    def test_same_key_same_hash(self) -> None:
        tag1 = AetherNetTag.from_public_key(KNOWN_KEY)
        tag2 = AetherNetTag.from_public_key(KNOWN_KEY)
        assert hash(tag1) == hash(tag2)


# ---------------------------------------------------------------------------
# 9. is_valid()
# ---------------------------------------------------------------------------

class TestIsValid:
    def test_from_public_key_is_valid(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        assert tag.is_valid() is True

    def test_parsed_tag_is_valid(self) -> None:
        tag = AetherNetTag.from_public_key(KNOWN_KEY)
        parsed = AetherNetTag.parse(tag.value)
        assert parsed.is_valid() is True

    def test_manually_constructed_valid(self) -> None:
        tag = AetherNetTag("12345-ABCDE")
        assert tag.is_valid() is True

    def test_manually_constructed_invalid_char(self) -> None:
        tag = AetherNetTag("IABCD-EFGH2")
        assert tag.is_valid() is False

    def test_manually_constructed_wrong_format(self) -> None:
        tag = AetherNetTag("ABCDE_FGHJ2")
        assert tag.is_valid() is False


# ---------------------------------------------------------------------------
# 10. from_public_key — error on wrong-length key
# ---------------------------------------------------------------------------

class TestFromPublicKeyValidation:
    def test_short_key_raises(self) -> None:
        with pytest.raises(ValueError):
            AetherNetTag.from_public_key(b"\x00" * 16)

    def test_long_key_raises(self) -> None:
        with pytest.raises(ValueError):
            AetherNetTag.from_public_key(b"\x00" * 64)

    def test_empty_key_raises(self) -> None:
        with pytest.raises(ValueError):
            AetherNetTag.from_public_key(b"")


# ---------------------------------------------------------------------------
# 11. Equality and hashing behave like value objects
# ---------------------------------------------------------------------------

class TestEqualityAndHashing:
    def test_equal_tags_have_same_hash(self) -> None:
        t1 = AetherNetTag.from_public_key(KNOWN_KEY)
        t2 = AetherNetTag.parse(t1.value)
        assert t1 == t2
        assert hash(t1) == hash(t2)

    def test_different_tags_not_equal(self) -> None:
        t1 = AetherNetTag.from_public_key(KNOWN_KEY)
        t2 = AetherNetTag.from_public_key(ALT_KEY)
        assert t1 != t2

    def test_usable_in_set(self) -> None:
        t1 = AetherNetTag.from_public_key(KNOWN_KEY)
        t2 = AetherNetTag.parse(t1.value)
        t3 = AetherNetTag.from_public_key(ALT_KEY)
        s = {t1, t2, t3}
        assert len(s) == 2  # t1 and t2 are the same tag

    def test_usable_as_dict_key(self) -> None:
        t1 = AetherNetTag.from_public_key(KNOWN_KEY)
        d = {t1: "hello"}
        t2 = AetherNetTag.parse(t1.value)
        assert d[t2] == "hello"
