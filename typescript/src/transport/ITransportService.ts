/**
 * Transport service interface contract
 * SPDX-License-Identifier: MIT
 */

/**
 * ITransportService defines the contract all transport implementations must satisfy
 */
export interface ITransportService {
  /**
   * Human-readable identifier (e.g., "BLE", "Wi-Fi Direct", "InProcess")
   */
  name: string;

  /**
   * Whether the transport is currently usable on this device
   */
  isAvailable: boolean;

  /**
   * Maximum throughput in bytes per second
   */
  maxBandwidthBps: number;

  /**
   * Maximum communication range in meters
   */
  maxRangeMeters: number;

  /**
   * Relative power consumption (1 = low, 10 = high)
   */
  powerCostRelative: number;

  /**
   * Maximum simultaneous peer connections
   */
  maxConcurrentPeers: number;

  /**
   * Send a byte array to a specific peer.
   * @param peerUhid The UHID of the target peer
   * @param data The data to send
   * @param cancellationToken Cancellation token
   * @returns true on success, false on failure
   */
  sendAsync(
    peerUhid: string,
    data: Uint8Array,
    cancellationToken?: AbortSignal
  ): Promise<boolean>;

  /**
   * Send a stream to a peer (for large transfers, voice, video).
   * @param peerUhid The UHID of the target peer
   * @param stream The stream to send
   * @param cancellationToken Cancellation token
   * @returns true on success, false on failure
   */
  sendStreamAsync(
    peerUhid: string,
    stream: ReadableStream<Uint8Array>,
    cancellationToken?: AbortSignal
  ): Promise<boolean>;

  /**
   * Check if a connection is active to a peer.
   * @param peerUhid The UHID of the peer
   * @returns true if connected, false otherwise
   */
  isConnected(peerUhid: string): boolean;

  /**
   * Event fired when data arrives from a peer.
   * Parameters: (senderUhid: string, data: Uint8Array)
   */
  onDataReceived?: (senderUhid: string, data: Uint8Array) => void;
}
