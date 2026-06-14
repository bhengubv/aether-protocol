/**
 * Incentive layer — generic mesh tipping (PacketType.TipPacket = 24).
 * SPDX-License-Identifier: MIT
 */

export {
  TipPacketPayload,
  guidBytesDotNet,
} from "./TipPacketPayload.js";
export type { TipPacketPayloadInit } from "./TipPacketPayload.js";

export {
  MeshTipService,
  NoopMeshTipSettlementProvider,
} from "./MeshTipService.js";
export type {
  TipMeshSender,
  TipPacketSigner,
  IdentitySigner,
  RouteResolver,
  MeshTipSettlementProvider,
  TipLogger,
} from "./MeshTipService.js";
