// SPDX-License-Identifier: MIT
// BEP-3 peer-wire: handshake, messages (exact big-endian framing), MSB-first bitfield.

export const PROTOCOL_STRING = Buffer.from('BitTorrent protocol', 'latin1');

export const CHOKE = 0;
export const UNCHOKE = 1;
export const INTERESTED = 2;
export const NOT_INTERESTED = 3;
export const HAVE = 4;
export const BITFIELD = 5;
export const REQUEST = 6;
export const PIECE = 7;
export const CANCEL = 8;
export const PORT = 9;
export const EXTENDED = 20;

export function defaultReserved(): Buffer {
  const r = Buffer.alloc(8);
  r[5] |= 0x10; // extension protocol
  r[7] |= 0x01; // DHT
  return r;
}

export class Handshake {
  constructor(
    public infoHash: Buffer,
    public peerId: Buffer,
    public reserved: Buffer = defaultReserved(),
  ) {}

  toBytes(): Buffer {
    return Buffer.concat([Buffer.from([19]), PROTOCOL_STRING, this.reserved, this.infoHash, this.peerId]);
  }

  static parse(data: Buffer): Handshake {
    if (data.length < 68) throw new Error(`handshake is ${data.length} bytes, need 68`);
    if (data[0] !== 19 || !data.subarray(1, 20).equals(PROTOCOL_STRING)) throw new Error('handshake prefix mismatch');
    return new Handshake(Buffer.from(data.subarray(28, 48)), Buffer.from(data.subarray(48, 68)), Buffer.from(data.subarray(20, 28)));
  }

  supportsExtended(): boolean {
    return (this.reserved[5] & 0x10) !== 0;
  }

  supportsDht(): boolean {
    return (this.reserved[7] & 0x01) !== 0;
  }
}

export class PeerMessage {
  constructor(
    public id: number | null,
    public payload: Buffer = Buffer.alloc(0),
  ) {}

  toBytes(): Buffer {
    if (this.id === null) return Buffer.from([0, 0, 0, 0]);
    const buf = Buffer.alloc(4 + 1 + this.payload.length);
    buf.writeUInt32BE(1 + this.payload.length, 0);
    buf[4] = this.id;
    this.payload.copy(buf, 5);
    return buf;
  }
}

export const keepAlive = (): PeerMessage => new PeerMessage(null);
export const choke = (): PeerMessage => new PeerMessage(CHOKE);
export const unchoke = (): PeerMessage => new PeerMessage(UNCHOKE);
export const interested = (): PeerMessage => new PeerMessage(INTERESTED);
export const notInterested = (): PeerMessage => new PeerMessage(NOT_INTERESTED);

export function have(pieceIndex: number): PeerMessage {
  const p = Buffer.alloc(4);
  p.writeUInt32BE(pieceIndex, 0);
  return new PeerMessage(HAVE, p);
}

export const bitfieldMessage = (bits: Buffer): PeerMessage => new PeerMessage(BITFIELD, bits);

function blockRef(id: number, index: number, begin: number, length: number): PeerMessage {
  const p = Buffer.alloc(12);
  p.writeUInt32BE(index, 0);
  p.writeUInt32BE(begin, 4);
  p.writeUInt32BE(length, 8);
  return new PeerMessage(id, p);
}

export const request = (index: number, begin: number, length: number): PeerMessage => blockRef(REQUEST, index, begin, length);
export const cancel = (index: number, begin: number, length: number): PeerMessage => blockRef(CANCEL, index, begin, length);

export function piece(index: number, begin: number, block: Buffer): PeerMessage {
  const p = Buffer.alloc(8 + block.length);
  p.writeUInt32BE(index, 0);
  p.writeUInt32BE(begin, 4);
  block.copy(p, 8);
  return new PeerMessage(PIECE, p);
}

export function port(value: number): PeerMessage {
  const p = Buffer.alloc(2);
  p.writeUInt16BE(value, 0);
  return new PeerMessage(PORT, p);
}

export function extended(subId: number, body: Buffer): PeerMessage {
  return new PeerMessage(EXTENDED, Buffer.concat([Buffer.from([subId]), body]));
}

export function parseFrame(data: Buffer): [PeerMessage, number] {
  if (data.length < 4) throw new Error('frame shorter than 4-byte length prefix');
  const length = data.readUInt32BE(0);
  if (4 + length > data.length) throw new Error('frame length exceeds available data');
  if (length === 0) return [new PeerMessage(null), 4];
  const body = data.subarray(4, 4 + length);
  return [new PeerMessage(body[0], Buffer.from(body.subarray(1))), 4 + length];
}

export class Bitfield {
  count: number;
  bits: Buffer;

  constructor(pieceCount: number, bits?: Buffer) {
    this.count = pieceCount;
    const need = Math.ceil(pieceCount / 8);
    this.bits = Buffer.alloc(need);
    if (bits) bits.subarray(0, need).copy(this.bits);
  }

  static fromBytes(data: Buffer, pieceCount: number): Bitfield {
    return new Bitfield(pieceCount, data);
  }

  get(i: number): boolean {
    if (i < 0 || i >= this.count) return false;
    return (this.bits[i >> 3] & (0x80 >> (i & 7))) !== 0;
  }

  set(i: number): void {
    if (i >= 0 && i < this.count) this.bits[i >> 3] |= 0x80 >> (i & 7);
  }

  popCount(): number {
    let n = 0;
    for (let i = 0; i < this.count; i++) if (this.get(i)) n++;
    return n;
  }

  hasAll(): boolean {
    return this.popCount() === this.count;
  }

  toBytes(): Buffer {
    return Buffer.from(this.bits);
  }
}
