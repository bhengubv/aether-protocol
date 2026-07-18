// SPDX-License-Identifier: MIT
// BEP-10 extension protocol + BEP-9 ut_metadata + BEP-11 ut_pex.
import { createHash } from 'node:crypto';
import { decode, decodeN, encode, type BValue } from './bencode.js';
import { decodeCompactPeers, encodeCompactPeers, type PeerAddr } from './dht.js';

export const EXTENDED_MESSAGE_ID = 20;
export const EXTENSION_HANDSHAKE_ID = 0;
export const METADATA_REQUEST = 0;
export const METADATA_DATA = 1;
export const METADATA_REJECT = 2;
export const METADATA_PIECE_SIZE = 16384;

export function wrapExtended(subId: number, body: Buffer): Buffer {
  return Buffer.concat([Buffer.from([subId]), body]);
}

export function splitExtended(payload: Buffer): [number, Buffer] {
  if (payload.length < 1) throw new Error('empty extended payload');
  return [payload[0], Buffer.from(payload.subarray(1))];
}

export function buildExtensionHandshake(supported: { [k: string]: number }, metadataSize = 0): Buffer {
  const m: { [k: string]: BValue } = {};
  for (const [k, v] of Object.entries(supported)) m[k] = v;
  const d: { [k: string]: BValue } = { m };
  if (metadataSize > 0) d['metadata_size'] = metadataSize;
  return wrapExtended(EXTENSION_HANDSHAKE_ID, encode(d));
}

export function parseExtensionHandshake(body: Buffer): { supported: { [k: string]: number }; metadataSize: number } {
  const d = decode(body) as { [k: string]: BValue };
  const supported: { [k: string]: number } = {};
  const m = (d['m'] ?? {}) as { [k: string]: BValue };
  for (const [k, v] of Object.entries(m)) supported[k] = v as number;
  return { supported, metadataSize: (d['metadata_size'] as number) ?? 0 };
}

export function buildMetadataRequest(piece: number): Buffer {
  return encode({ msg_type: METADATA_REQUEST, piece });
}

export function buildMetadataData(piece: number, totalSize: number, data: Buffer): Buffer {
  return Buffer.concat([encode({ msg_type: METADATA_DATA, piece, total_size: totalSize }), data]);
}

export function buildMetadataReject(piece: number): Buffer {
  return encode({ msg_type: METADATA_REJECT, piece });
}

export function parseMetadata(body: Buffer): { type: number; piece: number; totalSize: number; data: Buffer } {
  const [v, n] = decodeN(body, 0);
  const d = v as { [k: string]: BValue };
  return {
    type: d['msg_type'] as number,
    piece: d['piece'] as number,
    totalSize: (d['total_size'] as number) ?? 0,
    data: Buffer.from(body.subarray(n)),
  };
}

export class MetadataAssembler {
  private pieces = new Map<number, Buffer>();
  constructor(public totalSize: number) {}

  pieceCount(): number {
    return Math.ceil(this.totalSize / METADATA_PIECE_SIZE);
  }

  add(piece: number, data: Buffer): void {
    this.pieces.set(piece, Buffer.from(data));
  }

  isComplete(): boolean {
    return this.pieces.size === this.pieceCount();
  }

  tryFinish(infoHash: Buffer): Buffer | null {
    if (!this.isComplete()) return null;
    const parts: Buffer[] = [];
    for (let i = 0; i < this.pieceCount(); i++) parts.push(this.pieces.get(i) as Buffer);
    const out = Buffer.concat(parts);
    if (out.length !== this.totalSize) return null;
    if (!createHash('sha1').update(out).digest().equals(infoHash)) return null;
    return out;
  }
}

export function buildPexAdded(added: PeerAddr[]): Buffer {
  return encode({ added: encodeCompactPeers(added) });
}

export function parsePexAdded(body: Buffer): PeerAddr[] {
  const d = decode(body) as { [k: string]: BValue };
  if ('added' in d) return decodeCompactPeers(d['added'] as Buffer);
  return [];
}
