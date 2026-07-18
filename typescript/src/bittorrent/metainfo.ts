// SPDX-License-Identifier: MIT
import { createHash } from 'node:crypto';
import { decode, decodeN, encode, type BValue } from './bencode.js';

export function buildSingleFileTorrent(name: string, data: Buffer, pieceLength: number, announce = ''): Buffer {
  if (!name) throw new Error('name is required');
  if (pieceLength <= 0) throw new Error('piece length must be positive');
  const pieceCount = Math.ceil(data.length / pieceLength);
  const pieces = Buffer.alloc(pieceCount * 20);
  for (let i = 0; i < pieceCount; i++) {
    const start = i * pieceLength;
    const end = Math.min(start + pieceLength, data.length);
    createHash('sha1').update(data.subarray(start, end)).digest().copy(pieces, i * 20);
  }
  const info: { [k: string]: BValue } = {
    length: data.length,
    name: Buffer.from(name, 'utf8'),
    'piece length': pieceLength,
    pieces,
  };
  const root: { [k: string]: BValue } = {};
  if (announce && announce.trim()) root.announce = Buffer.from(announce, 'utf8');
  root.info = info;
  return encode(root);
}

export class TorrentMetainfo {
  constructor(
    public infoHashV1: Buffer,
    public name: string,
    public pieceLength: number,
    public pieceHashes: Buffer[],
    public totalLength: number,
    public announceUrls: string[],
    public isSingleFile: boolean,
  ) {}

  get infoHashV1Hex(): string {
    return this.infoHashV1.toString('hex');
  }
}

export function parseTorrent(data: Buffer): TorrentMetainfo {
  const root = decode(data) as { [k: string]: BValue };
  const info = root['info'] as { [k: string]: BValue };
  if (info === undefined) throw new Error("metainfo has no 'info' dictionary");

  const infoHash = createHash('sha1').update(extractInfoSpan(data)).digest();
  const name = (info['name'] as Buffer).toString('utf8');
  const pieceLength = info['piece length'] as number;

  const piecesBuf = info['pieces'] as Buffer;
  if (piecesBuf.length % 20 !== 0) throw new Error("'pieces' length is not a multiple of 20");
  const pieceHashes: Buffer[] = [];
  for (let i = 0; i < piecesBuf.length; i += 20) pieceHashes.push(Buffer.from(piecesBuf.subarray(i, i + 20)));

  let total = 0;
  let isSingle = false;
  if ('files' in info) {
    for (const f of info['files'] as BValue[]) total += (f as { [k: string]: BValue })['length'] as number;
  } else {
    isSingle = true;
    total = info['length'] as number;
  }

  const announce: string[] = [];
  const seen = new Set<string>();
  const add = (u: string) => {
    if (u && !seen.has(u)) {
      seen.add(u);
      announce.push(u);
    }
  };
  if ('announce' in root) add((root['announce'] as Buffer).toString('utf8'));
  if ('announce-list' in root) {
    for (const tier of root['announce-list'] as BValue[]) {
      for (const t of tier as BValue[]) add((t as Buffer).toString('utf8'));
    }
  }
  return new TorrentMetainfo(infoHash, name, pieceLength, pieceHashes, total, announce, isSingle);
}

function extractInfoSpan(data: Buffer): Buffer {
  if (data.length === 0 || data[0] !== 0x64) throw new Error('metainfo is not a bencoded dictionary');
  let pos = 1;
  while (pos < data.length && data[pos] !== 0x65) {
    const [kv, kn] = decodeN(data, pos);
    pos = kn;
    const valStart = pos;
    const [, vn] = decodeN(data, pos);
    pos = vn;
    if ((kv as Buffer).toString('latin1') === 'info') return Buffer.from(data.subarray(valStart, pos));
  }
  throw new Error("metainfo has no 'info' key");
}

export interface MagnetLink {
  infoHash: Buffer;
  displayName: string;
  trackers: string[];
}

export function parseMagnet(uri: string): MagnetLink {
  if (!uri.startsWith('magnet:?')) throw new Error('not a magnet URI');
  const params = new URLSearchParams(uri.slice('magnet:?'.length));
  let infoHash: Buffer | null = null;
  for (const xt of params.getAll('xt')) {
    if (xt.startsWith('urn:btih:')) {
      infoHash = decodeInfoHash(xt.slice('urn:btih:'.length));
      break;
    }
  }
  if (!infoHash) throw new Error('magnet has no xt=urn:btih: topic');
  return { infoHash, displayName: params.get('dn') ?? '', trackers: params.getAll('tr') };
}

function decodeInfoHash(s: string): Buffer {
  if (s.length === 40) return Buffer.from(s, 'hex');
  if (s.length === 32) return base32Decode(s);
  throw new Error(`info-hash must be 40 hex or 32 base32 chars, got ${s.length}`);
}

function base32Decode(s: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = 0;
  let value = 0;
  const out: number[] = [];
  for (const ch of s.toUpperCase()) {
    const idx = alphabet.indexOf(ch);
    if (idx < 0) throw new Error('invalid base32 info-hash');
    value = (value << 5) | idx;
    bits += 5;
    if (bits >= 8) {
      bits -= 8;
      out.push((value >> bits) & 0xff);
    }
  }
  return Buffer.from(out);
}
