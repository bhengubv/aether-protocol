// SPDX-License-Identifier: MIT
//
// The masthead every AetherNet page wears, painted rather than drawn.
//
// This is ThreeUI's visual language, and it is here because the aesthetic is the argument: somebody
// who sees a page like this decides the network is worth joining before they have read a word of it.
// Everything below follows the way ThreeUI's own examples are built — raw WebGL, hand-written
// shaders, a single hue lit toward white. There is no Three.js and no library of any kind, which is
// what makes it possible at all: a reader on a phone-to-phone link has no internet, so anything
// fetched never arrives, and six hundred kilobytes of library over a link measured in kilobits is not
// a trade. The shader is two kilobytes of text.
//
// One hue, mixed toward white by a lighting term — the same treatment as the ThreeUI surface
// shaders, and the reason those pages read as one design rather than as a colour scheme. The colour
// is the page's own accent, so every look is the same surface in its own material.
//
// Degrades honestly. No WebGL, no context, a failed compile — the canvas is removed and whatever the
// page put behind it stands: on a card that is the content-addressed SVG the mesh already carries,
// so a reader with no GPU sees the same picture, flat.

(function (global) {
    'use strict';

    var VERT =
        'attribute vec2 a_p;' +
        'varying vec2 v_uv;' +
        'void main(){ v_uv = a_p * 0.5 + 0.5; gl_Position = vec4(a_p, 0.0, 1.0); }';

    // The painter, in two halves.
    //
    // Everything a background has in common lives here: the uniforms, the normal taken by difference,
    // the lighting, and the single-hue mix that makes every one of them belong to the same family and
    // take on the colour of whatever page it is on.
    //
    // The half that differs is one function — float field(vec2 p) — and it comes from the page. That
    // is what makes the catalogue a catalogue: a new background is those few lines and nothing else,
    // and it arrives already lit, already in the right colour, already at the right cost.
    //
    // The source is never an author's. A card names a background by key; the key is looked up in the
    // catalogue and what gets inlined is source we shipped.
    var HEAD =
        'precision mediump float;' +
        'varying vec2 v_uv;' +
        'uniform vec2 u_size;' +
        'uniform vec3 u_bg;' +
        'uniform float u_t;' +
        'uniform float u_seed;' +
        'const float TAU = 6.28318530718;';

    // Two interfering ripples — the default, and the shape of every entry: take a point, give back a
    // height. Used when a page names nothing, or names something this build has never heard of.
    var FIELD =
        'float field(vec2 p){' +
        '  vec2 a = p - vec2(0.24 + 0.20*sin(u_seed), 0.34 + 0.16*cos(u_seed*1.7));' +
        '  vec2 b = p - vec2(0.82 + 0.12*cos(u_seed*2.3), 0.18 + 0.22*sin(u_seed*0.9));' +
        '  float folds = 3.0 + floor(mod(u_seed*3.0, 4.0));' +
        '  float wa = sin(length(a*vec2(1.0,1.35))*26.0 - u_t*0.55 + sin(atan(a.y,a.x)*folds)*0.85);' +
        '  float wb = sin(length(b*vec2(1.25,1.0))*19.0 + u_t*0.37 + cos(atan(b.y,b.x)*(folds+2.0))*0.70);' +
        '  return wa*0.62 + wb*0.48;' +
        '}';

    var TAIL =
        'void main(){' +
        '  vec2 p = v_uv;' +
        '  p.x *= u_size.x / max(u_size.y, 1.0);' +
        '  float e = 1.6 / max(u_size.y, 1.0);' +

        // The normal, taken from the field by difference. Cheap, and exact enough for a surface that
        // is being lit rather than measured — and it means a field never has to supply one.
        '  float h = field(p);' +
        '  float hx = field(p + vec2(e, 0.0)) - field(p - vec2(e, 0.0));' +
        '  float hy = field(p + vec2(0.0, e)) - field(p - vec2(0.0, e));' +
        '  vec3 N = normalize(vec3(-hx * 2.6, -hy * 2.6, 1.0));' +

        '  vec3 V = vec3(0.0, 0.0, 1.0);' +
        '  vec3 L = normalize(vec3(-0.30, 0.52, 0.80));' +
        '  vec3 H = normalize(L + V);' +

        // Diffuse raised to a power. A plate faces the viewer almost everywhere, so a plain dot
        // product sits near one across the whole surface and lifts the thing toward white — the
        // accent stops being the colour of anything and the page loses its material.
        '  float diff = pow(max(dot(N, L), 0.0), 2.2);' +
        '  float spec = pow(max(dot(N, H), 0.0), 34.0);' +

        // A baked gradient across the plate, the way a lit surface brightens toward one corner.
        '  float grad = dot(v_uv - 0.5, vec2(-0.42, 0.90)) + 0.5;' +

        // Most of the light is in the highlight, not the fill. The ridges read as ridges and the
        // colour between them stays the colour somebody chose.
        '  float k = 0.02 + 0.13 * diff + 0.54 * spec + 0.085 * grad + 0.045 * (h * 0.5 + 0.5);' +
        '  k = clamp(k, 0.0, 1.0);' +

        '  gl_FragColor = vec4(mix(u_bg, vec3(1.0), k), 1.0);' +
        '}';

    /** The background this page asked for, or the default if it asked for nothing we have. */
    function frag() {
        var named = global.aetherField;
        var field = typeof named === 'string' && named.indexOf('float field') >= 0 ? named : FIELD;
        return HEAD + field + TAIL;
    }

    function compile(gl, type, source) {
        var shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        return gl.getShaderParameter(shader, gl.COMPILE_STATUS) ? shader : null;
    }

    /** #rrggbb or #rgb to three floats. Anything else is refused rather than guessed at. */
    function rgb(hex) {
        var h = String(hex || '').trim();
        if (h.charAt(0) !== '#') return null;
        h = h.slice(1);
        if (h.length === 3) h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
        if (h.length !== 6 || !/^[0-9a-fA-F]{6}$/.test(h)) return null;
        return [
            parseInt(h.slice(0, 2), 16) / 255,
            parseInt(h.slice(2, 4), 16) / 255,
            parseInt(h.slice(4, 6), 16) / 255,
        ];
    }

    /**
     * A stable number per page, so the same page always folds the same way.
     *
     * Handed in as a number by whatever drew the canvas, never derived here from anything a person
     * wrote. The only two values this painter reads are a colour and this, and neither can be text.
     */
    function seedOf(value) {
        var n = parseFloat(value);
        return isFinite(n) ? (Math.abs(n) % 100000) / 10000 : 3.7;
    }

    function paint(canvas, accent, seed) {
        if (!canvas || canvas.dataset.painted === '1') return false;

        var colour = rgb(accent);
        if (!colour) return false;

        var gl = null;
        try {
            var opts = { alpha: false, antialias: false, depth: false, powerPreference: 'low-power' };
            gl = canvas.getContext('webgl', opts) || canvas.getContext('experimental-webgl', opts);
        } catch (e) {
            gl = null;
        }
        if (!gl) { canvas.remove(); return false; }

        var vs = compile(gl, gl.VERTEX_SHADER, VERT);
        var fs = compile(gl, gl.FRAGMENT_SHADER, frag());

        // A background that will not compile falls back to the one that always does, rather than
        // taking the masthead down. A catalogue is a thing people add to, and the cost of a bad
        // entry should be that one page looks ordinary — not that it looks broken.
        if (vs && !fs) fs = compile(gl, gl.FRAGMENT_SHADER, HEAD + FIELD + TAIL);
        if (!vs || !fs) { canvas.remove(); return false; }

        var program = gl.createProgram();
        gl.attachShader(program, vs);
        gl.attachShader(program, fs);
        gl.linkProgram(program);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) { canvas.remove(); return false; }

        gl.useProgram(program);

        var quad = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, quad);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        var slot = gl.getAttribLocation(program, 'a_p');
        gl.enableVertexAttribArray(slot);
        gl.vertexAttribPointer(slot, 2, gl.FLOAT, false, 0, 0);

        var uSize = gl.getUniformLocation(program, 'u_size');
        var uBg = gl.getUniformLocation(program, 'u_bg');
        var uT = gl.getUniformLocation(program, 'u_t');
        var uSeed = gl.getUniformLocation(program, 'u_seed');

        gl.uniform3f(uBg, colour[0], colour[1], colour[2]);
        gl.uniform1f(uSeed, seedOf(seed));

        canvas.dataset.painted = '1';

        // Half resolution. This runs on a 2019 handset behind a WebView, and a masthead is a backdrop
        // — the surface is smooth, so nobody can tell, and the phone stays cool enough to keep a radio
        // up while somebody reads.
        function size() {
            var w = Math.max(1, Math.round(canvas.clientWidth * 0.5));
            var h = Math.max(1, Math.round(canvas.clientHeight * 0.5));
            if (canvas.width !== w || canvas.height !== h) {
                canvas.width = w;
                canvas.height = h;
                gl.viewport(0, 0, w, h);
            }
            gl.uniform2f(uSize, w, h);
        }

        function frame(t) {
            size();
            gl.uniform1f(uT, t * 0.001);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
        }

        // One frame and done, when the reader asked for less motion — or when the plate is a
        // thumbnail. A look-picker puts five of these on screen at once, and five animating GL
        // contexts on a 2019 handset is a page competing with real websites and losing.
        var still = canvas.hasAttribute('data-still') ||
            (global.matchMedia && global.matchMedia('(prefers-reduced-motion: reduce)').matches);

        if (still) {
            // Twice: the first pass sizes the canvas, and a canvas sized after it was drawn is a
            // canvas that comes out blank.
            frame(0);
            frame(0);

            // And then hand the context back.
            //
            // A picker showing fourteen backgrounds at once wants fourteen contexts, and a browser
            // gives out about eight — so the fifteenth fails, and on a handset the whole view can go
            // down with it. A still frame does not need a live context: it becomes an image, and the
            // context is released immediately. One at a time, however many are on screen.
            keep(canvas, gl);
            return true;
        }

        var last = 0;
        var stopped = false;

        function loop(now) {
            if (stopped) return;
            // Twenty a second. A drift this slow needs no more, and the frames not drawn are the
            // battery a person still has when they close the page.
            if (now - last > 48) { last = now; frame(now); }
            global.requestAnimationFrame(loop);
        }

        function watch() {
            if (global.document.hidden) { stopped = true; return; }
            if (stopped) { stopped = false; global.requestAnimationFrame(loop); }
        }

        global.document.addEventListener('visibilitychange', watch);
        global.requestAnimationFrame(loop);
        return true;
    }

    /**
     * Freeze a drawn canvas into an image and give the GPU context back.
     *
     * The picture is the canvas's own pixels, so nothing is fetched and nothing changes visually.
     * What changes is that the page stops holding a WebGL context it will never draw into again —
     * which is the difference between a gallery of backgrounds and a gallery that takes the app down
     * on the ninth one.
     */
    function keep(canvas, gl) {
        var frozen;
        try {
            frozen = canvas.toDataURL('image/png');
        } catch (e) {
            return;
        }

        var img = global.document.createElement('img');
        img.src = frozen;
        img.className = canvas.className;
        img.setAttribute('alt', '');
        img.style.cssText = canvas.style.cssText;

        if (canvas.parentNode) canvas.parentNode.replaceChild(img, canvas);

        var lose = gl.getExtension('WEBGL_lose_context');
        if (lose) lose.loseContext();
    }

    /** Paint every masthead on the page that has not been painted yet. */
    function paintAll(root) {
        var where = root || global.document;
        var plates = where.querySelectorAll('canvas[data-aether-shader]');
        for (var i = 0; i < plates.length; i++)
            paint(plates[i], plates[i].getAttribute('data-accent'), plates[i].getAttribute('data-seed'));
    }

    global.aetherShader = { paint: paint, paintAll: paintAll };

    if (global.document.readyState === 'loading')
        global.document.addEventListener('DOMContentLoaded', function () { paintAll(); });
    else
        paintAll();
})(window);
