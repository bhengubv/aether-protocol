// SPDX-License-Identifier: MIT

using System.Net.Http.Json;
using AetherNet.Transport.Abstractions;
using Microsoft.Extensions.Logging;

namespace AetherNet.Transport.Windows.Services;

/// <summary>
/// HTTP-based relay transport (Aether Purple) — cellular / internet fallback.
///
/// Sends packets via <c>POST /relay/send</c> and polls for inbound packets via
/// <c>GET /relay/receive/{nodeId}</c> (long-poll, 10 s server-side timeout).
/// <see cref="IsAvailable"/> is <see langword="true"/> whenever the service is not disposed.
///
/// <para>
/// This is the transport of last resort — always available when internet is reachable,
/// but with the highest <see cref="PowerCostRelative"/> (100) so it is only selected after
/// NearLink, BLE, Wi-Fi Direct, CircleLink, and NFC have all failed.
/// </para>
///
/// <para>
/// Compatible relay server: <c>samples/AetherNet.RelayServer</c> (ASP.NET Core minimal API,
/// default port 5200). Pass the base URL when constructing this service.
/// </para>
/// </summary>
public sealed class HttpRelayTransportService : ITransportService, IAsyncDisposable
{
    private readonly string _baseUrl;
    private readonly string _localNodeId;
    private readonly ILogger<HttpRelayTransportService>? _logger;
    private readonly HttpClient _http;

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private volatile bool _disposed;
    private volatile bool _connected;

    // ── ITransportService ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public string Name => "Aether Purple (HTTP Relay)";

    /// <inheritdoc />
    public bool IsAvailable => !_disposed;

    /// <inheritdoc />
    public long MaxBandwidthBps => 10_000_000; // 10 Mbps assumed cellular

    /// <inheritdoc />
    public int MaxRangeMeters => 0; // Unlimited (internet)

    /// <inheritdoc />
    public int PowerCostRelative => 100; // Always last resort

    /// <inheritdoc />
    public int MaxConcurrentPeers => 1024;

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <param name="baseUrl">
    ///   Base URL of the relay server, e.g. <c>"http://localhost:5200"</c>.
    /// </param>
    /// <param name="localNodeId">
    ///   UHID of this node. Used to poll <c>/relay/receive/{id}</c>.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public HttpRelayTransportService(
        string baseUrl,
        string localNodeId,
        ILogger<HttpRelayTransportService>? logger = null)
    {
        _baseUrl     = baseUrl.TrimEnd('/');
        _localNodeId = localNodeId ?? throw new ArgumentNullException(nameof(localNodeId));
        _logger      = logger;
        _http        = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Starts the background receive-polling loop.</summary>
    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected) return;

        _connected = true;
        _pollCts   = new CancellationTokenSource();
        _pollTask  = Task.Run(() => PollLoopAsync(_pollCts.Token));

        _logger?.LogInformation("[Relay] Connected to {Url} as node {NodeId}", _baseUrl, _localNodeId);
    }

    // ── ITransportService ─────────────────────────────────────────────────────

    /// <summary>
    /// Sends <paramref name="data"/> to <paramref name="peerUhid"/> via
    /// <c>POST /relay/send</c>. Auto-starts the polling loop on first call.
    /// </summary>
    public async Task<bool> SendAsync(
        string peerUhid,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Connect(); // auto-start if not started

        try
        {
            var payload = new RelayMessage
            {
                From    = _localNodeId,
                To      = peerUhid,
                DataB64 = Convert.ToBase64String(data)
            };

            using var response = await _http.PostAsJsonAsync(
                $"{_baseUrl}/relay/send", payload, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("[Relay] ► TX {Bytes}B → {Peer}", data.Length, peerUhid);
                return true;
            }

            _logger?.LogWarning("[Relay] TX failed: {Status}", response.StatusCode);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Relay] TX exception → {Peer}", peerUhid);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendStreamAsync(
        string peerUhid,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see langword="true"/> while the transport is not disposed.
    /// The relay can forward to any node reachable by the server.
    /// </remarks>
    public bool IsConnected(string peerUhid) => !_disposed;

    // ── Poll loop ─────────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var pollUrl = $"{_baseUrl}/relay/receive/{Uri.EscapeDataString(_localNodeId)}";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync(pollUrl, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    var msg = await response.Content
                        .ReadFromJsonAsync<RelayMessage>(cancellationToken: ct);

                    if (msg is not null && !string.IsNullOrEmpty(msg.DataB64))
                    {
                        var data = Convert.FromBase64String(msg.DataB64);
                        _logger?.LogDebug("[Relay] ◄ RX {Bytes}B ← {Sender}", data.Length, msg.From);
                        DataReceived?.Invoke(msg.From, data);
                    }
                }
                else if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
                {
                    await Task.Delay(500, ct);
                }
                // NoContent (204) — queue empty, server already long-polled; no delay needed
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[Relay] Poll error; retrying in 1 s");
                await Task.Delay(1_000, ct);
            }
        }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_pollCts is not null)
        {
            await _pollCts.CancelAsync();

            if (_pollTask is not null)
                await _pollTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            _pollCts.Dispose();
        }

        _http.Dispose();
        _logger?.LogInformation("[Relay] Disconnected from {Url}", _baseUrl);
    }

    // ── Wire types ────────────────────────────────────────────────────────────

    private sealed class RelayMessage
    {
        public string From    { get; set; } = "";
        public string To      { get; set; } = "";
        public string DataB64 { get; set; } = "";
    }
}
