/**
 * Transport-backed WebRTC signalling carrier.
 *
 * Carries the WebRTC SDP/ICE handshake over an existing AetherNet transport — the QUIC/HTTP relay,
 * the radio mesh, the circuit-relay-v2 transport, or (in tests) an in-process loopback — so two
 * distant nodes negotiate a direct {@link RTCDataChannel} without a dedicated signalling server.
 * Once the channel is open the app traffic flows peer-to-peer; only the short handshake ever touches
 * the relay.
 *
 * Each {@link Signal} is framed with a 4-byte magic prefix (`AWS1`) followed by a compact JSON body.
 * Inbound bytes on the underlying transport that lack the prefix are ignored — they are ordinary
 * application traffic, not signalling. Give this a channel whose receive surface is dedicated to
 * signalling (e.g. a relay connection reserved for control traffic), so the prefixed control frames
 * never reach the application data path.
 *
 * It implements {@link Signaling}, so it plugs straight into the {@link WebRtcTransport} signalling
 * seam in place of the in-process {@link InMemorySignalingBus}.
 *
 * ## Wire format (cross-language interop)
 *
 * The framed bytes are byte-for-byte identical to the C# `RelayWebRtcSignaling` wire format, so a
 * TypeScript node and a C# node can exchange the handshake across languages. The body reproduces
 * `System.Text.Json`'s source-generated output for the C# `WebRtcSignal` record under
 * `JsonIgnoreCondition.WhenWritingNull`:
 *
 * - PascalCase member names in declaration order: `FromUhid`, `ToUhid`, `Type`, `Sdp`, `Candidate`,
 *   `SdpMLineIndex`, `SdpMid`.
 * - `FromUhid` / `ToUhid` are always written (required strings).
 * - `Type` and `SdpMLineIndex` are numeric value types, written even when zero.
 * - `Sdp` / `Candidate` / `SdpMid` are nullable strings, omitted when `undefined`.
 * - Strings are escaped exactly as `System.Text.Json`'s default `JavaScriptEncoder.Default` does —
 *   which escapes `+ < > & ' \`` and every non-ASCII code point as uppercase `\uXXXX`, unlike
 *   `JSON.stringify`. This matters because real SDP fingerprints carry base64 `+` characters. See
 *   {@link encodeSignalFrame}.
 *
 * (Mirrors the Go `RelayWebRtcSignaling` / `SignalingChannel` and the C# `RelayWebRtcSignaling` /
 * `ITransportService` seam. Like this TypeScript carrier, the Go carrier hand-rolls the same STJ-exact
 * escaping — `+ < > &` become uppercase `\uXXXX`, not `encoding/json`'s literal `+` / lowercase hex —
 * so all three (and the other five SDKs) match the C# reference byte-for-byte.)
 *
 * SPDX-License-Identifier: MIT
 */

import { Signal, Signaling, SignalType } from "./signaling.js";

/**
 * The transport seam {@link RelayWebRtcSignaling} rides: an outbound send by UHID plus the inbound
 * receive surface every real AetherNet transport exposes. This is the structural subset of
 * {@link ../ITransportService.ITransportService | ITransportService} the carrier needs, so any real
 * transport — {@link WebRtcTransport}, the circuit-relay-v2 `CircuitRelayTransportService`, the LoRa
 * serial transport, or {@link ../InProcessTransport.InProcessTransport} — satisfies it directly.
 *
 * Give it a channel whose receive surface is dedicated to signalling; the `AWS1`-prefixed frames then
 * never reach the application data path, and any inbound bytes lacking the prefix are ignored.
 */
export interface SignalingChannel {
  /** Hands the (already framed) bytes to the underlying transport for delivery to `peerUhid`. */
  sendAsync(
    peerUhid: string,
    data: Uint8Array,
    cancellationToken?: AbortSignal,
  ): Promise<boolean>;

  /** Fired when data arrives from a peer; the carrier subscribes to decode inbound signalling frames. */
  onDataReceived?: (senderUhid: string, data: Uint8Array) => void;
}

/** `AWS1` = Aether WebRtc Signal, framing v1. Byte-identical to the C#/Go magic prefix. */
const MAGIC = new Uint8Array([0x41, 0x57, 0x53, 0x31]); // 'A' 'W' 'S' '1'

/**
 * Carries WebRTC SDP/ICE signalling over an existing {@link SignalingChannel} (a real transport),
 * framing each signal as `AWS1` + JSON. Implements {@link Signaling} for the {@link WebRtcTransport}
 * seam.
 */
export class RelayWebRtcSignaling implements Signaling {
  private readonly channel: SignalingChannel;
  private handler?: (signal: Signal) => void;
  private readonly previousOnData?: (senderUhid: string, data: Uint8Array) => void;
  private disposed = false;

  /**
   * @param channel The transport channel that carries the framed handshake. The carrier subscribes to
   *                its {@link SignalingChannel.onDataReceived} immediately; inbound `AWS1` frames are
   *                decoded and delivered to the {@link onSignal} handler.
   */
  constructor(channel: SignalingChannel) {
    if (!channel) {
      throw new Error("RelayWebRtcSignaling: channel is required");
    }
    this.channel = channel;
    // Preserve any pre-existing receiver so a shared transport still surfaces non-signalling app
    // traffic; we only *consume* AWS1-prefixed frames and forward the rest on.
    this.previousOnData = channel.onDataReceived;
    channel.onDataReceived = (from, data) => this.onChannelData(from, data);
  }

  // ── Signaling ─────────────────────────────────────────────────────────────────

  /** Frames `signal` as `AWS1` + JSON and sends it over the transport channel to its addressee. */
  async sendSignal(peerUhid: string, signal: Signal): Promise<boolean> {
    if (this.disposed) return false;
    const frame = encodeSignalFrame(signal);
    return this.channel.sendAsync(peerUhid, frame);
  }

  /** Registers the handler invoked for signals addressed to the local node. */
  onSignal(handler: (signal: Signal) => void): void {
    this.handler = handler;
  }

  /** Detaches the carrier from the transport, restoring any prior receiver. */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    // Only relinquish the hook if it is still ours.
    if (this.channel.onDataReceived !== undefined) {
      this.channel.onDataReceived = this.previousOnData;
    }
    this.handler = undefined;
  }

  private onChannelData(fromUhid: string, data: Uint8Array): void {
    if (!hasMagic(data)) {
      // Ordinary app traffic, not a signalling frame — pass through to any prior receiver.
      this.previousOnData?.(fromUhid, data);
      return;
    }
    const signal = decodeSignalFrame(data);
    if (signal === undefined) return; // malformed frame — discard (ICE re-gathers)
    this.handler?.(signal);
  }
}

// ── Framing (byte-identical to C# System.Text.Json) ─────────────────────────────

/** True when `data` starts with the 4-byte `AWS1` magic. */
function hasMagic(data: Uint8Array): boolean {
  return (
    data.length >= MAGIC.length &&
    data[0] === MAGIC[0] &&
    data[1] === MAGIC[1] &&
    data[2] === MAGIC[2] &&
    data[3] === MAGIC[3]
  );
}

/**
 * Encodes `signal` as the wire frame: the 4-byte `AWS1` magic followed by the UTF-8 JSON body. The
 * body is byte-for-byte identical to what the C# `WebRtcSignal` record serialises to under
 * `System.Text.Json` (source-generated, `WhenWritingNull`).
 *
 * Exported for the interop acceptance test, which compares the produced bytes against captured C#
 * reference frames.
 */
export function encodeSignalFrame(signal: Signal): Uint8Array {
  const body = new TextEncoder().encode(serializeSignalBody(signal));
  const frame = new Uint8Array(MAGIC.length + body.length);
  frame.set(MAGIC, 0);
  frame.set(body, MAGIC.length);
  return frame;
}

/**
 * Decodes a wire frame (magic already assumed present) back into a {@link Signal}, or `undefined` if
 * the JSON body is malformed. Accepts the PascalCase wire shape produced by any language's carrier.
 *
 * Exported for the interop acceptance test.
 */
export function decodeSignalFrame(data: Uint8Array): Signal | undefined {
  if (!hasMagic(data)) return undefined;
  try {
    const json = new TextDecoder().decode(data.subarray(MAGIC.length));
    const w = JSON.parse(json) as WireSignal;
    if (typeof w.FromUhid !== "string" || typeof w.ToUhid !== "string" || typeof w.Type !== "number") {
      return undefined;
    }
    return {
      fromUhid: w.FromUhid,
      toUhid: w.ToUhid,
      type: w.Type as SignalType,
      sdp: w.Sdp ?? undefined,
      candidate: w.Candidate ?? undefined,
      sdpMid: w.SdpMid ?? undefined,
      sdpMLineIndex: typeof w.SdpMLineIndex === "number" ? w.SdpMLineIndex : undefined,
    };
  } catch {
    return undefined;
  }
}

/**
 * The on-the-wire JSON shape: PascalCase keys matching the C# `WebRtcSignal` record. Kept deliberately
 * separate from the mesh-facing {@link Signal} (which uses concise camelCase), exactly as the Go
 * `wireSignal` is separate from its `Signal` — the cross-language framing is pinned here.
 */
interface WireSignal {
  FromUhid: string;
  ToUhid: string;
  Type: number;
  Sdp?: string;
  Candidate?: string;
  SdpMLineIndex: number;
  SdpMid?: string;
}

/**
 * Serialises the signal body byte-identically to C# `System.Text.Json`. Built by hand (not via
 * `JSON.stringify`) so key order, always-present numeric fields, null-omission, AND string escaping
 * all match STJ's default encoder — including its escaping of `+ < > & ' \`` and non-ASCII as
 * uppercase `\uXXXX`, which `JSON.stringify` does not do.
 */
function serializeSignalBody(signal: Signal): string {
  const parts: string[] = [];
  // Declaration order, matching the C# record / STJ source-gen emission order.
  parts.push(`"FromUhid":${stjString(signal.fromUhid)}`);
  parts.push(`"ToUhid":${stjString(signal.toUhid)}`);
  parts.push(`"Type":${(signal.type as number) | 0}`);
  if (signal.sdp !== undefined && signal.sdp !== null) {
    parts.push(`"Sdp":${stjString(signal.sdp)}`);
  }
  if (signal.candidate !== undefined && signal.candidate !== null) {
    parts.push(`"Candidate":${stjString(signal.candidate)}`);
  }
  // Non-nullable ushort in C#: always written, even when 0. Defaults to 0 when absent in TS.
  const mline = signal.sdpMLineIndex ?? 0;
  parts.push(`"SdpMLineIndex":${mline | 0}`);
  if (signal.sdpMid !== undefined && signal.sdpMid !== null) {
    parts.push(`"SdpMid":${stjString(signal.sdpMid)}`);
  }
  return `{${parts.join(",")}}`;
}

/**
 * Encodes a JSON string literal (including the surrounding quotes) exactly as
 * `System.Text.Json`'s default `JavaScriptEncoder.Default` does.
 *
 * STJ escapes, beyond the JSON-mandated set:
 *  - `"` as `"` (NOT `\"`),
 *  - `+ < > & ' \`` as `\uXXXX`,
 *  - every non-ASCII code point (> 0x7E) as `\uXXXX` (surrogate pairs emitted as two `\uXXXX`),
 * all with UPPERCASE hex. C0 control characters use the short escapes `\b \t \n \f \r` where defined,
 * else `\uXXXX`. Backslash is `\\`.
 */
function stjString(s: string): string {
  let out = '"';
  for (let i = 0; i < s.length; i++) {
    const code = s.charCodeAt(i);
    switch (code) {
      case 0x08: out += "\\b"; break;
      case 0x09: out += "\\t"; break;
      case 0x0a: out += "\\n"; break;
      case 0x0c: out += "\\f"; break;
      case 0x0d: out += "\\r"; break;
      case 0x5c: out += "\\\\"; break; // backslash
      default:
        // ASCII letters, digits, and the "safe" punctuation STJ leaves literal.
        if (code >= 0x20 && code <= 0x7e && !STJ_ESCAPE_ASCII.has(code)) {
          out += s[i];
        } else {
          out += "\\u" + code.toString(16).toUpperCase().padStart(4, "0");
        }
    }
  }
  return out + '"';
}

/**
 * ASCII code points (0x20–0x7E) that `System.Text.Json`'s default encoder escapes as `\uXXXX` even
 * though plain JSON would not. Empirically captured from STJ: `" & ' + < > \``.
 */
const STJ_ESCAPE_ASCII: ReadonlySet<number> = new Set([
  0x22, // "
  0x26, // &
  0x27, // '
  0x2b, // +
  0x3c, // <
  0x3e, // >
  0x60, // `
]);
