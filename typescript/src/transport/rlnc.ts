/**
 * RLNC Engine — Random Linear Network Coding over GF(2⁸).
 * SPDX-License-Identifier: MIT
 *
 * Primitive polynomial: x⁸ + x⁴ + x³ + x² + 1 (0x11D — same as AES Rijndael).
 *
 * Components
 * ──────────
 *   gf256Exp / gf256Log — precomputed field tables.
 *   gf256Mul / gf256Inv — O(1) arithmetic.
 *   RlncEncoder          — systematic + repair packet generation.
 *   RlncDecoder          — incremental Gauss-Jordan elimination.
 *   RlncCodec            — IFecCodec adapter (bulk encode/decode).
 *
 * Wire format per packet:
 *   [ K coefficient bytes ][ symbolSize data bytes ]
 */

import type { IFecCodec } from './ITransportService.js';

// ── GF(2⁸) arithmetic ─────────────────────────────────────────────────────────

const GF256_EXP = new Uint8Array(512);
const GF256_LOG = new Uint8Array(256);

(function buildTables() {
  let x = 1;
  for (let i = 0; i < 255; i++) {
    GF256_EXP[i] = x;
    GF256_LOG[x] = i;
    x <<= 1;
    if (x & 0x100) x ^= 0x11d; // reduce mod p(x)
    x &= 0xff;
  }
  for (let i = 255; i < 512; i++) GF256_EXP[i] = GF256_EXP[i - 255];
  GF256_LOG[1] = 0;
})();

function gf256Mul(a: number, b: number): number {
  if (a === 0 || b === 0) return 0;
  return GF256_EXP[GF256_LOG[a] + GF256_LOG[b]];
}

function gf256Inv(a: number): number {
  if (a === 0) throw new RangeError('rlnc: GF256 inverse of zero');
  return GF256_EXP[255 - GF256_LOG[a]];
}

function gf256Add(a: number, b: number): number { return (a ^ b) & 0xff; }

// ── RlncEncoder ───────────────────────────────────────────────────────────────

/** Encodes K source symbols as systematic + random-repair RLNC packets. */
export class RlncEncoder {
  private readonly source: Uint8Array[];
  private nextIndex = 0;

  constructor(source: Uint8Array[], readonly isSystematic = true) {
    if (source.length === 0) throw new RangeError('rlnc: source must have at least one symbol');
    this.source = source;
  }

  get generationSize(): number { return this.source.length; }
  get symbolSize():     number { return this.source[0].length; }

  /**
   * Returns `{ coefficients, encodedSymbol }` for the next packet.
   * First K packets are systematic (identity coefficients) when `isSystematic = true`.
   */
  nextPacket(): { coefficients: Uint8Array; encodedSymbol: Uint8Array } {
    const k = this.generationSize;
    const s = this.symbolSize;
    let coefficients: Uint8Array;
    let encodedSymbol: Uint8Array;

    if (this.isSystematic && this.nextIndex < k) {
      // Systematic: e_i coefficient vector.
      coefficients = new Uint8Array(k);
      coefficients[this.nextIndex] = 1;
      encodedSymbol = this.source[this.nextIndex].slice();
    } else {
      // Repair: random GF(256) coefficients.
      coefficients = new Uint8Array(k);
      crypto.getRandomValues(coefficients);
      if (coefficients.every(c => c === 0)) coefficients[0] = 1;
      encodedSymbol = this._encodeSymbol(coefficients);
    }

    this.nextIndex++;
    return { coefficients, encodedSymbol };
  }

  private _encodeSymbol(coefficients: Uint8Array): Uint8Array {
    const s = this.symbolSize;
    const out = new Uint8Array(s);
    for (let k = 0; k < this.source.length; k++) {
      const c = coefficients[k];
      if (c === 0) continue;
      const sym = this.source[k];
      for (let i = 0; i < s; i++) {
        out[i] = gf256Add(out[i], gf256Mul(c, sym[i]));
      }
    }
    return out;
  }
}

// ── RlncDecoder ───────────────────────────────────────────────────────────────

/**
 * Accumulates encoded packets and decodes via incremental Gauss-Jordan elimination
 * over GF(2⁸).
 */
export class RlncDecoder {
  private readonly pivotCoeff: (Uint8Array | null)[];
  private readonly pivotData:  (Uint8Array | null)[];
  private _rank = 0;

  constructor(
    readonly generationSize: number,
    readonly symbolSize: number,
  ) {
    this.pivotCoeff = new Array<Uint8Array | null>(generationSize).fill(null);
    this.pivotData  = new Array<Uint8Array | null>(generationSize).fill(null);
  }

  get rank():       number  { return this._rank; }
  get isComplete(): boolean { return this._rank === this.generationSize; }

  /** Submit a packet. Returns `true` if rank increased. */
  addPacket(coefficients: Uint8Array, encodedSymbol: Uint8Array): boolean {
    const k = this.generationSize;
    const s = this.symbolSize;
    const row  = coefficients.slice();
    const data = encodedSymbol.slice();

    // ── Forward-elimination ──────────────────────────────────────────────────
    for (let j = 0; j < k; j++) {
      if (row[j] === 0 || this.pivotCoeff[j] === null) continue;
      const c  = row[j];
      const pr = this.pivotCoeff[j]!;
      const pd = this.pivotData[j]!;
      for (let i = 0; i < k; i++) row[i]  = gf256Add(row[i],  gf256Mul(c, pr[i]));
      for (let i = 0; i < s; i++) data[i] = gf256Add(data[i], gf256Mul(c, pd[i]));
    }

    // ── Find pivot column ────────────────────────────────────────────────────
    let pivotCol = -1;
    for (let j = 0; j < k; j++) {
      if (row[j] !== 0) { pivotCol = j; break; }
    }
    if (pivotCol < 0) return false; // linearly dependent

    // ── Normalise ────────────────────────────────────────────────────────────
    const inv = gf256Inv(row[pivotCol]);
    for (let i = 0; i < k; i++) row[i]  = gf256Mul(inv, row[i]);
    for (let i = 0; i < s; i++) data[i] = gf256Mul(inv, data[i]);

    // ── Back-substitution ────────────────────────────────────────────────────
    for (let r = 0; r < k; r++) {
      const pr = this.pivotCoeff[r];
      if (pr === null) continue;
      const c = pr[pivotCol];
      if (c === 0) continue;
      const pd = this.pivotData[r]!;
      for (let i = 0; i < k; i++) pr[i]  = gf256Add(pr[i],  gf256Mul(c, row[i]));
      for (let i = 0; i < s; i++) pd[i] = gf256Add(pd[i], gf256Mul(c, data[i]));
    }

    this.pivotCoeff[pivotCol] = row;
    this.pivotData[pivotCol]  = data;
    this._rank++;
    return true;
  }

  /** Returns decoded source bytes when `isComplete`, or `null` otherwise. */
  tryDecode(): Uint8Array | null {
    if (!this.isComplete) return null;
    const k = this.generationSize;
    const result = new Uint8Array(k * this.symbolSize);
    for (let j = 0; j < k; j++) {
      result.set(this.pivotData[j]!, j * this.symbolSize);
    }
    return result;
  }
}

// ── RlncCodec : IFecCodec ─────────────────────────────────────────────────────

/**
 * IFecCodec adapter for RLNC over GF(2⁸).
 *
 * Each encoded packet is `[ K coeff bytes ][ symbolSize data bytes ]`.
 */
export class RlncCodec implements IFecCodec {
  readonly codecName            = 'RLNC-GF256';
  readonly deviceTierRequired   = 0;
  readonly overheadFraction     = 0.05;
  readonly fixedSymbolSizeBytes = 0;

  private readonly k: number;

  constructor(generationSize = 16) {
    if (generationSize < 1 || generationSize > 255)
      throw new RangeError('rlnc: generationSize must be in [1, 255]');
    this.k = generationSize;
  }

  encode(source: Uint8Array, targetSymbolCount: number): Uint8Array {
    if (source.length === 0) throw new RangeError('rlnc: source must not be empty');
    const k           = this.k;
    const symbolSize  = Math.ceil(source.length / k);
    const packetSize  = k + symbolSize;
    const symbols     = this._split(source, symbolSize);
    const enc         = new RlncEncoder(symbols, true);
    const output      = new Uint8Array(targetSymbolCount * packetSize);

    for (let i = 0; i < targetSymbolCount; i++) {
      const { coefficients, encodedSymbol } = enc.nextPacket();
      const offset = i * packetSize;
      output.set(coefficients, offset);
      output.set(encodedSymbol, offset + k);
    }
    return output;
  }

  tryDecode(receivedSymbols: Uint8Array[], sourceSymbolCount: number): Uint8Array | null {
    if (receivedSymbols.length === 0) return null;
    const k          = this.k;
    const symbolSize = receivedSymbols[0].length - k;
    if (symbolSize <= 0) return null;

    const dec = new RlncDecoder(k, symbolSize);
    for (const pkt of receivedSymbols) {
      dec.addPacket(pkt.subarray(0, k), pkt.subarray(k));
      if (dec.isComplete) break;
    }
    return dec.tryDecode();
  }

  private _split(source: Uint8Array, symbolSize: number): Uint8Array[] {
    const symbols: Uint8Array[] = [];
    for (let i = 0; i < this.k; i++) {
      const sym    = new Uint8Array(symbolSize);
      const offset = i * symbolSize;
      const len    = Math.min(symbolSize, source.length - offset);
      if (len > 0) sym.set(source.subarray(offset, offset + len));
      symbols.push(sym);
    }
    return symbols;
  }
}
