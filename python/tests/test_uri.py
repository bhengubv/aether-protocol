# SPDX-License-Identifier: MIT

"""Tests for the ``aether://`` URI scheme.

Drives the Python implementation through the same cross-language JSON corpus
that every other AetherNet SDK consumes (``tests/cross-language/uri-fixtures.json``)
plus a small set of hand-written tests covering the builder and router.

Run from the python/ directory:
    python -m pytest tests/test_uri.py -v
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from aethernet.identity import AetherNetTag
from aethernet.uri import (
    AetherURI,
    AetherURIBuilder,
    AetherURIError,
    AetherURIRouter,
    DispatchContext,
    HandlerDescriptor,
    HandlerManifest,
    SCHEME,
    parse,
    try_parse,
)


# ---------------------------------------------------------------------------
# Corpus loader — walk up from this file to find tests/cross-language/uri-fixtures.json
# ---------------------------------------------------------------------------


def _load_corpus() -> dict:
    here = Path(__file__).resolve()
    for ancestor in (here.parent, *here.parents):
        candidate = ancestor / "tests" / "cross-language" / "uri-fixtures.json"
        if candidate.is_file():
            return json.loads(candidate.read_text(encoding="utf-8"))
    raise FileNotFoundError(
        "Could not locate tests/cross-language/uri-fixtures.json walking up from "
        + str(here)
    )


_CORPUS = _load_corpus()
_VALID_CASES = _CORPUS["valid"]
_INVALID_CASES = _CORPUS["invalid"]
_MANIFEST_DEF = _CORPUS["manifest"]
_MANIFEST_MATCHES = _MANIFEST_DEF["matches"]


def _valid_id(case: dict) -> str:
    return case["name"]


def _invalid_id(case: dict) -> str:
    return case["name"]


def _match_id(case: dict) -> str:
    return case["input"]


# ---------------------------------------------------------------------------
# Corpus — valid cases
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("case", _VALID_CASES, ids=[_valid_id(c) for c in _VALID_CASES])
def test_valid_fixture_parses_to_expected_components(case: dict) -> None:
    u = parse(case["input"])

    assert str(u) == case["canonical"], "canonical form mismatch"
    assert u.authority == case["authority"]
    assert u.path == case["path"]
    assert u.handler_name == case["handlerName"]
    assert u.fragment == case["fragment"]
    assert u.path_segments == list(case["pathSegments"])

    expected_query = case["query"]
    assert len(u.query) == len(expected_query)
    for key, value in expected_query.items():
        assert u.query[key] == value


@pytest.mark.parametrize("case", _VALID_CASES, ids=[_valid_id(c) for c in _VALID_CASES])
def test_valid_fixture_round_trips(case: dict) -> None:
    """Parse(s).canonical() must round-trip stably."""
    u1 = parse(case["input"])
    u2 = parse(str(u1))
    assert u1 == u2
    assert str(u1) == str(u2)


# ---------------------------------------------------------------------------
# Corpus — invalid cases
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("case", _INVALID_CASES, ids=[_invalid_id(c) for c in _INVALID_CASES])
def test_invalid_fixture_try_parse_returns_none(case: dict) -> None:
    uri, error = try_parse(case["input"])
    assert uri is None
    assert error is not None and len(error) > 0


@pytest.mark.parametrize("case", _INVALID_CASES, ids=[_invalid_id(c) for c in _INVALID_CASES])
def test_invalid_fixture_parse_raises(case: dict) -> None:
    with pytest.raises(AetherURIError):
        parse(case["input"])


# ---------------------------------------------------------------------------
# Corpus — manifest resolution
# ---------------------------------------------------------------------------


def _build_corpus_manifest() -> tuple[HandlerManifest, list[HandlerDescriptor]]:
    """Build the manifest defined in the corpus."""
    handlers = tuple(
        HandlerDescriptor(name=h["handlerName"], path_template=h["pathTemplate"])
        for h in _MANIFEST_DEF["handlers"]
    )
    manifest = HandlerManifest(app_id=_MANIFEST_DEF["appId"], handlers=handlers)
    return manifest, list(handlers)


@pytest.mark.parametrize(
    "case", _MANIFEST_MATCHES, ids=[_match_id(c) for c in _MANIFEST_MATCHES]
)
def test_manifest_fixture_resolves_as_expected(case: dict) -> None:
    manifest, handlers = _build_corpus_manifest()
    u = parse(case["input"])
    resolved = manifest.resolve(u)

    if not case["matched"]:
        assert resolved is None
        return

    assert resolved is not None
    handler, captures = resolved
    assert handler is handlers[case["handlerIndex"]]

    expected_caps = case.get("captures", {})
    assert len(captures) == len(expected_caps)
    for key, value in expected_caps.items():
        assert captures[key] == value


# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------


def test_scheme_constant_is_aether() -> None:
    assert SCHEME == "aether"


# ---------------------------------------------------------------------------
# Equality & hashing — beyond the corpus
# ---------------------------------------------------------------------------


class TestEquality:
    def test_same_content_equal(self) -> None:
        a = parse("aether://KXJB7-MN2P4/x?k=v")
        b = parse("aether://KXJB7-MN2P4/x?k=v")
        assert a == b
        assert hash(a) == hash(b)

    def test_query_order_irrelevant_for_equality(self) -> None:
        a = parse("aether://KXJB7-MN2P4/x?a=1&b=2")
        b = parse("aether://KXJB7-MN2P4/x?b=2&a=1")
        assert a == b
        assert hash(a) == hash(b)

    def test_query_key_lookup_is_case_insensitive(self) -> None:
        u = parse("aether://KXJB7-MN2P4/x?Codec=opus")
        assert u.query["codec"] == "opus"
        assert u.query["CODEC"] == "opus"
        assert "Codec" in u.query

    def test_different_authority_not_equal(self) -> None:
        a = parse("aether://KXJB7-MN2P4/x")
        b = parse("aether://KXJB7-MN2P5/x")
        assert a != b

    def test_different_path_not_equal(self) -> None:
        a = parse("aether://KXJB7-MN2P4/a")
        b = parse("aether://KXJB7-MN2P4/b")
        assert a != b

    def test_different_fragment_not_equal(self) -> None:
        a = parse("aether://KXJB7-MN2P4/x#one")
        b = parse("aether://KXJB7-MN2P4/x#two")
        assert a != b


# ---------------------------------------------------------------------------
# Builder
# ---------------------------------------------------------------------------


class TestBuilder:
    def test_authority_from_tag_succeeds(self) -> None:
        key = bytes(range(32))
        tag = AetherNetTag.from_public_key(key)
        u = (
            AetherURIBuilder()
            .authority(tag)
            .path("profile")
            .build()
        )
        assert u.authority == tag.value
        assert u.path == "profile"

    def test_fluent_chain_renders_correctly(self) -> None:
        u = (
            AetherURIBuilder()
            .authority("KXJB7-MN2P4")
            .path("content/sha256-abc")
            .query("codec", "opus")
            .fragment("t=1m30s")
            .build()
        )
        assert str(u) == "aether://KXJB7-MN2P4/content/sha256-abc?codec=opus#t=1m30s"

    def test_append_segment_builds_path(self) -> None:
        u = (
            AetherURIBuilder()
            .authority("KXJB7-MN2P4")
            .append_segment("watch")
            .append_segment("sess-99")
            .append_segment("join")
            .build()
        )
        assert u.path == "watch/sess-99/join"

    def test_remove_query_drops_key(self) -> None:
        u = (
            AetherURIBuilder()
            .authority("KXJB7-MN2P4")
            .path("x")
            .query("a", "1")
            .query("b", "2")
            .remove_query("a")
            .build()
        )
        assert "a" not in u.query
        assert u.query["b"] == "2"

    def test_strip_leading_slash_on_path(self) -> None:
        u = (
            AetherURIBuilder()
            .authority("KXJB7-MN2P4")
            .path("/profile")
            .build()
        )
        assert u.path == "profile"

    def test_strip_leading_hash_on_fragment(self) -> None:
        u = (
            AetherURIBuilder()
            .authority("KXJB7-MN2P4")
            .fragment("#anchor")
            .build()
        )
        assert u.fragment == "anchor"

    def test_missing_authority_raises_on_build(self) -> None:
        with pytest.raises(AetherURIError):
            AetherURIBuilder().path("x").build()

    def test_bad_authority_string_raises(self) -> None:
        with pytest.raises(AetherURIError):
            AetherURIBuilder().authority("not-an-id")

    def test_encodes_spaces_in_query(self) -> None:
        u = (
            AetherURIBuilder()
            .authority("KXJB7-MN2P4")
            .path("inbox")
            .query("title", "hello world")
            .build()
        )
        assert "hello%20world" in str(u)


# ---------------------------------------------------------------------------
# Router
# ---------------------------------------------------------------------------


def _sample_manifest() -> HandlerManifest:
    return HandlerManifest(
        app_id="aether.media",
        handlers=(
            HandlerDescriptor("profile", description="Get the profile."),
            HandlerDescriptor("profile", "avatar", description="Get the avatar."),
            HandlerDescriptor("content", "{hash}", description="Fetch content."),
            HandlerDescriptor(
                "watch", "{sessionId}/join", description="Join watch party."
            ),
        ),
    )


class TestRouter:
    @pytest.mark.asyncio
    async def test_dispatch_invokes_registered_callback(self) -> None:
        m = _sample_manifest()
        router = AetherURIRouter(m)
        invoked = False

        async def cb(ctx: DispatchContext) -> None:
            nonlocal invoked
            invoked = True

        router.register(m.handlers[0], cb)
        ok = await router.dispatch("aether://KXJB7-MN2P4/profile")
        assert ok is True
        assert invoked is True

    @pytest.mark.asyncio
    async def test_dispatch_no_match_returns_false(self) -> None:
        router = AetherURIRouter(_sample_manifest())
        ok = await router.dispatch("aether://KXJB7-MN2P4/nope")
        assert ok is False

    @pytest.mark.asyncio
    async def test_dispatch_context_has_route_parameters(self) -> None:
        m = _sample_manifest()
        router = AetherURIRouter(m)
        seen: list[DispatchContext] = []

        async def cb(ctx: DispatchContext) -> None:
            seen.append(ctx)

        router.register(m.handlers[2], cb)  # content/{hash}
        await router.dispatch("aether://KXJB7-MN2P4/content/sha256-xyz")
        assert len(seen) == 1
        assert seen[0].route_parameters["hash"] == "sha256-xyz"
        assert seen[0].handler is m.handlers[2]

    def test_register_handler_not_in_manifest_raises(self) -> None:
        router = AetherURIRouter(_sample_manifest())
        alien = HandlerDescriptor("stranger")

        async def cb(_: DispatchContext) -> None:
            pass

        with pytest.raises(ValueError):
            router.register(alien, cb)

    @pytest.mark.asyncio
    async def test_dispatch_no_callback_for_registered_handler_returns_false(
        self,
    ) -> None:
        # /profile is in the manifest but no callback is registered.
        router = AetherURIRouter(_sample_manifest())
        ok = await router.dispatch("aether://KXJB7-MN2P4/profile")
        assert ok is False

    @pytest.mark.asyncio
    async def test_dispatch_propagates_handler_exception(self) -> None:
        m = _sample_manifest()
        router = AetherURIRouter(m)

        async def cb(_: DispatchContext) -> None:
            raise RuntimeError("boom")

        router.register(m.handlers[0], cb)
        with pytest.raises(RuntimeError, match="boom"):
            await router.dispatch("aether://KXJB7-MN2P4/profile")

    @pytest.mark.asyncio
    async def test_dispatch_accepts_parsed_uri(self) -> None:
        m = _sample_manifest()
        router = AetherURIRouter(m)
        captured_uri: list[AetherURI] = []

        async def cb(ctx: DispatchContext) -> None:
            captured_uri.append(ctx.uri)

        router.register(m.handlers[0], cb)
        u = parse("aether://KXJB7-MN2P4/profile")
        await router.dispatch(u)
        assert captured_uri == [u]

    @pytest.mark.asyncio
    async def test_dispatch_parses_string_and_raises_on_bad_input(self) -> None:
        router = AetherURIRouter(_sample_manifest())
        with pytest.raises(AetherURIError):
            await router.dispatch("not-an-aether-uri")

    def test_router_rejects_none_manifest(self) -> None:
        with pytest.raises(ValueError):
            AetherURIRouter(None)  # type: ignore[arg-type]


# ---------------------------------------------------------------------------
# Manifest / descriptor — beyond the corpus
# ---------------------------------------------------------------------------


class TestManifest:
    def test_handler_descriptor_requires_name(self) -> None:
        with pytest.raises(AetherURIError):
            HandlerDescriptor("")

    def test_handler_descriptor_requires_non_whitespace_name(self) -> None:
        with pytest.raises(AetherURIError):
            HandlerDescriptor("   ")

    def test_manifest_requires_app_id(self) -> None:
        with pytest.raises(AetherURIError):
            HandlerManifest(app_id="", handlers=())

    def test_manifest_accepts_list_for_handlers(self) -> None:
        handlers_list = [HandlerDescriptor("profile")]
        m = HandlerManifest(app_id="aether.media", handlers=tuple(handlers_list))
        assert m.handlers[0].name == "profile"
