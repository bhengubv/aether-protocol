/**
 * Extension seams hosts can wire up to participate in incentive accounting,
 * cloud-relay fallbacks, and feature gating. Default no-op implementations
 * let the protocol layer call through these uniformly.
 *
 * SPDX-License-Identifier: MIT
 */

import { MeshPacket } from "./protocol/MeshPacket.js";
import { DtnBundle, SosAlert } from "./models/index.js";

export interface IncentiveProvider {
  recordRelay(localUhid: string, packet: MeshPacket): Promise<void>;
  shouldPrioritize(packet: MeshPacket): Promise<boolean>;
}

export interface BackendClient {
  relayMessage(
    senderUhid: string,
    recipientUhid: string,
    encryptedContent: Uint8Array,
    priority: number,
  ): Promise<boolean>;
  syncDtnBundle(bundle: DtnBundle): Promise<boolean>;
  syncSos(alert: SosAlert): Promise<boolean>;
}

export interface FeatureFlagProvider {
  isEnabled(featureName: string): Promise<boolean>;
}

export class NoopIncentiveProvider implements IncentiveProvider {
  async recordRelay(_localUhid: string, _packet: MeshPacket): Promise<void> {
    // intentionally no-op
  }
  async shouldPrioritize(_packet: MeshPacket): Promise<boolean> {
    return false;
  }
}

export class NoopBackendClient implements BackendClient {
  async relayMessage(): Promise<boolean> {
    return false;
  }
  async syncDtnBundle(): Promise<boolean> {
    return false;
  }
  async syncSos(): Promise<boolean> {
    return false;
  }
}

export class NoopFeatureFlagProvider implements FeatureFlagProvider {
  async isEnabled(_featureName: string): Promise<boolean> {
    return true;
  }
}
