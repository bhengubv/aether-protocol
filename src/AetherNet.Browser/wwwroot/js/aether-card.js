// SPDX-License-Identifier: MIT
//
// The one thing a rendered card is allowed to ask its host to do: go somewhere else on the mesh.
//
// A card is inert. It cannot fetch, it cannot execute anything its author wrote, and it certainly
// cannot navigate the app that is showing it. But a page with no working links is a leaflet, and the
// mesh-web is meant to be a web — so links exist, and they are handled the only way that keeps the
// card inert: the card never gets an address of its own to follow. It asks, by posting a message, and
// the host decides.
//
// Everything about that is deliberate. The address is one this renderer wrote into the page from a
// value already checked as an aether:// address; it is re-checked here before being posted; and the
// host re-checks it again before acting. A card that could hand the browser any address it liked is
// exactly what publishing cards as signed JSON instead of HTML exists to prevent.

(function (global) {
    'use strict';

    /** Only ever this. An http address from a card would be a card phoning home through its reader. */
    function mesh(address) {
        return typeof address === 'string' &&
            address.length < 512 &&
            address.slice(0, 9).toLowerCase() === 'aether://';
    }

    global.document.addEventListener('click', function (event) {
        var target = event.target;

        while (target && target !== global.document.body) {
            if (target.hasAttribute && target.hasAttribute('data-aether-to')) {
                var address = target.getAttribute('data-aether-to');
                event.preventDefault();

                if (mesh(address) && global.parent && global.parent !== global)
                    global.parent.postMessage({ aether: 'go', to: address }, '*');

                return;
            }
            target = target.parentNode;
        }
    }, true);

    // How tall the card actually is, so the host can give it exactly that much room.
    //
    // Without this the page sits in a box of somebody else's choosing and scrolls inside it — which
    // is the difference between a web page and a widget, and the difference a reader feels first.
    function measure() {
        if (!global.parent || global.parent === global) return;

        var body = global.document.body;
        var root = global.document.documentElement;
        var tall = Math.max(
            body.scrollHeight, body.offsetHeight,
            root.clientHeight, root.scrollHeight, root.offsetHeight);

        global.parent.postMessage({ aether: 'tall', px: tall }, '*');
    }

    global.addEventListener('load', measure);
    global.addEventListener('resize', measure);

    // Pictures land late — they come off a radio — and a card that was measured before its masthead
    // arrived is a card with a gap under it.
    if (global.ResizeObserver)
        new ResizeObserver(measure).observe(global.document.documentElement);
    else
        global.setTimeout(measure, 400);

    measure();
})(window);
