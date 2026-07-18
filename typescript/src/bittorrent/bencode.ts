// SPDX-License-Identifier: MIT
// Strict BEP-3 bencoding — byte-identical to the C#/Go/Python AetherNet references.

export type BValue = number | Buffer | BValue[] | { [key: string]: BValue };

export function decode(data: Buffer): BValue {
  const [v, n] = decodeN(data, 0);
  if (n !== data.length) throw new Error(`bencode: ${data.length - n} trailing byte(s)`);
  return v;
}

export function decodeN(data: Buffer, pos: number): [BValue, number] {
  if (pos >= data.length) throw new Error('bencode: empty input');
  const c = data[pos];
  if (c === 0x69) return decodeInt(data, pos); // 'i'
  if (c === 0x6c) return decodeList(data, pos); // 'l'
  if (c === 0x64) return decodeDict(data, pos); // 'd'
  if (c >= 0x30 && c <= 0x39) return decodeStr(data, pos);
  throw new Error(`bencode: unexpected byte 0x${c.toString(16)}`);
}

function decodeInt(data: Buffer, pos: number): [number, number] {
  const end = data.indexOf(0x65, pos); // 'e'
  if (end < 0) throw new Error("bencode: integer has no terminating 'e'");
  const body = data.subarray(pos + 1, end).toString('latin1');
  if (body === '') throw new Error('bencode: empty integer');
  if (body === '-0') throw new Error('bencode: negative zero is not allowed');
  const digits = body[0] === '-' ? body.slice(1) : body;
  if (digits === '') throw new Error('bencode: bare minus sign');
  if (digits.length > 1 && digits[0] === '0') throw new Error('bencode: leading zero');
  if (!/^[0-9]+$/.test(digits)) throw new Error('bencode: non-digit in integer');
  return [Number(body), end + 1];
}

function decodeStr(data: Buffer, pos: number): [Buffer, number] {
  const colon = data.indexOf(0x3a, pos); // ':'
  if (colon < 0) throw new Error("bencode: byte string has no ':'");
  const lenStr = data.subarray(pos, colon).toString('latin1');
  if (lenStr === '') throw new Error('bencode: empty length');
  if (lenStr.length > 1 && lenStr[0] === '0') throw new Error('bencode: leading zero in length');
  if (!/^[0-9]+$/.test(lenStr)) throw new Error('bencode: non-digit in length');
  const n = Number(lenStr);
  const start = colon + 1;
  if (start + n > data.length) throw new Error('bencode: byte string runs past end');
  return [Buffer.from(data.subarray(start, start + n)), start + n];
}

function decodeList(data: Buffer, pos: number): [BValue[], number] {
  pos += 1;
  const out: BValue[] = [];
  for (;;) {
    if (pos >= data.length) throw new Error("bencode: list has no terminating 'e'");
    if (data[pos] === 0x65) return [out, pos + 1];
    const [v, n] = decodeN(data, pos);
    out.push(v);
    pos = n;
  }
}

function decodeDict(data: Buffer, pos: number): [{ [key: string]: BValue }, number] {
  pos += 1;
  const out: { [key: string]: BValue } = {};
  let prev: Buffer | null = null;
  for (;;) {
    if (pos >= data.length) throw new Error("bencode: dictionary has no terminating 'e'");
    if (data[pos] === 0x65) return [out, pos + 1];
    const [keyBuf, kn] = decodeStr(data, pos);
    pos = kn;
    if (prev !== null) {
      const cmp = Buffer.compare(prev, keyBuf);
      if (cmp === 0) throw new Error('bencode: duplicate dictionary key');
      if (cmp > 0) throw new Error('bencode: dictionary keys are not sorted');
    }
    prev = keyBuf;
    if (pos >= data.length) throw new Error('bencode: dictionary key without a value');
    const [v, vn] = decodeN(data, pos);
    pos = vn;
    out[keyBuf.toString('latin1')] = v;
  }
}

export function encode(value: BValue | string): Buffer {
  const parts: Buffer[] = [];
  encodeInto(value, parts);
  return Buffer.concat(parts);
}

function encodeInto(value: BValue | string, parts: Buffer[]): void {
  if (typeof value === 'number') {
    if (!Number.isInteger(value)) throw new Error('bencode: non-integer number');
    parts.push(Buffer.from(`i${value}e`, 'latin1'));
  } else if (Buffer.isBuffer(value)) {
    parts.push(Buffer.from(`${value.length}:`, 'latin1'), value);
  } else if (typeof value === 'string') {
    const b = Buffer.from(value, 'utf8');
    parts.push(Buffer.from(`${b.length}:`, 'latin1'), b);
  } else if (Array.isArray(value)) {
    parts.push(Buffer.from('l', 'latin1'));
    for (const item of value) encodeInto(item, parts);
    parts.push(Buffer.from('e', 'latin1'));
  } else {
    parts.push(Buffer.from('d', 'latin1'));
    const keys = Object.keys(value).sort((a, b) => Buffer.compare(Buffer.from(a, 'latin1'), Buffer.from(b, 'latin1')));
    for (const k of keys) {
      const kb = Buffer.from(k, 'latin1');
      parts.push(Buffer.from(`${kb.length}:`, 'latin1'), kb);
      encodeInto((value as { [key: string]: BValue })[k], parts);
    }
    parts.push(Buffer.from('e', 'latin1'));
  }
}
