// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AetherNet.Transport.Relay;

/// <summary>
/// The other half of <see cref="HttpRelayTransportService"/> — the bit that was never written.
///
/// <para>
/// The client half takes a <c>baseUrl</c>, which made the relay look like it needed somebody else's
/// infrastructure: a server, somewhere, owned by whoever runs it. That reading is what would have put
/// a central API in the middle of a network whose entire point is not having one.
/// </para>
///
/// <para>
/// A phone is a small server. This is what it runs when it puts its hand up as a proxy: two endpoints
/// and a queue per node, holding messages for the phones around it that have no data of their own.
/// The <c>baseUrl</c> the others point at is simply that phone's address. Nobody's infrastructure,
/// no account, no operator — a peer that volunteered, and which stops being one the moment it stops.
/// </para>
///
/// <para>
/// It stores and forwards; it does not read. Everything in the queue arrived sealed in the sender's
/// session and leaves the same way, so a proxy learns who is talking to whom and nothing whatsoever
/// about what they said.
/// </para>
/// </summary>
public sealed class RelayServer : IAsyncDisposable
{
    /// <summary>
    /// How long a poll is held open before answering "nothing yet". Long enough that an idle phone is
    /// not making a request every second; short enough that a phone which has walked away is noticed.
    /// The client half already treats 204 as "ask again immediately", so this is the real poll rate.
    /// </summary>
    private static readonly TimeSpan PollHold = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Messages held for one node before the oldest is dropped.
    ///
    /// <para>
    /// A queue that grows without limit is a way to run a proxy phone out of memory from the outside:
    /// address enough traffic at a node that never polls, and the phone doing everyone a favour is the
    /// one that falls over. Dropping the oldest keeps the newest, which is the half a returning phone
    /// actually wants.
    /// </para>
    /// </summary>
    private const int QueueDepth = 256;

    private readonly ConcurrentDictionary<string, ConcurrentQueue<RelayMessage>> _queues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _waiters = new(StringComparer.Ordinal);
    private readonly HttpListener _listener = new();
    private readonly ILogger<RelayServer>? _logger;
    private readonly CancellationTokenSource _stopping = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Task? _loop;
    private bool _disposed;

    /// <param name="port">The port to listen on. The phones around this one are told to use it.</param>
    /// <param name="logger">Optional logger.</param>
    public RelayServer(int port = 5200, ILogger<RelayServer>? logger = null)
    {
        Port = port;
        _logger = logger;
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    /// <summary>The port this proxy answers on.</summary>
    public int Port { get; }

    /// <summary>Whether the proxy is currently accepting traffic for other phones.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>How many phones currently have something waiting here.</summary>
    public int QueuedNodes => _queues.Count(q => !q.Value.IsEmpty);

    /// <summary>
    /// Start carrying traffic for other phones.
    /// </summary>
    /// <remarks>
    /// Binding to <c>http://+</c> needs no permission on Android for a high port, but it can still be
    /// refused — a port already taken, or a platform that will not hand out the prefix. That is
    /// reported rather than thrown, because a phone that cannot be a proxy is a normal phone, not a
    /// broken one.
    /// </remarks>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return true;

        try
        {
            _listener.Start();
        }
        catch (Exception ex) when (ex is HttpListenerException or PlatformNotSupportedException)
        {
            _logger?.LogWarning(ex, "[Relay] This phone cannot open port {Port}, so it will not proxy", Port);
            return false;
        }

        IsRunning = true;
        _loop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
        _logger?.LogInformation("[Relay] Proxying on port {Port}", Port);
        return true;
    }

    /// <summary>Stop carrying traffic. Anything still queued is dropped — it was never ours to keep.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        await _stopping.CancelAsync().ConfigureAwait(false);
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        _queues.Clear();
        foreach (var w in _waiters.Values) w.Dispose();
        _waiters.Clear();
    }

    // ── Serving ───────────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening) { break; }
            catch (Exception ex) { _logger?.LogDebug(ex, "[Relay] accept failed"); continue; }

            // One slow poller must not hold up every other phone's traffic, so each request is served
            // on its own — a receive can be parked for ten seconds by design.
            _ = Task.Run(() => ServeAsync(context, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;

            if (context.Request.HttpMethod == "POST" && path.Equals("/relay/send", StringComparison.OrdinalIgnoreCase))
                await SendAsync(context).ConfigureAwait(false);
            else if (context.Request.HttpMethod == "GET" && path.StartsWith("/relay/receive/", StringComparison.OrdinalIgnoreCase))
                await ReceiveAsync(context, Uri.UnescapeDataString(path["/relay/receive/".Length..]), ct).ConfigureAwait(false);
            else
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Relay] request failed");
            try { context.Response.StatusCode = (int)HttpStatusCode.InternalServerError; } catch { /* gone */ }
        }
        finally
        {
            try { context.Response.Close(); } catch { /* the caller walked away */ }
        }
    }

    /// <summary>Take a message for somebody and hold it until they ask.</summary>
    private async Task SendAsync(HttpListenerContext context)
    {
        RelayMessage? message;
        try
        {
            message = await JsonSerializer
                .DeserializeAsync<RelayMessage>(context.Request.InputStream, Json)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        if (message is null || string.IsNullOrEmpty(message.To) || string.IsNullOrEmpty(message.DataB64))
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        var queue = _queues.GetOrAdd(message.To, _ => new ConcurrentQueue<RelayMessage>());
        queue.Enqueue(message);
        while (queue.Count > QueueDepth) queue.TryDequeue(out _);

        // Wake whoever is parked on a poll for this node, so a message crosses in milliseconds rather
        // than waiting out the hold.
        if (_waiters.TryGetValue(message.To, out var waiter))
        {
            try { waiter.Release(); } catch (SemaphoreFullException) { /* already awake */ }
            catch (ObjectDisposedException) { /* stopped */ }
        }

        context.Response.StatusCode = (int)HttpStatusCode.Accepted;
    }

    /// <summary>Hand a phone whatever is waiting for it, or hold the line until something is.</summary>
    private async Task ReceiveAsync(HttpListenerContext context, string nodeId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(nodeId))
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        var queue = _queues.GetOrAdd(nodeId, _ => new ConcurrentQueue<RelayMessage>());

        if (!queue.TryDequeue(out var message))
        {
            var waiter = _waiters.GetOrAdd(nodeId, _ => new SemaphoreSlim(0));
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(PollHold);
                await waiter.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* nothing came — answer empty */ }
            catch (ObjectDisposedException) { /* stopped */ }

            queue.TryDequeue(out message);
        }

        if (message is null)
        {
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
        try { _listener.Close(); } catch { /* already closed */ }
    }

    /// <summary>
    /// The envelope both halves agree on. Deliberately the same shape the client half already posts —
    /// a relay that invented its own would be a relay nothing could talk to.
    /// </summary>
    internal sealed class RelayMessage
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string DataB64 { get; set; } = "";
    }
}
