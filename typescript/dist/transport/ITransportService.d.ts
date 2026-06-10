/**
 * Transport service interface contract, per-transport EWMA metrics,
 * and static ranking helper.
 * SPDX-License-Identifier: MIT
 */
/**
 * Per-transport EWMA statistics updated after every send attempt.
 *
 * Tracks round-trip time, packet-loss rate, and throughput using
 * exponential moving averages (α = 0.2).  Used by {@link rankTransports}
 * for static scoring and by PredictiveTransportSelector as the EWMA
 * baseline on top of which the Kalman filter operates.
 *
 * All fields are read-only from the outside; mutations go through
 * {@link recordSample}.
 */
export declare class PerTransportMetrics {
    private _sampleCount;
    private _ewmaRttMs;
    private _ewmaLossRate;
    private _ewmaThroughputBps;
    /** Number of samples recorded so far. */
    get sampleCount(): number;
    /** EWMA round-trip time in milliseconds; starts at 200 ms. */
    get ewmaRttMs(): number;
    /** EWMA packet-loss rate [0, 1]; starts at 0.05 (5 %). */
    get ewmaLossRate(): number;
    /** EWMA throughput in bits per second; starts at 0 until bootstrapped. */
    get ewmaThroughputBps(): number;
    /**
     * Record one send attempt and update all EWMA metrics.
     *
     * @param rttMs           Round-trip time in milliseconds.
     *                        Pass 0 to skip RTT and throughput updates
     *                        (e.g. for instantaneous in-process transports).
     * @param success         `true` if the send succeeded.
     * @param bytesTransferred Bytes transferred; used for throughput only when
     *                        `success && rttMs > 0`.
     */
    recordSample(rttMs: number, success: boolean, bytesTransferred: number): void;
    /**
     * Composite score for static transport ranking.
     *
     * Formula: `(effectiveBps / powerCost) × (1 − lossRate) / max(rttMs, 1)`
     *
     * - `effectiveBps` = `ewmaThroughputBps` if bootstrapped, else
     *   `maxBandwidthBps × 0.1` (10 % of nominal as a conservative prior).
     * - `powerCost` = `max(powerCostRelative, 1)` — clamped so zero-cost
     *   transports are treated like cost-1 transports.
     */
    compositeScore(maxBandwidthBps: number, powerCostRelative: number): number;
}
/**
 * Rank a list of transports by composite score in descending order.
 *
 * Unavailable transports (`isAvailable === false`) are excluded from results.
 *
 * Score selection:
 * - Transport **with** `metrics` → `metrics.compositeScore(maxBandwidthBps, powerCostRelative)`
 * - Transport **without** `metrics` → static estimate:
 *   `maxBandwidthBps / max(powerCostRelative, 1)`
 */
export declare function rankTransports(transports: ITransportService[]): Array<{
    transport: ITransportService;
    score: number;
}>;
/**
 * ITransportService defines the contract all transport implementations must satisfy.
 */
export interface ITransportService {
    /**
     * Human-readable identifier (e.g. "BLE", "Wi-Fi Direct", "InProcess").
     */
    name: string;
    /**
     * Whether the transport is currently usable on this device.
     */
    isAvailable: boolean;
    /**
     * Maximum throughput in bits per second.
     */
    maxBandwidthBps: number;
    /**
     * Maximum communication range in metres.
     */
    maxRangeMeters: number;
    /**
     * Relative power consumption (1 = low, 10 = high).
     */
    powerCostRelative: number;
    /**
     * Maximum simultaneous peer connections.
     */
    maxConcurrentPeers: number;
    /**
     * Live per-transport EWMA statistics.
     * `undefined` for transports that do not track metrics; those transports
     * fall back to static scoring in {@link rankTransports}.
     */
    metrics?: PerTransportMetrics;
    /**
     * Send a byte array to a specific peer.
     *
     * @param peerUhid          UHID of the target peer.
     * @param data              Data to send.
     * @param cancellationToken Optional abort signal.
     * @returns `true` on success, `false` on failure.
     */
    sendAsync(peerUhid: string, data: Uint8Array, cancellationToken?: AbortSignal): Promise<boolean>;
    /**
     * Send a readable stream to a peer (for large transfers, voice, video).
     *
     * @param peerUhid          UHID of the target peer.
     * @param stream            Stream to send.
     * @param cancellationToken Optional abort signal.
     * @returns `true` on success, `false` on failure.
     */
    sendStreamAsync(peerUhid: string, stream: ReadableStream<Uint8Array>, cancellationToken?: AbortSignal): Promise<boolean>;
    /**
     * Check whether an active connection exists to a peer.
     *
     * @param peerUhid UHID of the peer to check.
     * @returns `true` if connected, `false` otherwise.
     */
    isConnected(peerUhid: string): boolean;
    /**
     * Fired when data arrives from a peer.
     * Parameters: `(senderUhid: string, data: Uint8Array)`
     */
    onDataReceived?: (senderUhid: string, data: Uint8Array) => void;
}
/**
 * Forward Error Correction codec contract.
 *
 * Implementations encode `source` into a stream of `targetSymbolCount`
 * packets and reconstruct the original from any `>= sourceSymbolCount`
 * received packets. Each packet is opaque bytes; coefficients are codec-defined.
 *
 * Reference impl: {@link ../transport/rlnc.RlncCodec | RlncCodec} (RLNC over GF(2^8)).
 */
export interface IFecCodec {
    /** Human-readable codec name (e.g. "RLNC-GF256", "RS-32-16"). */
    readonly codecName: string;
    /** Minimum device-tier required to run this codec (0 = phone-class baseline). */
    readonly deviceTierRequired: number;
    /** Expected bandwidth overhead fraction (e.g. 0.05 = 5 % redundancy). */
    readonly overheadFraction: number;
    /** Fixed packet symbol size in bytes, or 0 if size is derived from the source. */
    readonly fixedSymbolSizeBytes: number;
    /**
     * Encode `source` into `targetSymbolCount` packets, concatenated into a single
     * Uint8Array of length `targetSymbolCount * packetSize`.
     */
    encode(source: Uint8Array, targetSymbolCount: number): Uint8Array;
    /**
     * Attempt to reconstruct the original source from `receivedSymbols` packets.
     * Returns the source bytes on success or `null` if more packets are needed.
     *
     * @param sourceSymbolCount Number of symbols the original source was split into.
     */
    tryDecode(receivedSymbols: Uint8Array[], sourceSymbolCount: number): Uint8Array | null;
}
//# sourceMappingURL=ITransportService.d.ts.map