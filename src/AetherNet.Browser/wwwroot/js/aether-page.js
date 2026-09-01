// SPDX-License-Identifier: MIT
//
// The page editor. A rich text editor, which means what that has always meant:
//
//   1. a document model — here the card's own typed blocks, which already existed
//   2. a view that IS the input — the rendered document is what you type into
//   3. commands over a selection — "make this a heading", not "type two hashes"
//
// What stood here before was none of those. It was a plain-text editor over a serialisation of the
// model: the page was flattened to markdown, you edited the markdown, and it was parsed back. That is
// why the hashes and the dashes were on screen, why both layers had to share one set of font metrics,
// and why a heading could not be any bigger than a sentence. Every one of those was a consequence of
// editing the serialisation instead of the document.
//
// Here a heading is an <h2> and it is simply big.
//
// The blocks are the truth and they go back to C# as blocks. Nothing on this side invents a format.

(function (global) {
    'use strict';

    var doc = global.document;
    var owner = null, wrote = null;

    /** The tag a kind is written in, and the kind a tag came from. */
    var AS = {
        title: 'H1', heading: 'H2', eyebrow: 'H3', text: 'P',
        quote: 'BLOCKQUOTE', list: 'UL', index: 'OL', rule: 'HR', image: 'FIGURE',
        kv: 'P', link: 'P', tip: 'P',
    };

    var FROM = {
        H1: 'title', H2: 'heading', H3: 'eyebrow', P: 'text',
        BLOCKQUOTE: 'quote', UL: 'list', OL: 'index', HR: 'rule', FIGURE: 'image',
    };

    function el(tag) { return doc.createElement(tag); }

    // ── The model, drawn ────────────────────────────────────────────────────────

    /** One block as the element you will be typing into. */
    function draw(block) {
        var kind = block.k || 'text';
        var node = el(AS[kind] || 'P');
        node.setAttribute('data-k', kind);

        if (kind === 'rule') return node;

        if (kind === 'list' || kind === 'index') {
            (block.items || ['']).forEach(function (item) {
                var li = el('LI');
                li.innerHTML = global.aetherWrite.dress(item || '', 0);
                node.appendChild(li);
            });
            return node;
        }

        if (kind === 'image') {
            var img = el('IMG');
            img.setAttribute('src', block.src || '');
            img.setAttribute('data-hash', block.hash || '');
            img.setAttribute('alt', block.t || '');
            node.appendChild(img);

            var cap = el('FIGCAPTION');
            cap.textContent = block.t || '';
            node.appendChild(cap);
            return node;
        }

        if (kind === 'link') node.setAttribute('data-to', block.to || '');
        if (block.a) node.setAttribute('data-a', block.a);
        if (block.as) node.setAttribute('data-as', block.as);

        node.innerHTML = global.aetherWrite.dress(block.t || '', 0);
        return node;
    }

    function fill(host, blocks) {
        host.innerHTML = '';
        (blocks || []).forEach(function (b) { host.appendChild(draw(b)); });
        if (!host.firstChild) host.appendChild(draw({ k: 'text', t: '' }));
    }

    // ── The view, read back ─────────────────────────────────────────────────────

    /** The words in an element, in the characters a card stores. */
    function inline(node) {
        var out = '';
        for (var i = 0; i < node.childNodes.length; i++) out += global.aetherWrite.say(node.childNodes[i]);
        return out.replace(/\n+$/, '').replace(/ /g, ' ');
    }

    /**
     * Put right whatever the browser just made.
     *
     * Left alone, contenteditable invents its own structure: pressing return produces a DIV in one
     * engine and a P in another, pasting brings SPANs with inline styles, and deleting the first line
     * can leave bare text with no element round it at all. Every rich text editor ever written has
     * this pass, and skipping it is the difference between an editor and a box full of surprises.
     */
    function tidy(host) {
        var kids = [].slice.call(host.childNodes);

        kids.forEach(function (node) {
            if (node.nodeType === 3) {
                if (!node.nodeValue.trim()) { node.remove(); return; }
                var p = el('P');
                p.setAttribute('data-k', 'text');
                host.replaceChild(p, node);
                p.appendChild(node);
                return;
            }

            if (node.nodeType !== 1) { node.remove(); return; }

            if (!FROM[node.nodeName]) {
                var swap = el('P');
                swap.setAttribute('data-k', 'text');
                while (node.firstChild) swap.appendChild(node.firstChild);
                host.replaceChild(swap, node);
                node = swap;
            }

            if (!node.getAttribute('data-k')) node.setAttribute('data-k', FROM[node.nodeName] || 'text');
        });

        if (!host.firstChild) host.appendChild(draw({ k: 'text', t: '' }));
    }

    /** The document, as blocks. */
    function read(host) {
        var out = [];

        [].slice.call(host.children).forEach(function (node) {
            var kind = node.getAttribute('data-k') || FROM[node.nodeName] || 'text';

            if (kind === 'rule') { out.push({ k: 'rule', t: '' }); return; }

            if (kind === 'list' || kind === 'index') {
                var items = [].slice.call(node.querySelectorAll('li'))
                    .map(inline)
                    .filter(function (s) { return s.trim().length; });
                if (items.length) out.push({ k: kind, items: items });
                return;
            }

            if (kind === 'image') {
                var img = node.querySelector('img');
                var cap = node.querySelector('figcaption');
                if (img) out.push({
                    k: 'image',
                    t: cap ? cap.textContent : '',
                    hash: img.getAttribute('data-hash') || '',
                    as: node.getAttribute('data-as') || null,
                    a: node.getAttribute('data-a') || null,
                });
                return;
            }

            var said = inline(node);
            if (!said.trim()) return;

            var block = { k: kind, t: said };
            if (kind === 'link') block.to = node.getAttribute('data-to') || '';
            if (node.getAttribute('data-a')) block.a = node.getAttribute('data-a');
            if (node.getAttribute('data-as')) block.as = node.getAttribute('data-as');
            out.push(block);
        });

        return out;
    }

    // ── Commands ────────────────────────────────────────────────────────────────

    /** The block the caret is in. */
    function here(host) {
        var sel = doc.getSelection();
        if (!sel || !sel.rangeCount) return null;

        var node = sel.getRangeAt(0).startContainer;
        while (node && node.parentNode !== host) node = node.parentNode;
        return node && node.nodeType === 1 ? node : null;
    }

    function caretTo(node, atEnd) {
        var range = doc.createRange();
        range.selectNodeContents(node);
        range.collapse(!atEnd);
        var sel = doc.getSelection();
        sel.removeAllRanges();
        sel.addRange(range);
    }

    var pending = null, lastLink = null;

    function tell(host, settled) {
        if (!owner || !wrote) return;
        owner.invokeMethodAsync(wrote, read(host), !!settled);
    }

    function soon(host) {
        if (pending) { clearTimeout(pending); }
        pending = setTimeout(function () { pending = null; tell(host, false); }, 400);
    }

    function host() { return doc.querySelector('[data-aether-page]'); }

    function wire(field) {
        if (field.dataset.aetherWired) return;
        field.dataset.aetherWired = '1';

        var seed = field.getAttribute('data-seed');
        try { fill(field, seed ? JSON.parse(seed) : []); } catch (e) { fill(field, []); }

        field.addEventListener('input', function () {
            tidy(field);
            soon(field);
        });

        field.addEventListener('blur', function () {
            if (pending) { clearTimeout(pending); pending = null; }
            tidy(field);
            tell(field, true);
        });

        // Nothing arrives as markup. A paste from a web page carries its spans, its styles and
        // whatever else was in it; the words are what was wanted.
        field.addEventListener('paste', function (event) {
            event.preventDefault();
            var said = (event.clipboardData || global.clipboardData).getData('text/plain');
            doc.execCommand('insertText', false, said);
        });

        try { doc.execCommand('styleWithCSS', false, false); } catch (e) { /* older engines */ }
    }

    function sweep() { [].slice.call(doc.querySelectorAll('[data-aether-page]')).forEach(wire); }

    global.aetherPage = {
        wire: function (who, back) { owner = who; wrote = back; sweep(); },

        /** Turn the block the caret is in into another kind. */
        block: function (kind) {
            var field = host();
            if (!field) return false;

            var at = here(field);
            if (!at) return false;

            var was = at.getAttribute('data-k');
            var made = draw({
                k: kind,
                t: (kind === 'list' || kind === 'index') ? '' : inline(at),
                items: (kind === 'list' || kind === 'index') ? [inline(at)] : null,
            });

            field.replaceChild(made, at);
            if (kind === 'link') lastLink = made;
            caretTo(made.querySelector('li') || made, true);
            if (was !== kind) { tidy(field); tell(field, true); }
            return true;
        },

        /** Bold, italic, underline — on whatever is selected. */
        mark: function (name) {
            var field = host();
            if (!field) return false;
            doc.execCommand(name, false, null);
            tell(field, true);
            return true;
        },

        /** A line across the page, after the block the caret is in. */
        rule: function () {
            var field = host();
            var at = field && here(field);
            if (!at) return false;

            var line = draw({ k: 'rule' });
            var after = draw({ k: 'text', t: '' });
            at.parentNode.insertBefore(line, at.nextSibling);
            line.parentNode.insertBefore(after, line.nextSibling);
            caretTo(after, false);
            tell(field, true);
            return true;
        },

        /** A picture, at the caret — the one thing here that cannot be typed. */
        picture: function (hash, src) {
            var field = host();
            var at = field && here(field);
            if (!field) return false;

            var fig = draw({ k: 'image', hash: hash, src: src, t: '' });
            if (at) at.parentNode.insertBefore(fig, at.nextSibling);
            else field.appendChild(fig);

            var after = draw({ k: 'text', t: '' });
            fig.parentNode.insertBefore(after, fig.nextSibling);
            caretTo(after, false);
            tell(field, true);
            return true;
        },

        /**
         * Turn a setting on the block the caret is in on or off.
         *
         * Centred, wide, small — four of them exist in the card model and none of them had any
         * control at all once the wizard went. Pressing the same one twice takes it off again.
         */
        set: function (what, value) {
            var field = host();
            var at = field && here(field);
            if (!at) return false;

            var attr = (what === 'centre') ? 'data-a' : 'data-as';
            var want = (what === 'centre') ? 'centre' : value;

            if (at.getAttribute(attr) === want) at.removeAttribute(attr);
            else at.setAttribute(attr, want);

            tell(field, true);
            return true;
        },

        /** Where a link block goes. Set after the fact, because the address field takes the focus. */
        link: function (to) {
            var field = host();
            var at = lastLink && lastLink.parentNode ? lastLink : (field && here(field));
            if (!at) return false;

            at.setAttribute('data-k', 'link');
            at.setAttribute('data-to', to || '');
            tell(field, true);
            return true;
        },

        /** Where the link the caret is in currently goes. */
        linkTo: function () {
            var field = host();
            var at = field && here(field);
            return at && at.getAttribute('data-k') === 'link' ? (at.getAttribute('data-to') || '') : '';
        },

        /** Which kind the caret is sitting in, so the toolbar can say so. */
        at: function () {
            var field = host();
            var node = field && here(field);
            return node ? (node.getAttribute('data-k') || 'text') : '';
        },
    };

    doc.addEventListener('DOMContentLoaded', sweep);
    new MutationObserver(sweep).observe(doc.documentElement, { childList: true, subtree: true });
})(window);
