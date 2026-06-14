/**
 * Market layer — Proof-of-Vicinity tokens and on-mesh exchange
 * (PacketType.PoVTokenExchange = 43).
 * SPDX-License-Identifier: MIT
 */

export {
  PoVToken,
  PoVTransportType,
  buildSignableTokenData,
  parseTicksLiteral,
  isShortRange,
  transportToString,
  ticksToUnixMs,
  unixMsToTicks,
  TICKS_PER_SECOND,
  UNIX_EPOCH_TICKS,
} from "./PoVToken.js";
export type { PoVScore } from "./PoVToken.js";

export { PoVTokenExchangeService } from "./PoVTokenExchangeService.js";
export type {
  PoVMeshSender,
  PoVPacketSigner,
  PoVIdentitySigner,
  PoVLogger,
  NowMsProvider,
} from "./PoVTokenExchangeService.js";
