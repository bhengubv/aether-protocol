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
// Fifteen frames a second.
//
// It was eight, and before that twenty. Eight was chosen against the JavaScript bridge: every frame
// crossed it twice, out as an encoded chunk and back in as somebody else's to draw, and on a Redmi
// Note 9 that saturated at about four each way before the answers stopped coming back at all.
//
// Frames do not cross that bridge any more — they go over a WebSocket to a server inside this app,
// on loopback, which carries binary, has nothing else on it and no dispatcher in front of it. So the
// number that was chosen for the bridge is no longer the number that matters.
const FPS = 15;
const BITRATE = 400000;

// A keyframe a second. More often than a recording would use, because a receiver that joins late —
// the camera went on mid-call, a frame was lost — can draw nothing until the next one arrives.
/// What to capture at while this device is ALSO decoding somebody.
///
/// Ten, and this is now about the silicon rather than about any bridge.
///
/// With frames on their own socket both phones push fourteen a second and the P30 draws all of it,
/// steadily, for six minutes. merlin does not: it encodes fourteen happily and draws nothing while
/// doing so, stalling two to four times a minute. Decoding alone it manages 7.3 indefinitely, so the
/// ceiling is the two jobs together on a Helio G85, not either one.
///
/// This was five when frames crossed the JavaScript bridge, which was a different constraint that
/// happened to need a similar number. Kept separate from FPS precisely so the two can move apart.
///
/// Encoding and decoding are the two expensive things a video call does, and doing both at once costs
/// roughly double. Measured on merlin, the slower of the two handsets: decoding alone it holds 7.3
/// frames a second indefinitely with memory falling, and encoding at the same time it collapses after
/// four or five minutes — draw rate to zero, memory climbing, decoder rebuilds achieving nothing.
///
/// The decoder was never broken. It was being asked for more than the device had, so it is asked for
/// less: five frames a second while both cameras are on, eight when only this one is.
const FPS_BOTH_WAYS = 10;

const KEYFRAME_EVERY_MS = 1000;

/// How long a recovering decoder waits for a keyframe before giving up on finding one.
///
/// Three keyframe intervals. Long enough that a genuine gap is ridden out, short enough that a
/// decoder which is never going to recognise one does not sit black indefinitely.
const KeyframePatience = 3000;

// Everything belonging to one running camera. Torn down as a unit.
let session = null;

// One decoder and one canvas per person on screen.
const peers = new Map();

// How many frames may be crossing into .NET at once.
//
// Three is about a sixth of a second at twenty frames a second — enough to ride out a momentary
// stall in the bridge, far too few to accumulate the delay that made one phone send ten frames a
// second and deliver one and a half.
const MaxInFlight = 3;

/// How long a crossing may take before it is written off.
///
/// A frame handed to .NET is answered through the same bridge it went out on, so when that bridge
/// congests the ANSWERS are what stop arriving — not the frames. Measured on merlin: three in flight,
/// permanently, promises that never settled, and capture correctly refusing to add to the pile. The
/// backpressure worked and the result was no video at all, for the rest of the call.
///
/// Two seconds is far longer than a crossing can usefully take. Writing one off does not resend it —
/// a frame that old is worthless — it just stops one lost answer from being a permanent stop.
const InFlightTimeout = 2000;

let inFlight = 0;
let oldestInFlight = 0;

/// The frame channel, when one is open. Null means frames travel by interop.
let sock = null;

let onChunk = null;      // .NET, waiting for encoded bytes
let onState = null;      // .NET, waiting for capture-state changes

// -- counting what actually happens ------------------------------------------
//
// Every diagnosis in this area that was made by reasoning about the code turned out to be wrong, and
// each wrong one cost a build, an install and a two-minute wait for the radio to come back. A picture
// that is not appearing has about six places it can be lost, and they are indistinguishable from the
// outside — so each one is counted, and stats() reads them out.
const stat = {
    ticks: 0,          // requestVideoFrameCallback fired
    framesMade: 0,     // a VideoFrame was constructed from the video element
    encodeSkipped: 0,  // encoder queue was full, frame dropped before encoding
    encoded: 0,        // the encoder produced a chunk
    sentToNet: 0,      // a chunk was handed to .NET
    encoderErrors: 0,
    sameFrame: 0,      // the camera had not produced a new frame yet
    pacedDown: 0,      // skipped because this device is decoding as well as encoding
    captureErrors: 0,
    interopBusy: 0,    // skipped because .NET had not taken the last frames yet
    interopErrors: 0,
    interopTimeouts: 0,   // answers that never came back at all
    viaBridge: 0,         // frames that took the socket rather than the interop bridge
    bridgeOpened: 0,
    bridgeErrors: 0,

    played: 0,         // .NET handed us bytes to show
    noTile: 0,         // ...but there was no canvas to draw on yet
    awaitingKey: 0,    // ...dropped because the decoder has not had a keyframe
    decoded: 0,        // handed to the decoder
    drawn: 0,          // actually painted
    decodeErrors: 0,
    decoderResets: 0,
    stalls: 0,         // fed, but producing nothing — a broken reference chain
    keyframeGaveUp: 0, // waited too long for a keyframe and fed the decoder anyway
};

/// What the pipeline has actually done. Reset every time it is read, so two reads bracket a window.
export function stats() {
    const snapshot = { ...stat, at: Math.round(performance.now()) };
    snapshot.inFlight = inFlight;
    snapshot.bridge = sock ? { state: sock.readyState, buffered: sock.bufferedAmount } : null;
    snapshot.session = session
        ? { encoderState: session.encoder.state, queue: session.encoder.encodeQueueSize,
            size: session.width + 'x' + session.height, bitrate: session.bitrate,
            videoW: session.video.videoWidth, videoH: session.video.videoHeight,
            paused: session.video.paused, ended: session.video.srcObject
                ? session.video.srcObject.getVideoTracks().map(t => t.readyState).join(',') : 'no stream' }
        : null;
    snapshot.peers = [...peers.entries()].map(([who, p]) => ({
        who, state: p.decoder.state, waitingForKey: p.waitingForKey,
        canvas: p.canvas.width + 'x' + p.canvas.height,
    }));
    for (const k of Object.keys(stat)) stat[k] = 0;
    return snapshot;
}

// -- what this device can actually do ----------------------------------------

export async function capabilities() {
    const caps = {
        secure: isSecureContext,
        getUserMedia: !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia),
        webCodecs: typeof VideoEncoder === 'function' && typeof VideoDecoder === 'function',
        // Not required any more — capture runs on a timer, because rVFC only fires while the
        // element is painted and a call must keep sending when its preview is not on screen.
        frameCallback: true,
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

export async function start(chunkSink, stateSink, front, bridge) {
    if (session) return true;

    onChunk = chunkSink;
    onState = stateSink;
    inFlight = 0;
    oldestInFlight = 0;

    // Frames get their own channel where one is offered. See openBridge.
    if (bridge && bridge.port) await openBridge(bridge);
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
                stat.encoded++;

                // The socket, if it is up. No promise to wait on, no dispatcher, no base64 — just
                // bytes on a connection that carries nothing else.
                if (sock && sock.readyState === 1) {
                    try {
                        sock.send(bytes);
                        stat.sentToNet++;
                        stat.viaBridge++;
                    } catch (e) {
                        stat.bridgeErrors++;
                    }
                    return;
                }

                if (!onChunk) return;

                // Count what is genuinely in flight.
                //
                // invokeMethodAsync returns a promise and this never waited for it, so the only limit
                // on how many frames could be queued into the bridge was how fast the camera ran.
                // Measured on merlin: JavaScript handed .NET ten frames a second and the radio saw
                // 1.4 — nothing refused, nothing dropped by the transport, the rest simply piling up
                // in the crossing. A backlog in a real-time path is worse than a gap: every frame in
                // it is already too late by the time it moves.
                if (inFlight === 0) oldestInFlight = performance.now();
                inFlight++;
                stat.sentToNet++;

                const settle = () => {
                    inFlight = Math.max(0, inFlight - 1);
                    if (inFlight === 0) oldestInFlight = 0;
                };
                onChunk.invokeMethodAsync('ReceiveChunk', bytes)
                    .then(settle, () => { settle(); stat.interopErrors++; });
            },
            error: (e) => {
                stat.encoderErrors++;
                stat.lastEncoderError = String(e && e.message || e);
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
            lastKeyAt: 0, lastStamp: -1, lastSentAt: 0, timer: null, stopped: false, bitrate: BITRATE,
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

// Capture, on a clock of our own.
//
// This used to be driven by requestVideoFrameCallback, which is the "correct" API and the wrong
// choice here: it only fires while the element is being PAINTED. Capture therefore depended on the
// little self-view being composited — so anything that stopped it being drawn stopped the camera
// being sent, silently, with the track still reporting "live" and the encoder still "configured".
//
// Measured during a five-minute call: ticks 0, framesMade 0, sentToNet 0, on a video element that
// reported live, unpaused, 360x640, with the screen awake and the page visible. The far end simply
// stopped seeing anything and nothing anywhere said why. Worse, the chain is self-terminating: it
// re-arms itself from inside its own callback, so one missed call ends capture for the rest of the
// session with no way back.
//
// A timer does not care whether anything is on screen. new VideoFrame(video) reads the element's
// current frame regardless of paint, so this keeps sending while the preview is hidden, occluded, or
// behind whatever else the person has done with their phone — which is what a call has to do.
function pump() {
    const s = session;
    if (!s || s.stopped) return;

    s.timer = setInterval(() => {
        const cur = session;
        if (!cur || cur.stopped) return;

        try {
            // Nothing new to send. The camera runs slower than this in poor light — 4fps on a dark
            // room is ordinary — and re-encoding a frame already sent costs bytes for no picture.
            if (cur.video.readyState < 2) return;
            if (cur.video.currentTime === cur.lastStamp) { stat.sameFrame++; return; }

            // Slower while somebody else's picture is also being decoded here.
            const target = peers.size > 0 ? FPS_BOTH_WAYS : FPS;
            const now0 = performance.now();
            if (now0 - cur.lastSentAt < (1000 / target) - 2) { stat.pacedDown++; return; }
            cur.lastSentAt = now0;

            cur.lastStamp = cur.video.currentTime;

            stat.ticks++;

            const now = performance.now();
            const key = (now - cur.lastKeyAt) >= KEYFRAME_EVERY_MS;
            if (key) cur.lastKeyAt = now;

            // Never let the encoder build a backlog: a frame whose moment has passed is worthless,
            // and queueing it only makes the next one later still.
            if (cur.encoder.encodeQueueSize >= 2) { stat.encodeSkipped++; return; }

            // Nor let the crossing into .NET build one. Checked BEFORE encoding, so a frame that
            // could not be delivered is never paid for — on a phone where the bridge is the narrow
            // part, encoding into a queue is spending battery to make the picture later.
            if (!(sock && sock.readyState === 1) && inFlight >= MaxInFlight) {
                // Nothing has come back for a long time. The answers are lost rather than late, so
                // hold the count open no longer — otherwise one congested moment stops video for the
                // rest of the call, which is what happened.
                if (oldestInFlight && performance.now() - oldestInFlight > InFlightTimeout) {
                    stat.interopTimeouts++;
                    inFlight = 0;
                    oldestInFlight = 0;
                } else {
                    stat.interopBusy++;
                    return;
                }
            }

            const frame = new VideoFrame(cur.video, { timestamp: Math.round(now * 1000) });
            stat.framesMade++;
            try { cur.encoder.encode(frame, { keyFrame: key }); }
            finally { frame.close(); }
        } catch (e) {
            stat.captureErrors++;
            stat.lastCaptureError = String(e && e.message || e);
        }
    }, Math.round(1000 / FPS));
}

// -- going away --------------------------------------------------------------

export async function stop() {
    const s = session;
    session = null;

    if (s) {
        s.stopped = true;
        try { if (s.timer) clearInterval(s.timer); } catch (e) { }
        closeBridge();
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

    stat.played++;

    let peer = peers.get(who);
    if (!peer) {
        const canvas = document.getElementById('aether-remote-' + cssSafe(who));
        if (!canvas) { stat.noTile++; return; }   // no tile yet; the next keyframe is a second away

        const ctx = canvas.getContext('2d');
        peer = {
            decoder: null, canvas: canvas, ctx: ctx,
            waitingForKey: true, waitingSince: performance.now(),
            lastDrawAt: performance.now(), fedSinceDraw: 0,
            stamp: 0, rebuilds: 0,
        };
        peer.decoder = makeDecoder(peer, who);
        peers.set(who, peer);
    }

    try {
        if (!peer.decoder || peer.decoder.state !== 'configured') return;

        // A decoder cannot start mid-GOP. Until the first keyframe arrives everything is dropped,
        // which costs at most a second and avoids a burst of errors nobody can act on.
        const key = isKeyframe(bytes);
        if (peer.waitingForKey) {
            if (key) {
                peer.waitingForKey = false;
            } else if (performance.now() - peer.waitingSince > KeyframePatience) {
                // Waited long enough. A keyframe should arrive every second, so if none has been
                // recognised in three there is something wrong with the recognising rather than with
                // the sending — and sitting black forever on that basis is the worse failure.
                //
                // Measured on merlin: the decoder reset twice inside one minute and drew nothing at
                // all for the whole of it, while frames arrived the entire time at seven a second.
                // Feeding it produces either a picture or a real error, and either is progress.
                stat.keyframeGaveUp++;
                peer.waitingForKey = false;
            } else {
                stat.awaitingKey++;
                return;
            }
        }

        // Being fed and producing nothing.
        //
        // A decoder handed a chain with a link missing does not complain — it accepts every frame and
        // outputs none of them, with no error to react to, until a keyframe arrives. So silence has to
        // be treated as a fault in its own right, because it is the only symptom there is. Measured on
        // device before this existed: frames decoded 4 a second, frames drawn zero, decodeErrors zero,
        // for minutes.
        peer.fedSinceDraw++;
        if (peer.fedSinceDraw > 8 && performance.now() - peer.lastDrawAt > 700) {
            stat.stalls++;
            recover(peer, who);
            if (!isKeyframe(bytes)) { stat.awaitingKey++; return; }
        }

        stat.decoded++;

        // A counter, not a clock.
        //
        // This stamped every chunk with performance.now(), which is neither the sender's timeline nor
        // guaranteed to differ between two chunks arriving in the same millisecond — and a decoder
        // handed equal or decreasing timestamps is entitled to do anything it likes, including
        // quietly producing nothing. A counter is monotonic by construction. The values need not
        // match the sender's; nothing here reorders, and every frame is shown the moment it decodes.
        peer.stamp += Math.round(1000000 / FPS);

        peer.decoder.decode(new EncodedVideoChunk({
            type: key ? 'key' : 'delta',
            timestamp: peer.stamp,
            data: bytes,
        }));
    } catch (e) {
        stat.decodeErrors++;
        stat.decoderResets++;
        stat.lastDecodeError = String(e && e.message || e);
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

// Put a stalled decoder back to a state it can start from.
//
// Reset rather than close: the canvas, the tile and the peer entry all stay, so nothing on screen
// jumps. It picks up again on the next keyframe, which is never more than a second away.
// Build a decoder for one person.
//
// optimizeForLatency: show the first frame as soon as it decodes rather than filling a reorder buffer
// first. This is a conversation, not playback.
function makeDecoder(peer, who) {
    const decoder = new VideoDecoder({
        output: (frame) => {
            try {
                const c = peer.canvas;
                if (c.width !== frame.displayWidth) c.width = frame.displayWidth;
                if (c.height !== frame.displayHeight) c.height = frame.displayHeight;
                peer.ctx.drawImage(frame, 0, 0);
                stat.drawn++;
                peer.lastDrawAt = performance.now();
                peer.fedSinceDraw = 0;
            } finally {
                frame.close();
            }
        },
        error: (e) => {
            stat.decodeErrors++;
            stat.lastDecodeError = String(e && e.message || e);
            console.warn('[aether-video] decoder failed for', who, e);
            recover(peer, who);
        },
    });

    decoder.configure({ codec: CODEC, optimizeForLatency: true });
    return decoder;
}

// Put a stalled decoder back to a state it can start from.
//
// It used to reset() and reconfigure the same decoder, and that is not enough for one that has
// genuinely wedged: measured on merlin, the decoder accepted frames and produced nothing for minutes,
// through repeated resets, with keyframes arriving and being recognised the whole time. A hardware
// decoder in that state does not come back from reset — it comes back from being replaced.
//
// The canvas, the tile and the peer entry all survive, so nothing on screen jumps.
function recover(peer, who) {
    try { if (peer.decoder && peer.decoder.state !== 'closed') peer.decoder.close(); }
    catch (e) { /* it was already gone */ }

    try {
        peer.decoder = makeDecoder(peer, who);
    } catch (e) {
        console.warn('[aether-video] could not rebuild the decoder for', who, e);
        peer.decoder = null;
    }

    peer.waitingForKey = true;
    peer.waitingSince = performance.now();
    peer.fedSinceDraw = 0;
    peer.lastDrawAt = performance.now();
    peer.rebuilds++;
    stat.decoderResets++;
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

// -- the frame channel ------------------------------------------------------
//
// A WebSocket to a server inside this same app, on loopback. It never reaches a network interface.
//
// It exists because Blazor's interop is one message channel shared by every call in both directions
// and by the renderer's dispatcher — fine for a button, wrong for a video call, where every frame
// crosses it twice. Measured on a Redmi Note 9: about four frames a second each way before the
// ANSWERS stopped coming back and video stopped with them.
//
// Frames in from .NET arrive here too, tagged: one byte of length, the peer's tag, then the frame.
async function openBridge(bridge) {
    closeBridge();

    return new Promise((resolve) => {
        let settled = false;
        const done = (ok) => { if (!settled) { settled = true; resolve(ok); } };

        try {
            // The token is in the path because a browser cannot set headers on a WebSocket. Loopback
            // is not private on Android — any app here can reach 127.0.0.1 — so without it anything
            // installed on this phone could read one side of a call and inject frames into the other.
            const ws = new WebSocket(`ws://127.0.0.1:${bridge.port}/video/${bridge.token}`);
            ws.binaryType = 'arraybuffer';

            ws.onopen = () => { sock = ws; stat.bridgeOpened++; done(true); };

            ws.onmessage = (m) => {
                try {
                    const buf = new Uint8Array(m.data);
                    if (buf.length < 2) return;
                    const tagLen = buf[0];
                    if (buf.length < 1 + tagLen + 1) return;
                    const who = new TextDecoder().decode(buf.subarray(1, 1 + tagLen));
                    play(who, buf.subarray(1 + tagLen));
                } catch (e) {
                    stat.bridgeErrors++;
                }
            };

            ws.onerror = () => { stat.bridgeErrors++; done(false); };

            // Falling back is not a failure. A head with no server, or a socket that goes away
            // mid-call, simply means frames travel by interop again — slower, and still a call.
            ws.onclose = () => { if (sock === ws) sock = null; done(false); };

            setTimeout(() => done(false), 3000);
        } catch (e) {
            stat.bridgeErrors++;
            done(false);
        }
    });
}

function closeBridge() {
    const w = sock;
    sock = null;
    try { if (w && w.readyState <= 1) w.close(); } catch (e) { }
}

function raise(state) {
    try { if (onState) onState.invokeMethodAsync('ReceiveState', state); }
    catch (e) { console.warn('[aether-video] could not report state', e); }
}
