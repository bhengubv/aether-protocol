// SPDX-License-Identifier: MIT
//
// A code editor, written here rather than installed.
//
// A card renders with no network — that is the whole product — so an editor that pulls a library
// from a CDN is an editor that does not work in the place this app is for. This is the standard
// two-layer trick and it is about sixty lines: a <pre> underneath holding the same text, coloured,
// and a transparent <textarea> on top holding nothing but the caret, the selection and the
// scrolling. The browser keeps doing what browsers are good at — text editing on a touchscreen,
// which nobody should reimplement — and we only paint.

(function () {
    'use strict';

    // Comments first and as one token, or the colours leak out of them.
    var RULES = [
        [/\/\*[\s\S]*?(\*\/|$)/g, 'c-note'],
        [/(["'])(?:\.|(?!\1)[^\\r\n])*\1?/g, 'c-said'],
        [/@[\w-]+/g, 'c-at'],
        [/#[0-9a-fA-F]{3,8}\b/g, 'c-hue'],
        [/\b-?\d*\.?\d+(px|rem|em|%|vh|vw|s|ms|deg|fr|ch)?\b/g, 'c-num'],
        [/[\w-]+(?=\s*:)/g, 'c-key'],
        [/[{}();,:]/g, 'c-mark'],
    ];

    function safe(s) {
        return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    // One pass, longest-match-wins by position, so a colour inside a comment stays a comment.
    function paint(src) {
        var marks = [];
        RULES.forEach(function (rule) {
            var re = new RegExp(rule[0].source, rule[0].flags), m;
            while ((m = re.exec(src)) !== null) {
                if (m[0].length === 0) { re.lastIndex++; continue; }
                marks.push({ at: m.index, to: m.index + m[0].length, as: rule[1] });
            }
        });
        marks.sort(function (a, b) { return a.at - b.at || b.to - a.to; });

        var out = '', at = 0;
        marks.forEach(function (mk) {
            if (mk.at < at) return;                       // already inside something
            out += safe(src.slice(at, mk.at));
            out += '<span class="' + mk.as + '">' + safe(src.slice(mk.at, mk.to)) + '</span>';
            at = mk.to;
        });
        // A trailing newline needs a character after it or the last line has no height.
        return out + safe(src.slice(at)) + '\n';
    }

    function wire(pen) {
        if (pen.dataset.aetherWired) return;
        pen.dataset.aetherWired = '1';

        var raw = pen.querySelector('.raw');
        var ink = pen.querySelector('.ink');
        if (!raw || !ink) return;

        function draw() { ink.innerHTML = paint(raw.value); }
        function follow() { ink.scrollTop = raw.scrollTop; ink.scrollLeft = raw.scrollLeft; }

        raw.addEventListener('input', draw);
        raw.addEventListener('scroll', follow);

        // Tab indents rather than leaving the field. On a phone this matters less; on anything with a
        // keyboard, a code box that loses focus to Tab is not a code box.
        raw.addEventListener('keydown', function (e) {
            if (e.key !== 'Tab') return;
            e.preventDefault();
            var a = raw.selectionStart, b = raw.selectionEnd;
            raw.value = raw.value.slice(0, a) + '  ' + raw.value.slice(b);
            raw.selectionStart = raw.selectionEnd = a + 2;
            raw.dispatchEvent(new Event('input', { bubbles: true }));
        });

        draw();
    }

    function sweep() { document.querySelectorAll('[data-aether-code]').forEach(wire); }

    window.aetherCode = sweep;
    document.addEventListener('DOMContentLoaded', sweep);

    // Blazor puts the editor on screen after this file has run, and takes it away again on every
    // step change, so the page is watched rather than wired once.
    new MutationObserver(sweep).observe(document.documentElement, { childList: true, subtree: true });
})();
