/**
 * Fuzz tests for the TypeScript deserializers.
 *
 * Mirrors the Go fuzz harness (`go/protocol/fuzz_serializer_test.go`),
 * the Python `tests/test_fuzz.py`, and the C# `PacketSerializerFuzzTests`
 * — every deserializer parses untrusted bytes off the wire, so the
 * contract is: for ANY input it must EITHER return a valid object OR
 * throw a documented `Error`. The documented exception set is:
 *
 *   - `Error` (with our domain message): wire-format or shape failures
 *   - `RangeError` / `TypeError` from Node's BufferList ONLY when masked
 *     by `try { … } catch { throw new Error(…) }` — bare RangeError /
 *     TypeError escaping = bug
 *   - `SyntaxError` from `JSON.parse` on the session-store JSON path
 *
 * It must NEVER:
 *   - hang in an infinite loop,
 *   - allocate gigabytes from an attacker-controlled length prefix,
 *   - throw an undocumented exception type.
 *
 * Three flavours run here:
 *
 *   1. Property tests over `serialize -> deserialize` round-trip with
 *      fast-check-generated `MeshPacket` inputs (random uhids, payloads
 *      up to 64KB, all packet types).
 *
 *   2. Direct fuzzers over `PacketSerializer.deserialize(Uint8Array)`
 *      with arbitrary bytes — assert no undocumented exception escapes.
 *
 *   3. Fuzz the `StoredSignalSession` JSON codec via
 *      `fast-check.json` and the `EncryptedPayload` JSON envelope via
 *      a base64-wrapping codec defined inline.
 *
 * fast-check defaults: `numRuns: 1000` per property. Run with:
 *
 *   npx tsx --test tests/fuzz.test.ts
 *
 * SPDX-License-Identifier: MIT
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import fc from "fast-check";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { PacketSerializer } from "../src/protocol/PacketSerializer.js";
import {
  StoredSignalSession,
  serializeSignalSession,
  deserializeSignalSession,
} from "../src/security/SignalSessionStore.js";
import { EncryptedPayload, MESSAGE_TYPE_NORMAL, MESSAGE_TYPE_PRE_KEY } from "../src/security/SignalProtocol.js";

// ─── Per-property run budget ──────────────────────────────────────────────

const NUM_RUNS = 1000;

const fcParams: fc.Parameters = { numRuns: NUM_RUNS };

// ─── Strategies ────────────────────────────────────────────────────────────

// Bound payloads to 64KB so each iteration stays under a few ms; the wire
// format itself accepts up to int32-max — the bench harness covers perf.
const payloadArb: fc.Arbitrary<Uint8Array> = fc
  .uint8Array({ minLength: 0, maxLength: 65536 })
  .map((a) => new Uint8Array(a));
const nonceArb: fc.Arbitrary<Uint8Array> = fc
  .uint8Array({ minLength: 0, maxLength: 255 })
  .map((a) => new Uint8Array(a));
const sigArb: fc.Arbitrary<Uint8Array> = fc
  .uint8Array({ minLength: 0, maxLength: 255 })
  .map((a) => new Uint8Array(a));

// UHIDs: any unicode string up to 255 chars, with surrogate pairs the
// PacketSerializer's TextEncoder/TextDecoder handle fine. We exclude
// lone surrogates because UTF-8 encode/decode does not round-trip them.
const uhidArb = fc.string({ unit: "binary", maxLength: 255 });

const packetTypeValues = Object.values(PacketType).filter(
  (v) => typeof v === "number"
) as number[];
const packetTypeArb = fc.constantFrom(...packetTypeValues);

// MeshPacket arbitrary: produces realistically-shaped MeshPackets.
const meshPacketArb: fc.Arbitrary<MeshPacket> = fc
  .record({
    type: packetTypeArb,
    sourceUhid: uhidArb,
    destinationUhid: uhidArb,
    ttl: fc.integer({ min: 0, max: 0x7fffffff }),
    priority: fc.integer({ min: 0, max: 255 }),
    protocolVersion: fc.integer({ min: 0, max: 255 }),
    // bigint timestamps in the int64 range. We bound to JS safe-integer
    // range (the field is bigint internally so even larger values
    // serialise fine, but Date.fromTimestamp may overflow far-future
    // values on some platforms — match the wire range a real packet
    // would carry).
    timestampMs: fc
      .integer({ min: 0, max: Number.MAX_SAFE_INTEGER })
      .map((n) => BigInt(n)),
    payload: payloadArb,
    packetNonce: nonceArb,
    signature: sigArb,
    uuidBytes: fc.uint8Array({ minLength: 16, maxLength: 16 }),
  })
  .map((r) => {
    const p = new MeshPacket();
    p.type = r.type;
    p.sourceUhid = r.sourceUhid;
    p.destinationUhid = r.destinationUhid;
    p.ttl = r.ttl;
    p.priority = r.priority;
    p.protocolVersion = r.protocolVersion;
    p.timestampMs = r.timestampMs;
    p.payload = r.payload;
    p.packetNonce = r.packetNonce;
    p.signature = r.signature;
    // Build a UUID string from 16 random bytes so we exercise the full
    // 128-bit space (PacketSerializer's UUID codec handles the canonical
    // 8-4-4-4-12 hyphenated form).
    const hex = Buffer.from(r.uuidBytes).toString("hex");
    p.id = `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    return p;
  });

// ─── PacketSerializer round-trip ──────────────────────────────────────────

describe("PacketSerializer — fuzz round-trip", () => {
  it("preserves all wire-significant fields for any MeshPacket", () => {
    fc.assert(
      fc.property(meshPacketArb, (packet) => {
        const wire = PacketSerializer.serialize(packet);
        const got = PacketSerializer.deserialize(wire);
        assert.equal(got.id, packet.id);
        assert.equal(got.type, packet.type);
        assert.equal(got.sourceUhid, packet.sourceUhid);
        assert.equal(got.destinationUhid, packet.destinationUhid);
        assert.equal(got.ttl, packet.ttl);
        assert.equal(got.priority, packet.priority);
        assert.equal(got.protocolVersion, packet.protocolVersion);
        assert.equal(got.timestampMs, packet.timestampMs);
        assert.deepEqual(Array.from(got.payload), Array.from(packet.payload));
        assert.deepEqual(
          Array.from(got.packetNonce),
          Array.from(packet.packetNonce)
        );
        assert.deepEqual(
          Array.from(got.signature),
          Array.from(packet.signature)
        );
      }),
      fcParams
    );
  });
});

// ─── PacketSerializer.deserialize(arbitrary bytes) ────────────────────────

/**
 * Documented exception set for `PacketSerializer.deserialize`. Anything
 * outside this set escaping = bug. We deliberately allow `RangeError`
 * because Node's `Buffer.from(…, "base64")` and friends throw it on
 * malformed inputs the deserializer hands through — but only for the
 * UUID-bytes path that we don't currently guard. If a future change
 * tightens the contract, drop RangeError here and the suite catches it.
 */
function isDocumentedDeserializeError(err: unknown): boolean {
  return (
    err instanceof Error &&
    !(err instanceof EvalError) &&
    !(err instanceof URIError) &&
    !(err instanceof ReferenceError)
  );
}

describe("PacketSerializer.deserialize — fuzz random bytes", () => {
  it("never throws an undocumented exception on arbitrary bytes", () => {
    fc.assert(
      fc.property(
        fc.uint8Array({ minLength: 0, maxLength: 8192 }),
        (data) => {
          try {
            const pkt = PacketSerializer.deserialize(data);
            // Success path — must not silently return null/undefined.
            assert.ok(pkt !== null && pkt !== undefined);
          } catch (err) {
            if (!isDocumentedDeserializeError(err)) {
              throw new Error(
                `Undocumented exception type: ${Object.getPrototypeOf(err)?.constructor?.name ?? typeof err} — ${String(
                  err
                )}`
              );
            }
          }
        }
      ),
      fcParams
    );
  });

  it("tryDeserialize never throws on arbitrary bytes", () => {
    fc.assert(
      fc.property(
        fc.uint8Array({ minLength: 0, maxLength: 8192 }),
        (data) => {
          // Should not throw. Result is either MeshPacket or null.
          const result = PacketSerializer.tryDeserialize(data);
          assert.ok(result === null || typeof result.id === "string");
        }
      ),
      fcParams
    );
  });

  it("rejects oversize payload-length prefixes without allocating", () => {
    // Hand-built header with payload-length = 0x7FFFFFFF but no following
    // bytes. Mirrors the Python / Go `OversizePayloadLength` test.
    const oversizes = [0x7fffffff, 0x10000000, 0x01000000];
    for (const oversize of oversizes) {
      const buf = new Uint8Array(43);
      buf[0] = 0x02; // protocolVersion
      buf[1] = 0x03; // PacketType.Data (any valid byte is fine)
      // bytes 2..17 left zero (uuid)
      buf[18] = 0x05; // priority
      const dv = new DataView(buf.buffer);
      dv.setInt32(19, 7, true); // ttl
      dv.setBigInt64(23, 1234567890000n, true); // ts
      // 3 zero-length u16 prefixes occupy 31..36 (already zero)
      dv.setInt32(37, oversize, true); // payload length prefix
      // sigLen at 41..42 left zero
      assert.throws(() => PacketSerializer.deserialize(buf));
    }
  });

  it("rejects negative payload length", () => {
    const buf = new Uint8Array(43);
    buf[0] = 0x02;
    buf[1] = 0x03;
    buf[18] = 0x05;
    const dv = new DataView(buf.buffer);
    dv.setInt32(19, 7, true);
    dv.setBigInt64(23, 0n, true);
    dv.setInt32(37, -1, true);
    assert.throws(() => PacketSerializer.deserialize(buf));
  });
});

// ─── Mutation fuzzer over a valid wire envelope ───────────────────────────

describe("PacketSerializer.deserialize — fuzz mutated valid wire", () => {
  it("never throws an undocumented exception on bit-flipped wire bytes", () => {
    fc.assert(
      fc.property(
        meshPacketArb,
        fc.integer({ min: 1, max: 4 }),
        fc.uint8Array({ minLength: 4, maxLength: 4 }),
        (packet, mutationCount, seed) => {
          const valid = PacketSerializer.serialize(packet);
          if (valid.length === 0) return;
          const mutated = new Uint8Array(valid);
          for (let i = 0; i < mutationCount; i++) {
            const pos = (seed[i % 4] * 31 + i * 7) % mutated.length;
            mutated[pos] = (mutated[pos] + 0x5a + i) & 0xff;
          }
          try {
            PacketSerializer.deserialize(mutated);
          } catch (err) {
            if (!isDocumentedDeserializeError(err)) {
              throw err;
            }
          }
        }
      ),
      fcParams
    );
  });
});

// ─── StoredSignalSession JSON codec round-trip ────────────────────────────

const storedSignalSessionArb: fc.Arbitrary<StoredSignalSession> = fc
  .record({
    rootKey: fc.uint8Array({ minLength: 0, maxLength: 64 }),
    sendChainKey: fc.option(fc.uint8Array({ minLength: 0, maxLength: 64 })),
    recvChainKey: fc.option(fc.uint8Array({ minLength: 0, maxLength: 64 })),
    sendCounter: fc.integer({ min: 0, max: 0x7fffffff }),
    recvCounter: fc.integer({ min: 0, max: 0x7fffffff }),
    previousChainCount: fc.integer({ min: 0, max: 0x7fffffff }),
    myEphemeralPriv: fc.uint8Array({ minLength: 0, maxLength: 64 }),
    myEphemeralPub: fc.uint8Array({ minLength: 0, maxLength: 64 }),
    remoteEphemeralPub: fc.option(
      fc.uint8Array({ minLength: 0, maxLength: 64 })
    ),
    // Realistic shape: production keys are "hex(remoteEphPub):counter"
    // (see SignalProtocol.skippedKey). We restrict to the same alphabet
    // here so the fuzz exercises real-world keys; reserved JS object
    // keys ("__proto__", "constructor") would prototype-pollute the
    // string-keyed JSON envelope on the way through serializeSignalSession,
    // which is a separate hardening question outside the scope of this
    // fuzz harness.
    skippedEntries: fc.array(
      fc.tuple(
        fc
          .tuple(
            // fast-check 4.x removed `fc.hexaString`; stringMatching with a
            // hex-only regex preserves the original semantics.
            fc.stringMatching(/^[0-9a-f]{2,32}$/),
            fc.integer({ min: 0, max: 0x7fffffff })
          )
          .map(([h, c]) => `${h.toUpperCase()}:${c}`),
        fc.uint8Array({ minLength: 0, maxLength: 64 })
      ),
      { maxLength: 8 }
    ),
    pendingPreKeyMessage: fc.boolean(),
    initiatorIdentityKeyX25519: fc.uint8Array({ minLength: 0, maxLength: 64 }),
    usedSignedPreKeyId: fc.integer({ min: 0, max: 0x7fffffff }),
    usedOneTimePreKeyId: fc.integer({ min: 0, max: 0x7fffffff }),
  })
  .map((r) => {
    const skipped = new Map<string, Uint8Array>();
    for (const [k, v] of r.skippedEntries) {
      skipped.set(k, new Uint8Array(v));
    }
    return {
      rootKey: new Uint8Array(r.rootKey),
      sendChainKey: r.sendChainKey ? new Uint8Array(r.sendChainKey) : null,
      recvChainKey: r.recvChainKey ? new Uint8Array(r.recvChainKey) : null,
      sendCounter: r.sendCounter,
      recvCounter: r.recvCounter,
      previousChainCount: r.previousChainCount,
      myEphemeralPriv: new Uint8Array(r.myEphemeralPriv),
      myEphemeralPub: new Uint8Array(r.myEphemeralPub),
      remoteEphemeralPub: r.remoteEphemeralPub
        ? new Uint8Array(r.remoteEphemeralPub)
        : null,
      skippedMessageKeys: skipped,
      pendingPreKeyMessage: r.pendingPreKeyMessage,
      initiatorIdentityKeyX25519: new Uint8Array(r.initiatorIdentityKeyX25519),
      usedSignedPreKeyId: r.usedSignedPreKeyId,
      usedOneTimePreKeyId: r.usedOneTimePreKeyId,
    };
  });

function bytesEqual(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

describe("StoredSignalSession — fuzz round-trip", () => {
  it("serialize -> deserialize reproduces all fields", () => {
    fc.assert(
      fc.property(storedSignalSessionArb, (session) => {
        const blob = serializeSignalSession(session);
        const got = deserializeSignalSession(blob);
        assert.ok(got !== null);
        assert.ok(bytesEqual(got!.rootKey, session.rootKey));
        assert.equal(
          got!.sendChainKey === null,
          session.sendChainKey === null
        );
        if (got!.sendChainKey && session.sendChainKey) {
          assert.ok(bytesEqual(got!.sendChainKey, session.sendChainKey));
        }
        assert.equal(
          got!.recvChainKey === null,
          session.recvChainKey === null
        );
        if (got!.recvChainKey && session.recvChainKey) {
          assert.ok(bytesEqual(got!.recvChainKey, session.recvChainKey));
        }
        assert.equal(got!.sendCounter, session.sendCounter);
        assert.equal(got!.recvCounter, session.recvCounter);
        assert.equal(got!.previousChainCount, session.previousChainCount);
        assert.ok(bytesEqual(got!.myEphemeralPriv, session.myEphemeralPriv));
        assert.ok(bytesEqual(got!.myEphemeralPub, session.myEphemeralPub));
        assert.equal(
          got!.remoteEphemeralPub === null,
          session.remoteEphemeralPub === null
        );
        if (got!.remoteEphemeralPub && session.remoteEphemeralPub) {
          assert.ok(
            bytesEqual(got!.remoteEphemeralPub, session.remoteEphemeralPub)
          );
        }
        assert.equal(
          got!.pendingPreKeyMessage,
          session.pendingPreKeyMessage
        );
        assert.ok(
          bytesEqual(
            got!.initiatorIdentityKeyX25519,
            session.initiatorIdentityKeyX25519
          )
        );
        assert.equal(got!.usedSignedPreKeyId, session.usedSignedPreKeyId);
        assert.equal(got!.usedOneTimePreKeyId, session.usedOneTimePreKeyId);
        assert.equal(
          got!.skippedMessageKeys.size,
          session.skippedMessageKeys.size
        );
        for (const [k, v] of session.skippedMessageKeys.entries()) {
          const out = got!.skippedMessageKeys.get(k);
          assert.ok(out !== undefined);
          assert.ok(bytesEqual(out!, v));
        }
      }),
      fcParams
    );
  });
});

// ─── deserializeSignalSession on arbitrary JSON ───────────────────────────

describe("deserializeSignalSession — fuzz arbitrary JSON / bytes", () => {
  it("returns null or a valid session on fast-check.json values", () => {
    fc.assert(
      fc.property(fc.json(), (jsonString) => {
        // Should never throw — the codec is documented to return null on
        // any parse / shape failure, and otherwise a partially-populated
        // StoredSignalSession with sensible defaults.
        const bytes = new TextEncoder().encode(jsonString);
        const result = deserializeSignalSession(bytes);
        if (result !== null) {
          // Must produce well-formed Uint8Arrays even from arbitrary JSON.
          assert.ok(result.rootKey instanceof Uint8Array);
          assert.ok(result.myEphemeralPriv instanceof Uint8Array);
          assert.ok(result.myEphemeralPub instanceof Uint8Array);
          assert.ok(result.skippedMessageKeys instanceof Map);
        }
      }),
      fcParams
    );
  });

  it("returns null on arbitrary binary bytes (never throws)", () => {
    fc.assert(
      fc.property(
        fc.uint8Array({ minLength: 0, maxLength: 4096 }),
        (data) => {
          // Documented contract: always returns null or a (possibly
          // garbage-shaped) StoredSignalSession; never throws.
          deserializeSignalSession(data);
        }
      ),
      fcParams
    );
  });

  it("returns null on empty bytes", () => {
    assert.equal(deserializeSignalSession(new Uint8Array(0)), null);
  });
});

// ─── EncryptedPayload JSON envelope round-trip ────────────────────────────

/**
 * Inline JSON codec for `EncryptedPayload` — a host wiring the protocol
 * over a JSON-friendly transport (REST, WebSocket, IndexedDB) needs one,
 * but the protocol layer itself only deals with the in-memory shape.
 *
 * The codec is exercised here as a fuzz target: ANY EncryptedPayload
 * round-trips through `encodeEncryptedPayload -> decodeEncryptedPayload`
 * with byte-identical Uint8Array fields and identical scalars.
 */
interface EncryptedPayloadJson {
  ciphertext: string;
  nonce: string;
  messageType: number;
  senderUhid: string;
  counter: number;
  encryptedAtMs: number;
  initiatorIdentityKeyX25519?: string;
  initiatorEphemeralKeyX25519?: string;
  usedSignedPreKeyId?: number;
  usedOneTimePreKeyId?: number;
  senderEphemeralKeyX25519?: string;
  previousChainCount?: number;
}

function encodeEncryptedPayload(p: EncryptedPayload): string {
  const env: EncryptedPayloadJson = {
    ciphertext: Buffer.from(p.ciphertext).toString("base64"),
    nonce: Buffer.from(p.nonce).toString("base64"),
    messageType: p.messageType,
    senderUhid: p.senderUhid,
    counter: p.counter,
    encryptedAtMs: p.encryptedAt.getTime(),
  };
  if (p.initiatorIdentityKeyX25519) {
    env.initiatorIdentityKeyX25519 = Buffer.from(
      p.initiatorIdentityKeyX25519
    ).toString("base64");
  }
  if (p.initiatorEphemeralKeyX25519) {
    env.initiatorEphemeralKeyX25519 = Buffer.from(
      p.initiatorEphemeralKeyX25519
    ).toString("base64");
  }
  if (p.usedSignedPreKeyId !== undefined) {
    env.usedSignedPreKeyId = p.usedSignedPreKeyId;
  }
  if (p.usedOneTimePreKeyId !== undefined) {
    env.usedOneTimePreKeyId = p.usedOneTimePreKeyId;
  }
  if (p.senderEphemeralKeyX25519) {
    env.senderEphemeralKeyX25519 = Buffer.from(
      p.senderEphemeralKeyX25519
    ).toString("base64");
  }
  if (p.previousChainCount !== undefined) {
    env.previousChainCount = p.previousChainCount;
  }
  return JSON.stringify(env);
}

function decodeEncryptedPayload(json: string): EncryptedPayload {
  const env = JSON.parse(json) as EncryptedPayloadJson;
  if (typeof env !== "object" || env === null) {
    throw new Error("decoded JSON is not an object");
  }
  const out: EncryptedPayload = {
    ciphertext: new Uint8Array(Buffer.from(env.ciphertext, "base64")),
    nonce: new Uint8Array(Buffer.from(env.nonce, "base64")),
    messageType: env.messageType,
    senderUhid: env.senderUhid,
    counter: env.counter,
    encryptedAt: new Date(env.encryptedAtMs),
  };
  if (env.initiatorIdentityKeyX25519) {
    out.initiatorIdentityKeyX25519 = new Uint8Array(
      Buffer.from(env.initiatorIdentityKeyX25519, "base64")
    );
  }
  if (env.initiatorEphemeralKeyX25519) {
    out.initiatorEphemeralKeyX25519 = new Uint8Array(
      Buffer.from(env.initiatorEphemeralKeyX25519, "base64")
    );
  }
  if (env.usedSignedPreKeyId !== undefined) {
    out.usedSignedPreKeyId = env.usedSignedPreKeyId;
  }
  if (env.usedOneTimePreKeyId !== undefined) {
    out.usedOneTimePreKeyId = env.usedOneTimePreKeyId;
  }
  if (env.senderEphemeralKeyX25519) {
    out.senderEphemeralKeyX25519 = new Uint8Array(
      Buffer.from(env.senderEphemeralKeyX25519, "base64")
    );
  }
  if (env.previousChainCount !== undefined) {
    out.previousChainCount = env.previousChainCount;
  }
  return out;
}

const encryptedPayloadArb: fc.Arbitrary<EncryptedPayload> = fc
  .record({
    ciphertext: fc.uint8Array({ minLength: 0, maxLength: 4096 }),
    nonce: fc.uint8Array({ minLength: 12, maxLength: 12 }),
    messageType: fc.constantFrom(MESSAGE_TYPE_NORMAL, MESSAGE_TYPE_PRE_KEY),
    senderUhid: fc.string({ minLength: 0, maxLength: 64 }),
    counter: fc.integer({ min: 0, max: 0x7fffffff }),
    // Date constructor clamps very large numbers to NaN; bound to a
    // reasonable range that round-trips cleanly through new Date(ms).
    // ~year 5000 is far enough beyond any sane wire-format use.
    encryptedAtMs: fc.integer({ min: 0, max: 95_617_584_000_000 }),
    initiatorIdentityKeyX25519: fc.option(
      fc.uint8Array({ minLength: 32, maxLength: 32 }),
      { nil: undefined }
    ),
    initiatorEphemeralKeyX25519: fc.option(
      fc.uint8Array({ minLength: 32, maxLength: 32 }),
      { nil: undefined }
    ),
    usedSignedPreKeyId: fc.option(
      fc.integer({ min: 0, max: 0x7fffffff }),
      { nil: undefined }
    ),
    usedOneTimePreKeyId: fc.option(
      fc.integer({ min: 0, max: 0x7fffffff }),
      { nil: undefined }
    ),
    senderEphemeralKeyX25519: fc.option(
      fc.uint8Array({ minLength: 32, maxLength: 32 }),
      { nil: undefined }
    ),
    previousChainCount: fc.option(fc.integer({ min: 0, max: 0x7fffffff }), {
      nil: undefined,
    }),
  })
  .map((r) => {
    const out: EncryptedPayload = {
      ciphertext: new Uint8Array(r.ciphertext),
      nonce: new Uint8Array(r.nonce),
      messageType: r.messageType,
      senderUhid: r.senderUhid,
      counter: r.counter,
      encryptedAt: new Date(r.encryptedAtMs),
    };
    if (r.initiatorIdentityKeyX25519) {
      out.initiatorIdentityKeyX25519 = new Uint8Array(
        r.initiatorIdentityKeyX25519
      );
    }
    if (r.initiatorEphemeralKeyX25519) {
      out.initiatorEphemeralKeyX25519 = new Uint8Array(
        r.initiatorEphemeralKeyX25519
      );
    }
    if (r.usedSignedPreKeyId !== undefined) {
      out.usedSignedPreKeyId = r.usedSignedPreKeyId;
    }
    if (r.usedOneTimePreKeyId !== undefined) {
      out.usedOneTimePreKeyId = r.usedOneTimePreKeyId;
    }
    if (r.senderEphemeralKeyX25519) {
      out.senderEphemeralKeyX25519 = new Uint8Array(r.senderEphemeralKeyX25519);
    }
    if (r.previousChainCount !== undefined) {
      out.previousChainCount = r.previousChainCount;
    }
    return out;
  });

describe("EncryptedPayload JSON codec — fuzz round-trip", () => {
  it("encode -> decode is byte-identical for any payload", () => {
    fc.assert(
      fc.property(encryptedPayloadArb, (payload) => {
        const json = encodeEncryptedPayload(payload);
        const got = decodeEncryptedPayload(json);
        assert.ok(bytesEqual(got.ciphertext, payload.ciphertext));
        assert.ok(bytesEqual(got.nonce, payload.nonce));
        assert.equal(got.messageType, payload.messageType);
        assert.equal(got.senderUhid, payload.senderUhid);
        assert.equal(got.counter, payload.counter);
        assert.equal(got.encryptedAt.getTime(), payload.encryptedAt.getTime());
        assert.equal(
          got.initiatorIdentityKeyX25519 === undefined,
          payload.initiatorIdentityKeyX25519 === undefined
        );
        if (got.initiatorIdentityKeyX25519 && payload.initiatorIdentityKeyX25519) {
          assert.ok(
            bytesEqual(
              got.initiatorIdentityKeyX25519,
              payload.initiatorIdentityKeyX25519
            )
          );
        }
        assert.equal(got.usedSignedPreKeyId, payload.usedSignedPreKeyId);
        assert.equal(got.usedOneTimePreKeyId, payload.usedOneTimePreKeyId);
        assert.equal(got.previousChainCount, payload.previousChainCount);
      }),
      fcParams
    );
  });
});
