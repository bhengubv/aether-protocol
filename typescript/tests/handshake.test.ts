/**
 * Handshake service tests.
 *
 * Mirror the C# unit-suite in src/AetherNet.Core.Tests/Handshake/. Goals:
 *   - the basic Hello/HelloAck round-trip emits a PeerNegotiated event
 *     with the highest mutually-supported version + capability intersection
 *   - duplicate Hellos to the same peer are suppressed
 *   - non-overlapping version ranges fire IncompatiblePeer (no PeerNegotiated)
 *   - inverted ranges (min > max) fire IncompatiblePeer
 *   - HelloPayload JSON is snake_case (cross-language interop)
 *
 * SPDX-License-Identifier: MIT
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  HandshakeService,
  PeerCapabilities,
  IncompatiblePeerEvent,
  HelloPayload,
} from "../src/handshake/index.js";
import { FakeMeshSender } from "./fakes.js";

const ALICE = "alice";
const BOB = "bob";
const CAROL = "carol";

function buildHello(
  sourceUhid: string,
  destinationUhid: string,
  payload: HelloPayload,
  type: PacketType.Hello | PacketType.HelloAck = PacketType.Hello
): MeshPacket {
  const p = MeshPacket.create(type, sourceUhid);
  p.destinationUhid = destinationUhid;
  p.ttl = 1;
  p.priority = 0;
  p.payload = new Uint8Array(Buffer.from(JSON.stringify(payload), "utf8"));
  return p;
}

describe("HandshakeService — initiate", () => {
  it("emits a Hello packet with snake_case JSON", async () => {
    const sender = new FakeMeshSender(ALICE);
    const svc = new HandshakeService(sender);

    await svc.initiate(BOB);

    assert.equal(sender.unicasts.length, 1);
    const sent = sender.unicasts[0]!;
    assert.equal(sent.nextHopUhid, BOB);
    assert.equal(sent.packet.type, PacketType.Hello);
    assert.equal(sent.packet.sourceUhid, ALICE);
    assert.equal(sent.packet.destinationUhid, BOB);
    assert.equal(sent.packet.ttl, 1);

    const text = Buffer.from(sent.packet.payload).toString("utf8");
    const parsed = JSON.parse(text);
    assert.equal(typeof parsed.min_version, "number");
    assert.equal(typeof parsed.max_version, "number");
    assert.ok(Array.isArray(parsed.capabilities));
    assert.equal(typeof parsed.implementation, "string");
    // Confirm the JSON shape uses snake_case (load-bearing for interop).
    assert.ok(text.includes("min_version"));
    assert.ok(text.includes("max_version"));
    assert.ok(!text.includes("minVersion"));
  });

  it("suppresses duplicate Hellos to the same peer", async () => {
    const sender = new FakeMeshSender(ALICE);
    const svc = new HandshakeService(sender);

    await svc.initiate(BOB);
    await svc.initiate(BOB);
    await svc.initiate(BOB);

    assert.equal(sender.unicasts.length, 1);
  });

  it("does not Hello self", async () => {
    const sender = new FakeMeshSender(ALICE);
    const svc = new HandshakeService(sender);
    await svc.initiate(ALICE);
    assert.equal(sender.unicasts.length, 0);
  });

  it("re-initiates after renegotiate", async () => {
    const sender = new FakeMeshSender(ALICE);
    const svc = new HandshakeService(sender);

    await svc.initiate(BOB);
    await svc.renegotiate(BOB);
    await svc.initiate(BOB);

    assert.equal(sender.unicasts.length, 2);
  });
});

describe("HandshakeService — handleHello", () => {
  it("locks in capabilities + replies with HelloAck on a normal handshake", async () => {
    const sender = new FakeMeshSender(BOB);
    const svc = new HandshakeService(sender);
    let observed: PeerCapabilities | null = null;
    svc.onPeerNegotiated((caps) => { observed = caps; });

    const hello = buildHello(ALICE, BOB, {
      min_version: 1,
      max_version: 2,
      capabilities: ["signal-x3dh", "double-ratchet", "non-existent-cap"],
      implementation: "aether-go/1.0.0",
    });

    await svc.handleHello(hello);

    assert.ok(observed, "PeerNegotiated should have fired");
    const o = observed as unknown as PeerCapabilities;
    assert.equal(o.peerUhid, ALICE);
    // min(2, 2) — both sides max=2.
    assert.equal(o.negotiatedVersion, 2);
    // Intersection: only capabilities both sides advertise.
    assert.ok(o.capabilities.has("signal-x3dh"));
    assert.ok(o.capabilities.has("double-ratchet"));
    assert.ok(!o.capabilities.has("non-existent-cap"));
    assert.equal(o.implementationVersion, "aether-go/1.0.0");

    // HelloAck reply.
    assert.equal(sender.unicasts.length, 1);
    const ack = sender.unicasts[0]!;
    assert.equal(ack.packet.type, PacketType.HelloAck);
    assert.equal(ack.nextHopUhid, ALICE);
  });

  it("ignores Hello from self", async () => {
    const sender = new FakeMeshSender(ALICE);
    const svc = new HandshakeService(sender);
    let fired = false;
    svc.onPeerNegotiated(() => { fired = true; });

    const hello = buildHello(ALICE, ALICE, {
      min_version: 1, max_version: 2, capabilities: [], implementation: "",
    });
    await svc.handleHello(hello);

    assert.equal(fired, false);
    assert.equal(sender.unicasts.length, 0);
  });

  it("ignores malformed JSON payload", async () => {
    const sender = new FakeMeshSender(BOB);
    const svc = new HandshakeService(sender);
    let fired = false;
    svc.onPeerNegotiated(() => { fired = true; });

    const hello = MeshPacket.create(PacketType.Hello, ALICE);
    hello.destinationUhid = BOB;
    hello.payload = new Uint8Array(Buffer.from("not json", "utf8"));

    await svc.handleHello(hello);
    assert.equal(fired, false);
    assert.equal(sender.unicasts.length, 0);
  });

  it("rejects when destinationUhid mismatch (not implemented in C#) — handles gracefully", async () => {
    // Mirrors C# behaviour: HandshakeService does not check destinationUhid
    // (the transport layer does). We just confirm a non-matching dest does
    // not crash.
    const sender = new FakeMeshSender(BOB);
    const svc = new HandshakeService(sender);
    const hello = buildHello(ALICE, CAROL, {
      min_version: 1, max_version: 2, capabilities: ["signal-x3dh"], implementation: "x",
    });
    await svc.handleHello(hello);
    // Bob processes it anyway and replies — that matches C# current behaviour.
    assert.equal(sender.unicasts.length, 1);
  });
});

describe("HandshakeService — version selection", () => {
  it("negotiates the highest mutually-supported version", async () => {
    const sender = new FakeMeshSender(BOB);
    // Bob speaks 1..4 here.
    const svc = new HandshakeService(sender, { ourMinVersion: 1, ourMaxVersion: 4 });
    let observed: PeerCapabilities | null = null;
    svc.onPeerNegotiated((c) => { observed = c; });

    // Alice speaks 2..3.
    const hello = buildHello(ALICE, BOB, {
      min_version: 2, max_version: 3, capabilities: [], implementation: "",
    });
    await svc.handleHello(hello);

    assert.ok(observed);
    assert.equal((observed as unknown as PeerCapabilities).negotiatedVersion, 3);
  });

  it("fires incompatiblePeer when ranges do not overlap", async () => {
    const sender = new FakeMeshSender(BOB);
    // Bob speaks 5..6.
    const svc = new HandshakeService(sender, { ourMinVersion: 5, ourMaxVersion: 6 });
    let negotiated = false;
    let incompatible: IncompatiblePeerEvent | null = null;
    svc.onPeerNegotiated(() => { negotiated = true; });
    svc.onIncompatiblePeer((e) => { incompatible = e; });

    // Alice speaks 1..2 — no overlap.
    const hello = buildHello(ALICE, BOB, {
      min_version: 1, max_version: 2, capabilities: [], implementation: "",
    });
    await svc.handleHello(hello);

    assert.equal(negotiated, false);
    assert.ok(incompatible);
    const e = incompatible as unknown as IncompatiblePeerEvent;
    assert.equal(e.peerUhid, ALICE);
    assert.equal(e.theirMinVersion, 1);
    assert.equal(e.theirMaxVersion, 2);
    assert.equal(e.ourMinVersion, 5);
    assert.equal(e.ourMaxVersion, 6);
    assert.match(e.reason, /no version overlap/);
    // No HelloAck on incompatibility.
    assert.equal(sender.unicasts.length, 0);
  });

  it("fires incompatiblePeer for inverted ranges (min > max)", async () => {
    const sender = new FakeMeshSender(BOB);
    const svc = new HandshakeService(sender);
    let incompatible: IncompatiblePeerEvent | null = null;
    svc.onIncompatiblePeer((e) => { incompatible = e; });

    const hello = buildHello(ALICE, BOB, {
      min_version: 5, max_version: 3, capabilities: [], implementation: "",
    });
    await svc.handleHello(hello);

    assert.ok(incompatible);
    assert.match((incompatible as unknown as IncompatiblePeerEvent).reason, /inverted/);
  });

  it("rejects construction when ourMinVersion > ourMaxVersion", () => {
    const sender = new FakeMeshSender(BOB);
    assert.throws(() => new HandshakeService(sender, { ourMinVersion: 3, ourMaxVersion: 2 }));
  });
});

describe("HandshakeService — capability intersection", () => {
  it("locks in only capabilities both sides advertise", async () => {
    const sender = new FakeMeshSender(BOB);
    const svc = new HandshakeService(sender, {
      ourCapabilities: new Set(["signal-x3dh", "voice"]),
    });
    let observed: PeerCapabilities | null = null;
    svc.onPeerNegotiated((c) => { observed = c; });

    const hello = buildHello(ALICE, BOB, {
      min_version: 1,
      max_version: 2,
      capabilities: ["signal-x3dh", "stream", "dtn-custody"],
      implementation: "x",
    });
    await svc.handleHello(hello);

    const o = observed as unknown as PeerCapabilities;
    assert.equal(o.capabilities.size, 1);
    assert.ok(o.capabilities.has("signal-x3dh"));
  });

  it("treats capability names case-sensitively", async () => {
    const sender = new FakeMeshSender(BOB);
    const svc = new HandshakeService(sender, {
      ourCapabilities: new Set(["signal-x3dh"]),
    });
    let observed: PeerCapabilities | null = null;
    svc.onPeerNegotiated((c) => { observed = c; });

    const hello = buildHello(ALICE, BOB, {
      min_version: 1,
      max_version: 2,
      capabilities: ["Signal-X3DH"], // wrong case
      implementation: "x",
    });
    await svc.handleHello(hello);

    assert.equal((observed as unknown as PeerCapabilities).capabilities.size, 0);
  });

  it("yields an empty capability set when peer advertises none", async () => {
    const sender = new FakeMeshSender(BOB);
    const svc = new HandshakeService(sender);
    let observed: PeerCapabilities | null = null;
    svc.onPeerNegotiated((c) => { observed = c; });

    const hello = buildHello(ALICE, BOB, {
      min_version: 1, max_version: 2, capabilities: [], implementation: "",
    });
    await svc.handleHello(hello);

    assert.equal((observed as unknown as PeerCapabilities).capabilities.size, 0);
  });
});

describe("HandshakeService — handleHelloAck", () => {
  it("locks in capabilities without sending another packet", async () => {
    const sender = new FakeMeshSender(ALICE);
    const svc = new HandshakeService(sender);

    // Alice initiates first.
    await svc.initiate(BOB);
    sender.clear();

    let observed: PeerCapabilities | null = null;
    svc.onPeerNegotiated((c) => { observed = c; });

    const ack = buildHello(BOB, ALICE, {
      min_version: 1, max_version: 2, capabilities: ["signal-x3dh"], implementation: "y",
    }, PacketType.HelloAck);

    await svc.handleHelloAck(ack);

    assert.ok(observed);
    assert.equal((observed as unknown as PeerCapabilities).peerUhid, BOB);
    // No HelloAck-of-HelloAck (avoid infinite loop).
    assert.equal(sender.unicasts.length, 0);
  });

  it("rejects a packet whose type is not HelloAck", async () => {
    const sender = new FakeMeshSender(ALICE);
    const svc = new HandshakeService(sender);

    const wrong = buildHello(BOB, ALICE, {
      min_version: 1, max_version: 2, capabilities: [], implementation: "",
    }, PacketType.Hello); // type=Hello but routed via HelloAck handler

    await assert.rejects(() => svc.handleHelloAck(wrong));
  });
});

describe("HandshakeService — getAllNegotiated / getPeerCapabilities", () => {
  it("getPeerCapabilities returns null before negotiation", async () => {
    const sender = new FakeMeshSender(ALICE);
    const svc = new HandshakeService(sender);
    assert.equal(await svc.getPeerCapabilities(BOB), null);
  });

  it("getAllNegotiated includes legacy-v1 fallback peers", async () => {
    const sender = new FakeMeshSender(ALICE);
    const svc = new HandshakeService(sender);
    let count = 0;
    svc.onPeerNegotiated(() => { count += 1; });

    svc.assumeLegacyV1(BOB);
    svc.assumeLegacyV1(BOB); // idempotent

    assert.equal(count, 1);
    const all = svc.getAllNegotiated();
    assert.equal(all.length, 1);
    assert.equal(all[0]!.negotiatedVersion, 1);
    assert.equal(all[0]!.capabilities.size, 0);
  });
});

describe("HandshakeService — round-trip Alice <-> Bob", () => {
  it("both sides observe matching PeerCapabilities", async () => {
    const aliceSender = new FakeMeshSender(ALICE);
    const bobSender = new FakeMeshSender(BOB);
    const aliceSvc = new HandshakeService(aliceSender);
    const bobSvc = new HandshakeService(bobSender);

    let aliceCaps: PeerCapabilities | null = null;
    let bobCaps: PeerCapabilities | null = null;
    aliceSvc.onPeerNegotiated((c) => { aliceCaps = c; });
    bobSvc.onPeerNegotiated((c) => { bobCaps = c; });

    // Alice sends Hello to Bob.
    await aliceSvc.initiate(BOB);
    assert.equal(aliceSender.unicasts.length, 1);
    const aliceHello = aliceSender.unicasts[0]!.packet;

    // Bob receives and replies HelloAck.
    await bobSvc.handleHello(aliceHello);
    assert.equal(bobSender.unicasts.length, 1);
    const bobAck = bobSender.unicasts[0]!.packet;

    // Alice receives HelloAck.
    await aliceSvc.handleHelloAck(bobAck);

    assert.ok(aliceCaps);
    assert.ok(bobCaps);
    const a = aliceCaps as unknown as PeerCapabilities;
    const b = bobCaps as unknown as PeerCapabilities;
    assert.equal(a.peerUhid, BOB);
    assert.equal(b.peerUhid, ALICE);
    assert.equal(a.negotiatedVersion, b.negotiatedVersion);
    // Both sides used DEFAULT_CAPABILITIES so the intersection is the full
    // default set (6 caps).
    assert.equal(a.capabilities.size, 6);
    assert.equal(b.capabilities.size, 6);
  });
});
