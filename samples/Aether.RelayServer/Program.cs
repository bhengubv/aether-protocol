// SPDX-License-Identifier: MIT
// Aether Purple Relay Server
//
// In-memory HTTP relay for the cellular fallback transport.
//
//   POST /relay/send              body: { "From":"…", "To":"…", "DataB64":"…" }
//                                 → 200 OK { queued: true }
//
//   GET  /relay/receive/{nodeId}  long-poll up to 10 s
//                                 → 200 OK (RelayMessage JSON) if a message arrives
//                                 → 204 No Content if the 10-second server timeout fires
//
// Default bind: http://0.0.0.0:5200 (configure via --urls or ASPNETCORE_URLS).
// No authentication — test infrastructure only.

using System.Collections.Concurrent;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5200");
builder.Logging.SetMinimumLevel(LogLevel.Warning); // keep output quiet during RF tests

var app = builder.Build();

// ── In-memory relay store ─────────────────────────────────────────────────────
// Each node ID maps to a bounded Channel (capacity 1 024, oldest dropped when full).
var relay = new ConcurrentDictionary<string, Channel<RelayMessage>>(
    StringComparer.OrdinalIgnoreCase);

Channel<RelayMessage> GetChannel(string nodeId) =>
    relay.GetOrAdd(nodeId, _ => Channel.CreateBounded<RelayMessage>(
        new BoundedChannelOptions(1024)
        {
            FullMode       = BoundedChannelFullMode.DropOldest,
            SingleReader   = false,
            SingleWriter   = false,
        }));

// ── POST /relay/send ──────────────────────────────────────────────────────────

app.MapPost("/relay/send", async (RelayMessage msg) =>
{
    if (string.IsNullOrWhiteSpace(msg.To) || string.IsNullOrWhiteSpace(msg.DataB64))
        return Results.BadRequest(new { error = "Missing 'To' or 'DataB64'" });

    await GetChannel(msg.To).Writer.WriteAsync(msg);
    return Results.Ok(new { queued = true, to = msg.To });
});

// ── GET /relay/receive/{nodeId} ───────────────────────────────────────────────

app.MapGet("/relay/receive/{nodeId}", async (string nodeId, CancellationToken clientCt) =>
{
    // Long-poll: hold the connection open for up to 10 seconds waiting for a message.
    using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
        clientCt, serverTimeout.Token);

    try
    {
        var msg = await GetChannel(nodeId).Reader.ReadAsync(linked.Token);
        return Results.Ok(msg);
    }
    catch (OperationCanceledException)
    {
        // Either the 10-second server timeout fired, or the client disconnected.
        // Either way, return 204 so the client knows to poll again.
        return Results.NoContent();
    }
});

app.Run();

// ── Wire types ────────────────────────────────────────────────────────────────

/// <summary>Message envelope exchanged between relay client and server.</summary>
public sealed record RelayMessage(
    string From    = "",
    string To      = "",
    string DataB64 = "");
