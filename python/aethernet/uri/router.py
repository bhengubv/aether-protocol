# SPDX-License-Identifier: MIT

"""Dispatches an incoming ``aether://`` URI to the registered handler for its route.

The router is per-app — each app constructs one with its own
:class:`HandlerManifest`.

Lifecycle
---------
1. App startup: build a :class:`HandlerManifest` describing every route the app
   accepts.
2. App startup: register an async callback per :class:`HandlerDescriptor` via
   :meth:`AetherURIRouter.register`.
3. At runtime: when a URI is received (incoming intent, deep link, or in-mesh
   dispatch), call :meth:`AetherURIRouter.dispatch` to invoke the right callback.

Example
-------
::

    manifest = HandlerManifest("aether.media", handlers=(
        HandlerDescriptor("profile", description="Get profile."),
        HandlerDescriptor("content", "{hash}", description="Fetch content."),
    ))
    router = AetherURIRouter(manifest)

    async def on_content(ctx: DispatchContext) -> None:
        await content_service.request(ctx.route_parameters["hash"])

    router.register(manifest.handlers[1], on_content)
    await router.dispatch("aether://KXJB7-MN2P4/content/sha256-abc")
"""

from __future__ import annotations

import asyncio
from dataclasses import dataclass
from typing import Awaitable, Callable, Mapping, Union

from aethernet.uri.manifest import HandlerDescriptor, HandlerManifest
from aethernet.uri.uri import AetherURI, parse


# A handler callback receives one DispatchContext and returns an awaitable.
HandlerCallback = Callable[["DispatchContext"], Awaitable[None]]


@dataclass(frozen=True)
class DispatchContext:
    """Context delivered to a registered URI handler when its route matches.

    Attributes
    ----------
    uri:
        The original URI being dispatched.
    handler:
        The matched descriptor.
    route_parameters:
        Route parameters captured from the path template (e.g. ``{"hash": "abc"}``).
    """

    uri: AetherURI
    handler: HandlerDescriptor
    route_parameters: Mapping[str, str]


class AetherURIRouter:
    """Reference in-process router.

    Async- and thread-safe registration via an :class:`asyncio.Lock`.
    """

    def __init__(self, manifest: HandlerManifest) -> None:
        if manifest is None:
            raise ValueError("Manifest is null.")
        self._manifest = manifest
        self._handlers: dict[HandlerDescriptor, HandlerCallback] = {}
        self._lock = asyncio.Lock()

    @property
    def manifest(self) -> HandlerManifest:
        return self._manifest

    # ------------------------------------------------------------------
    # Registration
    # ------------------------------------------------------------------

    def register(self, descriptor: HandlerDescriptor, callback: HandlerCallback) -> None:
        """Register *callback* for *descriptor*.

        The descriptor must be one present in :attr:`manifest`. Re-registering
        replaces the existing callback.

        Note
        ----
        Registration is synchronous so it can be called from app startup code
        outside any event loop. Dispatch itself takes the lock.
        """
        if descriptor is None:
            raise ValueError("Descriptor is null.")
        if callback is None:
            raise ValueError("Handler is null.")
        if descriptor not in self._manifest.handlers:
            raise ValueError(f"Descriptor '{descriptor.name}' is not in the manifest.")
        # No lock needed for the registration write — Python dict assignment is
        # atomic, and the dispatch path takes the lock when *reading*.
        self._handlers[descriptor] = callback

    # ------------------------------------------------------------------
    # Dispatch
    # ------------------------------------------------------------------

    async def dispatch(self, uri: Union[AetherURI, str]) -> bool:
        """Resolve and dispatch *uri*.

        Returns ``True`` iff a registered callback was invoked. If no handler
        is registered (or no manifest entry matches) returns ``False``.
        Handler exceptions propagate to the caller.

        Parameters
        ----------
        uri:
            Either a parsed :class:`AetherURI` or a string. A string is parsed
            via :func:`aethernet.uri.parse` and an :class:`AetherURIError` is
            raised on parse failure.
        """
        parsed: AetherURI = parse(uri) if isinstance(uri, str) else uri

        resolved = self._manifest.resolve(parsed)
        if resolved is None:
            return False
        descriptor, captures = resolved

        async with self._lock:
            callback = self._handlers.get(descriptor)
        if callback is None:
            return False

        ctx = DispatchContext(uri=parsed, handler=descriptor, route_parameters=captures)
        await callback(ctx)
        return True
