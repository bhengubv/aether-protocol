/**
 * Acceptance tests for the transport-backed WebRTC signalling carrier (RelayWebRtcSignaling).
 * SPDX-License-Identifier: MIT
 *
 * Two SEPARATE carrier instances (two separate nodes) exchange the SDP/ICE handshake over a real
 * transport seam — an in-process loopback pair standing in for the AetherNet relay / mesh, exactly
 * as the C# `LoopbackTransport` and the Go in-process loopback do. Signalling is out-of-band: this
 * touches NO mesh wire-serialization and NO fixtures.
 *
 * Levels covered:
 *   1. Interop byte-identity — a framed signal equals the SHARED cross-language fixture bytes
 *      (magic `AWS1` + JSON) at fixtures/webrtc/expected/<name>.bin, built from the one committed
 *      fixtures/webrtc/inputs.json, and decode round-trips losslessly. This is the same oracle the
 *      C#, Go, and other language legs assert against.
 *   2. Transport round-trip — the two carriers round-trip an offer AND an answer over the loopback
 *      transport, decoding back to the original signal.
 *   3. Full werift handshake — two real `WebRtcTransport` instances whose signalling rides the
 *      transport-backed carriers negotiate a direct data channel over the loopback and carry bytes.
 *
 * Run with: node --import tsx --test tests/transport_webrtc_relay_signaling.test.ts
 */

import { describe, it, test } from "node:test";
import { strict as assert } from "node:assert";
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

import { ITransportService, PerTransportMetrics } from "../src/transport/ITransportService.js";
import {
  WebRtcTransport,
  RelayWebRtcSignaling,
  encodeSignalFrame,
  decodeSignalFrame,
  type Signal,
  SignalType,
} from "../src/transport/webrtc/index.js";

// ── In-process loopback transport pair (the "relay" the carriers ride) ──────────

/**
 * Minimal in-process {@link ITransportService} that delivers everything it sends to its paired
 * instance — a stand-in for the QUIC/HTTP relay so the carrier is exercised over a real
 * ITransportService seam without a network. Mirrors the C# `LoopbackTransport`.
 */
class LoopbackTransport implements ITransportService {
  peer?: LoopbackTransport;

  readonly name = "Loopback";
  isAvailable = true;
  readonly maxBandwidthBps = Number.MAX_SAFE_INTEGER;
  readonly maxRangeMeters = 0;
  readonly powerCostRelative = 100;
  readonly maxConcurrentPeers = 2;
  readonly metrics = new PerTransportMetrics();
  onDataReceived?: (senderUhid: string, data: Uint8Array) => void;

  constructor(private readonly localUhid: string) {}

  async sendAsync(_peerUhid: string, data: Uint8Array): Promise<boolean> {
    const peer = this.peer;
    if (peer === undefined) return false;
    // Copy so the far end can never observe later mutation, and deliver off the sender's stack
    // (ordered, reliable) — matching a real signalling channel.
    const copy = new Uint8Array(data);
    queueMicrotask(() => peer.onDataReceived?.(this.localUhid, copy));
    return true;
  }

  async sendStreamAsync(): Promise<boolean> {
    throw new Error("not supported");
  }

  isConnected(): boolean {
    return this.peer !== undefined;
  }
}

/** Wires two loopback endpoints to each other. */
function loopbackPair(a: string, b: string): [LoopbackTransport, LoopbackTransport] {
  const left = new LoopbackTransport(a);
  const right = new LoopbackTransport(b);
  left.peer = right;
  right.peer = left;
  return [left, right];
}

// ── Level 1: interop byte-identity against the shared cross-language fixture ─────
// The one committed oracle lives at fixtures/webrtc/{inputs.json,expected/<name>.bin} and is shared
// byte-for-byte with the C#, Go, and other language legs. Each expected/<name>.bin is the exact
// `AWS1` + JSON frame. No golden bytes are hardcoded here; they are read from that fixture.

/** One shared-fixture input case (fields per fixtures/webrtc/inputs.json). */
interface WebRtcInput {
  name: string;
  from_uhid: string;
  to_uhid: string;
  type: number;
  sdp?: string;
  candidate?: string;
  sdp_mid?: string;
  sdp_mline_index?: number;
}

const here = path.dirname(fileURLToPath(import.meta.url));

/** Walk up from this test file until the shared fixtures/webrtc directory is found. */
function fixturesDir(): string {
  let dir = here;
  for (let i = 0; i < 10; i++) {
    if (existsSync(path.join(dir, "fixtures", "webrtc", "inputs.json"))) {
      return path.join(dir, "fixtures", "webrtc");
    }
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  throw new Error("fixtures/webrtc/inputs.json not found from " + here);
}

function loadInputs(): WebRtcInput[] {
  return JSON.parse(readFileSync(path.join(fixturesDir(), "inputs.json"), "utf8"));
}

/**
 * Builds the {@link Signal} for a fixture case. An empty (or omitted) sdp / candidate / sdp_mid is
 * treated as omitted — the field stays `undefined` so the frame drops it, exactly as the fixture
 * generator intends.
 */
function signalFor(input: WebRtcInput): Signal {
  const nonEmpty = (s: string | undefined): string | undefined =>
    s !== undefined && s.length > 0 ? s : undefined;
  return {
    fromUhid: input.from_uhid,
    toUhid: input.to_uhid,
    type: input.type as SignalType,
    sdp: nonEmpty(input.sdp),
    candidate: nonEmpty(input.candidate),
    sdpMid: nonEmpty(input.sdp_mid),
    sdpMLineIndex: input.sdp_mline_index,
  };
}

describe("RelayWebRtcSignaling — wire byte-identity with the shared cross-language fixture", () => {
  for (const input of loadInputs()) {
    test(`webrtc fixture encode ${input.name}`, () => {
      const got = encodeSignalFrame(signalFor(input));
      const expected = new Uint8Array(
        readFileSync(path.join(fixturesDir(), "expected", input.name + ".bin")),
      );
      // Byte-for-byte identity with the committed oracle frame.
      assert.deepEqual([...got], [...expected]);
    });

    test(`webrtc fixture decode round-trips ${input.name}`, () => {
      const expected = new Uint8Array(
        readFileSync(path.join(fixturesDir(), "expected", input.name + ".bin")),
      );
      const decoded = decodeSignalFrame(expected);
      assert.notEqual(decoded, undefined);
      // Decoding is lossless: re-encoding the decoded signal reproduces the exact oracle bytes.
      assert.deepEqual([...encodeSignalFrame(decoded!)], [...expected]);
      // And the meaningful fields survived the trip.
      assert.equal(decoded!.fromUhid, input.from_uhid);
      assert.equal(decoded!.toUhid, input.to_uhid);
      assert.equal(decoded!.type, input.type);
      // sdp / candidate / sdpMid are present iff the fixture supplied a non-empty value.
      assert.equal(decoded!.sdp, input.sdp && input.sdp.length > 0 ? input.sdp : undefined);
      assert.equal(
        decoded!.candidate,
        input.candidate && input.candidate.length > 0 ? input.candidate : undefined,
      );
      assert.equal(
        decoded!.sdpMid,
        input.sdp_mid && input.sdp_mid.length > 0 ? input.sdp_mid : undefined,
      );
      // The wire always carries SdpMLineIndex (a non-nullable ushort), so a decode surfaces it,
      // defaulting to 0 when the fixture omitted it.
      assert.equal(decoded!.sdpMLineIndex, input.sdp_mline_index ?? 0);
    });
  }
});

// ── Level 2: transport round-trip between two separate carriers ─────────────────

/** Resolves with the first signal a carrier receives, or rejects on timeout. */
function firstSignal(carrier: RelayWebRtcSignaling, timeoutMs: number): Promise<Signal> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("timed out waiting for a signal")), timeoutMs);
    carrier.onSignal((s) => {
      clearTimeout(timer);
      resolve(s);
    });
  });
}

describe("RelayWebRtcSignaling — two separate carriers over an in-process transport pair", () => {
  it("round-trips an offer AND an answer over the transport", async () => {
    const [aliceRelay, bobRelay] = loopbackPair("alice", "bob");
    const aliceSig = new RelayWebRtcSignaling(aliceRelay);
    const bobSig = new RelayWebRtcSignaling(bobRelay);

    try {
      // alice → bob: offer
      const bobGetsOffer = firstSignal(bobSig, 5_000);
      const offer: Signal = {
        fromUhid: "alice",
        toUhid: "bob",
        type: SignalType.Offer,
        sdp: "v=0\r\no=- 42 42 IN IP4 0.0.0.0\r\na=ice-ufrag:AB+cd",
      };
      assert.equal(await aliceSig.sendSignal("bob", offer), true, "offer send should succeed");
      const gotOffer = await bobGetsOffer;
      // The C# wire always carries SdpMLineIndex (a non-nullable ushort), so a decoded offer surfaces
      // it as 0. Assert the meaningful fields survived the trip byte-for-byte.
      assert.equal(gotOffer.fromUhid, "alice");
      assert.equal(gotOffer.toUhid, "bob");
      assert.equal(gotOffer.type, SignalType.Offer);
      assert.equal(gotOffer.sdp, offer.sdp, "bob must receive the exact SDP (incl. base64 '+') alice sent");
      assert.equal(gotOffer.sdpMLineIndex, 0);
      assert.equal(gotOffer.candidate, undefined);

      // bob → alice: answer
      const aliceGetsAnswer = firstSignal(aliceSig, 5_000);
      const answer: Signal = {
        fromUhid: "bob",
        toUhid: "alice",
        type: SignalType.Answer,
        sdp: "v=0\r\na=ice-pwd:ZZ/xy+00",
      };
      assert.equal(await bobSig.sendSignal("alice", answer), true, "answer send should succeed");
      const gotAnswer = await aliceGetsAnswer;
      assert.equal(gotAnswer.fromUhid, "bob");
      assert.equal(gotAnswer.toUhid, "alice");
      assert.equal(gotAnswer.type, SignalType.Answer);
      assert.equal(gotAnswer.sdp, answer.sdp, "alice must receive the exact SDP (incl. '/'+'+') bob sent");
      assert.equal(gotAnswer.sdpMLineIndex, 0);
    } finally {
      aliceSig.dispose();
      bobSig.dispose();
    }
  });

  it("ignores non-AWS1 app bytes arriving on the same transport", async () => {
    const [selfRelay, peerRelay] = loopbackPair("self", "peer");
    const selfSig = new RelayWebRtcSignaling(selfRelay);

    let raised = false;
    selfSig.onSignal(() => {
      raised = true;
    });

    try {
      // Drive plain (unframed) app bytes into `self` from its peer.
      assert.equal(
        await peerRelay.sendAsync("self", new TextEncoder().encode("ordinary app data")),
        true,
      );
      // Let the microtask delivery run.
      await new Promise<void>((r) => setTimeout(r, 20));
      assert.equal(raised, false, "non-prefixed app bytes must not decode as signalling");
    } finally {
      selfSig.dispose();
    }
  });
});

// ── Level 3: full werift handshake driven over transport-backed carriers ─────────

/** Resolves with the first payload `to` receives from `fromUhid`, or rejects on timeout. */
function firstPayload(
  transport: WebRtcTransport,
  fromUhid: string,
  timeoutMs: number,
): Promise<Uint8Array> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(
      () => reject(new Error("timed out waiting for bytes over the data channel")),
      timeoutMs,
    );
    transport.onDataReceived = (from, data) => {
      if (from === fromUhid) {
        clearTimeout(timer);
        resolve(new Uint8Array(data));
      }
    };
  });
}

describe("WebRtcTransport over transport-backed RelayWebRtcSignaling — full handshake", () => {
  it("two nodes negotiate a data channel with the SDP/ICE handshake carried over the transport", async () => {
    const [aliceRelay, bobRelay] = loopbackPair("alice", "bob");
    const aliceSig = new RelayWebRtcSignaling(aliceRelay);
    const bobSig = new RelayWebRtcSignaling(bobRelay);

    const hostOnly: [] = []; // empty (not undefined) => host-candidate-only ICE, no network
    const alice = new WebRtcTransport("alice", aliceSig, hostOnly);
    const bob = new WebRtcTransport("bob", bobSig, hostOnly);

    try {
      const payload = new TextEncoder().encode("handshake rode the relay; the data went direct");
      const received = firstPayload(bob, "alice", 60_000);

      const ok = await alice.sendAsync("bob", payload);
      assert.equal(ok, true, "negotiation over the transport-backed carrier should succeed");

      const got = await received;
      assert.deepEqual(got, payload, "bob must receive the exact bytes alice sent");
      assert.equal(alice.isConnected("bob"), true, "alice should report connected to bob");
      assert.equal(bob.isConnected("alice"), true, "bob should report connected to alice");
    } finally {
      await alice.dispose();
      await bob.dispose();
      aliceSig.dispose();
      bobSig.dispose();
    }
  });
});
