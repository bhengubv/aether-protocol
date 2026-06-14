/*
 * ArkTS ↔ C# byte-parity proof, run under Node.
 *
 * This is the parity proof in lieu of the gated on-device DevEco/hvigor build. It
 * bundles the ACTUAL ArkTS `.ets` sources (via esbuild, `.ets`→ts) and asserts the
 * three features against the shared cross-language fixtures:
 *
 *   1. Tipping   — TipPacketPayload canonical bytes byte-identical; deterministic
 *                  Ed25519 reproduces the fixture signatures exactly + they verify;
 *                  null reference_id → 16 zero bytes; .NET mixed-endian GUID order;
 *                  invariant-decimal amount string; service emits TipPacket(24) with
 *                  the exact fixture signature; inbound dispatch + settlement hook;
 *                  malformed-signature drop.
 *   2. Vault     — every systematic data shard + every Cauchy parity shard
 *                  byte-identical; every K-of-N recovery subset decodes to the
 *                  fixture input; K-1 survivors FAIL.
 *   3. PoV       — canonical body byte-identical across all three transports;
 *                  deterministic witness Ed25519 reproduces the fixture signatures +
 *                  they verify; i64 ticks beyond the safe-integer range survive a
 *                  JSON round-trip; witness→subject exchange over PoVTokenExchange(43)
 *                  with countersign + replay rejection; self-vouch / non-short-range
 *                  refusal.
 *
 * Run: npx tsx arkts/test/parity.test.ts
 * SPDX-License-Identifier: MIT
 */

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { loadEtsModule } from './build-ets.ts';

// ── tiny assertion harness (no test runner; we report exact pass/fail) ──────────

let PASS = 0;
let FAIL = 0;
let SECTION_PASS = 0;
const FAILURES: string[] = [];
const VERBOSE = process.env.VERBOSE === '1';

function sectionStart(): void {
  SECTION_PASS = PASS;
}
function sectionEnd(name: string): void {
  console.log(`   ${name}: ${PASS - SECTION_PASS} assertions passed`);
}

function check(cond: boolean, label: string): void {
  if (cond) {
    PASS++;
    if (VERBOSE) {
      console.log(`  ✓ ${label}`);
    }
  } else {
    FAIL++;
    FAILURES.push(label);
    console.error(`  ✗ FAIL: ${label}`);
  }
}

function eqHex(got: string, want: string, label: string): void {
  if (got === want) {
    PASS++;
    if (VERBOSE) {
      console.log(`  ✓ ${label}`);
    }
  } else {
    FAIL++;
    FAILURES.push(label);
    console.error(`  ✗ FAIL: ${label}\n      got : ${got}\n      want: ${want}`);
  }
}

const hereDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(hereDir, '..', '..');
const fixturesDir = join(repoRoot, 'fixtures');
const indexEts = join(hereDir, '..', 'Index.ets');

const toHexBuf = (b: Uint8Array): string => Buffer.from(b).toString('hex');
const fromHexBuf = (s: string): Uint8Array => new Uint8Array(Buffer.from(s, 'hex'));

// ── ArkTS module surface (the exports we exercise) ──────────────────────────────

interface TipInit {
  tipperUhid: string;
  recipientUhid: string;
  amount: string;
  trafficType: string;
  referenceId: string | null;
  timestampUnixMs: bigint;
}
interface TipPayload {
  amount: string;
  referenceId: string | null;
  signature: Uint8Array | null;
  buildCanonicalData(): Uint8Array;
  hasWellFormedSignature(): boolean;
  toJsonString(): string;
}
interface MeshPacketLike {
  type: number;
  sourceUhid: string;
  destinationUhid: string;
  ttl: number;
  payload: Uint8Array;
  signature: Uint8Array;
  packetNonce: Uint8Array;
}
interface RsCodec {
  dataShards: number;
  parityShards: number;
  shardCount: number;
}
interface PoVTok {
  witnessSignature: Uint8Array | null;
  subjectSignature: Uint8Array | null;
  timestampTicks: bigint;
  transportUsed: number;
  signableData(): Uint8Array;
  toJsonString(): string;
}

interface ArkTsModule {
  // ctors / classes
  TipPacketPayload: new (init: TipInit) => TipPayload;
  TipPacketPayloadInit: new () => TipInit;
  MeshPacket: new () => MeshPacketLike;
  MeshTipService: new (
    sender: object,
    signer: object,
    identity: object,
    routing: object | null,
    settle: object | null,
    logger: object | null
  ) => {
    sendTip(
      recipientUhid: string,
      amount: string,
      trafficType: string,
      referenceId: string | null,
      timestampUnixMs: bigint
    ): Promise<MeshPacketLike>;
    handleTipPacket(packet: MeshPacketLike | null): Promise<boolean>;
  };
  NoopMeshTipSettlementProvider: new () => { settleMeshTip(p: TipPayload): Promise<void> };
  guidBytesDotNet: (guid: string) => Uint8Array;

  ReedSolomonCodec: new (k: number, m: number) => RsCodec;
  encodeData: (codec: RsCodec, data: Uint8Array) => Uint8Array[];
  reconstructData: (codec: RsCodec, available: Map<number, Uint8Array>, size: number) => Uint8Array;

  PoVToken: new (init: object) => PoVTok;
  PoVTokenInit: new () => {
    witnessUhid: string;
    subjectUhid: string;
    timestampTicks: bigint;
    transportUsed: number;
    witnessSignature: Uint8Array | null;
    subjectSignature: Uint8Array | null;
  };
  PoVTransportType: { Ble: number; Nfc: number; NearLink: number };
  buildSignableTokenData: (subject: string, ticks: bigint, transport: number) => Uint8Array;
  transportToString: (t: number) => string;
  PoVTokenExchangeService: new (
    sender: object,
    signer: object,
    identity: object,
    logger: object | null,
    clock: object | null
  ) => {
    issueToken(subjectUhid: string, transport: number): Promise<PoVTok | null>;
    handleTokenExchange(packet: MeshPacketLike | null, senderPub: Uint8Array | null): boolean;
    getScore(uhid: string): { uniqueWitnesses: number };
    tokenReceivedListener: { onTokenReceived(t: PoVTok): void } | null;
  };

  // crypto
  publicKeyFromSeed: (seed: Uint8Array) => Uint8Array;
  signWithSeed: (seed: Uint8Array, data: Uint8Array) => Uint8Array;
  verifyEd25519: (pub: Uint8Array, data: Uint8Array, sig: Uint8Array) => boolean;
  NobleEd25519Signer: new (seed: Uint8Array) => {
    getPublicKey(): Uint8Array;
    signData(data: Uint8Array): Uint8Array;
    verifySignature(pub: Uint8Array, data: Uint8Array, sig: Uint8Array): boolean;
  };
  PacketType: { TipPacket: number; PoVTokenExchange: number };
}

// ── fixture shapes ──────────────────────────────────────────────────────────────

interface TipCase {
  tipper_uhid: string;
  recipient_uhid: string;
  amount: string;
  traffic_type: string;
  reference_id: string | null;
  timestamp_unix_ms: number;
  canonical_bytes: string;
  signature: string;
}
interface TipVectors {
  ed25519_seed: string;
  public_key: string;
  cases: TipCase[];
}
interface RsVectors {
  k: number;
  m: number;
  n: number;
  input_size: number;
  shard_size: number;
  input: string;
  shards: { index: number; hex: string }[];
  recovery: { note: string; survivor_indices: number[]; recovered: string }[];
  should_fail: { survivor_indices: number[] };
}
interface PoVCase {
  subject_uhid: string;
  transport: string;
  transport_byte: number;
  canonical_body: string;
  witness_signature: string;
}
interface PoVVectors {
  witness_seed: string;
  witness_public_key: string;
  cases: PoVCase[];
}

// ── service-level fakes (ArkTS interfaces are structural at the JS level) ───────

class FakeTipSender {
  localUhid: string;
  sent: MeshPacketLike[] = [];
  broadcasts: MeshPacketLike[] = [];
  constructor(localUhid: string) {
    this.localUhid = localUhid;
  }
  async send(packet: MeshPacketLike, _nextHop: string): Promise<boolean> {
    this.sent.push(packet);
    return true;
  }
  async broadcast(packet: MeshPacketLike): Promise<number> {
    this.broadcasts.push(packet);
    return 1;
  }
}

class FakeEnvelopeSigner {
  signPacket(packet: MeshPacketLike): MeshPacketLike {
    packet.signature = Uint8Array.from(Buffer.from('envelope-sig', 'utf8'));
    packet.packetNonce = Uint8Array.from([1, 2, 3, 4, 5, 6, 7, 8]);
    return packet;
  }
}

function makeSeedIdentity(M: ArkTsModule, seed: Uint8Array): object {
  return {
    signData(data: Uint8Array): Uint8Array {
      return M.signWithSeed(seed, data);
    },
  };
}

class RecordingSettler {
  calls: TipPayload[] = [];
  async settleMeshTip(payload: TipPayload): Promise<void> {
    this.calls.push(payload);
  }
}

// PoV envelope signer with real Ed25519 over "src:dst" + nonce replay-dedup.
class PoVPassSigner {
  private readonly seed: Uint8Array;
  private readonly M: ArkTsModule;
  private seen = new Set<string>();
  private nonceCounter = 0;
  constructor(M: ArkTsModule, seed: Uint8Array) {
    this.M = M;
    this.seed = seed;
  }
  signPacket(packet: MeshPacketLike): MeshPacketLike {
    const n = ++this.nonceCounter;
    packet.packetNonce = Uint8Array.from([n, 9, 9, 9, 9, 9, 9, 9]);
    packet.signature = this.M.signWithSeed(
      this.seed,
      Uint8Array.from(Buffer.from(`${packet.sourceUhid}:${packet.destinationUhid}`, 'utf8'))
    );
    return packet;
  }
  verifyPacket(packet: MeshPacketLike, senderPub: Uint8Array): boolean {
    const key = `${packet.sourceUhid}:${toHexBuf(packet.packetNonce)}`;
    if (this.seen.has(key)) {
      return false;
    }
    this.seen.add(key);
    return this.M.verifyEd25519(
      senderPub,
      Uint8Array.from(Buffer.from(`${packet.sourceUhid}:${packet.destinationUhid}`, 'utf8')),
      packet.signature
    );
  }
}

function makeRealIdentity(M: ArkTsModule, seed: Uint8Array): object {
  return {
    signData(data: Uint8Array): Uint8Array {
      return M.signWithSeed(seed, data);
    },
    verifySignature(pub: Uint8Array, data: Uint8Array, sig: Uint8Array): boolean {
      return M.verifyEd25519(pub, data, sig);
    },
  };
}

// ── main ─────────────────────────────────────────────────────────────────────

async function main(): Promise<void> {
  console.log('Bundling ArkTS .ets sources via esbuild (.ets → ts) ...');
  const M = await loadEtsModule<ArkTsModule>(indexEts);
  console.log('Loaded ArkTS module. Running parity assertions against shared fixtures.\n');

  // ════════════════════════════ 1. TIPPING ═══════════════════════════════════
  console.log('── Tipping (TipPacket = 24) ──');
  sectionStart();
  const TV = JSON.parse(
    readFileSync(join(fixturesDir, 'tipping', 'tip_packet_basic.json'), 'utf8')
  ) as TipVectors;

  // 1a. Derived public key from seed matches the fixture.
  const tipSeed = fromHexBuf(TV.ed25519_seed);
  check(tipSeed.length === 32, 'tip: seed is 32 bytes');
  eqHex(toHexBuf(M.publicKeyFromSeed(tipSeed)), TV.public_key, 'tip: derived public key matches fixture');

  function buildTipPayload(c: TipCase): TipPayload {
    const init = new M.TipPacketPayloadInit();
    init.tipperUhid = c.tipper_uhid;
    init.recipientUhid = c.recipient_uhid;
    init.amount = c.amount;
    init.trafficType = c.traffic_type;
    init.referenceId = c.reference_id;
    init.timestampUnixMs = BigInt(c.timestamp_unix_ms);
    return new M.TipPacketPayload(init);
  }

  for (const c of TV.cases) {
    const p = buildTipPayload(c);
    // 1b. Canonical bytes byte-identical.
    eqHex(toHexBuf(p.buildCanonicalData()), c.canonical_bytes, `tip canonical bytes [${c.tipper_uhid}]`);

    // 1c. Deterministic Ed25519 reproduces the fixture signature exactly.
    const sig = M.signWithSeed(tipSeed, p.buildCanonicalData());
    eqHex(toHexBuf(sig), c.signature, `tip deterministic signature [${c.tipper_uhid}]`);

    // 1d. The fixture signature verifies.
    check(
      M.verifyEd25519(M.publicKeyFromSeed(tipSeed), p.buildCanonicalData(), fromHexBuf(c.signature)),
      `tip fixture signature verifies [${c.tipper_uhid}]`
    );

    // 1e. amount stays a string, verbatim.
    check(typeof p.amount === 'string' && p.amount === c.amount, `tip amount is verbatim string [${c.tipper_uhid}]`);
  }

  // 1f. null reference_id → 16 zero bytes; present id → .NET mixed-endian order.
  const withId = TV.cases.find((c) => c.reference_id !== null)!;
  const withoutId = TV.cases.find((c) => c.reference_id === null)!;
  const cNull = buildTipPayload(withoutId).buildCanonicalData();
  const guidRegionNull = cNull.subarray(cNull.length - 8 - 16, cNull.length - 8);
  eqHex(toHexBuf(guidRegionNull), '00000000000000000000000000000000', 'tip: null reference_id is 16 zero bytes');
  const cId = buildTipPayload(withId).buildCanonicalData();
  const guidRegionId = cId.subarray(cId.length - 8 - 16, cId.length - 8);
  eqHex(toHexBuf(guidRegionId), '22221111333344445555666677778888', 'tip: .NET mixed-endian GUID byte order');
  // direct guidBytesDotNet check
  eqHex(
    toHexBuf(M.guidBytesDotNet('11112222-3333-4444-5555-666677778888')),
    '22221111333344445555666677778888',
    'tip: guidBytesDotNet mixed-endian'
  );

  // 1g. JSON round-trip preserves canonical bytes + signature.
  {
    const c = TV.cases[0];
    const p = buildTipPayload(c);
    p.signature = M.signWithSeed(tipSeed, p.buildCanonicalData());
    const back = (M.TipPacketPayload as unknown as { parse: (s: string) => TipPayload }).parse(p.toJsonString());
    eqHex(
      toHexBuf(back.buildCanonicalData()),
      toHexBuf(p.buildCanonicalData()),
      'tip: canonical bytes unchanged across JSON round-trip'
    );
    eqHex(toHexBuf(back.signature!), toHexBuf(p.signature), 'tip: signature unchanged across JSON round-trip');
  }

  // 1h. Service emits TipPacket(24) carrying the exact fixture signature; broadcasts.
  {
    const c = TV.cases[0];
    const sender = new FakeTipSender(c.tipper_uhid);
    const svc = new M.MeshTipService(sender, new FakeEnvelopeSigner(), makeSeedIdentity(M, tipSeed), null, null, null);
    const signed = await svc.sendTip(
      c.recipient_uhid,
      c.amount,
      c.traffic_type,
      c.reference_id,
      BigInt(c.timestamp_unix_ms)
    );
    check(signed.type === M.PacketType.TipPacket, 'tip service: emitted packet type is TipPacket(24)');
    const emitted = (M.TipPacketPayload as unknown as { parseBytes: (b: Uint8Array) => TipPayload }).parseBytes(
      signed.payload
    );
    eqHex(toHexBuf(emitted.signature!), c.signature, 'tip service: emitted signature is byte-identical to fixture');
    check(sender.broadcasts.length === 1 && sender.sent.length === 0, 'tip service: broadcast with no route resolver');
  }

  // 1i. Inbound reaches settlement hook; malformed signature dropped first.
  {
    const c = TV.cases[0];
    const sender = new FakeTipSender(c.recipient_uhid);
    const settler = new RecordingSettler();
    const svc = new M.MeshTipService(
      sender,
      new FakeEnvelopeSigner(),
      makeSeedIdentity(M, tipSeed),
      null,
      settler,
      null
    );

    const p = buildTipPayload(c);
    p.signature = M.signWithSeed(tipSeed, p.buildCanonicalData());
    const pkt = new M.MeshPacket();
    pkt.type = M.PacketType.TipPacket;
    pkt.sourceUhid = c.tipper_uhid;
    pkt.destinationUhid = c.recipient_uhid;
    pkt.payload = Uint8Array.from(Buffer.from(p.toJsonString(), 'utf8'));
    const handled = await svc.handleTipPacket(pkt);
    check(handled && settler.calls.length === 1, 'tip service: inbound tip reaches settlement hook');

    settler.calls = [];
    p.signature = Uint8Array.from([0, 1, 2]); // malformed (wrong length)
    const badPkt = new M.MeshPacket();
    badPkt.type = M.PacketType.TipPacket;
    badPkt.sourceUhid = c.tipper_uhid;
    badPkt.destinationUhid = c.recipient_uhid;
    badPkt.payload = Uint8Array.from(Buffer.from(p.toJsonString(), 'utf8'));
    const handledBad = await svc.handleTipPacket(badPkt);
    check(!handledBad && settler.calls.length === 0, 'tip service: malformed-signature tip dropped before hook');
  }

  // 1j. No-op provider settles nothing without throwing.
  {
    let threw = false;
    try {
      await new M.NoopMeshTipSettlementProvider().settleMeshTip(buildTipPayload(TV.cases[0]));
    } catch {
      threw = true;
    }
    check(!threw, 'tip service: no-op settlement provider does not throw');
  }

  sectionEnd('Tipping');

  // ════════════════════════════ 2. VAULT (Reed-Solomon) ══════════════════════
  console.log('\n── Vault (Cauchy-Reed-Solomon K=10 M=4 GF(2⁸) 0x11D α=2) ──');
  sectionStart();
  const RV = JSON.parse(
    readFileSync(join(fixturesDir, 'vault', 'reed_solomon_basic.json'), 'utf8')
  ) as RsVectors;

  const rsInput = fromHexBuf(RV.input);
  check(rsInput.length === RV.input_size, 'rs: input size matches fixture');
  const codec = new M.ReedSolomonCodec(RV.k, RV.m);
  check(
    codec.dataShards === RV.k && codec.parityShards === RV.m && codec.shardCount === RV.n,
    'rs: codec params K/M/N match fixture'
  );

  const shards = M.encodeData(codec, rsInput);
  check(shards.length === RV.n, 'rs: produced N shards');
  check(shards[0].length === RV.shard_size, 'rs: shard size matches fixture');

  // 2a. Every shard (systematic data + Cauchy parity) byte-identical.
  for (const want of RV.shards) {
    eqHex(toHexBuf(shards[want.index]), want.hex, `rs shard ${want.index} byte-identical`);
  }

  // 2b. Every recovery subset decodes to the fixture input byte-for-byte.
  for (const rec of RV.recovery) {
    const available = new Map<number, Uint8Array>();
    for (const idx of rec.survivor_indices) {
      available.set(idx, shards[idx]);
    }
    const recovered = M.reconstructData(codec, available, RV.input_size);
    eqHex(toHexBuf(recovered), rec.recovered, `rs recovery "${rec.note}"`);
    check(toHexBuf(recovered) === RV.input, `rs recovery "${rec.note}" equals original input`);
  }

  // 2c. K-1 survivors must FAIL.
  {
    check(RV.should_fail.survivor_indices.length === RV.k - 1, 'rs: should_fail carries K-1 survivors');
    const available = new Map<number, Uint8Array>();
    for (const idx of RV.should_fail.survivor_indices) {
      available.set(idx, shards[idx]);
    }
    let threw = false;
    try {
      M.reconstructData(codec, available, RV.input_size);
    } catch {
      threw = true;
    }
    check(threw, 'rs: K-1 survivors is unrecoverable (decode throws)');
  }

  // 2d. Matrix-inversion path: drop first M data shards, survive on data[M..K-1]+parity.
  {
    const available = new Map<number, Uint8Array>();
    for (let i = RV.m; i < RV.k; i++) {
      available.set(i, shards[i]);
    }
    for (let i = RV.k; i < RV.n; i++) {
      available.set(i, shards[i]);
    }
    const recovered = M.reconstructData(codec, available, RV.input_size);
    check(toHexBuf(recovered) === RV.input, 'rs: parity-assisted (inversion) recovery equals input');
  }

  sectionEnd('Vault');

  // ════════════════════════════ 3. PoV (PoVTokenExchange = 43) ════════════════
  console.log('\n── Market / Proof-of-Vicinity (PoVTokenExchange = 43) ──');
  sectionStart();
  const povText = readFileSync(join(fixturesDir, 'market', 'pov_token_basic.json'), 'utf8');
  const PVraw = JSON.parse(povText) as PoVVectors;
  // Read ticks literals losslessly (beyond Number.MAX_SAFE_INTEGER) in doc order.
  const tickLiterals = [...povText.matchAll(/"timestamp_ticks"\s*:\s*(-?\d+)/g)].map((m) => BigInt(m[1]));
  const povTicks: bigint[] = PVraw.cases.map((_c, i) => tickLiterals[i]);

  const povSeed = fromHexBuf(PVraw.witness_seed);
  check(povSeed.length === 32, 'pov: witness seed is 32 bytes');
  eqHex(toHexBuf(M.publicKeyFromSeed(povSeed)), PVraw.witness_public_key, 'pov: witness public key matches fixture');

  for (let i = 0; i < PVraw.cases.length; i++) {
    const c = PVraw.cases[i];
    const ticks = povTicks[i];
    const body = M.buildSignableTokenData(c.subject_uhid, ticks, c.transport_byte);
    // 3a. Canonical body byte-identical.
    eqHex(toHexBuf(body), c.canonical_body, `pov canonical body [${c.subject_uhid}/${c.transport}]`);
    // transport enum byte ↔ name.
    check(M.transportToString(c.transport_byte) === c.transport, `pov transport name for byte ${c.transport_byte}`);
    // 3b. Deterministic witness signature reproduces the fixture.
    const sig = M.signWithSeed(povSeed, body);
    eqHex(toHexBuf(sig), c.witness_signature, `pov deterministic witness signature [${c.subject_uhid}]`);
    // 3c. Fixture witness signature verifies.
    check(
      M.verifyEd25519(M.publicKeyFromSeed(povSeed), body, fromHexBuf(c.witness_signature)),
      `pov fixture witness signature verifies [${c.subject_uhid}]`
    );
  }

  // 3d. i64 ticks beyond the safe-integer range survive a JSON round-trip exactly.
  {
    const big = 638123456789012345n;
    check(big > BigInt(Number.MAX_SAFE_INTEGER), 'pov: test ticks value exceeds safe-integer range');
    const init = new M.PoVTokenInit();
    init.witnessUhid = 'aether:witness:big';
    init.subjectUhid = 'aether:subject:big';
    init.timestampTicks = big;
    init.transportUsed = M.PoVTransportType.Nfc;
    const tok = new M.PoVToken(init);
    const back = (M.PoVToken as unknown as { parse: (s: string) => PoVTok }).parse(tok.toJsonString());
    check(back.timestampTicks === big, 'pov: i64 ticks survive JSON round-trip losslessly');
    eqHex(toHexBuf(back.signableData()), toHexBuf(tok.signableData()), 'pov: canonical body identical after round-trip');
  }

  // 3e. Full on-mesh exchange over packet 43: countersign + replay rejection.
  {
    const wSeed = randomSeed();
    const sSeed = randomSeed();
    const witnessPub = M.publicKeyFromSeed(wSeed);
    const subjectPub = M.publicKeyFromSeed(sSeed);
    const witnessUhid = 'aether:node:witness';
    const subjectUhid = 'aether:node:subject';

    const wSender = new FakeTipSender(witnessUhid); // send(packet, subject) shape matches
    const witness = new M.PoVTokenExchangeService(
      wSender,
      new PoVPassSigner(M, wSeed),
      makeRealIdentity(M, wSeed),
      null,
      null
    );
    const token = await witness.issueToken(subjectUhid, M.PoVTransportType.Ble);
    check(token !== null && wSender.sent.length === 1, 'pov exchange: witness issued exactly one directed packet');
    const exchangePkt = wSender.sent[0];
    check(exchangePkt.type === M.PacketType.PoVTokenExchange, 'pov exchange: issued packet type is PoVTokenExchange(43)');
    check(exchangePkt.ttl === 1, 'pov exchange: issued packet TTL is 1 (one short-range hop)');

    const sSender = new FakeTipSender(subjectUhid);
    const subject = new M.PoVTokenExchangeService(
      sSender,
      new PoVPassSigner(M, sSeed),
      makeRealIdentity(M, sSeed),
      null,
      null
    );
    let received: PoVTok | null = null;
    subject.tokenReceivedListener = {
      onTokenReceived(t: PoVTok): void {
        received = t;
      },
    };
    const accepted = subject.handleTokenExchange(exchangePkt, witnessPub);
    check(accepted && received !== null, 'pov exchange: subject accepted + recorded the witness token');

    if (received !== null) {
      const acc = received as PoVTok;
      const body = acc.signableData();
      check(
        acc.witnessSignature !== null && M.verifyEd25519(witnessPub, body, acc.witnessSignature),
        'pov exchange: witness signature verifies on accepted token'
      );
      check(
        acc.subjectSignature !== null && M.verifyEd25519(subjectPub, body, acc.subjectSignature),
        'pov exchange: subject countersignature verifies on accepted token'
      );
    }
    check(subject.getScore(subjectUhid).uniqueWitnesses === 1, 'pov exchange: score reflects one unique witness');

    // Replay of the same packet is rejected by the signer's nonce dedup.
    const replay = subject.handleTokenExchange(exchangePkt, witnessPub);
    check(!replay, 'pov exchange: replayed packet is rejected');
  }

  // 3f. Refuses self-vouch and non-short-range minting.
  {
    const seed = randomSeed();
    const sender = new FakeTipSender('aether:node:self');
    const svc = new M.PoVTokenExchangeService(
      sender,
      new PoVPassSigner(M, seed),
      makeRealIdentity(M, seed),
      null,
      null
    );
    const selfVouch = await svc.issueToken('aether:node:self', M.PoVTransportType.Ble);
    check(selfVouch === null, 'pov exchange: self-vouch refused');
    const remote = await svc.issueToken('aether:node:other', 9); // transport 9 is not short-range
    check(remote === null, 'pov exchange: non-short-range minting refused');
    check(sender.sent.length === 0, 'pov exchange: no packet sent for refused issuances');
  }

  sectionEnd('PoV');

  // ── summary ──
  console.log('\n────────────────────────────────────────────');
  console.log(`ArkTS byte-parity harness: ${PASS} passed, ${FAIL} failed.`);
  if (FAIL > 0) {
    console.log('Failures:');
    for (const f of FAILURES) {
      console.log(`  - ${f}`);
    }
    process.exitCode = 1;
  } else {
    console.log('ALL PARITY ASSERTIONS PASSED — ArkTS is byte-identical to the C# reference fixtures.');
  }
}

/** 32 random bytes for the exchange-flow keys (non-deterministic part of the test). */
function randomSeed(): Uint8Array {
  const s = new Uint8Array(32);
  for (let i = 0; i < 32; i++) {
    s[i] = Math.floor(Math.random() * 256);
  }
  return s;
}

main().catch((err: unknown) => {
  console.error('Harness crashed:', err);
  process.exitCode = 1;
});
