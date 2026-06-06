// SPDX-License-Identifier: MIT
// Aether Protocol — RF bring-up test
// Sends one Aether wire-format packet over real BLE GATT to the Android
// ble-node app and verifies the echo response.  Closes OPEN_ISSUES.md item 8.

using System.Security.Cryptography;
using System.Text;
using AetherMesh.Protocol;
using AetherMesh.Transport;
using AetherMesh.Transport.Windows.Services;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = Encoding.UTF8;

using var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(LogLevel.Debug));

var log = loggerFactory.CreateLogger("BleRfTest");

// ── 1. Build a DATA packet ────────────────────────────────────────────────────

var localUhid = "aether:win-rf-test:01";

var packet = new MeshPacket
{
    ProtocolVersion = 2,
    Type            = PacketType.Data,
    Priority        = 1,
    Ttl             = 7,
    SourceUhid      = localUhid,
    DestinationUhid = "aether:android:ble-node",
    Payload         = Encoding.UTF8.GetBytes("Hello from Windows via BLE!"),
    PacketNonce     = RandomNumberGenerator.GetBytes(8),
};

var wireBytes = PacketSerializer.Serialize(packet);

log.LogInformation("UHID    : {Uhid}", localUhid);
log.LogInformation("Packet  : {Type}  {PayloadLen}B payload  → {WireLen}B on wire",
    packet.Type, packet.Payload.Length, wireBytes.Length);
log.LogInformation("");

// ── 2. Start BLE transport + scan ─────────────────────────────────────────────

var rxTcs    = new TaskCompletionSource<(string uhid, byte[] data)>();
var phoneUhid = "";

await using var ble = new WinBleGattTransportService(localUhid,
    loggerFactory.CreateLogger<WinBleGattTransportService>());

ble.DataReceived += (peerUhid, data) =>
{
    log.LogInformation("◄ RX {Bytes}B from {Uhid}", data.Length, peerUhid);
    rxTcs.TrySetResult((peerUhid, data));
};

ble.AdvertisementReceived += adv =>
{
    if (string.IsNullOrEmpty(phoneUhid))
    {
        phoneUhid = adv.SourceUhid;
        log.LogInformation("Aether peripheral found: {Uhid}  RSSI={Rssi} dBm",
            adv.SourceUhid, adv.Rssi);
    }
};

log.LogInformation("Scanning for Aether BLE peripheral (service UUID {Uuid})...",
    BleGattConstants.ServiceUuid);
log.LogInformation("Start the Android ble-node app and tap START.");
log.LogInformation("");

ble.StartScanning();

// ── 3. Wait for peer, then send ───────────────────────────────────────────────

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

while (!cts.IsCancellationRequested)
{
    if (!string.IsNullOrEmpty(phoneUhid) && ble.IsConnected(phoneUhid)) break;
    await Task.Delay(500, CancellationToken.None);
}

if (cts.IsCancellationRequested)
{
    log.LogError("Timeout — no Aether peripheral within 60 s. Exiting.");
    Environment.Exit(1);
}

log.LogInformation("Connected to {Uhid}. Sending {Bytes}B packet...", phoneUhid, wireBytes.Length);

var sent = await ble.SendAsync(phoneUhid, wireBytes);
log.LogInformation("► TX {Result} ({Bytes}B)", sent ? "OK" : "FAILED", wireBytes.Length);

// ── 4. Wait for echo response ─────────────────────────────────────────────────

log.LogInformation("Waiting for echo response (15 s timeout)...");

var echoTask    = rxTcs.Task;
var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));

if (await Task.WhenAny(echoTask, timeoutTask) == timeoutTask)
{
    log.LogError("No response within 15 s. Exiting.");
    Environment.Exit(2);
}

var (_, echoBytes) = await echoTask;

// ── 5. Verify ─────────────────────────────────────────────────────────────────

// Wire format starts with: [1 byte] protocol_ver (0x02), [1 byte] packet_type (0x03)
bool headerOk = echoBytes.Length >= 2
             && echoBytes[0] == 0x02  // ProtocolVersion = 2
             && echoBytes[1] == 0x03; // PacketType.Data = 3

// TTL was decremented by the Android node (7 → 6), so sizes should still match.
bool sizeOk = echoBytes.Length == wireBytes.Length;

Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("══════════════════════════════════════════");
Console.WriteLine("  AETHER RF BRING-UP RESULT");
Console.WriteLine("══════════════════════════════════════════");
Console.ResetColor();
Console.WriteLine($"  TX bytes      : {wireBytes.Length}");
Console.WriteLine($"  RX bytes      : {echoBytes.Length}");
Console.WriteLine($"  Ver=2 / Type=3: {(headerOk ? "✓" : $"✗ (got {echoBytes[0]:X2}{(echoBytes.Length > 1 ? echoBytes[1].ToString("X2") : "??")})")}");
Console.WriteLine($"  Size match    : {(sizeOk ? "✓" : "✗")}");

bool pass = headerOk;
Console.ForegroundColor = pass ? ConsoleColor.Green : ConsoleColor.Red;
Console.WriteLine(pass
    ? "\n  ✅  RF BRING-UP PASSED — Aether packet crossed real BLE!"
    : "\n  ❌  RF BRING-UP FAILED — unexpected response format");
Console.ResetColor();
Console.WriteLine();

Environment.Exit(pass ? 0 : 3);
