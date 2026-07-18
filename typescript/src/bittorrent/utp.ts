// SPDX-License-Identifier: MIT
// µTP packet (BEP-29, version 1) — byte-exact 20-byte header.

export const UTP_DATA = 0;
export const UTP_FIN = 1;
export const UTP_STATE = 2;
export const UTP_RESET = 3;
export const UTP_SYN = 4;

export const UTP_VERSION = 1;
export const UTP_HEADER_SIZE = 20;

export class UtpPacket {
  constructor(
    public type: number,
    public connId = 0,
    public timestamp = 0,
    public timestampDiff = 0,
    public window = 0,
    public seq = 0,
    public ack = 0,
    public payload: Buffer = Buffer.alloc(0),
  ) {}

  toBytes(): Buffer {
    const h = Buffer.alloc(UTP_HEADER_SIZE);
    h[0] = (this.type << 4) | UTP_VERSION;
    h[1] = 0; // no extensions
    h.writeUInt16BE(this.connId, 2);
    h.writeUInt32BE(this.timestamp, 4);
    h.writeUInt32BE(this.timestampDiff, 8);
    h.writeUInt32BE(this.window, 12);
    h.writeUInt16BE(this.seq, 16);
    h.writeUInt16BE(this.ack, 18);
    return Buffer.concat([h, this.payload]);
  }

  static parse(data: Buffer): UtpPacket {
    if (data.length < UTP_HEADER_SIZE) throw new Error(`µTP packet is ${data.length} bytes, shorter than ${UTP_HEADER_SIZE}`);
    const version = data[0] & 0x0f;
    if (version !== UTP_VERSION) throw new Error(`unsupported µTP version ${version}`);
    const type = data[0] >> 4;
    let offset = UTP_HEADER_SIZE;
    let nextExt = data[1];
    while (nextExt !== 0) {
      if (offset + 2 > data.length) throw new Error('truncated µTP extension header');
      const thisNext = data[offset];
      const extLen = data[offset + 1];
      offset += 2 + extLen;
      if (offset > data.length) throw new Error('truncated µTP extension data');
      nextExt = thisNext;
    }
    return new UtpPacket(
      type,
      data.readUInt16BE(2),
      data.readUInt32BE(4),
      data.readUInt32BE(8),
      data.readUInt32BE(12),
      data.readUInt16BE(16),
      data.readUInt16BE(18),
      Buffer.from(data.subarray(offset)),
    );
  }
}
