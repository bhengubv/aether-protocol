// SPDX-License-Identifier: MIT
//
// The receive side of casting: a caster on the LAN told this phone to play a URL, and this drives the
// full-screen <video> that shows it. The element plays the URL directly (the bytes come from the caster's
// own HTTP server, by content hash); these helpers just apply the transport commands that arrive after.

export function load(id, url, dotnetRef) {
    const v = document.getElementById(id);
    if (!v) return false;
    v.src = url;
    // Report position back so the caster's progress bar and "is it playing" are truthful.
    v.ontimeupdate = () => {
        try { dotnetRef.invokeMethodAsync('OnProgress', Math.round((v.currentTime || 0) * 1000), Math.round((v.duration || 0) * 1000)); }
        catch (e) { /* the page went away */ }
    };
    v.onended = () => { try { dotnetRef.invokeMethodAsync('OnEnded'); } catch (e) { } };
    v.play().catch(() => { /* autoplay may need a tap; controls are shown */ });
    return true;
}

export function play(id) { const v = document.getElementById(id); if (v) v.play().catch(() => { }); }
export function pause(id) { const v = document.getElementById(id); if (v) { try { v.pause(); } catch (e) { } } }
export function seek(id, ms) { const v = document.getElementById(id); if (v) { try { v.currentTime = (ms || 0) / 1000; } catch (e) { } } }
export function stop(id) { const v = document.getElementById(id); if (v) { try { v.pause(); v.removeAttribute('src'); v.load(); } catch (e) { } } }
