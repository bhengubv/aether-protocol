/**
 * Unit tests for the ABMF WIRE bindings: BandwidthProbe(53), BandwidthAck(54),
 * BandwidthGossip(55). Binary little-endian byte-identity gates + send/handle behaviour.
 * Mirrors the C# BandwidthWireTests, plus the canonical hex vectors from
 * fixtures/bandwidth/vectors.json (SHARED corpus — do NOT edit).
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/bandwidthWire.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { Buffer } from "node:buffer";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { DEFAULT_TTL } from "../src/constants.js";
import {
  BandwidthWireService,
  BandwidthWireCodec,
  BandwidthConfidence,
  makeBandwidthProbeAck,
  type BandwidthProbe,
  type BandwidthProbeReceived,
  type BandwidthProbeAck,
  type BandwidthGossipPayload,
} from "../src/bandwidth/index.js";
import { FakeMeshSender } from "./fakes.js";

const LOCAL = "aether:local:01";

const hex = (b: Uint8Array): string => Buffer.from(b).toString("hex");

function ack(fields: {
  sequence: number;
  senderSendUs: bigint;
  receiverReceiveUs: bigint;
  receiverSendUs: bigint;
  senderReceiveUs: bigint;
  probeBytes: number;
}): BandwidthProbeAck {
  return makeBandwidthProbeAck(fields);
}

function gossip(fields: {
  peerUhid: string;
  transportName: string;
  btlBwBps: bigint;
  rtPropUs: bigint;
  confidence: BandwidthConfidence;
}): BandwidthGossipPayload {
  return { ...fields, measuredAt: new Date(0) };
}

// ── Byte-identity gates ─────────────────────────────────────────────────────────

describe("ABMF WIRE — byte-identity gates", () => {
  it("Probe serializes to canonical bytes", () => {
    const probe: BandwidthProbe = { sequence: 42, senderSendUs: 1_700_000_000_000_000n };
    assert.equal(hex(BandwidthWireCodec.serializeProbe(probe)), "2a00000000401e18240a0600");
  });

  it("Ack serializes to canonical bytes (senderReceiveUs is local-only)", () => {
    // senderReceiveUs (999) is local-only and must NOT change the wire bytes.
    const a = ack({
      sequence: 42,
      senderSendUs: 1_700_000_000_000_000n,
      receiverReceiveUs: 1_700_000_000_012_345n,
      receiverSendUs: 1_700_000_000_013_000n,
      senderReceiveUs: 999n,
      probeBytes: 1200,
    });
    assert.equal(
      hex(BandwidthWireCodec.serializeAck(a)),
      "2a00000000401e18240a060039701e18240a0600c8721e18240a0600b0040000",
    );
  });

  it("Gossip serializes to canonical bytes (peerUhid/transportName/measuredAt off-wire)", () => {
    const g = gossip({
      peerUhid: "peer",
      transportName: "tp",
      btlBwBps: 5_000_000n,
      rtPropUs: 25_000n,
      confidence: BandwidthConfidence.Medium,
    });
    assert.equal(hex(BandwidthWireCodec.serializeGossip(g)), "404b4c0000000000a861000002");
  });

  // Cross-language parity: reproduce every vector in the SHARED fixtures/bandwidth/vectors.json.
  it("reproduces every shared fixture vector byte-for-byte", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/bandwidth/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      vectors: Array<{
        name: string;
        kind: "probe" | "ack" | "gossip";
        sequence?: number;
        sender_send_us?: number;
        receiver_receive_us?: number;
        receiver_send_us?: number;
        probe_bytes?: number;
        btlbw_bps?: number;
        rtprop_us?: number;
        confidence?: number;
        expected_hex: string;
      }>;
    };
    assert.ok(V.vectors.length >= 3, "fixture must carry probe + ack + gossip vectors");

    for (const vec of V.vectors) {
      let bytes: Uint8Array;
      switch (vec.kind) {
        case "probe":
          bytes = BandwidthWireCodec.serializeProbe({
            sequence: vec.sequence!,
            senderSendUs: BigInt(vec.sender_send_us!),
          });
          break;
        case "ack":
          bytes = BandwidthWireCodec.serializeAck(
            ack({
              sequence: vec.sequence!,
              senderSendUs: BigInt(vec.sender_send_us!),
              receiverReceiveUs: BigInt(vec.receiver_receive_us!),
              receiverSendUs: BigInt(vec.receiver_send_us!),
              senderReceiveUs: 0n,
              probeBytes: vec.probe_bytes!,
            }),
          );
          break;
        case "gossip":
          bytes = BandwidthWireCodec.serializeGossip(
            gossip({
              peerUhid: "",
              transportName: "",
              btlBwBps: BigInt(vec.btlbw_bps!),
              rtPropUs: BigInt(vec.rtprop_us!),
              confidence: vec.confidence! as BandwidthConfidence,
            }),
          );
          break;
      }
      assert.equal(hex(bytes), vec.expected_hex, `canonical bytes for vector "${vec.name}"`);
    }
  });

  it("Ack round-trips with senderReceiveUs zeroed (not on wire)", () => {
    const back = BandwidthWireCodec.deserializeAck(
      BandwidthWireCodec.serializeAck(
        ack({
          sequence: 7,
          senderSendUs: 100n,
          receiverReceiveUs: 200n,
          receiverSendUs: 300n,
          senderReceiveUs: 400n,
          probeBytes: 512,
        }),
      ),
    );
    assert.equal(back.sequence, 7);
    assert.equal(back.senderSendUs, 100n);
    assert.equal(back.receiverReceiveUs, 200n);
    assert.equal(back.receiverSendUs, 300n);
    assert.equal(back.senderReceiveUs, 0n); // not on wire
    assert.equal(back.probeBytes, 512);
  });
});

// ── Behaviour ───────────────────────────────────────────────────────────────────

describe("BandwidthWireService — send", () => {
  it("sendProbe emits a directed probe", async () => {
    const sender = new FakeMeshSender("aether:a:01");
    const svc = new BandwidthWireService(sender);

    assert.equal(await svc.sendProbe("aether:b:02", { sequence: 42, senderSendUs: 1_700_000_000_000_000n }), true);
    assert.equal(sender.unicasts.length, 1);
    const sent = sender.unicasts[0]!;
    assert.equal(sent.packet.type, PacketType.BandwidthProbe);
    assert.equal(sent.nextHopUhid, "aether:b:02");
    assert.equal(sent.packet.sourceUhid, "aether:a:01");
    assert.equal(sent.packet.destinationUhid, "aether:b:02");
    assert.equal(sent.packet.ttl, DEFAULT_TTL);
  });

  it("sendAck emits a directed ack", async () => {
    const sender = new FakeMeshSender(LOCAL);
    const svc = new BandwidthWireService(sender);
    const a = ack({
      sequence: 1,
      senderSendUs: 2n,
      receiverReceiveUs: 3n,
      receiverSendUs: 4n,
      senderReceiveUs: 5n,
      probeBytes: 6,
    });
    assert.equal(await svc.sendAck("aether:b:02", a), true);
    assert.equal(sender.unicasts.length, 1);
    assert.equal(sender.unicasts[0]!.packet.type, PacketType.BandwidthAck);
  });

  it("broadcastGossip emits gossip, and handle raises onGossipReceived with the source peer", async () => {
    const sender = new FakeMeshSender(LOCAL);
    sender.addPeer({ uhid: "p1" } as any);
    sender.addPeer({ uhid: "p2" } as any);
    sender.addPeer({ uhid: "p3" } as any);
    const svc = new BandwidthWireService(sender);

    const g = gossip({
      peerUhid: "",
      transportName: "",
      btlBwBps: 5_000_000n,
      rtPropUs: 25_000n,
      confidence: BandwidthConfidence.Medium,
    });
    assert.equal(await svc.broadcastGossip(g), 3);
    assert.equal(sender.broadcasts.length, 1);
    const sent = sender.broadcasts[0]!;
    assert.equal(sent.type, PacketType.BandwidthGossip);

    let got: BandwidthGossipPayload | undefined;
    svc.onGossipReceived = (e) => { got = e; };
    sent.sourceUhid = "aether:peer:09";
    assert.equal(await svc.handle(sent), true);
    assert.ok(got);
    assert.equal(got!.btlBwBps, 5_000_000n);
    assert.equal(got!.rtPropUs, 25_000n);
    assert.equal(got!.confidence, BandwidthConfidence.Medium);
    assert.equal(got!.peerUhid, "aether:peer:09");
  });
});

describe("BandwidthWireService — handle", () => {
  it("Probe raises onProbeReceived with the source", async () => {
    const svc = new BandwidthWireService(new FakeMeshSender(LOCAL));
    let got: BandwidthProbeReceived | undefined;
    svc.onProbeReceived = (e) => { got = e; };

    const pkt = new MeshPacket();
    pkt.type = PacketType.BandwidthProbe;
    pkt.sourceUhid = "aether:x:01";
    pkt.payload = BandwidthWireCodec.serializeProbe({ sequence: 9, senderSendUs: 123n });

    assert.equal(await svc.handle(pkt), true);
    assert.ok(got);
    assert.equal(got!.probe.sequence, 9);
    assert.equal(got!.probe.senderSendUs, 123n);
    assert.equal(got!.fromUhid, "aether:x:01");
  });

  it("Ack raises onAckReceived", async () => {
    const svc = new BandwidthWireService(new FakeMeshSender(LOCAL));
    let got: BandwidthProbeAck | undefined;
    svc.onAckReceived = (e) => { got = e; };

    const pkt = new MeshPacket();
    pkt.type = PacketType.BandwidthAck;
    pkt.sourceUhid = "aether:x:01";
    pkt.payload = BandwidthWireCodec.serializeAck(
      ack({
        sequence: 3,
        senderSendUs: 10n,
        receiverReceiveUs: 20n,
        receiverSendUs: 30n,
        senderReceiveUs: 0n,
        probeBytes: 64,
      }),
    );

    assert.equal(await svc.handle(pkt), true);
    assert.ok(got);
    assert.equal(got!.sequence, 3);
    assert.equal(got!.probeBytes, 64);
  });

  it("wrong packet type returns false", async () => {
    const svc = new BandwidthWireService(new FakeMeshSender(LOCAL));
    const pkt = new MeshPacket();
    pkt.type = PacketType.Data;
    pkt.payload = new Uint8Array();
    assert.equal(await svc.handle(pkt), false);
  });

  it("short/malformed body returns false", async () => {
    const svc = new BandwidthWireService(new FakeMeshSender(LOCAL));
    const pkt = new MeshPacket();
    pkt.type = PacketType.BandwidthProbe;
    pkt.payload = new Uint8Array([1, 2, 3]); // < 12 bytes
    assert.equal(await svc.handle(pkt), false);
  });
});
