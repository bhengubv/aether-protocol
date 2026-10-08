// SPDX-License-Identifier: MIT
//
// Getting a photograph onto a page, small enough to cross a radio.
//
// A picture off a phone is three to eight megabytes. Over Wi-Fi Direct that is a second; over a
// Bluetooth link measured at a couple of kilobytes a second it is most of an hour, and the reader
// gives up long before it lands. So nothing full-size ever reaches the mesh: the photograph is
// redrawn here, at the size a page actually shows it, and shrunk until it fits a budget — and the
// budget is chosen so that the slowest link we have can still deliver it while somebody waits.
//
// Done in the browser rather than in C# because the canvas is already a JPEG encoder that every
// phone has. The alternative is a native imaging library, which means a build per architecture, a
// second package, and a dependency on somebody else's release schedule — for something the WebView
// does for free.
//
// The file is never read as a file. It is decoded, drawn onto a canvas and re-encoded, so what
// leaves this function is pixels this code produced: no EXIF, no GPS coordinates, no camera serial,
// no original bytes. That is a privacy property, not a side effect — a photograph published to a
// mesh with the street it was taken on written inside it is a person handing out their address.

(function (global) {
    'use strict';

    /** What a page will actually show. Anything larger is detail nobody sees and bytes everybody pays. */
    var LONGEST_EDGE = 1200;

    /** The budget. Chosen for the slowest link, not the fastest. */
    var MOST_BYTES = 120 * 1024;

    /** Quality steps, tried in order until one fits. */
    var QUALITIES = [0.82, 0.72, 0.62, 0.52, 0.42];

    /** And if quality alone will not do it, the picture gets smaller. */
    var SHRINKS = [1.0, 0.8, 0.62, 0.48];

    function readable(file) {
        return file && typeof file.type === 'string' && file.type.indexOf('image/') === 0;
    }

    /** Decode a file into something drawable, whichever way this browser supports. */
    function decode(file) {
        if (global.createImageBitmap) {
            return global.createImageBitmap(file).catch(function () { return viaElement(file); });
        }
        return viaElement(file);
    }

    function viaElement(file) {
        return new Promise(function (resolve, reject) {
            var url = URL.createObjectURL(file);
            var img = new Image();
            img.onload = function () { URL.revokeObjectURL(url); resolve(img); };
            img.onerror = function () { URL.revokeObjectURL(url); reject(new Error('undecodable')); };
            img.src = url;
        });
    }

    function draw(source, scale) {
        var w = source.width || source.naturalWidth;
        var h = source.height || source.naturalHeight;
        var longest = Math.max(w, h);
        var fit = Math.min(1, (LONGEST_EDGE * scale) / longest);

        var canvas = global.document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(w * fit));
        canvas.height = Math.max(1, Math.round(h * fit));

        var ctx = canvas.getContext('2d');
        ctx.imageSmoothingQuality = 'high';
        ctx.drawImage(source, 0, 0, canvas.width, canvas.height);
        return canvas;
    }

    /** The base64 payload of a data URI, without the header. */
    function payload(uri) {
        var comma = uri.indexOf(',');
        return comma < 0 ? '' : uri.slice(comma + 1);
    }

    /** How many bytes a base64 string stands for. */
    function weigh(b64) {
        var padding = b64.endsWith('==') ? 2 : b64.endsWith('=') ? 1 : 0;
        return Math.floor((b64.length * 3) / 4) - padding;
    }

    /**
     * Shrink until it fits, and say so honestly if it will not.
     *
     * Quality first, then size. Dropping the pixel count is what a reader notices, so it is the last
     * thing tried rather than the first.
     */
    function fit(source) {
        var best = null;

        for (var s = 0; s < SHRINKS.length; s++) {
            var canvas = draw(source, SHRINKS[s]);

            for (var q = 0; q < QUALITIES.length; q++) {
                var b64 = payload(canvas.toDataURL('image/jpeg', QUALITIES[q]));
                if (!b64) continue;

                var bytes = weigh(b64);
                if (!best || bytes < best.bytes)
                    best = { base64: b64, bytes: bytes, width: canvas.width, height: canvas.height };

                if (bytes <= MOST_BYTES)
                    return { ok: true, mime: 'image/jpeg', base64: b64, bytes: bytes, width: canvas.width, height: canvas.height };
            }
        }

        // Nothing fit. Hand back the smallest we managed anyway with ok:false, so the caller can say
        // what happened rather than showing somebody a silent failure over a picture they chose.
        return best
            ? { ok: false, why: 'too big', mime: 'image/jpeg', base64: best.base64, bytes: best.bytes, width: best.width, height: best.height }
            : { ok: false, why: 'could not read that picture' };
    }

    /**
     * Watch a file input, and hand whatever is chosen back to .NET, already shrunk.
     *
     * The input is a real one in the markup rather than one created here, because a file chooser
     * opens only from a genuine tap. A click synthesised after an await has lost the gesture that
     * would have permitted it, and the chooser silently never appears.
     */
    function watch(input, owner, method) {
        if (!input || input.dataset.watched === '1') return;
        input.dataset.watched = '1';

        input.addEventListener('change', function () {
            var file = input.files && input.files[0];
            input.value = '';

            if (!readable(file)) {
                owner.invokeMethodAsync(method, { ok: false, why: 'that is not a picture' });
                return;
            }

            decode(file)
                .then(function (source) {
                    var got = fit(source);
                    if (source.close) source.close();
                    return owner.invokeMethodAsync(method, got);
                })
                .catch(function () {
                    return owner.invokeMethodAsync(method, { ok: false, why: 'could not read that picture' });
                });
        });
    }

    global.aetherPhoto = { watch: watch, longestEdge: LONGEST_EDGE, mostBytes: MOST_BYTES };
})(window);
