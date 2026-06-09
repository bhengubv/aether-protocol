# SPDX-License-Identifier: MIT

"""Fluent builder for :class:`AetherURI`.

Use when programmatically constructing an Aether URI from parts; for parsing an
existing string, use :func:`aethernet.uri.parse`.

Example
-------
::

    uri = (
        AetherURIBuilder()
        .authority("KXJB7-MN2P4")
        .path("content/sha256-abc123")
        .query("codec", "opus")
        .fragment("t=1m30s")
        .build()
    )
    # -> aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus#t=1m30s
"""

from __future__ import annotations

from typing import Optional, Union

from aethernet.identity import AetherNetTag
from aethernet.uri.uri import AetherURI, AetherURIError, parse, try_parse


class AetherURIBuilder:
    """Chainable builder that produces a validated :class:`AetherURI`.

    Every mutating method returns ``self`` so calls can be chained:

        >>> AetherURIBuilder().authority("KXJB7-MN2P4").path("profile").build()

    :meth:`build` round-trips the assembled URI through :func:`parse` to
    guarantee canonicalisation and full validation.
    """

    def __init__(self) -> None:
        self._authority: Optional[str] = None
        self._path: str = ""
        # Insertion-ordered; keys are stored lower-cased for case-insensitive
        # replacement on duplicate set, matching the parser's behaviour.
        self._query: dict[str, str] = {}
        self._fragment: str = ""

    # ------------------------------------------------------------------
    # Authority
    # ------------------------------------------------------------------

    def authority(self, value: Union[str, AetherNetTag]) -> "AetherURIBuilder":
        """Set the authority from an :class:`AetherNetTag` or raw string.

        Raises
        ------
        AetherURIError
            If *value* is empty or is neither a valid AetherTag nor a 64-char
            hex UHID.
        """
        if isinstance(value, AetherNetTag):
            if not value.is_valid():
                raise AetherURIError("AetherTag is uninitialised.")
            self._authority = value.value
            return self

        if not isinstance(value, str) or len(value) == 0:
            raise AetherURIError("Authority is null or empty.")

        # Validate + canonicalise by round-tripping through the parser.
        uri, err = try_parse(f"aether://{value}")
        if uri is None:
            raise AetherURIError(err or "Invalid authority.")
        self._authority = uri.authority
        return self

    # ------------------------------------------------------------------
    # Path
    # ------------------------------------------------------------------

    def path(self, value: str) -> "AetherURIBuilder":
        """Set the path component. A leading ``/`` is stripped."""
        self._path = value.lstrip("/") if value else ""
        return self

    def append_segment(self, segment: str) -> "AetherURIBuilder":
        """Append a single segment to the current path.

        No-op if *segment* is empty. Strips a leading ``/`` from *segment*.
        """
        if not segment:
            return self
        cleaned = segment.lstrip("/")
        self._path = cleaned if not self._path else f"{self._path}/{cleaned}"
        return self

    # ------------------------------------------------------------------
    # Query
    # ------------------------------------------------------------------

    def query(self, key: str, value: str) -> "AetherURIBuilder":
        """Add or replace a query parameter.

        Keys are stored case-insensitively (matching the parser).

        Raises
        ------
        AetherURIError
            If *key* is empty.
        """
        if not key:
            raise AetherURIError("Query key is null or empty.")
        # Lower-case the key to mirror the parser's case-insensitive store.
        self._query[key.lower()] = value if value is not None else ""
        return self

    def remove_query(self, key: str) -> "AetherURIBuilder":
        """Remove a query parameter by key. No-op if the key is not present."""
        if not key:
            return self
        self._query.pop(key.lower(), None)
        return self

    # ------------------------------------------------------------------
    # Fragment
    # ------------------------------------------------------------------

    def fragment(self, value: str) -> "AetherURIBuilder":
        """Set the fragment. A leading ``#`` is stripped."""
        self._fragment = value.lstrip("#") if value else ""
        return self

    # ------------------------------------------------------------------
    # Build
    # ------------------------------------------------------------------

    def build(self) -> AetherURI:
        """Build the final :class:`AetherURI`.

        Round-trips the assembled URI through :func:`parse` so the result is
        guaranteed canonical and fully validated.

        Raises
        ------
        AetherURIError
            If any component is invalid.
        """
        if not self._authority:
            raise AetherURIError("Authority is required.")
        return parse(self._render())

    def __str__(self) -> str:
        """Return the URI string this builder currently represents (no validation)."""
        if not self._authority:
            return ""
        return self._render()

    # ------------------------------------------------------------------
    # Internals
    # ------------------------------------------------------------------

    def _render(self) -> str:
        # Caller already verified _authority is set when this matters.
        assert self._authority is not None
        out: list[str] = ["aether://", self._authority]
        if self._path:
            out.append("/")
            out.append(self._path)
        if self._query:
            out.append("?")
            first = True
            for key, value in self._query.items():
                if not first:
                    out.append("&")
                first = False
                out.append(key)
                if value:
                    out.append("=")
                    out.append(value)
        if self._fragment:
            out.append("#")
            out.append(self._fragment)
        return "".join(out)
