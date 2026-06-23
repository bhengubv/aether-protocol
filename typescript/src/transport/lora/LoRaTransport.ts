/**
 * Real LoRa (Aether Red / CircleLink) transport over a serial-attached RYLR-class module.
 *
 * Speaks the RYLR-class AT command set (Reyax RYLR896/RYLR998 and compatibles) on an
 * SX127x/SX126x radio, mirroring the C#/Go/Rust/C `LoRaSerialTransport`: opens the serial
 * port, configures the radio, sends with `AT+SEND`, and surfaces inbound `+RCV` frames.
 *
 * Node-only. Requires the optional `serialport` package, loaded via dynamic import so the
 * core library compiles and runs (in browsers / without a radio) without it:
 *
 *     npm install serialport
 *
 * Verification status: real driver; compiles with `tsc` without `serialport` installed
 * (the dependency is resolved at runtime in {@link open}). Runtime-UNVERIFIED — not
 * exercised against a physical module.
 *
 * SPDX-License-Identifier: MIT
 */

import { ITransportService, PerTransportMetrics } from "../ITransportService.js";

/** Configuration for a RYLR-class serial LoRa module. */
export interface LoRaOptions {
  /** "COM5" or "/dev/ttyUSB0" — required. */
  portName: string;
  baudRate?: number; // 115200
  address?: number; // this node's LoRa address (1-65535); default 1
  networkId?: number; // RYLR network id; default 18
  bandHz?: number; // EU868 = 868500000; US915 = 915000000
  spreadingFactor?: number; // 7-12; default 9
  bandwidthIndex?: number; // 7=125kHz, 8=250, 9=500; default 7
  codingRate?: number; // 1=4/5; default 1
  preambleLength?: number; // default 12
}

/**
 * Minimal structural type for the bits of the `serialport` package we use, so `tsc`
 * type-checks without the optional dependency present. Resolved at runtime via dynamic import.
 */
interface SerialPortLike {
  write(data: string, callback?: (error?: Error | null) => void): boolean;
  on(event: "data", listener: (chunk: Uint8Array) => void): void;
  close(callback?: (error?: Error | null) => void): void;
}

export class LoRaSerialTransport implements ITransportService {
  name = "Aether Red (LoRa/CircleLink)";
  isAvailable = false;
  maxBandwidthBps = 37_500; // SF7/BW125 ~= 37.5 kbps
  maxRangeMeters = 15_000; // up to ~15 km LOS
  powerCostRelative = 8; // high TX power (1-10 scale)
  maxConcurrentPeers = 255;
  readonly metrics = new PerTransportMetrics();
  onDataReceived?: (senderUhid: string, data: Uint8Array) => void;

  private readonly opts: Required<LoRaOptions>;
  private port?: SerialPortLike;
  private readonly peerAddrs = new Map<string, number>();
  private rxBuffer = "";

  constructor(options: LoRaOptions) {
    this.opts = {
      baudRate: 115_200,
      address: 1,
      networkId: 18,
      bandHz: 868_500_000,
      spreadingFactor: 9,
      bandwidthIndex: 7,
      codingRate: 1,
      preambleLength: 12,
      ...options,
    };
  }

  /** Opens the serial port and configures the radio. Requires the `serialport` package (Node). */
  async open(): Promise<void> {
    if (this.isAvailable) return;
    // Indirect specifier: `tsc` cannot resolve a non-literal import, so it does not require
    // the optional `serialport` types at build time (resolved at runtime in Node).
    const moduleName = "serialport";
    const mod: any = await import(moduleName).catch(() => {
      throw new Error("LoRa transport requires the 'serialport' package: npm install serialport");
    });
    const SerialPortCtor = mod.SerialPort ?? mod.default?.SerialPort ?? mod.default;
    this.port = new SerialPortCtor({
      path: this.opts.portName,
      baudRate: this.opts.baudRate,
    }) as SerialPortLike;
    this.port.on("data", (chunk) => this.onSerialData(chunk));
    this.configure();
    this.isAvailable = true;
  }

  private configure(): void {
    const o = this.opts;
    for (const cmd of [
      `AT+ADDRESS=${o.address}`,
      `AT+NETWORKID=${o.networkId}`,
      `AT+BAND=${o.bandHz}`,
      `AT+PARAMETER=${o.spreadingFactor},${o.bandwidthIndex},${o.codingRate},${o.preambleLength}`,
    ]) {
      this.port!.write(cmd + "\r\n");
    }
  }

  /** Map an AetherNet peer UHID to a numeric LoRa node address (1-65535) for directed sends. */
  registerPeer(peerUhid: string, address: number): void {
    if (peerUhid) this.peerAddrs.set(peerUhid, address);
  }

  async sendAsync(peerUhid: string, data: Uint8Array): Promise<boolean> {
    if (!this.isAvailable || !this.port || data.length === 0) return false;
    const addr = this.peerAddrs.get(peerUhid) ?? 0; // 0 = broadcast (managed-flood mesh)
    // Hex-encode so the payload survives the AT text protocol; length field is the hex length.
    const hex = Array.from(data, (b) => b.toString(16).padStart(2, "0").toUpperCase()).join("");
    const cmd = `AT+SEND=${addr},${hex.length},${hex}\r\n`;
    try {
      this.port.write(cmd);
      this.metrics.recordSample(0, true, data.length);
      return true;
    } catch {
      this.metrics.recordSample(0, false, 0);
      return false;
    }
  }

  async sendStreamAsync(peerUhid: string, stream: ReadableStream<Uint8Array>): Promise<boolean> {
    const chunks: Uint8Array[] = [];
    const reader = stream.getReader();
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        if (value) chunks.push(value);
      }
    } finally {
      reader.releaseLock();
    }
    const total = chunks.reduce((sum, c) => sum + c.length, 0);
    const combined = new Uint8Array(total);
    let offset = 0;
    for (const c of chunks) {
      combined.set(c, offset);
      offset += c.length;
    }
    return this.sendAsync(peerUhid, combined);
  }

  isConnected(_peerUhid: string): boolean {
    return this.isAvailable; // connectionless broadcast medium
  }

  close(): void {
    this.isAvailable = false;
    this.port?.close();
    this.port = undefined;
  }

  // ── Receive ──────────────────────────────────────────────────────────────────

  private onSerialData(chunk: Uint8Array): void {
    let ascii = "";
    for (let i = 0; i < chunk.length; i++) ascii += String.fromCharCode(chunk[i]);
    this.rxBuffer += ascii;
    let idx: number;
    while ((idx = this.rxBuffer.search(/[\r\n]/)) >= 0) {
      const line = this.rxBuffer.slice(0, idx).trim();
      this.rxBuffer = this.rxBuffer.slice(idx + 1);
      if (line) this.handleLine(line);
    }
  }

  private handleLine(line: string): void {
    // RYLR inbound frame: +RCV=<address>,<length>,<hexdata>,<rssi>,<snr>
    if (!line.startsWith("+RCV=")) return;
    const parts = line.slice(5).split(",");
    if (parts.length < 3) return;
    const addr = parseInt(parts[0], 10);
    if (Number.isNaN(addr)) return;
    const hex = parts[2];
    if (hex.length % 2 !== 0) return;
    const data = new Uint8Array(hex.length / 2);
    for (let i = 0; i < data.length; i++) {
      const byte = parseInt(hex.substr(i * 2, 2), 16);
      if (Number.isNaN(byte)) return;
      data[i] = byte;
    }
    this.onDataReceived?.(String(addr), data);
  }
}
