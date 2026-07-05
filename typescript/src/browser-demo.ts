/**
 * Browser proof for the TypeScript WebRTC transport over the native DOM `RTCPeerConnection`.
 * Two in-page AetherNet nodes form a real `RTCDataChannel` via an in-memory signalling bus and
 * exchange a byte — the werift loopback test, running in the browser through the DOM backend (no
 * werift, no Node). Bundled by esbuild (see package.json `build:browser`) and loaded by index.html.
 *
 * SPDX-License-Identifier: MIT
 */

import { setDefaultPeerLinkFactory } from "./transport/webrtc/peer-link.js";
import { createDomPeerLink } from "./transport/webrtc/peer-link.dom.js";
import { WebRtcTransport } from "./transport/webrtc/WebRtcTransport.js";
import { InMemorySignalingBus } from "./transport/webrtc/signaling.js";

// Register the native-DOM backend for this browser build (the Node barrel registers werift instead).
setDefaultPeerLinkFactory(createDomPeerLink);

function logln(msg: string): void {
  console.log(msg);
  const out = document.getElementById("out");
  if (out) out.textContent += msg + "\n";
}

async function run(): Promise<void> {
  logln("aether TS demo — WebRTC loopback in the browser (native RTCPeerConnection, no werift)");

  const bus = new InMemorySignalingBus();
  const a = new WebRtcTransport("node-a", bus.endpoint("node-a"));
  const b = new WebRtcTransport("node-b", bus.endpoint("node-b"));

  const received = new Promise<Uint8Array>((resolve) => {
    b.onDataReceived = (_from, data) => resolve(data);
  });

  const payload = new TextEncoder().encode("AETHER-DOM-PING");
  logln("node-a → node-b: opening a browser RTCDataChannel and sending…");

  const ok = await a.sendAsync("node-b", payload);
  if (!ok) {
    logln("RESULT: FAIL — sendAsync returned false");
    return;
  }

  const timeout = new Promise<null>((resolve) => setTimeout(() => resolve(null), 20_000));
  const result = await Promise.race([received, timeout]);
  if (result) {
    logln(
      `node-b received ${result.length}B over the data channel: ${new TextDecoder().decode(result)}`,
    );
    logln("RESULT: PASS");
  } else {
    logln("RESULT: FAIL — timeout waiting for the data-channel echo");
  }
}

void run().catch((e: unknown) => logln("RESULT: FAIL — " + String(e)));
