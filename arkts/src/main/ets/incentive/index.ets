/*
 * Incentive layer — generic mesh tipping (PacketType.TipPacket = 24).
 * SPDX-License-Identifier: MIT
 */

export { TipPacketPayload, TipPacketPayloadInit, guidBytesDotNet } from './TipPacketPayload';

export { MeshTipService, NoopMeshTipSettlementProvider } from './MeshTipService';
export type {
  TipMeshSender,
  TipPacketSigner,
  TipIdentitySigner,
  TipRouteResolver,
  MeshTipSettlementProvider,
  TipLogger,
} from './MeshTipService';
