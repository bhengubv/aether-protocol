# SPDX-License-Identifier: MIT

"""Handler manifest — declares the ``aether://`` surface a single app accepts.

A handler is identified by its first path segment (``HandlerName``) plus an
optional path-template that captures route parameters.

Path template syntax
--------------------
::

    "content/{hash}"             # matches /content/abc      -> {hash: abc}
    "watch/{sessionId}/join"     # matches /watch/123/join   -> {sessionId: 123}
    "profile"                    # matches /profile exactly
    "profile/avatar"             # matches /profile/avatar exactly
    ""                           # matches /<handler-name>   (root-handler form)
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional

from aethernet.uri.uri import AetherURI, AetherURIError


@dataclass(frozen=True)
class HandlerDescriptor:
    """Describes one handler an app exposes on its ``aether://`` surface.

    Attributes
    ----------
    name:
        The handler name — the first path segment (e.g. ``"content"``,
        ``"stream"``).
    path_template:
        Path template **after** ``name`` (e.g. ``"{hash}"``). Empty for a
        root handler.
    expected_query_keys:
        Informational list of query keys this handler reads.
    description:
        Human-readable description for diagnostics and docs.
    """

    name: str
    path_template: str = ""
    expected_query_keys: tuple[str, ...] = ()
    description: str = ""

    def __post_init__(self) -> None:
        if not self.name or not self.name.strip():
            raise AetherURIError("HandlerName is required.")

    # ------------------------------------------------------------------
    # Matching
    # ------------------------------------------------------------------

    def match(self, path: str) -> Optional[dict[str, str]]:
        """Match *path* against this descriptor's template.

        Returns the captured route parameters on success, or ``None`` on no
        match. Comparison of literal segments is case-sensitive.
        """
        if not self.path_template:
            template_segs: list[str] = [self.name]
        else:
            template_segs = (self.name + "/" + self.path_template.lstrip("/")).split("/")

        path_segs = path.split("/") if path else [""]
        if len(template_segs) != len(path_segs):
            return None

        captures: dict[str, str] = {}
        for t, p in zip(template_segs, path_segs):
            if len(t) >= 2 and t.startswith("{") and t.endswith("}"):
                captures[t[1:-1]] = p
            elif t != p:
                return None
        return captures


@dataclass
class HandlerManifest:
    """An app's complete ``aether://`` handler manifest.

    Attributes
    ----------
    app_id:
        Reverse-DNS-style identifier (e.g. ``"aether.media"``, ``"aether.txtme"``).
    handlers:
        The set of routes this app accepts.
    """

    app_id: str
    handlers: tuple[HandlerDescriptor, ...] = field(default_factory=tuple)

    def __post_init__(self) -> None:
        if not self.app_id or not self.app_id.strip():
            raise AetherURIError("AppId is required.")
        # Defensive copy in case caller passed a list.
        if not isinstance(self.handlers, tuple):
            self.handlers = tuple(self.handlers)

    def resolve(
        self, uri: AetherURI
    ) -> Optional[tuple[HandlerDescriptor, dict[str, str]]]:
        """Resolve *uri* against this manifest.

        Returns ``(handler, captures)`` on the first matching descriptor, or
        ``None`` if no handler matched.
        """
        if not uri.authority:
            return None
        for handler in self.handlers:
            if handler.name != uri.handler_name:
                continue
            captures = handler.match(uri.path)
            if captures is not None:
                return handler, captures
        return None
