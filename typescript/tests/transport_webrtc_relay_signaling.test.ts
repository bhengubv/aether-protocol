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
 *   1. Interop byte-identity — a framed signal equals the captured C# `System.Text.Json` reference
 *      bytes (magic `AWS1` + JSON), including STJ's escaping of base64 `+`.
 *   2. Transport round-trip — the two carriers round-trip an offer AND an answer over the loopback
 *      transport, decoding back to the original signal.
 *   3. Full werift handshake — two real `WebRtcTransport` instances whose signalling rides the
 *      transport-backed carriers negotiate a direct data channel over the loopback and carry bytes.
 *
 * Run with: node --import tsx --test tests/transport_webrtc_relay_signaling.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

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

// ── Captured C# System.Text.Json reference frames (byte-identity fixtures) ──────
// Produced by serializing the C# `WebRtcSignal` record via the same source-generated context
// (WhenWritingNull) and prefixing the `AWS1` magic. These are the exact bytes a C# node emits.

/** offer: from "alice" to "bob", sdp "v=0\r\no=- 1 1 IN IP4 0.0.0.0". */
const CS_OFFER_HEX =
  "415753317B2246726F6D55686964223A22616C696365222C22546F55686964223A22626F62222C2254797065223A302C22536470223A22763D305C725C6E6F3D2D2031203120494E2049503420302E302E302E30222C225364704D4C696E65496E646578223A307D";

/** answer: from "bob" to "alice", sdp "v=0\r\na=answer". */
const CS_ANSWER_HEX =
  "415753317B2246726F6D55686964223A22626F62222C22546F55686964223A22616C696365222C2254797065223A312C22536470223A22763D305C725C6E613D616E73776572222C225364704D4C696E65496E646578223A307D";

/** candidate: from "alice" to "bob", candidate + sdpMid "0" + sdpMLineIndex 0. */
const CS_CANDIDATE_HEX =
  "415753317B2246726F6D55686964223A22616C696365222C22546F55686964223A22626F62222C2254797065223A322C2243616E646964617465223A2263616E6469646174653A3120312075647020312031302E302E302E3120353030302074797020686F7374222C225364704D4C696E65496E646578223A302C225364704D6964223A2230227D";

/** escaping edge case: sdp with base64 `+`, and `< > &` — proves STJ-exact escaping. */
const CS_ESCAPING_JSON =
  '{"FromUhid":"a","ToUhid":"b","Type":0,"Sdp":"a=fingerprint:sha-256 AB\\u002B/CD=xy \\u003Ct\\u003E \\u0026z ual/set\\u002Bice","SdpMLineIndex":0}';

function toHex(bytes: Uint8Array): string {
  let s = "";
  for (const b of bytes) s += b.toString(16).toUpperCase().padStart(2, "0");
  return s;
}

// ── Level 1: interop byte-identity ──────────────────────────────────────────────

describe("RelayWebRtcSignaling — wire byte-identity with C#", () => {
  it("frames an offer identically to the C# System.Text.Json reference", () => {
    const offer: Signal = {
      fromUhid: "alice",
      toUhid: "bob",
      type: SignalType.Offer,
      sdp: "v=0\r\no=- 1 1 IN IP4 0.0.0.0",
    };
    assert.equal(toHex(encodeSignalFrame(offer)), CS_OFFER_HEX);
  });

  it("frames an answer identically to the C# reference", () => {
    const answer: Signal = {
      fromUhid: "bob",
      toUhid: "alice",
      type: SignalType.Answer,
      sdp: "v=0\r\na=answer",
    };
    assert.equal(toHex(encodeSignalFrame(answer)), CS_ANSWER_HEX);
  });

  it("frames an ICE candidate identically to the C# reference (mid + mline order)", () => {
    const cand: Signal = {
      fromUhid: "alice",
      toUhid: "bob",
      type: SignalType.Candidate,
      candidate: "candidate:1 1 udp 1 10.0.0.1 5000 typ host",
      sdpMid: "0",
      sdpMLineIndex: 0,
    };
    assert.equal(toHex(encodeSignalFrame(cand)), CS_CANDIDATE_HEX);
  });

  it("escapes base64 '+' and '<>&' exactly as STJ does (not as JSON.stringify)", () => {
    const sig: Signal = {
      fromUhid: "a",
      toUhid: "b",
      type: SignalType.Offer,
      sdp: "a=fingerprint:sha-256 AB+/CD=xy <t> &z ual/set+ice",
    };
    const frame = encodeSignalFrame(sig);
    // Body is the frame minus the 4-byte AWS1 magic.
    const body = new TextDecoder().decode(frame.subarray(4));
    assert.equal(body, CS_ESCAPING_JSON);
  });

  it("round-trips a signal through encode → decode → encode unchanged (wire is stable)", () => {
    const original: Signal = {
      fromUhid: "n1",
      toUhid: "n2",
      type: SignalType.Candidate,
      candidate: "candidate:2 1 tcp 9 192.168.1.9 0 typ host tcptype active",
      sdpMid: "0",
      sdpMLineIndex: 0,
    };
    const frame = encodeSignalFrame(original);
    const decoded = decodeSignalFrame(frame);
    assert.notEqual(decoded, undefined);
    // Decoding is lossless: re-encoding the decoded signal reproduces the exact same wire bytes.
    assert.equal(toHex(encodeSignalFrame(decoded!)), toHex(frame));
    // And the meaningful fields survived.
    assert.equal(decoded!.fromUhid, "n1");
    assert.equal(decoded!.toUhid, "n2");
    assert.equal(decoded!.type, SignalType.Candidate);
    assert.equal(decoded!.candidate, original.candidate);
    assert.equal(decoded!.sdpMid, "0");
    assert.equal(decoded!.sdpMLineIndex, 0);
  });
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
