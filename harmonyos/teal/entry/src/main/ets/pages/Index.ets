// SPDX-License-Identifier: MIT

/**
 * Index.ets — Aether Teal main page.
 *
 * Surfaces both transport roles:
 *
 *   PERIPHERAL ROLE (top section)
 *     [START SERVER] — creates SSAP server, registers Aether SLE service,
 *                      begins accepting incoming client connections.
 *     [ADVERTISE]    — broadcasts Aether SLE service UUID so scanning nodes
 *                      can find and connect to us.
 *     Live counter: N clients connected.
 *
 *   CENTRAL ROLE (bottom section)
 *     [SCAN]         — active SLE peer discovery.
 *     Discovered list with [Connect] per peer.
 *     [DISCONNECT]   — closes active outbound client connection.
 *
 *   Hardware absent → IsAvailable = false notice.
 *
 *   Log — live event stream, last 200 lines.
 */

import { common } from '@kit.AbilityKit';
import { NearLinkTransportService } from '../service/NearLinkTransportService';

@Entry
@Component
struct Index {

  // ── State ──────────────────────────────────────────────────────────────

  @State private statusText: string = 'Initialising…';
  @State private isAvailable: boolean = false;

  // Peripheral role
  @State private isServerRunning: boolean = false;
  @State private isAdvertising: boolean = false;
  @State private connectedClientCount: number = 0;

  // Central role
  @State private connectedAddress: string = '';
  @State private discoveredAddresses: string[] = [];

  // Log
  @State private logLines: string[] = [];

  private service: NearLinkTransportService | undefined = undefined;

  // Poll server state (connected-client count changes on the OS thread;
  // we need to pull it into ArkTS @State periodically).
  private pollTimer: number = -1;

  // ── Lifecycle ──────────────────────────────────────────────────────────

  aboutToAppear(): void {
    const ctx = getContext(this) as common.UIAbilityContext;
    this.service = new NearLinkTransportService(ctx);

    this.service.onDataReceived((data: Uint8Array) => {
      this.pushLog(`[Rx] ${data.byteLength} bytes`);
    });

    this.service.probeAvailability().then((available: boolean) => {
      this.isAvailable = available;
      if (available) {
        this.statusText = 'NearLink SLE ready.';
        this.pushLog('[Init] IsAvailable = true — NearLink hardware confirmed');
        // Start polling connected-client count once hardware is available.
        this.pollTimer = setInterval(() => {
          if (this.service !== undefined) {
            const n = this.service.connectedClientCount;
            if (n !== this.connectedClientCount) {
              this.connectedClientCount = n;
            }
            const srv = this.service.isServerRunning;
            if (srv !== this.isServerRunning) {
              this.isServerRunning = srv;
            }
          }
        }, 500);
      } else {
        this.statusText = 'NearLink hardware not present on this device.\nIsAvailable = false';
        this.pushLog('[Init] IsAvailable = false — no NearLink silicon');
      }
    }).catch((err: Error) => {
      this.statusText = `Init failed: ${err.message}`;
      this.pushLog(`[Init] ERROR: ${err.message}`);
    });
  }

  aboutToDisappear(): void {
    if (this.pollTimer >= 0) {
      clearInterval(this.pollTimer);
      this.pollTimer = -1;
    }
    this.service?.release();
  }

  // ── Build ──────────────────────────────────────────────────────────────

  build() {
    Column({ space: 0 }) {

      // ── Header ─────────────────────────────────────────────────────────
      Column({ space: 4 }) {
        Text('Aether Teal')
          .fontSize(28).fontWeight(FontWeight.Bold).width('100%')
        Text('NearLink (SLE) Transport Node')
          .fontSize(14).fontColor('#888888').width('100%')
      }
      .width('100%')
      .padding({ top: 20, left: 20, right: 20, bottom: 12 })

      Divider().strokeWidth(1).color('#E0E0E0').margin({ left: 20, right: 20 })

      // ── Availability badge ──────────────────────────────────────────────
      Row({ space: 8 }) {
        Circle({ width: 12, height: 12 })
          .fill(this.isAvailable ? '#4CAF50' : '#F44336')
        Text(this.isAvailable ? 'IsAvailable = true' : 'IsAvailable = false')
          .fontSize(13)
          .fontColor(this.isAvailable ? '#4CAF50' : '#F44336')
          .fontWeight(FontWeight.Medium)
      }
      .width('100%')
      .padding({ top: 10, left: 20, right: 20, bottom: 4 })

      Text(this.statusText)
        .fontSize(13).fontColor('#555555').width('100%')
        .padding({ left: 20, right: 20, bottom: 10 })

      // ── Main content ────────────────────────────────────────────────────

      if (this.isAvailable) {

        Scroll() {
          Column({ space: 0 }) {

            // ── PERIPHERAL ROLE ───────────────────────────────────────────

            Text('Peripheral Role (server)')
              .fontSize(12).fontColor('#888888').fontWeight(FontWeight.Medium)
              .width('100%')
              .padding({ left: 20, right: 20, top: 8, bottom: 6 })

            // Server + advertising controls
            Row({ space: 10 }) {
              Button(this.isServerRunning ? 'STOP SERVER' : 'START SERVER', { type: ButtonType.Normal })
                .onClick(() => {
                  if (this.isServerRunning) { this.stopServer(); }
                  else { this.startServer(); }
                })
                .layoutWeight(1).height(40)
                .backgroundColor(this.isServerRunning ? '#795548' : '#2196F3')

              Button(this.isAdvertising ? 'STOP ADV' : 'ADVERTISE', { type: ButtonType.Normal })
                .onClick(() => {
                  if (this.isAdvertising) { this.stopAdv(); }
                  else { this.startAdv(); }
                })
                .layoutWeight(1).height(40)
                .backgroundColor(this.isAdvertising ? '#FF9800' : '#009688')
            }
            .width('100%')
            .padding({ left: 20, right: 20, bottom: 8 })

            // Server status badges
            Row({ space: 12 }) {
              if (this.isServerRunning) {
                Row({ space: 6 }) {
                  Circle({ width: 8, height: 8 }).fill('#4CAF50')
                  Text('Server running')
                    .fontSize(12).fontColor('#4CAF50')
                }
                Row({ space: 6 }) {
                  Circle({ width: 8, height: 8 })
                    .fill(this.connectedClientCount > 0 ? '#2196F3' : '#BDBDBD')
                  Text(`${this.connectedClientCount} client${this.connectedClientCount === 1 ? '' : 's'} connected`)
                    .fontSize(12)
                    .fontColor(this.connectedClientCount > 0 ? '#2196F3' : '#AAAAAA')
                }
              }
              if (this.isAdvertising) {
                Row({ space: 6 }) {
                  Circle({ width: 8, height: 8 }).fill('#FF9800')
                  Text('Advertising Aether SLE service')
                    .fontSize(12).fontColor('#FF9800')
                }
              }
            }
            .width('100%')
            .padding({ left: 20, right: 20, bottom: 8 })

            Divider().strokeWidth(1).color('#F0F0F0').margin({ left: 20, right: 20 })

            // ── CENTRAL ROLE ──────────────────────────────────────────────

            Text('Central Role (client)')
              .fontSize(12).fontColor('#888888').fontWeight(FontWeight.Medium)
              .width('100%')
              .padding({ left: 20, right: 20, top: 8, bottom: 6 })

            Row({ space: 10 }) {
              Button('SCAN', { type: ButtonType.Normal })
                .onClick(() => { this.startScan(); })
                .layoutWeight(1).height(40)
                .backgroundColor('#607D8B')

              Button('STOP SCAN', { type: ButtonType.Normal })
                .onClick(() => { this.stopScan(); })
                .layoutWeight(1).height(40)
                .backgroundColor('#78909C')
            }
            .width('100%')
            .padding({ left: 20, right: 20, bottom: 8 })

            if (this.connectedAddress !== '') {
              Row({ space: 8 }) {
                Circle({ width: 8, height: 8 }).fill('#4CAF50')
                Text(`Connected to: ${this.connectedAddress}`)
                  .fontSize(12).fontColor('#4CAF50').layoutWeight(1)
                Button('DISCONNECT', { type: ButtonType.Normal })
                  .onClick(() => { this.disconnect(); })
                  .height(32).fontSize(12)
                  .padding({ left: 12, right: 12 })
                  .backgroundColor('#F44336')
              }
              .width('100%')
              .padding({ left: 20, right: 20, bottom: 8 })
            }

            if (this.discoveredAddresses.length > 0) {
              Text('Discovered peers:')
                .fontSize(12).fontColor('#888888').width('100%')
                .padding({ left: 20, right: 20, bottom: 4 })

              ForEach(this.discoveredAddresses, (addr: string) => {
                Row({ space: 8 }) {
                  Text(addr)
                    .fontSize(12).fontFamily('monospace').fontColor('#212121').layoutWeight(1)
                  Button('Connect', { type: ButtonType.Normal })
                    .onClick(() => { this.connectTo(addr); })
                    .height(32).fontSize(12)
                    .padding({ left: 12, right: 12 })
                    .enabled(this.connectedAddress === '')
                }
                .width('100%')
                .padding({ left: 20, right: 20, top: 3, bottom: 3 })
                .border({ width: { bottom: 1 }, color: '#F5F5F5' })
              }, (addr: string) => addr)
            } else {
              Text('No peers discovered yet.  Tap SCAN to search.')
                .fontSize(12).fontColor('#AAAAAA').width('100%')
                .padding({ left: 20, right: 20, bottom: 8 })
            }
          }
          .width('100%')
        }
        .layoutWeight(1)
        .width('100%')

      } else {

        // ── Hardware-absent notice ──────────────────────────────────────

        Scroll() {
          Column({ space: 8 }) {
            Row({ space: 6 }) {
              Text('⚠').fontSize(18)
              Text('NearLink hardware not available')
                .fontSize(15).fontWeight(FontWeight.Medium).fontColor('#FF9800')
            }.width('100%')

            Text(
              'NearLink (SLE — SparkLink Low-Energy) requires dedicated silicon.\n\n' +
              'Supported devices:\n' +
              '  • Huawei Mate 60 / 70 series\n' +
              '  • Huawei Mate X6, Pocket 2\n' +
              '  • Huawei Pura 70 Pro / Pro+ / Ultra\n' +
              '  • HarmonyOS PC HAD-W32\n\n' +
              'Specification:\n' +
              '  Frequency:   2.4 GHz\n' +
              '  Range:       up to 600 m\n' +
              '  Bandwidth:   12 Mbps  (10× BLE 5.0)\n' +
              '  Latency:     20 µs\n' +
              '  Power:       ~60 % lower than BLE 5.0\n\n' +
              'Both peripheral (server) and central (client) roles are\n' +
              'implemented.  isAvailable = true when NearLink silicon is present.'
            )
              .fontSize(12).fontColor('#757575').width('100%')
          }
          .width('100%')
          .alignItems(HorizontalAlign.Start)
          .padding({ left: 20, right: 20, bottom: 12 })
        }
        .layoutWeight(1)
        .width('100%')

      }

      // ── Log ────────────────────────────────────────────────────────────

      Divider().strokeWidth(1).color('#E0E0E0').margin({ left: 20, right: 20 })

      Text('Log')
        .fontSize(11).fontColor('#AAAAAA').width('100%')
        .padding({ left: 20, right: 20, top: 6, bottom: 2 })

      Scroll() {
        Column({ space: 1 }) {
          ForEach(this.logLines, (line: string) => {
            Text(line)
              .fontSize(11).fontFamily('monospace').fontColor('#37474F').width('100%')
          }, (line: string) => line)
        }
        .width('100%')
        .alignItems(HorizontalAlign.Start)
        .padding({ left: 20, right: 20, top: 4, bottom: 8 })
      }
      .height(160)
      .width('100%')
      .align(Alignment.TopStart)

    }
    .width('100%')
    .height('100%')
    .backgroundColor('#FAFAFA')
  }

  // ── Actions: server ────────────────────────────────────────────────────

  private startServer(): void {
    this.service?.startServer().then(() => {
      this.isServerRunning = true;
      this.pushLog('[Server] SSAP peripheral server started');
    }).catch((err: Error) => {
      this.pushLog(`[Server] ERROR: ${err.message}`);
    });
  }

  private stopServer(): void {
    this.service?.stopServer();
    this.isServerRunning = false;
    this.connectedClientCount = 0;
    this.pushLog('[Server] SSAP server stopped');
  }

  private startAdv(): void {
    this.service?.startAdvertising().then(() => {
      this.isAdvertising = true;
      this.pushLog('[Adv] NearLink advertising started');
    }).catch((err: Error) => {
      this.pushLog(`[Adv] ERROR: ${err.message}`);
    });
  }

  private stopAdv(): void {
    this.service?.stopAdvertising().then(() => {
      this.isAdvertising = false;
      this.pushLog('[Adv] advertising stopped');
    });
  }

  // ── Actions: client ────────────────────────────────────────────────────

  private startScan(): void {
    this.discoveredAddresses = [];
    this.pushLog('[Scan] starting active SLE discovery…');
    this.service?.startDiscovery((addr: string) => {
      if (!this.discoveredAddresses.includes(addr)) {
        this.discoveredAddresses = [...this.discoveredAddresses, addr];
        this.pushLog(`[Scan] peer: ${addr}`);
      }
    }).catch((err: Error) => {
      this.pushLog(`[Scan] ERROR: ${err.message}`);
    });
  }

  private stopScan(): void {
    this.service?.stopDiscovery().then(() => {
      this.pushLog('[Scan] stopped');
    });
  }

  private connectTo(addr: string): void {
    this.pushLog(`[Connect] opening SSAP channel to ${addr}…`);
    this.service?.connectAsync(addr).then(() => {
      this.connectedAddress = addr;
      this.statusText = `Connected (client) → ${addr}`;
      this.pushLog('[Connect] ✓ client channel open');
    }).catch((err: Error) => {
      this.pushLog(`[Connect] ERROR: ${err.message}`);
    });
  }

  private disconnect(): void {
    this.service?.disconnectAsync().then(() => {
      this.pushLog('[Disconnect] client channel closed');
      this.connectedAddress = '';
      this.statusText = 'Disconnected.  SCAN to find peers.';
    });
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private pushLog(line: string): void {
    const tail = this.logLines.length > 199
      ? this.logLines.slice(this.logLines.length - 199)
      : this.logLines;
    this.logLines = [...tail, line];
  }
}
