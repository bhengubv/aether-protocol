// SPDX-License-Identifier: MIT

/**
 * NearLinkTransportService.ets
 *
 * OpenHarmony / HarmonyOS 5.0.1+ (API 13) implementation of the Aether Teal
 * transport using the HarmonyOS NearLink Kit.
 *
 * Protocol: NearLink SLE — SparkLink Low-Energy
 *   Frequency:  2.4 GHz
 *   Range:      up to 600 m line-of-sight
 *   Bandwidth:  12 Mbps
 *   Latency:    20 µs
 *   Power:      ~60 % lower than BLE 5.0
 *
 * HarmonyOS Kit: @kit.NearLinkKit
 *   Namespaces: scan · advertising · ssap · constant
 *   Permission: ohos.permission.ACCESS_NEARLINK  (user_grant — runtime request)
 *   Min API:    HarmonyOS 5.0.1 Beta3 (API 13)
 *
 * Device support (NearLink silicon required):
 *   Huawei Mate 60 / 70 series, Mate X6, Pocket 2,
 *   Pura 70 Pro / Pro+ / Ultra, HarmonyOS PC HAD-W32.
 *
 * ── Dual-role architecture ─────────────────────────────────────────────────
 *
 * A node can operate in CLIENT role, SERVER role, or both simultaneously.
 * This enables full mesh connectivity — any node can connect to any other.
 *
 *  CLIENT role (central):
 *    scan.startScan → onDeviceFound(addr) → ssap.createClient(addr)
 *    → client.connect() → client.writeProperty() [TX]
 *    → client.on('propertyChange') [RX]
 *
 *  SERVER role (peripheral):
 *    ssap.createServer() → server.addService(aetherService)
 *    → advertising.startAdvertising() [discovery]
 *    → server.on('propertyWrite') [RX from connected clients]
 *    → server.notifyPropertyChanged(clientAddr, property) [TX to clients]
 *
 *  sendAsync() dispatches through whichever roles are currently active:
 *    - CLIENT active  → client.writeProperty(WRITE_NO_RESPONSE)
 *    - SERVER active  → notifyPropertyChanged to every connected client
 *
 * ── SSAP service / property UUIDs ─────────────────────────────────────────
 *
 *  Aether SLE Service:   61657468-6572-0003-0000-000000000000
 *  Aether Data Property: 61657468-6572-0003-0001-000000000000
 *    ('aether\0' prefix + sub-ID 0003 for NearLink layer; mirrors android/blue)
 *
 * ── NOTE on server-side API confidence ────────────────────────────────────
 *
 *  The CLIENT side (scan / connect / writeProperty / propertyChange) is
 *  confirmed verbatim from Huawei docs and multiple Chinese-language
 *  primary sources.
 *
 *  The SERVER side (ssap.createServer / addService / propertyWrite event /
 *  notifyPropertyChanged) is confirmed via search-engine snippets for
 *  ssap.createServer() and the server-side event names, and inferred from the
 *  BLE GATT server analogy (ble.createGattServer + characteristicWrite +
 *  notifyCharacteristicChanged) which mirrors SSAP exactly. The Huawei docs
 *  page is a JS-rendered SPA that Web tools cannot execute.
 *
 *  BEFORE SHIPPING: open the project in DevEco Studio 5.0.1+ and verify
 *  the server-side type names via IDE auto-complete against the SDK's
 *  NearLinkKit.d.ets declarations. Items marked [VERIFY] below may need
 *  minor name corrections.
 *
 * Sources:
 *   https://developer.huawei.com/consumer/en/doc/harmonyos-guides/nearlink-kit-guide
 *   https://developer.huawei.com/consumer/en/doc/harmonyos-guides/nearlink-ssap-client-connect
 *   https://developer.huawei.com/consumer/en/doc/harmonyos-guides-V13/nearlink-ssap-server-connect-V13
 *   https://harmonyosdev.csdn.net/6940e55e96fa167eeece6b2e.html
 */

import { scan, advertising, ssap, constant } from '@kit.NearLinkKit';
import { abilityAccessCtrl, Permissions, common } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';

const TAG: string = 'NearLinkTransportService';
const DOMAIN: number = 0xf005;

const AETHER_SLE_SERVICE_UUID: string = '61657468-6572-0003-0000-000000000000';
const AETHER_SLE_DATA_PROPERTY_UUID: string = '61657468-6572-0003-0001-000000000000';
const NEARLINK_PERMISSION: Permissions = 'ohos.permission.ACCESS_NEARLINK';

// ---------------------------------------------------------------------------
// Interface — mirrors C# ICircleLinkTransportService
// ---------------------------------------------------------------------------

export interface ICircleLinkTransportService {
  readonly transportName: string;
  readonly isAvailable: boolean;
  readonly isServerRunning: boolean;
  readonly connectedClientCount: number;
  readonly frequencyMHz: number;
  readonly rangeKm: number;
  readonly maxMtu: number;

  probeAvailability(): Promise<boolean>;

  // Discovery (central role)
  startDiscovery(onDeviceFound: (deviceAddress: string) => void): Promise<void>;
  stopDiscovery(): Promise<void>;

  // Advertising + server (peripheral role)
  startAdvertising(): Promise<void>;
  stopAdvertising(): Promise<void>;
  startServer(): Promise<void>;
  stopServer(): void;

  // Client connection (central role)
  connectAsync(deviceAddress: string): Promise<void>;
  disconnectAsync(): Promise<void>;

  // Bidirectional data
  sendAsync(data: Uint8Array): Promise<void>;
  onDataReceived(callback: (data: Uint8Array) => void): void;

  release(): void;
}

// ---------------------------------------------------------------------------
// Implementation
// ---------------------------------------------------------------------------

export class NearLinkTransportService implements ICircleLinkTransportService {

  // ── Properties ───────────────────────────────────────────────────────────

  readonly transportName: string = 'Aether Teal (NearLink/SLE)';
  readonly frequencyMHz: number = 2400;
  readonly rangeKm: number = 0.6;
  readonly maxMtu: number = 512;

  get isAvailable(): boolean { return this._available; }
  get isServerRunning(): boolean { return this._server !== undefined; }
  get connectedClientCount(): number { return this._connectedClients.size; }

  // ── Private state ────────────────────────────────────────────────────────

  private _available: boolean = false;
  private _context: common.UIAbilityContext;
  private _isScanning: boolean = false;
  private _advertisingHandle: number = -1;

  // Client role
  private _client: ssap.Client | undefined = undefined;

  // Server role
  private _server: ssap.Server | undefined = undefined;           // [VERIFY] ssap.Server type
  private _connectedClients: Set<string> = new Set<string>();     // addresses of clients connected to our server

  // Shared inbound callback
  private _dataCallback: ((data: Uint8Array) => void) | undefined = undefined;

  constructor(context: common.UIAbilityContext) {
    this._context = context;
  }

  // ── Availability probe ───────────────────────────────────────────────────

  async probeAvailability(): Promise<boolean> {
    const atManager = abilityAccessCtrl.createAtManager();
    let permissionGranted = false;
    try {
      const tokenId = this._context.applicationInfo.accessTokenId;
      const status = await atManager.checkAccessToken(tokenId, NEARLINK_PERMISSION);
      if (status === abilityAccessCtrl.GrantStatus.PERMISSION_GRANTED) {
        permissionGranted = true;
      } else {
        const reqResult = await atManager.requestPermissionsFromUser(
          this._context, [NEARLINK_PERMISSION]);
        permissionGranted =
          reqResult.authResults[0] === abilityAccessCtrl.GrantStatus.PERMISSION_GRANTED;
      }
    } catch (err) {
      hilog.warn(DOMAIN, TAG,
        'Permission check/request failed: %{public}s', JSON.stringify(err));
    }

    if (!permissionGranted) {
      hilog.warn(DOMAIN, TAG, 'ACCESS_NEARLINK denied — IsAvailable = false');
      this._available = false;
      return false;
    }

    try {
      scan.on('deviceFound', () => { /* probe listener */ });
      await scan.startScan([], { scanMode: 0 });
      await scan.stopScan();
      scan.off('deviceFound');
      this._available = true;
      hilog.info(DOMAIN, TAG, 'NearLink hardware detected — IsAvailable = true');
    } catch (err) {
      scan.off('deviceFound');
      this._available = false;
      const be = err as BusinessError;
      hilog.warn(DOMAIN, TAG,
        'NearLink hardware probe failed (code=%{public}d): %{public}s',
        be.code, be.message);
    }
    return this._available;
  }

  // ── CLIENT ROLE: Discovery ───────────────────────────────────────────────

  async startDiscovery(onDeviceFound: (deviceAddress: string) => void): Promise<void> {
    this.assertAvailable('startDiscovery');
    if (this._isScanning) {
      return;
    }
    const seen = new Set<string>();
    scan.on('deviceFound', (results: Array<scan.ScanResults>) => {
      for (const result of results) {
        const addr: string = result.deviceId;
        if (addr && !seen.has(addr)) {
          seen.add(addr);
          hilog.info(DOMAIN, TAG, 'NearLink peer found: %{public}s', addr);
          onDeviceFound(addr);
        }
      }
    });
    try {
      await scan.startScan([], { scanMode: 2 });
      this._isScanning = true;
      hilog.info(DOMAIN, TAG, 'NearLink active scan started');
    } catch (err) {
      scan.off('deviceFound');
      const be = err as BusinessError;
      throw new Error(`NearLink startScan failed (code=${be.code}): ${be.message}`);
    }
  }

  async stopDiscovery(): Promise<void> {
    if (!this._isScanning) {
      return;
    }
    try {
      await scan.stopScan();
    } catch (err) {
      const be = err as BusinessError;
      hilog.warn(DOMAIN, TAG,
        'stopScan error (ignored, code=%{public}d): %{public}s', be.code, be.message);
    } finally {
      scan.off('deviceFound');
      this._isScanning = false;
      hilog.info(DOMAIN, TAG, 'NearLink scan stopped');
    }
  }

  // ── CLIENT ROLE: Connect / disconnect ────────────────────────────────────

  async connectAsync(deviceAddress: string): Promise<void> {
    this.assertAvailable('connectAsync');
    if (this._client !== undefined) {
      await this.disconnectAsync();
    }

    const client: ssap.Client = ssap.createClient(deviceAddress);

    client.on('connectionStateChange', (state: ssap.ConnectionChangeState) => {
      hilog.info(DOMAIN, TAG,
        'SSAP client state → %{public}d (peer=%{public}s)', state.state, deviceAddress);
      if (state.state === constant.ConnectionState.STATE_DISCONNECTED) {
        this._client = undefined;
      }
    });

    client.on('propertyChange', (property: ssap.Property) => {
      if (property.value !== undefined && this._dataCallback !== undefined) {
        this._dataCallback(new Uint8Array(property.value));
      }
    });

    try {
      await client.connect();
      hilog.info(DOMAIN, TAG, 'SSAP client connected to %{public}s', deviceAddress);
    } catch (err) {
      client.close();
      const be = err as BusinessError;
      throw new Error(`NearLink connect failed (code=${be.code}): ${be.message}`);
    }

    try {
      const services: Array<ssap.Service> = await client.getServices();
      if (services.find(s => s.serviceUuid === AETHER_SLE_SERVICE_UUID) !== undefined) {
        const dataProperty: ssap.Property = {
          serviceUuid: AETHER_SLE_SERVICE_UUID,
          propertyUuid: AETHER_SLE_DATA_PROPERTY_UUID,
          value: new ArrayBuffer(0)
        };
        await client.setPropertyNotification(dataProperty, true);
        hilog.info(DOMAIN, TAG, 'SSAP property notification enabled on %{public}s', deviceAddress);
      } else {
        hilog.warn(DOMAIN, TAG,
          'Aether SLE service not found on %{public}s — peer may not be running aether.teal',
          deviceAddress);
      }
    } catch (err) {
      const be = err as BusinessError;
      hilog.warn(DOMAIN, TAG,
        'getServices/setPropertyNotification failed (code=%{public}d): %{public}s',
        be.code, be.message);
    }

    this._client = client;
  }

  async disconnectAsync(): Promise<void> {
    const client = this._client;
    this._client = undefined;
    if (client === undefined) {
      return;
    }
    try {
      client.close();
      hilog.info(DOMAIN, TAG, 'SSAP client closed');
    } catch (err) {
      const be = err as BusinessError;
      hilog.warn(DOMAIN, TAG,
        'client.close() error (ignored, code=%{public}d): %{public}s', be.code, be.message);
    }
  }

  // ── SERVER ROLE: SSAP peripheral ─────────────────────────────────────────

  /**
   * Create and start the SSAP peripheral server.
   *
   * Registers the Aether SLE service with one data property and wires:
   *   'connectionStateChange' — tracks which clients are connected
   *   'propertyWrite'         — receives data from connecting clients
   *   'propertyRead'          — responds to client read requests
   *
   * Call startAdvertising() alongside this so scanning peers can discover
   * this node and initiate a connection.
   *
   * NOTE: server-side type names (ssap.Server, ssap.PropertyWriteRequest,
   * ssap.PropertyReadRequest, notifyPropertyChanged) are confirmed via
   * search-engine snippets + BLE GATT analogy.  Verify against
   * sdk/HarmonyOS/<ver>/openharmony/ets/api/NearLinkKit.d.ets in DevEco Studio.
   */
  async startServer(): Promise<void> {
    this.assertAvailable('startServer');
    if (this._server !== undefined) {
      hilog.warn(DOMAIN, TAG, 'startServer: server already running');
      return;
    }

    // [VERIFY] ssap.createServer() — confirmed in multiple search snippets.
    const server: ssap.Server = ssap.createServer();  // [VERIFY] type ssap.Server

    // ── Register the Aether SLE service ──────────────────────────────────
    //
    // ssap.Service and ssap.Property are the same types used by client.getServices().
    // The server-side service definition uses the same structure.
    //
    // [VERIFY] If ssap.Property has an additional 'permissions' or 'ability' field
    // on the server side (analogous to BLE GattProperties), add it here.
    const aetherDataProperty: ssap.Property = {
      serviceUuid: AETHER_SLE_SERVICE_UUID,
      propertyUuid: AETHER_SLE_DATA_PROPERTY_UUID,
      value: new ArrayBuffer(0)
    };

    const aetherService: ssap.Service = {
      serviceUuid: AETHER_SLE_SERVICE_UUID,
      isPrimary: true,
      properties: [aetherDataProperty]
    };

    // [VERIFY] server.addService(service) — confirmed by snippets.
    server.addService(aetherService);

    // ── Connection-state tracking ─────────────────────────────────────────
    //
    // 'connectionStateChange' is confirmed on both client and server sides.
    // ssap.ConnectionChangeState: { deviceId: string, state: constant.ConnectionState }
    server.on('connectionStateChange', (state: ssap.ConnectionChangeState) => {
      const addr = state.deviceId;
      if (state.state === constant.ConnectionState.STATE_CONNECTED) {
        this._connectedClients.add(addr);
        hilog.info(DOMAIN, TAG,
          'SSAP server: client connected %{public}s (total=%{public}d)',
          addr, this._connectedClients.size);
      } else if (state.state === constant.ConnectionState.STATE_DISCONNECTED) {
        this._connectedClients.delete(addr);
        hilog.info(DOMAIN, TAG,
          'SSAP server: client disconnected %{public}s (total=%{public}d)',
          addr, this._connectedClients.size);
      }
    });

    // ── Receive data from clients ─────────────────────────────────────────
    //
    // 'propertyWrite' fires when a client calls client.writeProperty(...).
    // [VERIFY] Type 'ssap.PropertyWriteRequest' — inferred from BLE pattern
    //   (ble.CharacteristicWriteRequest → ssap.PropertyWriteRequest).
    //   Expected fields: deviceId, transId, isPrep, needRsp, offset, value.
    server.on('propertyWrite', (request: ssap.PropertyWriteRequest) => { // [VERIFY] type name
      if (request.value !== undefined && this._dataCallback !== undefined) {
        this._dataCallback(new Uint8Array(request.value));
      }
      // WRITE_NO_RESPONSE does not require a server response.
      // If the client uses WRITE (with response), send an acknowledgement:
      //   [VERIFY] server.sendResponse({ deviceId: request.deviceId,
      //                                  transId: request.transId,
      //                                  status: 0, offset: 0,
      //                                  value: new ArrayBuffer(0) });
      hilog.debug(DOMAIN, TAG,
        'SSAP server RX %{public}d bytes from %{public}s',
        request.value?.byteLength ?? 0, request.deviceId);
    });

    // ── Respond to client read requests ──────────────────────────────────
    //
    // [VERIFY] Event name 'propertyRead' (confirmed in search snippets).
    //   Type 'ssap.PropertyReadRequest': inferred from BLE CharacteristicReadRequest.
    //   Fields: deviceId, transId, offset.
    server.on('propertyRead', (request: ssap.PropertyReadRequest) => { // [VERIFY] type name
      // [VERIFY] server.sendResponse() — mirrors ble.GattServer.sendResponse().
      server.sendResponse({   // [VERIFY] method name + parameter structure
        deviceId: request.deviceId,
        transId: request.transId,
        status: 0,
        offset: 0,
        value: new ArrayBuffer(0)
      });
    });

    this._server = server;
    hilog.info(DOMAIN, TAG, 'SSAP peripheral server started — Aether SLE service registered');
  }

  /** Stop the SSAP peripheral server and disconnect all connected clients. */
  stopServer(): void {
    const server = this._server;
    this._server = undefined;
    this._connectedClients.clear();
    if (server === undefined) {
      return;
    }
    try {
      // [VERIFY] server.close() — mirrors ble.GattServer.close().
      server.close();   // [VERIFY] method name
      hilog.info(DOMAIN, TAG, 'SSAP peripheral server stopped');
    } catch (err) {
      const be = err as BusinessError;
      hilog.warn(DOMAIN, TAG,
        'server.close() error (ignored, code=%{public}d): %{public}s', be.code, be.message);
    }
  }

  // ── PERIPHERAL ROLE: Advertising ─────────────────────────────────────────

  async startAdvertising(): Promise<void> {
    this.assertAvailable('startAdvertising');
    if (this._advertisingHandle >= 0) {
      return;
    }
    const settings: advertising.AdvertisingSettings = {
      interval: 160,
      power: 2
    };
    const advData: advertising.AdvertisingData = {
      serviceUuids: [AETHER_SLE_SERVICE_UUID],
      includeDeviceName: true
    };
    const params: advertising.AdvertisingParams = {
      advertisingSettings: settings,
      advertisingData: advData
    };
    try {
      this._advertisingHandle = await advertising.startAdvertising(params);
      hilog.info(DOMAIN, TAG,
        'NearLink advertising started (handle=%{public}d)', this._advertisingHandle);
    } catch (err) {
      const be = err as BusinessError;
      throw new Error(`NearLink startAdvertising failed (code=${be.code}): ${be.message}`);
    }
  }

  async stopAdvertising(): Promise<void> {
    if (this._advertisingHandle < 0) {
      return;
    }
    try {
      await advertising.stopAdvertising(this._advertisingHandle);
      hilog.info(DOMAIN, TAG, 'NearLink advertising stopped');
    } catch (err) {
      const be = err as BusinessError;
      hilog.warn(DOMAIN, TAG,
        'stopAdvertising error (ignored, code=%{public}d): %{public}s', be.code, be.message);
    } finally {
      this._advertisingHandle = -1;
    }
  }

  // ── Bidirectional send ───────────────────────────────────────────────────

  /**
   * Transmit a raw Aether packet to all reachable peers.
   *
   * Dispatches through whichever roles are currently active:
   *   CLIENT active → client.writeProperty(WRITE_NO_RESPONSE) to connected server
   *   SERVER active → server.notifyPropertyChanged to every connected client
   *
   * At least one role must be active with a live peer, otherwise throws.
   *
   * @param data  Aether wire-format bytes (max maxMtu = 512 bytes).
   */
  async sendAsync(data: Uint8Array): Promise<void> {
    this.assertAvailable('sendAsync');

    const hasClientConnection = this._client !== undefined;
    const hasServerClients = this._server !== undefined && this._connectedClients.size > 0;

    if (!hasClientConnection && !hasServerClients) {
      throw new Error(
        'NearLink: no active peers. Connect as client (connectAsync) or ' +
        'start server and wait for clients (startServer + startAdvertising).');
    }

    if (data.byteLength > this.maxMtu) {
      throw new Error(
        `NearLink: payload ${data.byteLength} bytes exceeds maxMtu ${this.maxMtu}. ` +
        'Fragment using BleGattFramer before calling sendAsync().');
    }

    const buf = data.buffer.slice(
      data.byteOffset,
      data.byteOffset + data.byteLength
    ) as ArrayBuffer;

    const property: ssap.Property = {
      serviceUuid: AETHER_SLE_SERVICE_UUID,
      propertyUuid: AETHER_SLE_DATA_PROPERTY_UUID,
      value: buf
    };

    // CLIENT path — write to the server we are connected to.
    if (hasClientConnection) {
      try {
        await this._client!.writeProperty(property, ssap.PropertyWriteType.WRITE_NO_RESPONSE);
        hilog.debug(DOMAIN, TAG, 'NearLink TX (client path) %{public}d bytes', data.byteLength);
      } catch (err) {
        const be = err as BusinessError;
        hilog.warn(DOMAIN, TAG,
          'client writeProperty failed (code=%{public}d): %{public}s', be.code, be.message);
      }
    }

    // SERVER path — notify every connected client.
    if (hasServerClients) {
      for (const clientAddr of this._connectedClients) {
        try {
          // [VERIFY] notifyPropertyChanged(deviceId, property, confirm)
          //   Mirrors ble.GattServer.notifyCharacteristicChanged(deviceId, char, confirm).
          //   confirm = false → unsolicited notify (no acknowledgement required).
          //   confirm = true  → indicated notify (client ACKs receipt).
          this._server!.notifyPropertyChanged(clientAddr, property, false); // [VERIFY]
          hilog.debug(DOMAIN, TAG,
            'NearLink TX (server notify → %{public}s) %{public}d bytes',
            clientAddr, data.byteLength);
        } catch (err) {
          const be = err as BusinessError;
          hilog.warn(DOMAIN, TAG,
            'notifyPropertyChanged to %{public}s failed (code=%{public}d): %{public}s',
            clientAddr, be.code, be.message);
        }
      }
    }
  }

  onDataReceived(callback: (data: Uint8Array) => void): void {
    this._dataCallback = callback;
  }

  // ── Lifecycle ────────────────────────────────────────────────────────────

  release(): void {
    if (this._isScanning) {
      scan.stopScan().catch(() => { /* ignore */ });
      scan.off('deviceFound');
      this._isScanning = false;
    }
    if (this._advertisingHandle >= 0) {
      advertising.stopAdvertising(this._advertisingHandle).catch(() => { /* ignore */ });
      this._advertisingHandle = -1;
    }
    if (this._client !== undefined) {
      try { this._client.close(); } catch (_) { /* ignore */ }
      this._client = undefined;
    }
    this.stopServer();
    this._available = false;
    hilog.info(DOMAIN, TAG, 'NearLinkTransportService released');
  }

  // ── Internal helpers ─────────────────────────────────────────────────────

  private assertAvailable(caller: string): void {
    if (!this._available) {
      throw new Error(
        `Aether Teal NearLink is not available on this device (${caller}). ` +
        'Requires HarmonyOS 5.0.1+ with NearLink silicon. Call probeAvailability() first.'
      );
    }
  }
}
