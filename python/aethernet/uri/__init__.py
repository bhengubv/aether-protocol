# SPDX-License-Identifier: MIT

"""Aether URI scheme — canonical addressing for resources on the Aether mesh.

The full grammar and design notes live in ``docs/aether-uri-scheme.md``.

Public API
----------
- :class:`AetherURI` — immutable value type
- :class:`AetherURIError` — raised on parse / build / dispatch failure
- :func:`parse` / :func:`try_parse` — parsing entrypoints
- :class:`AetherURIBuilder` — fluent builder
- :class:`HandlerDescriptor` / :class:`HandlerManifest` — handler manifest
- :class:`AetherURIRouter` / :class:`DispatchContext` — async dispatcher
- :data:`SCHEME` — the constant ``"aether"``

Example
-------
::

    from aethernet.uri import parse, AetherURIBuilder

    u = parse("aether://KXJB7-MN2P4/content/sha256-abc?codec=opus")
    assert u.authority == "KXJB7-MN2P4"
    assert u.handler_name == "content"
    assert u.query["codec"] == "opus"
"""

from aethernet.uri.uri import (
    SCHEME,
    AetherURI,
    AetherURIError,
    parse,
    try_parse,
)
from aethernet.uri.builder import AetherURIBuilder
from aethernet.uri.manifest import HandlerDescriptor, HandlerManifest
from aethernet.uri.router import AetherURIRouter, DispatchContext

__all__ = [
    "SCHEME",
    "AetherURI",
    "AetherURIError",
    "parse",
    "try_parse",
    "AetherURIBuilder",
    "HandlerDescriptor",
    "HandlerManifest",
    "AetherURIRouter",
    "DispatchContext",
]
