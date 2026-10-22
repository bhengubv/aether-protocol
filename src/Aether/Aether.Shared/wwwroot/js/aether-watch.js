// SPDX-License-Identifier: MIT
//
// Watch together: keep an HTML5 <video> in step across phones. The HOST's own controls are reported to
// .NET (which puts them on the mesh); every FOLLOWER applies what arrives back. Only the host is bound
// to report, and a follower only applies — so there is no feedback loop between the two.

const bound = new Map();

// Host side: report play / pause / seek as the person drives the element.
export function bind(id, dotnetRef) {
    const v = document.getElementById(id);
    if (!v) return false;
    unbind(id);

    const report = (kind) => {
        try { dotnetRef.invokeMethodAsync('OnHostAction', kind, Math.round((v.currentTime || 0) * 1000)); }
        catch (e) { /* the page went away */ }
    };
    const onPlay = () => report('play');
    const onPause = () => report('pause');
    const onSeeked = () => report('seek');

    v.addEventListener('play', onPlay);
    v.addEventListener('pause', onPause);
    v.addEventListener('seeked', onSeeked);
    bound.set(id, { v, onPlay, onPause, onSeeked });
    return true;
}

export function unbind(id) {
    const b = bound.get(id);
    if (!b) return;
    try {
        b.v.removeEventListener('play', b.onPlay);
        b.v.removeEventListener('pause', b.onPause);
        b.v.removeEventListener('seeked', b.onSeeked);
    } catch (e) { }
    bound.delete(id);
}

// Follower side: bring the element to the host's position and play-state, without fighting it on small
// drift — re-seeking every tick stutters and would re-fire events.
export function apply(id, positionMs, playing) {
    const v = document.getElementById(id);
    if (!v) return;

    const target = (positionMs || 0) / 1000;
    if (Math.abs((v.currentTime || 0) - target) > 0.75) {
        try { v.currentTime = target; } catch (e) { }
    }

    if (playing && v.paused) { v.play().catch(() => { }); }
    else if (!playing && !v.paused) { try { v.pause(); } catch (e) { } }
}

// The host's current position, for seeding a fresh follower or a sync tick.
export function positionMs(id) {
    const v = document.getElementById(id);
    return v ? Math.round((v.currentTime || 0) * 1000) : 0;
}
