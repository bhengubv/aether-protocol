/*
 * On-device (Hypium) byte-parity tests for the Aether Protocol ArkTS port.
 *
 * These mirror the Node parity harness (arkts/test/parity.test.ts) but run under
 * the HarmonyOS Hypium framework on a device/emulator via DevEco Studio or the
 * hvigor CLI. The cross-language fixture vectors are embedded inline so the suite
 * is self-contained on-device (the test runner does not read the repo's
 * fixtures/ directory).
 *
 * Vectors are copied verbatim from:
 *   fixtures/tipping/tip_packet_basic.json
 *   fixtures/vault/reed_solomon_basic.json   (params + a subset of shards/recovery)
 *   fixtures/market/pov_token_basic.json
 *
 * SPDX-License-Identifier: MIT
 */

import { describe, it, expect } from '@ohos/hypium';

import { toHex, fromHex } from '../../../main/ets/protocol/Bytes';
import { publicKeyFromSeed, signWithSeed, verifyEd25519 } from '../../../main/ets/protocol/Ed25519Provider';
import { TipPacketPayload, TipPacketPayloadInit } from '../../../main/ets/incentive/TipPacketPayload';
import { ReedSolomonCodec } from '../../../main/ets/vault/ReedSolomonCodec';
import { encodeData, reconstructData } from '../../../main/ets/vault/VaultCodec';
import { buildSignableTokenData, transportToString, PoVTransportType } from '../../../main/ets/market/PoVToken';

// ── Tipping vectors (fixtures/tipping/tip_packet_basic.json) ────────────────────

const TIP_SEED_HEX: string = '000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f';
const TIP_PUBLIC_KEY_HEX: string = '03a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1ddc8664125531b8';

class TipCase {
  tipper: string;
  recipient: string;
  amount: string;
  traffic: string;
  referenceId: string | null;
  timestampMs: bigint;
  canonicalHex: string;
  signatureHex: string;

  constructor(
    tipper: string,
    recipient: string,
    amount: string,
    traffic: string,
    referenceId: string | null,
    timestampMs: bigint,
    canonicalHex: string,
    signatureHex: string
  ) {
    this.tipper = tipper;
    this.recipient = recipient;
    this.amount = amount;
    this.traffic = traffic;
    this.referenceId = referenceId;
    this.timestampMs = timestampMs;
    this.canonicalHex = canonicalHex;
    this.signatureHex = signatureHex;
  }
}

const TIP_CASES: TipCase[] = [
  new TipCase(
    'aether:tipper:aa',
    'aether:recipient:bb',
    '12.50',
    'message-relay',
    '11112222-3333-4444-5555-666677778888',
    1700000000000n,
    '100000006165746865723a7469707065723a6161130000006165746865723a726563697069656e743a62620500000031322e35300d0000006d6573736167652d72656c6179222211113333444455556666777788880068e5cf8b010000',
    '60831d07066f9a46da8bc28e00ca44db838b07162b6c17e5b4866aa29c069d8cde27101f1ea062756e147a90450cf530cead1c2d9357e58d92374cf3e180c40b'
  ),
  new TipCase(
    'aether:tipper:zz',
    'aether:recipient:bb',
    '0.0001',
    'gateway-share',
    null,
    1699999999001n,
    '100000006165746865723a7469707065723a7a7a130000006165746865723a726563697069656e743a626206000000302e303030310d000000676174657761792d7368617265000000000000000000000000000000001964e5cf8b010000',
    '70563193674f88a960232a22bb3da37229063d59bf132c251a271ad64dc92c33e19d223f8ff9d92d96da5bc6d9e7b66452940ad6bdf80db063806caf42a2ae04'
  ),
  new TipCase(
    'aether:node:00',
    'aether:node:01',
    '123456.789',
    'stream-relay',
    'deadbeef-cafe-4bad-8f00-0123456789ab',
    1701234567890n,
    '0e0000006165746865723a6e6f64653a30300e0000006165746865723a6e6f64653a30310a0000003132333435362e3738390c00000073747265616d2d72656c6179efbeaddefecaad4b8f000123456789abd26a7b198c010000',
    '6e5d747150352b84540296a1720042129359bdb3330cbdcb9615bb9b42f31495688b6a53dbee6fe9d23f21fb101910b357131a50abc5ec0062e23460c7336b07'
  ),
];

function buildTip(c: TipCase): TipPacketPayload {
  const init = new TipPacketPayloadInit();
  init.tipperUhid = c.tipper;
  init.recipientUhid = c.recipient;
  init.amount = c.amount;
  init.trafficType = c.traffic;
  init.referenceId = c.referenceId;
  init.timestampUnixMs = c.timestampMs;
  return new TipPacketPayload(init);
}

// ── Vault vectors (fixtures/vault/reed_solomon_basic.json) ──────────────────────
// Full input + the canonical parity shards + the systematic recovery, embedded so
// the device test reproduces the C# bytes without filesystem access.

const RS_K: number = 10;
const RS_M: number = 4;
const RS_N: number = 14;
const RS_INPUT_SIZE: number = 2222;
const RS_SHARD_SIZE: number = 223;

// The repeated data pattern from the fixture (2222 bytes). Generated identically to
// the fixture input so encode() reproduces every shard.
const RS_INPUT_HEX: string =
  '0726456483a2c1e0ff1e3d5c7b9ab9d8f71635547392b1d0ef0e2d4c6b8aa9c8e70625446382a1c0dffe1d3c5b7a99b8d7f61534537291b0cfee0d2c4b6a89a8c7e60524436281a0bfdefd1c3b5a7998b7d6f51433527190afceed0c2b4a6988a7c6e504234261809fbeddfc1b3a597897b6d5f4133251708faecdec0b2a496887a6c5e4032241607f9ebddcfb1a39587796b5d4f31231506f8eadcceb0a29486786a5c4e30221405f7e9dbcdbfa1938577695b4d3f211304f6e8daccbea0928476685a4c3e201203f5e7d9cbbdaf91837567594b3d2f1102f4e6d8cabcae90827466584a3c2e1001f3e5d7c9bbad9f81736557493b2d1f00f2e4d6c8baac9e80726456483a2c1e0ff1e3d5c7b9ab9d8f71635547392b1d0ef0e2d4c6b8aa9c8e70625446382a1c0dffe1d3c5b7a99b8d7f61534537291b0cfee0d2c4b6a89a8c7e60524436281a0bfdefd1c3b5a7998b7d6f51433527190afceed0c2b4a6988a7c6e504234261809fbeddfc1b3a597897b6d5f4133251708faecdec0b2a496887a6c5e4032241607f9ebddcfb1a39587796b5d4f31231506f8eadcceb0a29486786a5c4e30221405f7e9dbcdbfa1938577695b4d3f211304f6e8daccbea0928476685a4c3e201203f5e7d9cbbdaf91837567594b3d2f1102f4e6d8cabcae90827466584a3c2e1001f3e5d7c9bbad9f81736557493b2d1f00f2e4d6c8baac9e80726456483a2c1e0ff1e3d5c7b9ab9d8f71635547392b1d0ef0e2d4c6b8aa9c8e70625446382a1c0dffe1d3c5b7a99b8d7f61534537291b0cfee0d2c4b6a89a8c7e60524436281a0bfdefd1c3b5a7998b7d6f51433527190afceed0c2b4a6988a7c6e504234261809fbeddfc1b3a597897b6d5f4133251708faecdec0b2a496887a6c5e4032241607f9ebddcfb1a39587796b5d4f31231506f8eadcceb0a29486786a5c4e30221405f7e9dbcdbfa1938577695b4d3f211304f6e8daccbea0928476685a4c3e201203f5e7d9cbbdaf91837567594b3d2f1102f4e6d8cabcae90827466584a3c2e1001f3e5d7c9bbad9f81736557493b2d1f00f2e4d6c8baac9e80726456483a2c1e0ff1e3d5c7b9ab9d8f71635547392b1d0ef0e2d4c6b8aa9c8e70625446382a1c0dffe1d3c5b7a99b8d7f61534537291b0cfee0d2c4b6a89a8c7e60524436281a0bfdefd1c3b5a7998b7d6f51433527190afceed0c2b4a6988a7c6e504234261809fbeddfc1b3a597897b6d5f4133251708faecdec0b2a496887a6c5e4032241607f9ebddcfb1a39587796b5d4f31231506f8eadcceb0a29486786a5c4e30221405f7e9dbcdbfa1938577695b4d3f211304f6e8daccbea0928476685a4c3e201203f5e7d9cbbdaf91837567594b3d2f1102f4e6d8cabcae90827466584a3c2e1001f3e5d7c9bbad9f81736557493b2d1f00f2e4d6c8baac9e80726456483a2c1e0ff1e3d5c7b9ab9d8f71635547392b1d0ef0e2d4c6b8aa9c8e70625446382a1c0dffe1d3c5b7a99b8d7f61534537291b0cfee0d2c4b6a89a8c7e60524436281a0bfdefd1c3b5a7998b7d6f51433527190afceed0c2b4a6988a7c6e504234261809fbeddfc1b3a597897b6d5f4133251708faecdec0b2a496887a6c5e4032241607f9ebddcfb1a39587796b5d4f31231506f8eadcceb0a29486786a5c4e30221405f7e9dbcdbfa1938577695b4d3f211304f6e8daccbea0928476685a4c3e201203f5e7d9cbbdaf91837567594b3d2f1102f4e6d8cabcae90827466584a3c2e1001f3e5d7c9bbad9f81736557493b2d1f00f2e4d6c8baac9e80726456483a2c1e0ff1e3d5c7b9ab9d8f71635547392b1d0ef0e2d4c6b8aa9c8e70625446382a1c0dffe1d3c5b7a99b8d7f61534537291b0cfee0d2c4b6a89a8c7e60524436281a0bfdefd1c3b5a7998b7d6f51433527190afceed0c2b4a6988a7c6e504234261809fbeddfc1b3a597897b6d5f4133251708faecdec0b2a496887a6c5e4032241607f9ebddcfb1a39587796b5d4f31231506f8eadcceb0a29486786a5c4e30221405f7e9dbcdbfa1938577695b4d3f211304f6e8daccbea0928476685a4c3e201203f5e7d9cbbdaf91837567594b3d2f1102f4e6d8cabcae90827466584a3c2e1001f3e5d7c9bbad9f81736557493b2d1f00f2e4d6c8baac9e80726456483a2c1e0ff1e3d5c7b9ab9d8f71635547392b1d0ef0e2d4c6b8aa9c8e70625446382a1c0dffe1d3c5b7a99b8d7f61534537291b0cfee0d2c4b6a89a8c7e60524436281a0bfdefd1c3b5a7998b7d6f51433527190afceed0c2b4a6988a7c6e504234261809fbeddfc1b3a597897b6d5f4133251708faecdec0b2a496887a6c5e4032241607f9ebddcfb1a39587796b5d4f31231506f8eadcceb0a29486786a5c4e30221405f7e9dbcdbfa1938577695b4d3f211304f6e8daccbea0928476685a4c3e201203f5e7d9cbbdaf91837567594b3d2f1102f4e6d8cabcae90827466584a3c2e1001f3e5d7c9bbad9f81736557493b2d1f00f2e4d6c8baac9e80726456483a2c1e0ff1e3d5c7b9ab9d8f71635547392b1d0ef0e2d4c6b8aa9c8e70625446382a1c0dffe1d3c5b7a99b8d7f61534537291b0cfee0d2c4b6a89a8c7e60524436281a0bfdefd1c3b5a7998b7d6f51433527190afceed0c2b4a6988a7c6e504234261809fbeddfc1b3a597897b6d5f4133251708faecdec0b2a496887a6c5e4032241607f9ebddcfb1a39587796b5d4f31231506f8eadcceb0a29486786a5c4e30221405f7e9dbcdbfa1938577695b4d3f211304f6e8daccbea0928476685a4c3e201203f5e7d9cbbdaf91837567594b3d2f1102f4e6d8cabcae90827466584a3c2e1001f3e5d7c9bbad9f81736557493b2d1f00f2e4d6c8baac9e80726456483a2c1e0ff1e3d5c7b9ab9d8f71635547392b1d0ef0e2d4c6b8aa9c8e70625446382a1c0dffe1d3c5b7a99b8d7f61534537291b0cfee0d2c4b6a89a8c7e60524436281a0bfdefd1c3b5a7998b7d6f51433527190afceed0c2b4a6988a7c6e504234261809fbeddfc1b3a597897b6d5f4133251708faecdec0b2a496887a6c5e4032241607f9ebddcfb1a39587796b5d4f31231506f8eadcceb0a29486786a5c4e30221405f7e9dbcdbfa';

// Parity shards 10..13 (Cauchy MDS) — the bytes the encoder MUST reproduce.
const RS_PARITY_10: string =
  'b5a06244e588be3df46e32e3ff5b2e7c7e4bf0af85632cd6ddcd740e9dee56de8c12a9f6dc3a758f2671615dad30a25c8c724264375a9e1d6ff4c6c52fd7e4153e2b1b3d6e03c744cde5669acdd0d9058cc07bd6fce8a7afa446ff77e465dda7f599228fa5b1fef65fc7ea3cd4de291cf50bc9ef4e231596168d4d4e56ae6f9e475290b6177a4ccf069cc0110da9dc8e8cb9025d7791de242f3f86fc6f1ca42c7ee05b042ec8877dd48393af5fc250ae7e80b096c5a86cef9d063437dd2516e7ccd9e9cf9cf135b63f1794683f222bf77e3289240e1a55a1b572d4f67a2d8a';
const RS_PARITY_11: string =
  'a1f3ac17f1db216ed23d09b052080d2fb1183efc9130b3851e9e9d5da5bd4c8d984167a5c869eadcad22c60ecd63af0f63218c372309014eaca72f961784fe462a78d56e7a505817d6b678c91d83a6568893b585e8bb38fc67151624dc36c7f4e1caecdcb1e261a5d4944d6fb48d244f1a5807bc5a708ac5d5dea41d6efd75cd53015ee50329d39c20cffb42a0faffdd43eacc0e63c24177ec6c6faf574fbe7f6ab395573a9b182e5fd034fc3f915dfd91d37ec5d1fbf3bc5e55dd64e5760cb4d88a279c88a2aae524448a3bef7154a47a6147771a49ca8c8942df1274234c';
const RS_PARITY_12: string =
  '4a0aada1f4e2314dfa9e1f41b56b10a89c1e89b5b0f615595d9db31a1d7eccf5021285b9bcfa1955a2f4c7c75a7563224f56919da8be0d7145d5ab320536d4dd1a5a9d91a4b2017d41ced271d43b9698d54ed98580a645696dcde32a2d2e9cc53242d5898caa496592f897d66a21332b7f66c1cd988e5d2175e5fb623506848d2a6acdc19482512d9afe7f21d50b70c8fc7ee9d5d09675393dfdd37a7d1eac956272e5d9dc9a7935c294a7a73a1503422f36f1fdc8de6d1125b5cb526556b4bd7a3afdf1c4d2611d21aeb211b45bf6f8b52eb9e5e0c6255da7ef3f9069d39f';
const RS_PARITY_13: string =
  '34c3ff682e2b6784c2578988bfa2ba6148d7db7c6a3f43900054cdd3e0b7be3c7cdbd77066334f9ce83de10ee9bc59eb609fc35472775bb8181cd5fbf8ffa6146493cf587e7b57b4250739b8fbf2015158878b4c5a6f13a030049de3d0e7ee0c4c8b874056631facd831b11fd9e809e250af930442470be8282c85abc8cff64454a39f084e4b07e4a237e9e8dfc2da0128b7bb1c0a5f23f06034adb380d7de5c1cbbb71006532ffc885d816e89dc398b00ffa33412173bd8787cb59b989fc67404f3af381e1b37d4456759d89b92613138e7eb2c3a0f73815eb86ee19d7a3c';

// ── PoV vectors (fixtures/market/pov_token_basic.json) ──────────────────────────

const POV_SEED_HEX: string = '202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f';
const POV_PUBLIC_KEY_HEX: string = '29acbae141bccaf0b22e1a94d34d0bc7361e526d0bfe12c89794bc9322966dd7';

class PoVCase {
  subject: string;
  ticks: bigint;
  transport: PoVTransportType;
  transportName: string;
  canonicalHex: string;
  signatureHex: string;

  constructor(
    subject: string,
    ticks: bigint,
    transport: PoVTransportType,
    transportName: string,
    canonicalHex: string,
    signatureHex: string
  ) {
    this.subject = subject;
    this.ticks = ticks;
    this.transport = transport;
    this.transportName = transportName;
    this.canonicalHex = canonicalHex;
    this.signatureHex = signatureHex;
  }
}

const POV_CASES: PoVCase[] = [
  new PoVCase(
    'aether:subject:01',
    638000000000000000n,
    PoVTransportType.Ble,
    'ble',
    '110000006165746865723a7375626a6563743a30310000b3a69ea1da0800',
    'd4780e376ba00d59fdd07fced1aec4f55197ecc16354e5eac5998b37e018c6d71e080bdcd97cb45f63975a70b43dc8cc5f877c653993bb48a908f4aee557cc0f'
  ),
  new PoVCase(
    'aether:subject:02',
    638123456789012345n,
    PoVTransportType.Nfc,
    'nfc',
    '110000006165746865723a7375626a6563743a303279dfc02ce711db0801',
    'de9e7b617744861e77a9394ee76613ba39fc0978cb58df3bd1bf5da817ad22fd760ed2df7f88ac9dcbca8a2162eae5731d6803c6f5626bb6d9e994fd594f9704'
  ),
  new PoVCase(
    'aether:subject:03',
    637900000000000001n,
    PoVTransportType.NearLink,
    'nearlink',
    '110000006165746865723a7375626a6563743a303301c03896ab46da0802',
    'e38c050ec6d22683425b86a58332c5ff1367c18b257e735e2e9b991bc468180a57f48fcfa3b1b69aa564d00a129875bd90dc74426005e309bca016b15a55ce0e'
  ),
];

export default function parityTest(): void {
  describe('AetherNetArkTsParity', (): void => {
    // ── Tipping ──
    it('tip_canonical_bytes_and_deterministic_signature', 0, (): void => {
      const seed: Uint8Array = fromHex(TIP_SEED_HEX);
      expect(toHex(publicKeyFromSeed(seed))).assertEqual(TIP_PUBLIC_KEY_HEX);

      for (let i = 0; i < TIP_CASES.length; i++) {
        const c: TipCase = TIP_CASES[i];
        const p: TipPacketPayload = buildTip(c);
        const canonical: Uint8Array = p.buildCanonicalData();
        expect(toHex(canonical)).assertEqual(c.canonicalHex);

        const sig: Uint8Array = signWithSeed(seed, canonical);
        expect(toHex(sig)).assertEqual(c.signatureHex);
        expect(verifyEd25519(publicKeyFromSeed(seed), canonical, fromHex(c.signatureHex))).assertTrue();
      }
    });

    it('tip_null_reference_id_is_16_zero_bytes_and_guid_is_dotnet_mixed_endian', 0, (): void => {
      const withId: TipCase = TIP_CASES[0];
      const withoutId: TipCase = TIP_CASES[1];
      const cNull: Uint8Array = buildTip(withoutId).buildCanonicalData();
      const guidNull: Uint8Array = cNull.subarray(cNull.length - 24, cNull.length - 8);
      expect(toHex(guidNull)).assertEqual('00000000000000000000000000000000');
      const cId: Uint8Array = buildTip(withId).buildCanonicalData();
      const guidId: Uint8Array = cId.subarray(cId.length - 24, cId.length - 8);
      expect(toHex(guidId)).assertEqual('22221111333344445555666677778888');
    });

    // ── Vault ──
    it('rs_encoder_reproduces_every_shard_byte_for_byte', 0, (): void => {
      const input: Uint8Array = fromHex(RS_INPUT_HEX);
      expect(input.length).assertEqual(RS_INPUT_SIZE);

      const codec = new ReedSolomonCodec(RS_K, RS_M);
      const shards: Uint8Array[] = encodeData(codec, input);
      expect(shards.length).assertEqual(RS_N);
      expect(shards[0].length).assertEqual(RS_SHARD_SIZE);

      // Systematic data shard 0 is the first 223 input bytes verbatim.
      expect(toHex(shards[0])).assertEqual(RS_INPUT_HEX.substring(0, RS_SHARD_SIZE * 2));
      // Cauchy parity shards.
      expect(toHex(shards[10])).assertEqual(RS_PARITY_10);
      expect(toHex(shards[11])).assertEqual(RS_PARITY_11);
      expect(toHex(shards[12])).assertEqual(RS_PARITY_12);
      expect(toHex(shards[13])).assertEqual(RS_PARITY_13);
    });

    it('rs_recovers_from_K_of_N_and_fails_on_K_minus_1', 0, (): void => {
      const input: Uint8Array = fromHex(RS_INPUT_HEX);
      const codec = new ReedSolomonCodec(RS_K, RS_M);
      const shards: Uint8Array[] = encodeData(codec, input);

      // Drop the first 4 data shards; recover from data[4..9] + parity[10..13].
      const survivors: Map<number, Uint8Array> = new Map<number, Uint8Array>();
      for (let i = 4; i < RS_N; i++) {
        survivors.set(i, shards[i]);
      }
      const recovered: Uint8Array = reconstructData(codec, survivors, RS_INPUT_SIZE);
      expect(toHex(recovered)).assertEqual(RS_INPUT_HEX);

      // Systematic fast-path: all K data shards present.
      const allData: Map<number, Uint8Array> = new Map<number, Uint8Array>();
      for (let i = 0; i < RS_K; i++) {
        allData.set(i, shards[i]);
      }
      expect(toHex(reconstructData(codec, allData, RS_INPUT_SIZE))).assertEqual(RS_INPUT_HEX);

      // K-1 survivors → unrecoverable (must throw).
      const tooFew: Map<number, Uint8Array> = new Map<number, Uint8Array>();
      for (let i = 0; i < RS_K - 1; i++) {
        tooFew.set(i, shards[i]);
      }
      let threw: boolean = false;
      try {
        reconstructData(codec, tooFew, RS_INPUT_SIZE);
      } catch (_e) {
        threw = true;
      }
      expect(threw).assertTrue();
    });

    // ── PoV ──
    it('pov_canonical_body_and_deterministic_witness_signature', 0, (): void => {
      const seed: Uint8Array = fromHex(POV_SEED_HEX);
      expect(toHex(publicKeyFromSeed(seed))).assertEqual(POV_PUBLIC_KEY_HEX);

      for (let i = 0; i < POV_CASES.length; i++) {
        const c: PoVCase = POV_CASES[i];
        const body: Uint8Array = buildSignableTokenData(c.subject, c.ticks, c.transport);
        expect(toHex(body)).assertEqual(c.canonicalHex);
        expect(transportToString(c.transport)).assertEqual(c.transportName);

        const sig: Uint8Array = signWithSeed(seed, body);
        expect(toHex(sig)).assertEqual(c.signatureHex);
        expect(verifyEd25519(publicKeyFromSeed(seed), body, fromHex(c.signatureHex))).assertTrue();
      }
    });
  });
}
