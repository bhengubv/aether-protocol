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

    // ── The host side of the same conversation ───────────────────────────────────
    //
    // The other half of this file. Inlined into a card, everything above asks its host to navigate
    // and reports how tall it is; loaded by the host, this listens. One file, because they are two
    // ends of one protocol and splitting them is how two ends drift apart.

    global.aetherCardHost = {
        /**
         * How tall a rendered card actually is, asked rather than waited for.
         *
         * The card also posts its height when it changes, which covers pictures landing late off a
         * radio. But a message that never arrives is indistinguishable from a page that happens to be
         * exactly the default height, so the host asks as well — same-origin srcdoc, so it can simply
         * look. Returns 0 when there is nothing to measure yet, and the caller keeps what it had.
         */
        fit: function (frame) {
            try {
                var doc = frame && (frame.contentDocument || (frame.contentWindow || {}).document);
                if (!doc || !doc.body) return 0;

                var tall = Math.max(
                    doc.body.scrollHeight, doc.body.offsetHeight,
                    doc.documentElement.scrollHeight, doc.documentElement.offsetHeight);

                return Math.min(Math.round(tall), 20000);
            } catch (e) {
                return 0;
            }
        },

        /**
         * Listen for what a rendered card asks for.
         *
         * Only ever two things, and both are checked here as well as on the way out: go to a mesh
         * address, and "I am this tall". Being rendered does not make a stranger's document trusted,
         * so the host re-checks everything it is told.
         */
        listen: function (owner, go, tall) {
            if (global.__aetherCardHost) return;
            global.__aetherCardHost = true;

            global.addEventListener('message', function (event) {
                var said = event.data;
                if (!said || said.aether === undefined) return;

                if (said.aether === 'go' && typeof said.to === 'string' &&
                    said.to.length < 512 && said.to.slice(0, 9).toLowerCase() === 'aether://') {
                    owner.invokeMethodAsync(go, said.to);
                    return;
                }

                if (said.aether === 'tall' && typeof said.px === 'number' &&
                    isFinite(said.px) && said.px > 0) {
                    // Bounded. A page claiming to be a hundred thousand pixels tall is a page that
                    // makes the app unusable, and refusing costs nothing.
                    owner.invokeMethodAsync(tall, Math.min(Math.round(said.px), 20000));
                }
            });
        },
    };
})(window);
