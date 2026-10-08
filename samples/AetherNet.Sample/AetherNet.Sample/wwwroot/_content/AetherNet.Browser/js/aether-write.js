// SPDX-License-Identifier: MIT
//
// Writing on a card, with the formatting visible while you write it.
//
// A card stores its prose as plain words. Bold is two asterisks, a link is [words](where) — see
// CardMarks on the C# side for the whole vocabulary and for why it is characters rather than tags.
// That is right for the document and wrong for the person: nobody should have to know a syntax to put
// one word in italics.
//
// So this is the bridge, and it goes both ways on every keystroke. The field holds real formatting,
// the document holds characters, and neither one ever sees the other's shape:
//
//     characters  ──dress()──>  what you are looking at
//     what you edit  ──say()──>  characters, back into the card
//
// Two rules make it safe rather than clever. Nothing arrives as markup — a paste is stripped to its
// text and the field is only ever filled from our own parse of our own characters, so there is no
// path by which a document could put an element into this page. And the field's contents are set
// once, when it is wired; Blazor is never allowed to redraw them underneath a caret.

(function (global) {
    'use strict';

    var doc = global.document;

    // ── characters → what you are looking at ────────────────────────────────────

    /** Text into an element, so nothing that is not text can arrive as anything else. */
    function words(s) {
        var box = doc.createElement('span');
        box.textContent = s;
        return box.innerHTML;
    }

    function space(c) {
        return c === '' || c === ' ' || c === '\n' || c === '\t';
    }

    /** Where the brackets round an address close — counted, because addresses contain brackets. */
    function closing(text, from) {
        var depth = 0;

        for (var i = from; i < text.length; i++) {
            if (text.charAt(i) === '(') depth++;
            else if (text.charAt(i) === ')' && depth-- === 0) return i;
        }

        return -1;
    }

    /**
     * The same four marks the renderer draws, drawn again here.
     *
     * Deliberately a second implementation rather than a shared one: this is JavaScript in a browser
     * and that is C# on the mesh, and the alternative to two small honest copies is one of them
     * pretending to be the other over a bridge on every keypress. They are kept together by the tests
     * either side, which use the same sentences.
     */
    function dress(text, depth) {
        if (!text) return '';
        if (depth > 4) return words(text);

        var out = '';

        for (var i = 0; i < text.length; i++) {
            var c = text.charAt(i);

            if (c === '[') {
                var shut = text.indexOf(']', i + 1);
                var opens = shut >= 0 ? text.charAt(shut + 1) : '';
                var ends = opens === '(' ? closing(text, shut + 2) : -1;

                if (shut > i + 1 && ends > 0) {
                    var label = text.slice(i + 1, shut);
                    var target = text.slice(shut + 2, ends);

                    out += '<a href="' + words(target) + '">' + dress(label, depth + 1) + '</a>';
                    i = ends;
                    continue;
                }
            }

            if (c === '*' || c === '_') {
                var strong = c === '*' && text.charAt(i + 1) === '*';
                var mark = strong ? '**' : c;
                var tag = strong ? 'b' : c === '*' ? 'i' : 'u';
                var from = i + mark.length;
                var close = space(text.charAt(from)) ? -1 : text.indexOf(mark, from);

                while (close > from && space(text.charAt(close - 1)))
                    close = text.indexOf(mark, close + 1);

                if (close > from) {
                    out += '<' + tag + '>' + dress(text.slice(from, close), depth + 1) + '</' + tag + '>';
                    i = close + mark.length - 1;
                    continue;
                }
            }

            if (c === '\n') { out += '<br>'; continue; }

            out += words(c);
        }

        return out;
    }

    // ── what you edit → characters ──────────────────────────────────────────────

    /**
     * Put marks around some words, with any spaces left outside them.
     *
     * Dragging across "the shop" almost always takes the space in front of it too, and a mark that
     * opens against a space is not a mark — it is two characters in the middle of a sentence. So the
     * spaces stay where they were and the marks close up around the words, which is what the person
     * dragging meant and what they are already looking at in the field.
     */
    function wrap(inner, open, shut) {
        var lead = inner.match(/^\s*/)[0];
        var trail = inner.match(/\s*$/)[0];
        var core = inner.slice(lead.length, inner.length - trail.length);

        return core ? lead + open + core + shut + trail : inner;
    }

    /** One node, as the card would store it. */
    function say(node) {
        if (node.nodeType === 3) return node.nodeValue || '';
        if (node.nodeType !== 1) return '';

        var tag = node.nodeName;
        if (tag === 'BR') return '\n';

        var inner = '';
        for (var i = 0; i < node.childNodes.length; i++) inner += say(node.childNodes[i]);
        if (!inner) return '';

        // Browsers disagree about which tag a bold is, and all of them are bold.
        if (tag === 'B' || tag === 'STRONG') return wrap(inner, '**', '**');
        if (tag === 'I' || tag === 'EM') return wrap(inner, '*', '*');
        if (tag === 'U') return wrap(inner, '_', '_');
        if (tag === 'A') return wrap(inner, '[', '](' + (node.getAttribute('href') || '') + ')');

        // Return makes a div or a paragraph, depending on the browser. Either way it is a new line.
        if (tag === 'DIV' || tag === 'P') return inner + '\n';

        return inner;
    }

    function text(field) {
        var out = '';
        for (var i = 0; i < field.childNodes.length; i++) out += say(field.childNodes[i]);

        // A trailing newline is the browser's, not the writer's.
        return out.replace(/\n+$/, '');
    }

    // ── The field ───────────────────────────────────────────────────────────────

    function which(field) {
        return field && field.getAttribute('data-w');
    }

    global.aetherWrite = {
        /*  Shared with the page editor next door, which needs exactly these two and must not own a
            second copy of them. Marks are a vocabulary — the moment there are two implementations of
            it, a card written in one place stops being a card read in the other. */
        dress: dress,
        say: say,

        /**
         * Wire every writing field on the page that is not wired already.
         *
         * Called after every render, because a field can appear at any point — a block added, a step
         * changed, a wizard reopened. Fields keep a flag so this costs nothing when there is nothing
         * new, and an already-wired field is never refilled: its contents belong to the person typing
         * in it, not to the last render.
         */
        wire: function (owner, wrote) {
            var fields = doc.querySelectorAll('[data-w]:not([data-live])');

            for (var i = 0; i < fields.length; i++) (function (field) {
                field.setAttribute('data-live', '1');
                field.innerHTML = dress(field.getAttribute('data-text') || '', 0);

                field.addEventListener('input', function () {
                    owner.invokeMethodAsync(wrote, which(field), text(field));
                });

                // Nothing arrives as markup. A pasted paragraph from a web page carries its styles,
                // its spans and whatever else was in it; the words are what was wanted.
                field.addEventListener('paste', function (event) {
                    event.preventDefault();
                    var said = (event.clipboardData || global.clipboardData).getData('text/plain');
                    doc.execCommand('insertText', false, said);
                });
            })(fields[i]);

            // Tags rather than inline styles, so what comes back out is a mark and not a stylesheet.
            try { doc.execCommand('styleWithCSS', false, false); } catch (e) { /* older engines */ }
        },

        /**
         * Bold, italic, underline, link — applied to whatever is selected.
         *
         * The toolbar never takes the focus (its buttons refuse the mousedown), so the selection is
         * still where the writer left it and these act on it directly.
         */
        mark: function (name, how, target) {
            var field = doc.querySelector('[data-w="' + name + '"]');
            if (!field) return false;

            field.focus();

            try {
                if (how === 'link') {
                    if (target) doc.execCommand('createLink', false, target);
                    else doc.execCommand('unlink', false, null);
                } else {
                    doc.execCommand(how, false, null);
                }
            } catch (e) {
                return false;
            }

            // execCommand does not raise input on every engine, so the card is told either way.
            field.dispatchEvent(new Event('input', { bubbles: true }));
            return true;
        },

        /** Whether anything is selected in this field — a link needs words to attach to. */
        picked: function (name) {
            var field = doc.querySelector('[data-w="' + name + '"]');
            var chosen = doc.getSelection();

            if (!field || !chosen || chosen.isCollapsed || chosen.rangeCount === 0) return false;

            return field.contains(chosen.getRangeAt(0).commonAncestorContainer);
        },
    };
})(window);
