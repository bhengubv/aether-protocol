// SPDX-License-Identifier: MIT
// KRPC (BEP-5) DHT messages over bencode.
import { decode, encode, type BValue } from './bencode.js';

export function encodeQuery(tx: Buffer, method: string, args: { [k: string]: BValue }): Buffer {
  return encode({ t: tx, y: Buffer.from('q', 'latin1'), q: Buffer.from(method, 'utf8'), a: args });
}

export function encodeResponse(tx: Buffer, response: { [k: string]: BValue }): Buffer {
  return encode({ t: tx, y: Buffer.from('r', 'latin1'), r: response });
}

export function encodeError(tx: Buffer, code: number, message: string): Buffer {
  return encode({ t: tx, y: Buffer.from('e', 'latin1'), e: [code, Buffer.from(message, 'utf8')] });
}

export interface KrpcDecoded {
  transactionId: Buffer;
  type: string;
  method?: string;
  arguments?: { [k: string]: BValue };
  response?: { [k: string]: BValue };
  errorCode?: number;
  errorMessage?: string;
}

export function decodeKrpc(data: Buffer): KrpcDecoded {
  const d = decode(data) as { [k: string]: BValue };
  const y = (d['y'] as Buffer).toString('latin1');
  const out: KrpcDecoded = { transactionId: d['t'] as Buffer, type: y };
  if (y === 'q') {
    out.method = (d['q'] as Buffer).toString('utf8');
    out.arguments = d['a'] as { [k: string]: BValue };
  } else if (y === 'r') {
    out.response = d['r'] as { [k: string]: BValue };
  } else if (y === 'e') {
    const e = d['e'] as BValue[];
    if (e.length >= 2) {
      out.errorCode = e[0] as number;
      out.errorMessage = (e[1] as Buffer).toString('utf8');
    }
  }
  return out;
}
