// SPDX-License-Identifier: MIT
// Rarest-first picker + SHA-1-verified piece store.
import { createHash } from 'node:crypto';
import { Bitfield } from './wire.js';

export class RarestFirstPicker {
  private have: boolean[];
  private inflight: boolean[];
  private avail: number[];
  private peerHas = new Map<string, boolean[]>();

  constructor(private count: number) {
    this.have = new Array(count).fill(false);
    this.inflight = new Array(count).fill(false);
    this.avail = new Array(count).fill(0);
  }

  setHave(i: number): void {
    if (i >= 0 && i < this.count) {
      this.have[i] = true;
      this.inflight[i] = false;
    }
  }

  addPeer(peer: string): void {
    if (!this.peerHas.has(peer)) this.peerHas.set(peer, new Array(this.count).fill(false));
  }

  peerHasPiece(peer: string, i: number): void {
    this.addPeer(peer);
    const has = this.peerHas.get(peer) as boolean[];
    if (i >= 0 && i < this.count && !has[i]) {
      has[i] = true;
      this.avail[i]++;
    }
  }

  pickFor(peer: string): number {
    const has = this.peerHas.get(peer);
    if (!has) return -1;
    let best = -1;
    let bestAvail = 0;
    for (let i = 0; i < this.count; i++) {
      if (this.have[i] || this.inflight[i] || !has[i]) continue;
      if (best === -1 || this.avail[i] < bestAvail) {
        best = i;
        bestAvail = this.avail[i];
      }
    }
    if (best !== -1) this.inflight[best] = true;
    return best;
  }

  release(i: number): void {
    if (i >= 0 && i < this.count) this.inflight[i] = false;
  }

  isComplete(): boolean {
    return this.count > 0 && this.have.every((h) => h);
  }
}

export class PieceStore {
  private pieces = new Map<number, Buffer>();

  constructor(
    private pieceLength: number,
    private totalLength: number,
    public pieceHashes: Buffer[],
  ) {}

  pieceCount(): number {
    return this.pieceHashes.length;
  }

  lengthOfPiece(i: number): number {
    if (i < 0 || i >= this.pieceHashes.length) return 0;
    if (i === this.pieceHashes.length - 1) return this.totalLength - i * this.pieceLength;
    return this.pieceLength;
  }

  has(i: number): boolean {
    return this.pieces.has(i);
  }

  tryComplete(i: number, data: Buffer): boolean {
    if (i < 0 || i >= this.pieceHashes.length) return false;
    if (data.length !== this.lengthOfPiece(i)) return false;
    if (!createHash('sha1').update(data).digest().equals(this.pieceHashes[i])) return false;
    this.pieces.set(i, Buffer.from(data));
    return true;
  }

  readBlock(i: number, begin: number, length: number): Buffer | null {
    const p = this.pieces.get(i);
    if (!p || begin < 0 || begin + length > p.length) return null;
    return Buffer.from(p.subarray(begin, begin + length));
  }

  buildBitfield(): Bitfield {
    const bf = new Bitfield(this.pieceHashes.length);
    for (let i = 0; i < this.pieceHashes.length; i++) if (this.has(i)) bf.set(i);
    return bf;
  }

  isComplete(): boolean {
    return this.pieces.size === this.pieceHashes.length;
  }

  assemble(): Buffer | null {
    if (!this.isComplete()) return null;
    const parts: Buffer[] = [];
    for (let i = 0; i < this.pieceHashes.length; i++) parts.push(this.pieces.get(i) as Buffer);
    return Buffer.concat(parts);
  }
}

export function pieceStoreFromContent(data: Buffer, pieceLength: number): PieceStore {
  const pieceCount = Math.ceil(data.length / pieceLength);
  const hashes: Buffer[] = [];
  const store = new PieceStore(pieceLength, data.length, []);
  for (let i = 0; i < pieceCount; i++) {
    const start = i * pieceLength;
    const end = Math.min(start + pieceLength, data.length);
    hashes.push(createHash('sha1').update(data.subarray(start, end)).digest());
    (store as unknown as { pieces: Map<number, Buffer> }).pieces.set(i, Buffer.from(data.subarray(start, end)));
  }
  store.pieceHashes = hashes;
  return store;
}
