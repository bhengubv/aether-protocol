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

    // Who to hand the text to, remembered so a pen that appears later is wired the same way.
    var owner = null, penned = null;

    // The page's own vocabulary. Line-led, so each rule is anchored to the start of a line.
    var PAGE = [
        //  Every one of these stays on its line.
        //
        //  A character class matches a newline, so [^=]+ ran from the top of the document to the
        //  first equals sign anywhere below it and painted the whole page as one token. "." does not
        //  match a newline, which is the property wanted here, so it is what the greedy parts use.
        [/^###.*/gm, 'c-key'],
        [/^##.*/gm, 'c-at'],
        [/^#.*/gm, 'c-at'],
        [/^%[a-z]+.*/gm, 'c-note'],
        [/^(?:-{3,}|>|!!|=>|-|\d+\.)/gm, 'c-mark'],
        [/!?\[.*?\]\(.*?\)/g, 'c-said'],
        [/\*\*.+?\*\*|_.+?_/g, 'c-num'],
        [/^.+?=/gm, 'c-key'],
        [/::[a-z]+/g, 'c-mark'],
    ];

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
    function paint(src, rules) {
        var marks = [];
        (rules || RULES).forEach(function (rule) {
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

        // The text starts here rather than in a value= attribute.
        //
        // While Blazor owned that attribute it also re-wrote it: a save re-renders, the render
        // carries whatever the last processed input event held, and every character typed since is
        // overwritten. On a desktop the gap is too small to see. On a P30 a stylesheet came out cut
        // off mid-property, and the author is never told. So the field is seeded once and is the
        // browser's from then on; C# reads it on input and never writes it back.
        var seed = pen.getAttribute('data-seed');
        if (seed !== null && raw.value === '') { raw.value = seed; }

        var rules = pen.getAttribute('data-lang') === 'page' ? PAGE : RULES;
        var which = pen.getAttribute('data-lang') || 'css';

        function draw() { ink.innerHTML = paint(raw.value, rules); }
        function follow() { ink.scrollTop = raw.scrollTop; ink.scrollLeft = raw.scrollLeft; }

        // The text goes to C# from here, not through a bound value.
        //
        // A bound textarea is a two-way street and the other direction is the problem: a save
        // re-renders, the render carries whatever the last processed event held, and everything typed
        // since is overwritten. aether-write.js already learned this for prose — the field's contents
        // belong to the person typing in them. So the field is the browser's, and C# is told what is
        // in it once the typing stops. Once per pause rather than once per character also keeps a
        // P30 from writing the whole page to disk sixty times in a sentence.
        var pending = null;

        function tell(settled) {
            if (!owner || !penned) return;
            owner.invokeMethodAsync(penned, which, raw.value, settled);
        }

        function soon() {
            if (pending) { clearTimeout(pending); }
            pending = setTimeout(function () { pending = null; tell(false); }, 400);
        }

        raw.addEventListener('input', function () { draw(); soon(); });
        raw.addEventListener('scroll', follow);

        // Leaving the field is what redraws the page beside it.
        //
        // Redrawing on every pause cost the author their typing: the preview is an iframe, a new
        // srcdoc reloads it, and on Android that reload takes the focus out of this box. Every 400ms
        // the caret left, and everything typed after went nowhere — which is why a stylesheet kept
        // arriving cut off. So the text is made safe on a pause and the picture waits for a breath.
        raw.addEventListener('blur', function () {
            if (pending) { clearTimeout(pending); pending = null; }
            tell(true);
        });

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

    window.aetherCode = {
        sweep: sweep,
        /** Remember who to tell, then wire whatever is already on screen. */
        wire: function (who, what) { owner = who; penned = what; sweep(); },
    };
    document.addEventListener('DOMContentLoaded', sweep);

    // Blazor puts the editor on screen after this file has run, and takes it away again on every
    // step change, so the page is watched rather than wired once.
    new MutationObserver(sweep).observe(document.documentElement, { childList: true, subtree: true });
})();
