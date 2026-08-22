// SPDX-License-Identifier: MIT
//
// Live video for a call, in the layer every head already shares.
//
// This replaces a native Android implementation — camera2 into a MediaCodec surface, decoded onto
// TextureViews layered UNDER the BlazorWebView. That runs on exactly one platform, and it spends its
// whole life fighting the host it lives in: the WebView has to be made transparent to see through it,
// which means the page has to be made transparent, which means the app bar and the tab bar have to be
// hidden — which is why a failure anywhere along that chain showed a blank screen rather than a
// missing picture. None of it could ever run on the web head, and none of it could ever run on iOS.
//
// The browser has already solved most of what that code was for:
//
//   * getUserMedia hands back a stream that is ALREADY the right way up. Every line of sensor-
//     orientation arithmetic, and the rotation byte that travelled on the wire beside the camera
//     state, existed only because a raw camera2 buffer is not.
//   * Mirroring a self-view is transform: scaleX(-1) on the local element and nothing else. That is
//     the whole of what "reversed" was.
//   * The picture is a DOM element, so it lays out WITH the controls instead of underneath them.
//
// What is left is genuinely ours: encode, decode, and handing bytes to the mesh.

const CODEC = 'avc1.42E01E';   // H.264 Baseline 3.0 — probed as supported on every head we ship to

// 640x360 at 400 kbps, 20fps.
//
// Not picked. 400 kbps is the WebRTC reference figure for H.264 webcam at this size, and this link is
// the constraint: Wi-Fi Direct measured about 2.5 Mbps TOTAL, carrying both directions plus the voice
// they share it with. The native path asked for 1280x720 at a nominal 800 kbps and measured 2.6 Mbps
// in each direction — four times what the radio could carry, which is what every symptom downstream
// was really about.
const WIDTH = 640;
const HEIGHT = 360;
const FPS = 20;
const BITRATE = 400000;

// A keyframe a second. More often than a recording would use, because a receiver that joins late —
// the camera went on mid-call, a frame was lost — can draw nothing until the next one arrives.
const KEYFRAME_EVERY_MS = 1000;

// Everything belonging to one running camera. Torn down as a unit.
let session = null;

// One decoder and one canvas per person on screen.
const peers = new Map();

let onChunk = null;      // .NET, waiting for encoded bytes
let onState = null;      // .NET, waiting for capture-state changes

// -- what this device can actually do ----------------------------------------

export async function capabilities() {
    const caps = {
        secure: isSecureContext,
        getUserMedia: !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia),
        webCodecs: typeof VideoEncoder === 'function' && typeof VideoDecoder === 'function',
        frameCallback: 'requestVideoFrameCallback' in HTMLVideoElement.prototype,
        codec: false,
        cameras: 0,
    };

    if (caps.webCodecs) {
        try {
            const probe = await VideoEncoder.isConfigSupported({
                codec: CODEC, width: WIDTH, height: HEIGHT,
                bitrate: BITRATE, framerate: FPS, avc: { format: 'annexb' },
            });
            caps.codec = !!probe.supported;
        } catch (e) { caps.codec = false; }
    }

    try {
        const devices = await navigator.mediaDevices.enumerateDevices();
        caps.cameras = devices.filter(d => d.kind === 'videoinput').length;
    } catch (e) { /* before permission this reads 0 or 1; not a failure */ }

    return caps;
}

// -- coming up ---------------------------------------------------------------

export async function start(chunkSink, stateSink, front) {
    if (session) return true;

    onChunk = chunkSink;
    onState = stateSink;
    raise('Starting');

    try {
        const stream = await navigator.mediaDevices.getUserMedia({
            audio: false,
            video: {
                width: { ideal: WIDTH },
                height: { ideal: HEIGHT },
                frameRate: { ideal: FPS, max: FPS },
                facingMode: front ? 'user' : 'environment',
            },
        });

        const video = document.getElementById('aether-local');
        if (!video) {
            stream.getTracks().forEach(t => t.stop());
            raise('Idle');
            return false;
        }

        video.srcObject = stream;
        video.muted = true;
        video.playsInline = true;
        try { await video.play(); } catch (e) { /* autoplay policy; the frame loop still runs */ }

        // Ask the camera what it actually gave us, and encode THAT.
        //
        // The size was hardcoded to the size that was requested, and a camera is under no obligation
        // to agree. Asked for 640x360 and handed back 360x640 — the browser had already turned the
        // picture upright, which is the entire reason none of the old sensor-orientation arithmetic is
        // needed here. Encoding a portrait frame into a landscape box squashed every face on the far
        // end, which is exactly the class of mistake the native path kept making with assumed
        // dimensions.
        const size = await measure(video);

        const encoder = new VideoEncoder({
            output: (chunk) => {
                const bytes = new Uint8Array(chunk.byteLength);
                chunk.copyTo(bytes);
                // Fire and forget. Awaiting here would stall the encoder's own output queue behind a
                // round trip into .NET, twenty times a second.
                if (onChunk) onChunk.invokeMethodAsync('ReceiveChunk', bytes);
            },
            error: (e) => {
                console.error('[aether-video] encoder failed', e);
                stop();
            },
        });

        encoder.configure({
            codec: CODEC,
            width: size.width, height: size.height,
            bitrate: BITRATE,
            framerate: FPS,
            latencyMode: 'realtime',
            // annexb puts SPS and PPS inline ahead of every keyframe, so a phone whose camera went on
            // mid-call starts decoding at the next one with nothing negotiated out of band. The native
            // path had to collect those by hand and re-inject them.
            avc: { format: 'annexb' },
        });

        session = {
            stream, video, encoder, front,
            width: size.width, height: size.height,
            lastKeyAt: 0, lastFrameAt: 0, stopped: false, bitrate: BITRATE,
        };

        pump();
        raise('Capturing');
        return true;
    } catch (e) {
        console.error('[aether-video] could not start', e);
        await stop();
        return false;
    }
}

// What the camera actually delivered, once it has delivered anything.
//
// videoWidth is 0 until metadata arrives, so this waits rather than assuming — and falls back to the
// requested size if the camera never says, because a squashed picture is still better than none.
async function measure(video) {
    if (video.videoWidth > 0) return { width: video.videoWidth, height: video.videoHeight };

    await new Promise((resolve) => {
        const done = () => { video.removeEventListener('loadedmetadata', done); resolve(); };
        video.addEventListener('loadedmetadata', done);
        setTimeout(done, 2000);
    });

    return video.videoWidth > 0
        ? { width: video.videoWidth, height: video.videoHeight }
        : { width: WIDTH, height: HEIGHT };
}

// One encoded frame per displayed frame, paced to FPS.
//
// requestVideoFrameCallback rather than MediaStreamTrackProcessor: the processor is Chromium-only and
// this has to run in WKWebView too. It also fires on real presentation, so pacing follows what the
// camera actually delivered rather than a timer's idea of it.
function pump() {
    const s = session;
    if (!s || s.stopped) return;

    const tick = (now) => {
        const cur = session;
        if (!cur || cur.stopped) return;

        try {
            const minGap = 1000 / FPS;
            if (now - cur.lastFrameAt >= minGap - 1) {
                cur.lastFrameAt = now;

                const key = (now - cur.lastKeyAt) >= KEYFRAME_EVERY_MS;
                if (key) cur.lastKeyAt = now;

                const frame = new VideoFrame(cur.video, { timestamp: Math.round(now * 1000) });
                // Never let the encoder build a backlog: a frame whose moment has passed is worthless,
                // and queueing it only makes the next one later still.
                if (cur.encoder.encodeQueueSize < 2) cur.encoder.encode(frame, { keyFrame: key });
                frame.close();
            }
        } catch (e) {
            console.warn('[aether-video] dropped a frame', e);
        }

        cur.video.requestVideoFrameCallback(tick);
    };

    s.video.requestVideoFrameCallback(tick);
}

// -- going away --------------------------------------------------------------

export async function stop() {
    const s = session;
    session = null;

    if (s) {
        s.stopped = true;
        try { if (s.encoder.state !== 'closed') s.encoder.close(); } catch (e) { }
        try { s.stream.getTracks().forEach(t => t.stop()); } catch (e) { }
        try { s.video.srcObject = null; } catch (e) { }
    }

    raise('Idle');
}

// Stop this device's camera and leave everybody else's picture alone.
export async function stopSending() { await stop(); }

// Everything, including the people we were watching.
export async function stopAll() {
    await stop();
    const who = Array.from(peers.keys());
    for (const w of who) forget(w);
}

// -- playing what arrives ----------------------------------------------------

export function play(who, bytes) {
    if (!who || !bytes || bytes.length === 0) return;

    let peer = peers.get(who);
    if (!peer) {
        const canvas = document.getElementById('aether-remote-' + cssSafe(who));
        if (!canvas) return;   // no tile laid out yet; the next keyframe is at most a second away

        const ctx = canvas.getContext('2d');
        const decoder = new VideoDecoder({
            output: (frame) => {
                try {
                    if (canvas.width !== frame.displayWidth) canvas.width = frame.displayWidth;
                    if (canvas.height !== frame.displayHeight) canvas.height = frame.displayHeight;
                    ctx.drawImage(frame, 0, 0);
                } finally {
                    frame.close();
                }
            },
            error: (e) => {
                console.warn('[aether-video] decoder failed for', who, e);
                forget(who);
            },
        });

        // optimizeForLatency: show the first frame as soon as it decodes rather than filling a reorder
        // buffer first. This is a conversation, not playback.
        decoder.configure({ codec: CODEC, optimizeForLatency: true });
        peer = { decoder: decoder, canvas: canvas, ctx: ctx, waitingForKey: true };
        peers.set(who, peer);
    }

    try {
        if (peer.decoder.state !== 'configured') return;

        // A decoder cannot start mid-GOP. Until the first keyframe arrives everything is dropped,
        // which costs at most a second and avoids a burst of errors nobody can act on.
        const key = isKeyframe(bytes);
        if (peer.waitingForKey) {
            if (!key) return;
            peer.waitingForKey = false;
        }

        peer.decoder.decode(new EncodedVideoChunk({
            type: key ? 'key' : 'delta',
            timestamp: Math.round(performance.now() * 1000),
            data: bytes,
        }));
    } catch (e) {
        console.warn('[aether-video] could not decode from', who, e);
        peer.waitingForKey = true;
    }
}

// Whether an annexb access unit carries an IDR.
//
// Read from the bytes rather than taken on trust, so a receiver never depends on a separate message
// arriving first. annexb puts SPS (7) and PPS (8) immediately ahead of every IDR (5).
function isKeyframe(bytes) {
    const limit = Math.min(bytes.length - 4, 512);
    for (let i = 0; i < limit; i++) {
        if (bytes[i] !== 0 || bytes[i + 1] !== 0) continue;
        if (bytes[i + 2] === 1 && nalIsKey(bytes[i + 3])) return true;
        if (bytes[i + 2] === 0 && bytes[i + 3] === 1 && nalIsKey(bytes[i + 4])) return true;
    }
    return false;
}

function nalIsKey(b) {
    const type = b & 0x1f;
    return type === 5 || type === 7 || type === 8;
}

export function forget(who) {
    const peer = peers.get(who);
    if (!peer) return;

    peers.delete(who);
    try { if (peer.decoder.state !== 'closed') peer.decoder.close(); } catch (e) { }
    try { peer.ctx.clearRect(0, 0, peer.canvas.width, peer.canvas.height); } catch (e) { }
}

// -- adjusting ---------------------------------------------------------------

// Re-aim the encoder at a bitrate the link can actually carry.
export function sizeToLink(bitrate) {
    const s = session;
    if (!s || s.stopped || bitrate <= 0) return;
    if (Math.abs(bitrate - s.bitrate) < s.bitrate / 10) return;

    try {
        // A reconfigure, not a new encoder: the stream keeps its sequence, so the far side sees a
        // change in quality rather than a gap it has to recover from.
        s.encoder.configure({
            codec: CODEC, width: s.width, height: s.height,
            bitrate: bitrate, framerate: FPS, latencyMode: 'realtime',
            avc: { format: 'annexb' },
        });
        s.bitrate = bitrate;
    } catch (e) {
        console.warn('[aether-video] could not change the bitrate', e);
    }
}

export function bitrateBps() { return session ? session.bitrate : 0; }

export async function switchCamera() {
    const s = session;
    if (!s) return false;

    const front = !s.front;
    const chunk = onChunk, state = onState;
    await stop();
    return await start(chunk, state, front);
}

// -- plumbing ----------------------------------------------------------------

// An AetherTag is already safe for an id; anything else might not be.
function cssSafe(who) { return String(who).replace(/[^A-Za-z0-9_-]/g, ''); }

function raise(state) {
    try { if (onState) onState.invokeMethodAsync('ReceiveState', state); }
    catch (e) { console.warn('[aether-video] could not report state', e); }
}
