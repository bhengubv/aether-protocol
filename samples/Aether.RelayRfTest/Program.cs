// SPDX-License-Identifier: MIT
// Aether Purple Relay RF Test
//
// Validates the end-to-end HTTP relay path:
//   1. Starts an in-process relay server on http://localhost:5200.
//   2. Creates two HttpRelayTransportService instances (node-a and node-b).
//   3. node-a sends a test packet → node-b.
//   4. node-b echoes the packet back → node-a.
//   5. Measures round-trip latency and exits 0 on success, 1 on failure.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using AetherMesh.Transport.Windows.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

const string RelayUrl   = "http://localhost:5201"; // use 5201 to avoid clashing with a running server
const string NodeAId    = "relay-test-node-a";
const string NodeBId    = "relay-test-node-b";

// ── 1. Start in-process relay server ─────────────────────────────────────────

var serverBuilder = WebApplication.CreateBuilder(["--urls", RelayUrl]);
serverBuilder.Logging.SetMinimumLevel(LogLevel.Warning);
var server = serverBuilder.Build();

var relayStore = new ConcurrentDictionary<string, Channel<RelayPayload>>(
    StringComparer.OrdinalIgnoreCase);

Channel<RelayPayload> GetCh(string id) =>
    relayStore.GetOrAdd(id, _ => Channel.CreateBounded<RelayPayload>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest }));

server.MapPost("/relay/send", async (RelayPayload msg) =>
{
    if (string.IsNullOrWhiteSpace(msg.To) || string.IsNullOrWhiteSpace(msg.DataB64))
        return Results.BadRequest();
    await GetCh(msg.To).Writer.WriteAsync(msg);
    return Results.Ok(new { queued = true });
});

server.MapGet("/relay/receive/{nodeId}", async (string nodeId, CancellationToken ct) =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
    try
    {
        var msg = await GetCh(nodeId).Reader.ReadAsync(linked.Token);
        return Results.Ok(msg);
    }
    catch (OperationCanceledException) { return Results.NoContent(); }
});

_ = server.RunAsync(); // non-blocking
await Task.Delay(300); // allow Kestrel to bind

using var logFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

// ── 2. Create transport nodes ─────────────────────────────────────────────────

await using var nodeA = new HttpRelayTransportService(
    RelayUrl, NodeAId, logFactory.CreateLogger<HttpRelayTransportService>());
await using var nodeB = new HttpRelayTransportService(
    RelayUrl, NodeBId, logFactory.CreateLogger<HttpRelayTransportService>());

// ── 3. Wire node-b echo handler ───────────────────────────────────────────────

nodeB.DataReceived += async (sender, data) =>
{
    Console.WriteLine($"[node-b] RX {data.Length}B from {sender} — echoing back");
    await nodeB.SendAsync(sender, data);
};

// ── 4. Connect both nodes (starts polling) ────────────────────────────────────

nodeA.Connect();
nodeB.Connect();
await Task.Delay(200); // allow first poll to reach server

// ── 5. Send and measure RTT ───────────────────────────────────────────────────

var rttTcs = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
long startTick = 0L; // set just before SendAsync

nodeA.DataReceived += (sender, data) =>
    rttTcs.TrySetResult(Stopwatch.GetElapsedTime(startTick));

var testPayload = System.Text.Encoding.UTF8.GetBytes(
    $"AETHER-RELAY-PING-{DateTime.UtcNow:O}");

Console.WriteLine($"[node-a] Sending {testPayload.Length}B → {NodeBId}");
startTick = Stopwatch.GetTimestamp();

var sent = await nodeA.SendAsync(NodeBId, testPayload);
if (!sent)
{
    Console.Error.WriteLine("FAIL: SendAsync returned false.");
    await server.StopAsync();
    return 1;
}

// Wait for echo (max 15 s)
using var rttTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
try
{
    var rtt = await rttTcs.Task.WaitAsync(rttTimeout.Token);
    Console.WriteLine($"\n✓ PASS — Relay round-trip verified.  RTT: {rtt.TotalMilliseconds:F1} ms");
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("\n✗ FAIL — Timeout waiting for echo (15 s).");
    await server.StopAsync();
    return 1;
}

await server.StopAsync();
return 0;

// ── Wire types ────────────────────────────────────────────────────────────────

internal sealed record RelayPayload(
    string From    = "",
    string To      = "",
    string DataB64 = "");
