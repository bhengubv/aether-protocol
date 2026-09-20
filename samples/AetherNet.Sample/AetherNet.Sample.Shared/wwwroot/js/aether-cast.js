// SPDX-License-Identifier: MIT
//
// The receive side of casting: a caster on the LAN told this phone to play a video, and this drives the
// full-screen <video> that shows it.
//
// The bytes cannot be handed to the <video> as the caster's http:// URL: the app is served to this WebView
// over https://, and Chromium blocks an http:// media subresource on an https page as "mixed content" —
// and setMixedContentMode(ALWAYS_ALLOW) does NOT override it for media in modern WebView (proven on device).
// So C# fetches the bytes on the Android network stack (no Blink policy applies there) and streams them in
// here; we wrap them in a same-origin blob: URL, which no mixed-content rule can touch. loadStream is the
// path the receiver uses; load (a direct URL) stays for a same-origin or directly-playable source.

function wire(v, dotnetRef) {
    const say = (m, ...a) => { try { dotnetRef.invokeMethodAsync(m, ...a); } catch (e) { /* the page went away */ } };
    // Tell C# what the <video> is actually doing, so the overlay shows a truthful state instead of a blank frame.
    v.onloadstart = () => say('OnState', 'loading');
    v.onwaiting = () => say('OnState', 'loading');
    v.oncanplay = () => say('OnState', 'playing');
    v.onplaying = () => say('OnState', 'playing');
    v.onerror = () => say('OnError', (v.error && v.error.code) || 0);
    // Report position back so the caster's progress bar and "is it playing" are truthful.
    v.ontimeupdate = () => say('OnProgress', Math.round((v.currentTime || 0) * 1000), Math.round((v.duration || 0) * 1000));
    v.onended = () => say('OnEnded');
}

function revoke(v) {
    if (v && v._aetherUrl) { try { URL.revokeObjectURL(v._aetherUrl); } catch (e) { } v._aetherUrl = null; }
}

// A directly-playable, same-origin URL (kept for completeness; the receiver uses loadStream).
export function load(id, url, dotnetRef) {
    const v = document.getElementById(id);
    if (!v) return false;
    wire(v, dotnetRef);
    revoke(v);
    v.src = url;
    v.play().catch(() => { /* autoplay may need a tap; controls are shown */ });
    return true;
}

// The bytes arrive from C# (which fetched them off the LAN). Wrap them in a blob so the <video> plays a
// same-origin source — no mixed content. Download progress is shown by C# while it reads; this is the
// hand-off once the bytes are here.
export async function loadStream(id, streamRef, contentType, dotnetRef) {
    const v = document.getElementById(id);
    if (!v) return false;
    wire(v, dotnetRef);
    try {
        const buf = await streamRef.arrayBuffer();
        revoke(v);
        v._aetherUrl = URL.createObjectURL(new Blob([buf], { type: contentType || 'video/mp4' }));
        v.src = v._aetherUrl;
        v.play().catch(() => { /* autoplay may need a tap; controls are shown */ });
    } catch (e) {
        try { dotnetRef.invokeMethodAsync('OnError', 2); } catch (_) { }
    }
    return true;
}

export function play(id) { const v = document.getElementById(id); if (v) v.play().catch(() => { }); }
export function pause(id) { const v = document.getElementById(id); if (v) { try { v.pause(); } catch (e) { } } }
export function seek(id, ms) { const v = document.getElementById(id); if (v) { try { v.currentTime = (ms || 0) / 1000; } catch (e) { } } }
export function stop(id) { const v = document.getElementById(id); if (v) { try { v.pause(); revoke(v); v.removeAttribute('src'); v.load(); } catch (e) { } } }
