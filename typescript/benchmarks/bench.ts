/**
 * tinybench harness for the TypeScript aether-protocol hot paths.
 *
 * Mirrors the C# Aether.Benchmarks suite, the Go `go/bench` harness,
 * the Python `python/benchmarks/test_benchmark.py`, and the C
 * `c/benchmarks/` runner — same eleven hot paths so a regression in any
 * language shows up as a delta against the committed baseline.
 *
 * Eleven cases:
 *
 *   - x25519Agree                 — one ECDH agreement (X3DH inner loop).
 *   - hkdfSha256_64Bytes          — KDF_RK (Signal §5.2) per ratchet step.
 *   - x3dhEstablish               — full pre-key bundle process; 4 X25519 + HKDF.
 *   - signalEncrypt               — steady-state Encrypt; HMAC chain + AES-GCM.
 *   - signalDecrypt               — steady-state Decrypt.
 *   - packetSerialize             — wire serialiser, 50-byte payload.
 *   - packetSerialize_large       — wire serialiser, 10KB payload.
 *   - packetDeserialize           — wire deserialiser.
 *   - packetRoundTrip             — single-number regression detector.
 *   - routeStore_lookup           — cached-route hot path.
 *   - routeStore_save             — install a new route entry.
 *
 * Run from `typescript/`:
 *
 *   npm run bench
 *
 * Output is a markdown table to stdout, ready to paste into BENCHMARKS.md
 * or a CI baseline diff comment. The table columns are:
 *   - Bench       — case name
 *   - mean (μs)   — sample mean per op (microseconds)
 *   - p99 (μs)    — 99th-percentile per op
 *   - hz          — operations / second
 *   - rme (%)     — relative margin of error
 *
 * The harness only calls exported APIs from the protocol package and
 * Node's built-in `crypto`, the same primitives the production code
 * uses, so the numbers are directly comparable to the C# / Go / Python
 * runs.
 *
 * SPDX-License-Identifier: MIT
 */

import { Bench } from "tinybench";
import {
  createCipheriv,
  createPrivateKey,
  createPublicKey,
  diffieHellman,
  generateKeyPairSync,
  randomBytes,
} from "node:crypto";
import { hkdf } from "@noble/hashes/hkdf";
import { sha256 } from "@noble/hashes/sha256";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { PacketSerializer } from "../src/protocol/PacketSerializer.js";
import { SignalProtocol } from "../src/security/SignalProtocol.js";
import { InMemoryRouteStore } from "../src/routing/IRouteStore.js";
import { RouteEntry } from "../src/models/index.js";

// ─── Local helpers ─────────────────────────────────────────────────────────

const ALICE = "alice-uhid-0001";
const BOB = "bob-uhid-0001";
const PLAINTEXT_SMALL = new TextEncoder().encode("hello, mesh");
const PAYLOAD_FOR_DECRYPT = randomBytes(256);

/**
 * Re-implementation of the X25519 agreement helper used by the production
 * SignalProtocol — same Node-builtin primitives, no third-party crypto.
 * Kept inline so we never reach into private internals.
 */
function x25519GenerateKeyPair(): { priv: Uint8Array; pub: Uint8Array } {
  const { publicKey, privateKey } = generateKeyPairSync("x25519");
  const privJwk = privateKey.export({ format: "jwk" }) as { d?: string };
  const pubJwk = publicKey.export({ format: "jwk" }) as { x?: string };
  return {
    priv: new Uint8Array(Buffer.from(privJwk.d!, "base64url")),
    pub: new Uint8Array(Buffer.from(pubJwk.x!, "base64url")),
  };
}

function x25519Agree(localPriv: Uint8Array, remotePub: Uint8Array): Uint8Array {
  const privKey = createPrivateKey({
    key: {
      kty: "OKP",
      crv: "X25519",
      d: Buffer.from(localPriv).toString("base64url"),
      x: "",
    },
    format: "jwk",
  } as any);
  const pubKey = createPublicKey({
    key: {
      kty: "OKP",
      crv: "X25519",
      x: Buffer.from(remotePub).toString("base64url"),
    },
    format: "jwk",
  } as any);
  return new Uint8Array(diffieHellman({ privateKey: privKey, publicKey: pubKey }));
}

/** Build a representative MeshPacket of a given payload size. */
function makePacket(payloadSize: number): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.Data;
  p.sourceUhid = "alice-uhid-0001";
  p.destinationUhid = "bob-uhid-0002";
  p.ttl = 7;
  p.priority = 1;
  p.protocolVersion = 2;
  p.timestampMs = BigInt(Date.now());
  p.packetNonce = randomBytes(8);
  p.payload = randomBytes(payloadSize);
  p.signature = randomBytes(64);
  return p;
}

/**
 * Build an Alice/Bob pair with a fully-primed Double Ratchet so the
 * encrypt/decrypt benches measure the steady-state chain step rather
 * than the one-shot X3DH cost.
 */
async function warmedPair(): Promise<{
  alice: SignalProtocol;
  bob: SignalProtocol;
}> {
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  await alice.generatePreKeyBundle(ALICE);
  const bobBundle = await bob.generatePreKeyBundle(BOB);
  await alice.processPreKeyBundle(bobBundle);
  // Drive the first PreKey message through so future encrypt/decrypts
  // exercise only the chain step.
  const first = await alice.encrypt(BOB, PLAINTEXT_SMALL);
  await bob.decrypt(ALICE, first);
  return { alice, bob };
}

// ─── Format helpers ────────────────────────────────────────────────────────

function formatMicros(seconds: number): string {
  // tinybench reports sample times in milliseconds.
  // The harness here prints microseconds for parity with the Go / Python
  // tables and so the values fit a sensible decimal range.
  return (seconds * 1000).toFixed(2);
}

function formatHz(hz: number): string {
  if (hz >= 1_000_000) return `${(hz / 1_000_000).toFixed(2)}M`;
  if (hz >= 1_000) return `${(hz / 1_000).toFixed(1)}k`;
  return hz.toFixed(0);
}

function printMarkdownTable(bench: Bench): void {
  console.log("");
  console.log("| Bench | mean (μs) | p99 (μs) | hz | rme (%) |");
  console.log("|-------|----------:|---------:|---:|--------:|");
  for (const task of bench.tasks) {
    const r = task.result;
    if (!r || r.error) {
      console.log(
        `| ${task.name} | ERROR | — | — | — |`
      );
      continue;
    }
    console.log(
      `| ${task.name} | ${formatMicros(r.mean)} | ${formatMicros(r.p99)} | ${formatHz(r.hz)} | ${r.rme.toFixed(2)} |`
    );
  }
  console.log("");
}

// ─── Bench wiring ─────────────────────────────────────────────────────────

async function main(): Promise<void> {
  // tinybench defaults: warmup + 0.5s per task. The SignalProtocol
  // benches are async — tinybench iterates its own loop and awaits each
  // call, so the per-iteration latency is measured (not throughput
  // through Promise pipelining).
  const bench = new Bench({ time: 500 });

  // ─── Crypto primitives ──────────────────────────────────────────────

  // x25519Agree — one ECDH agreement, the inner loop primitive of X3DH
  // (4x per session establishment) and DH-ratchet (2x per ratchet step).
  {
    const me = x25519GenerateKeyPair();
    const peer = x25519GenerateKeyPair();
    bench.add("x25519Agree", () => {
      x25519Agree(me.priv, peer.pub);
    });
  }

  // hkdfSha256_64Bytes — KDF_RK per Signal §5.2 (32-byte new root +
  // 32-byte new chain = 64 bytes out, called once per DH-ratchet step).
  {
    const ikm = randomBytes(32);
    const salt = randomBytes(32);
    const info = new TextEncoder().encode("aether-ratchet-rk-v1");
    bench.add("hkdfSha256_64Bytes", () => {
      hkdf(sha256, ikm, salt, info, 64);
    });
  }

  // x3dhEstablish — full pre-key bundle process: 4 X25519 + HKDF root
  // derivation. One-shot per peer. Each iteration uses a fresh initiator
  // so the session table doesn't grow unbounded.
  {
    const bob = new SignalProtocol();
    await bob.generatePreKeyBundle(BOB);
    bench.add(
      "x3dhEstablish",
      async function () {
        const bundle = await bob.generatePreKeyBundle(BOB);
        const alice = new SignalProtocol();
        await alice.generatePreKeyBundle(ALICE);
        await alice.processPreKeyBundle(bundle);
      }
    );
  }

  // ─── Signal Protocol (steady state) ─────────────────────────────────

  // signalEncrypt — steady-state Encrypt: 1 HMAC chain step + AES-GCM.
  {
    const { alice } = await warmedPair();
    bench.add("signalEncrypt", async () => {
      await alice.encrypt(BOB, PLAINTEXT_SMALL);
    });
  }

  // signalDecrypt — steady-state Decrypt. Each iteration consumes a
  // freshly-encrypted payload (the receive ratchet advances, so
  // re-decrypting the same bytes is invalid).
  {
    const { alice, bob } = await warmedPair();
    let pending: Awaited<ReturnType<typeof alice.encrypt>> | null = null;
    bench.add(
      "signalDecrypt",
      async () => {
        await bob.decrypt(ALICE, pending!);
      },
      {
        beforeEach: async () => {
          pending = await alice.encrypt(BOB, PAYLOAD_FOR_DECRYPT);
        },
      }
    );
  }

  // ─── Wire-format serializer ─────────────────────────────────────────

  // packetSerialize — Serialize on a representative 50-byte Data packet.
  // Every packet on the mesh runs through this on send.
  {
    const pkt = makePacket(50);
    bench.add("packetSerialize", () => {
      PacketSerializer.serialize(pkt);
    });
  }

  // packetSerialize_large — Serialize on a 10KB payload (typical
  // chunked-data or video-frame packet).
  {
    const pkt = makePacket(10240);
    bench.add("packetSerialize_large", () => {
      PacketSerializer.serialize(pkt);
    });
  }

  // packetDeserialize — Deserialize on a representative wire envelope.
  // Every hop runs this on receive; a regression multiplies across
  // every router.
  {
    const wire = PacketSerializer.serialize(makePacket(50));
    bench.add("packetDeserialize", () => {
      PacketSerializer.deserialize(wire);
    });
  }

  // packetRoundTrip — Combined Serialize + Deserialize. Single-number
  // regression detector that catches changes in either side.
  {
    const pkt = makePacket(50);
    bench.add("packetRoundTrip", () => {
      const wire = PacketSerializer.serialize(pkt);
      const got = PacketSerializer.deserialize(wire);
      // Defeat dead-store elimination — touch a field so the runtime
      // can't optimise the deserialize away.
      if (!got || !got.sourceUhid) {
        throw new Error("unexpected nil/empty packet");
      }
    });
  }

  // ─── Routing ────────────────────────────────────────────────────────

  // routeStore_lookup — cached-route hot path; the steady state for
  // every outbound packet that already has a route.
  {
    const store = new InMemoryRouteStore();
    const entry: RouteEntry = {
      destinationUhid: BOB,
      nextHopUhid: "relay-uhid",
      hopCount: 2,
      qualityScore: 90,
      expiresAt: new Date(Date.now() + 3600 * 1000),
    };
    await store.save(entry);
    bench.add("routeStore_lookup", async () => {
      const got = await store.get(BOB);
      if (!got) throw new Error("expected cached route");
    });
  }

  // routeStore_save — install a new route entry; what happens on every
  // successful RREP arrival.
  {
    const store = new InMemoryRouteStore();
    const expires = new Date(Date.now() + 3600 * 1000);
    bench.add("routeStore_save", async () => {
      await store.save({
        destinationUhid: "dest",
        nextHopUhid: "hop",
        hopCount: 1,
        qualityScore: 100,
        expiresAt: expires,
      });
    });
  }

  // ─── Run ────────────────────────────────────────────────────────────

  await bench.warmup();
  await bench.run();
  printMarkdownTable(bench);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
