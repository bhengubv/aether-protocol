/*
 * Aether Protocol — ArkTS (HarmonyOS) library entry point.
 *
 * The 9th language implementation of the three AetherNet money/vault features,
 * byte-identical to the C# reference and proven against the shared fixtures:
 *   - Incentive: generic mesh tipping (PacketType.TipPacket = 24).
 *   - Vault:     systematic Cauchy-Reed-Solomon (K=10, M=4) over GF(2⁸) (0x11D, α=2).
 *   - Market:    Proof-of-Vicinity token exchange (PacketType.PoVTokenExchange = 43).
 *
 * ArkTS is a strict TypeScript superset: no `any`, explicit types everywhere,
 * classes over object-literal types, `Uint8Array`/`ArrayBuffer` for bytes, and
 * `bigint` for i64 fields. Real deterministic Ed25519 is provided by @noble/ed25519
 * (pure JS, byte-stable under both ArkTS and Node) — NO stubs.
 *
 * SPDX-License-Identifier: MIT
 */

// Protocol layer
export { PacketType, packetTypeToString } from './src/main/ets/protocol/PacketType';
export { MeshPacket } from './src/main/ets/protocol/MeshPacket';
export {
  utf8Encode,
  utf8Decode,
  toHex,
  fromHex,
  toBase64,
  fromBase64,
  bytesEqual,
} from './src/main/ets/protocol/Bytes';
export {
  NobleEd25519Signer,
  publicKeyFromSeed,
  signWithSeed,
  verifyEd25519,
  ED25519_SEED_LENGTH,
  ED25519_PUBLIC_KEY_LENGTH,
  ED25519_SIGNATURE_LENGTH,
} from './src/main/ets/protocol/Ed25519Provider';
export type { Ed25519Signer } from './src/main/ets/protocol/Ed25519Provider';

// Incentive layer — mesh tipping (TipPacket = 24)
export {
  TipPacketPayload,
  TipPacketPayloadInit,
  guidBytesDotNet,
} from './src/main/ets/incentive/TipPacketPayload';
export { MeshTipService, NoopMeshTipSettlementProvider } from './src/main/ets/incentive/MeshTipService';
export type {
  TipMeshSender,
  TipPacketSigner,
  TipIdentitySigner,
  TipRouteResolver,
  MeshTipSettlementProvider,
  TipLogger,
} from './src/main/ets/incentive/MeshTipService';

// Vault layer — Reed-Solomon erasure coding (K=10, M=4)
export { ReedSolomonCodec } from './src/main/ets/vault/ReedSolomonCodec';
export { splitIntoDataShards, encodeData, reconstructData } from './src/main/ets/vault/VaultCodec';

// Market layer — Proof-of-Vicinity (PoVTokenExchange = 43)
export {
  PoVToken,
  PoVTokenInit,
  PoVScore,
  PoVTransportType,
  buildSignableTokenData,
  parseTicksLiteral,
  isShortRange,
  transportToString,
  ticksToUnixMs,
  unixMsToTicks,
  TICKS_PER_SECOND,
  UNIX_EPOCH_TICKS,
} from './src/main/ets/market/PoVToken';
export {
  PoVTokenExchangeService,
  SystemNowMsProvider,
} from './src/main/ets/market/PoVTokenExchangeService';
export type {
  PoVMeshSender,
  PoVPacketSigner,
  PoVIdentitySigner,
  PoVLogger,
  NowMsProvider,
  PoVTokenReceivedListener,
} from './src/main/ets/market/PoVTokenExchangeService';
