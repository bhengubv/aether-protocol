// SPDX-License-Identifier: MIT
// DHT (BEP-5): XOR distance + compact node (26B) / peer (6B) info.

export interface DhtContact {
  id: Buffer;
  ip: string;
  port: number;
}

export interface PeerAddr {
  ip: string;
  port: number;
}

export function xorDistance(a: Buffer, b: Buffer): Buffer {
  const out = Buffer.alloc(a.length);
  for (let i = 0; i < a.length; i++) out[i] = a[i] ^ b[i];
  return out;
}

function ipBytes(ip: string): Buffer {
  return Buffer.from(ip.split('.').map((x) => parseInt(x, 10)));
}

function ipStr(b: Buffer): string {
  return `${b[0]}.${b[1]}.${b[2]}.${b[3]}`;
}

export function encodeCompactNodes(nodes: DhtContact[]): Buffer {
  const parts: Buffer[] = [];
  for (const c of nodes) {
    const p = Buffer.alloc(2);
    p.writeUInt16BE(c.port, 0);
    parts.push(c.id, ipBytes(c.ip), p);
  }
  return Buffer.concat(parts);
}

export function decodeCompactNodes(data: Buffer): DhtContact[] {
  if (data.length % 26 !== 0) throw new Error('compact nodes length is not a multiple of 26');
  const out: DhtContact[] = [];
  for (let i = 0; i < data.length; i += 26) {
    out.push({
      id: Buffer.from(data.subarray(i, i + 20)),
      ip: ipStr(data.subarray(i + 20, i + 24)),
      port: data.readUInt16BE(i + 24),
    });
  }
  return out;
}

export function encodeCompactPeers(peers: PeerAddr[]): Buffer {
  const parts: Buffer[] = [];
  for (const p of peers) {
    const port = Buffer.alloc(2);
    port.writeUInt16BE(p.port, 0);
    parts.push(ipBytes(p.ip), port);
  }
  return Buffer.concat(parts);
}

export function decodeCompactPeers(data: Buffer): PeerAddr[] {
  if (data.length % 6 !== 0) throw new Error('compact peers length is not a multiple of 6');
  const out: PeerAddr[] = [];
  for (let i = 0; i < data.length; i += 6) {
    out.push({ ip: ipStr(data.subarray(i, i + 4)), port: data.readUInt16BE(i + 4) });
  }
  return out;
}
