// SPDX-License-Identifier: MIT
//
// 3-node mesh-integration proof for circuit-relay-v2: the engine relays A->B through R
// over real MeshPacket frames (type CircuitRelayControl) with NO direct A-B link,
// surfacing at B via the transport onData callback — exactly how a host mesh consumes
// it. Mirrors the C# CircuitRelayMeshIntegrationTests and the Go / Python mesh tests.

import test from "node:test";
import assert from "node:assert/strict";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { MeshRelayLink, MeshCircuitRelay } from "../src/circuitrelay/MeshRelayLink.js";
import { Transport, defaultRelayOptions } from "../src/circuitrelay/Transport.js";
import { TransportManager } from "../src/transport/TransportManager.js";

// In-process mesh whose adjacency is A-R-B with NO direct A-B edge; routes each
// MeshPacket one hop to the destination node's link. Stands in for the real radios
// (the sendOneHop callable in production).
class MeshHub {
  private readonly links = new Map<string, MeshRelayLink>();
  private readonly edges = new Set<string>();

  connect(x: string, y: string): void {
    this.edges.add(x + "|" + y);
    this.edges.add(y + "|" + x);
  }
  adjacent(x: string, y: string): boolean {
    return this.edges.has(x + "|" + y);
  }
  register(node: string, link: MeshRelayLink): void {
    this.links.set(node, link);
  }
  sendFrom(node: string): (pkt: MeshPacket) => boolean {
    return (pkt: MeshPacket): boolean => {
      if (!this.adjacent(node, pkt.destinationUhid)) return false;
      const link = this.links.get(pkt.destinationUhid);
      if (link) queueMicrotask(() => link.handleIncomingPacket(pkt)); // async one-hop delivery
      return true;
    };
  }
  canReachFrom(node: string): (other: string) => boolean {
    return (other: string): boolean => this.adjacent(node, other);
  }
}

test("relay works as a mesh transport over real MeshPacket frames", async () => {
  const hub = new MeshHub();
  hub.connect("A", "R");
  hub.connect("R", "B"); // deliberately NO A-B edge

  const aL = new MeshRelayLink("A", hub.sendFrom("A"), hub.canReachFrom("A"));
  const rL = new MeshRelayLink("R", hub.sendFrom("R"), hub.canReachFrom("R"));
  const bL = new MeshRelayLink("B", hub.sendFrom("B"), hub.canReachFrom("B"));
  hub.register("A", aL);
  hub.register("R", rL);
  hub.register("B", bL);

  const a = new Transport("A", aL, defaultRelayOptions());
  const r = new Transport("R", rL, defaultRelayOptions());
  const b = new Transport("B", bL, defaultRelayOptions());

  const received = new Promise<{ sender: string; data: Uint8Array }>((resolve) => {
    b.setOnData((sender, data) => resolve({ sender, data }));
  });

  assert.equal(a.isConnected("B"), false); // no direct path
  assert.equal(await b.reserve("R"), true); // B reserves on the relay
  a.setRoute("B", "R"); // A learns B is reachable via R

  const payload = new Uint8Array([0xde, 0xad, 0xbe, 0xef]);
  assert.equal(await a.send("B", payload), true); // relayed A -> R -> B

  const got = await Promise.race([
    received,
    new Promise<null>((resolve) => setTimeout(() => resolve(null), 3000)),
  ]);
  assert.notEqual(got, null, "B never received the relayed message via the mesh link");
  assert.equal(got!.sender, "A");
  assert.deepEqual(got!.data, payload);
  assert.equal(r.activeBridgeCount(), 1); // R is genuinely bridging over real packets
});

// The gap-2 acceptance test (mirrors the C# CircuitRelayMeshIntegrationTests
// Relay_Is_Auto_Selected_By_TransportManager_As_Fallback): the relay must be picked
// automatically by TransportManager as the last-resort fallback — NOT called directly. A and B
// each run a manager whose ONLY transport is the relay; A.sendAsync routes B's payload through the
// manager's selection (additional transports, ascending power cost, relay at 90) and B receives it,
// tagged with the relay transport's name — proving selection, not hand-wiring.
test("relay is auto-selected by TransportManager as the last-resort fallback", async () => {
  const hub = new MeshHub();
  hub.connect("A", "R");
  hub.connect("R", "B"); // no A-B edge

  const a = MeshCircuitRelay.create("A", hub.sendFrom("A"), hub.canReachFrom("A"));
  const r = MeshCircuitRelay.create("R", hub.sendFrom("R"), hub.canReachFrom("R"));
  const b = MeshCircuitRelay.create("B", hub.sendFrom("B"), hub.canReachFrom("B"));
  hub.register("A", a.link);
  hub.register("R", r.link);
  hub.register("B", b.link);

  // A and B each run a TransportManager whose ONLY transport is the relay (no BLE/Wi-Fi/NearLink).
  const aMgr = new TransportManager([a.transport]);
  const bMgr = new TransportManager([b.transport]);

  const received = new Promise<{ sender: string; data: Uint8Array; via: string }>((resolve) => {
    bMgr.onDataReceived = (sender, data, via) => resolve({ sender, data, via });
  });

  assert.equal(await b.transport.reserveAsync("R"), true); // B reserves on the relay
  a.transport.setRoute("B", "R"); // A learns B is reachable via R

  const payload = new Uint8Array([0x11, 0x22, 0x33, 0x44]);
  assert.equal(await aMgr.sendAsync("B", payload), true); // via the MANAGER, which must select the relay

  const got = await Promise.race([
    received,
    new Promise<null>((resolve) => setTimeout(() => resolve(null), 3000)),
  ]);
  assert.notEqual(got, null, "B never received the relayed message via TransportManager selection");
  assert.equal(got!.sender, "A");
  assert.deepEqual(got!.data, payload);
  assert.equal(got!.via, "Circuit Relay (v2)"); // the manager chose the relay transport, by name
  assert.equal(r.transport.activeBridgeCount, 1);

  aMgr.dispose();
  bMgr.dispose();
});
