// SPDX-License-Identifier: MIT
// Aether Green Wi-Fi Direct RF Bring-Up Test
//
// Usage:
//   1. Run this program on Windows — it advertises as a Wi-Fi Direct Group Owner.
//   2. Launch the aether.green Android app on a nearby device.
//   3. The Android app connects, the programs exchange an Aether packet + echo.
//   4. This console prints the round-trip latency and exits 0 on success.
//
// Prerequisites:
//   - Wi-Fi Direct capable Wi-Fi adapter (most laptops manufactured after 2014)
//   - aether.green APK installed on the Android device

using System.Diagnostics;
using System.Text;
using AetherMesh.Transport.Windows.Services;
using Microsoft.Extensions.Logging;

const string LocalNodeId = "wfd-rf-test-windows";
const int    TimeoutSec  = 60;

using var logFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(LogLevel.Information));

var logger = logFactory.CreateLogger("WifiDirectRfTest");

await using var wfd = new WinWifiDirectTransportService(
    LocalNodeId, logFactory.CreateLogger<WinWifiDirectTransportService>());

if (!wfd.IsAvailable)
{
    logger.LogError("Wi-Fi Direct is not available on this machine. " +
                    "Ensure you have a Wi-Fi Direct capable adapter and Wi-Fi is enabled.");
    return 1;
}

// ── Advertise ─────────────────────────────────────────────────────────────────

logger.LogInformation("Starting Wi-Fi Direct advertisement as '{NodeId}'...", LocalNodeId);
wfd.StartAdvertising();
logger.LogInformation("Advertising — launch aether.green on your Android device.");

// ── Wait for peer connection ──────────────────────────────────────────────────

var peerConnectedTcs = new TaskCompletionSource<string>(
    TaskCreationOptions.RunContinuationsAsynchronously);
wfd.PeerConnected += uhid => peerConnectedTcs.TrySetResult(uhid);

using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSec));
string peerUhid;
try
{
    peerUhid = await peerConnectedTcs.Task.WaitAsync(connectTimeout.Token);
    logger.LogInformation("Peer connected: {Peer}", peerUhid);
}
catch (OperationCanceledException)
{
    logger.LogError("FAIL — No Android peer connected within {Sec} seconds.", TimeoutSec);
    return 1;
}

// ── Send test packet ──────────────────────────────────────────────────────────

var rttTcs = new TaskCompletionSource<TimeSpan>(
    TaskCreationOptions.RunContinuationsAsynchronously);
long startTick = 0;

wfd.DataReceived += (sender, data) =>
{
    var elapsed = Stopwatch.GetElapsedTime(startTick);
    logger.LogInformation("◄ Echo received from {Sender}: {Bytes}B", sender, data.Length);
    rttTcs.TrySetResult(elapsed);
};

var payload = Encoding.UTF8.GetBytes($"AETHER-GREEN-PING-{DateTime.UtcNow:O}");
logger.LogInformation("► Sending {Bytes}B to {Peer}...", payload.Length, peerUhid);

startTick = Stopwatch.GetTimestamp();
var sent = await wfd.SendAsync(peerUhid, payload);
if (!sent)
{
    logger.LogError("FAIL — SendAsync returned false.");
    return 1;
}

// ── Wait for echo ─────────────────────────────────────────────────────────────

using var echoTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    var rtt = await rttTcs.Task.WaitAsync(echoTimeout.Token);
    logger.LogInformation("\n✓ PASS — Wi-Fi Direct round-trip verified.  RTT: {Ms:F1} ms",
        rtt.TotalMilliseconds);
}
catch (OperationCanceledException)
{
    logger.LogError("FAIL — Timeout waiting for echo (30 s).");
    return 1;
}

return 0;
