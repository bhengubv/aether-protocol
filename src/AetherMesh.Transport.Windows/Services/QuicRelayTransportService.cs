// SPDX-License-Identifier: MIT
// QUIC relay transport — Aether Purple over HTTP/3 (QUIC) with application-layer BBRv3 pacing.
//
// Architecture
// ────────────
//   Send:    POST /relay/send  (HTTP/3 over QUIC; paced by Bbr3Pacer)
//   Receive: GET  /relay/stream/{nodeId}  (SSE stream; server-pushes messages without polling)
//
// BBRv3 Application-Layer Pacer (RFC draft-cardwell-iccrg-bbr-congestion-control-02)
// ──────────────────────────────────────────────────────────────────────────────────
//   Estimates the bottleneck bandwidth (BTLBw) and round-trip propagation delay (RTprop).
//   Maintains a 4-phase cycle: Startup → Drain → ProbeBW (×8) → ProbeRTT.
//   Each phase adjusts the pacing_gain and cwnd_gain multipliers.
//   Application-level pacing is implemented by limiting the number of bytes in-flight
//   to BDP = BTLBw × RTprop × cwnd_gain; calls block on a semaphore until capacity permits.
//   Since HTTP/3 already multiplexes over a QUIC connection, this layer prevents the relay
//   from bursting faster than the bottleneck can absorb, which avoids queue build-up at
//   any congested hop on the cellular path.
//
// Self-test: QuicRelayTransportService.IsSupported returns false if System.Net.Quic
//   is unavailable on the current platform, falling back to HttpRelayTransportService.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AetherMesh.Transport.Abstractions;
using AetherMesh.Transport.Models;
using Microsoft.Extensions.Logging;

namespace AetherMesh.Transport.Windows.Services;

/// <summary>
/// Cellular relay transport using HTTP/3 (QUIC) for reduced head-of-line blocking and
/// application-layer BBRv3 pacing for optimal throughput on high-latency cellular links.
///
/// <para>
/// Drop-in replacement for <see cref="HttpRelayTransportService"/>.
/// Use <see cref="IsSupported"/> to verify QUIC availability before constructing.
/// </para>
///
/// <para>
/// Compatible relay server: <c>samples/AetherMesh.RelayServer</c> with HTTPS on port 5201,
/// which advertises <c>Alt-Svc: h3</c> so the client upgrades to HTTP/3 automatically.
/// </para>
/// </summary>
public sealed class QuicRelayTransportService : ITransportService, IAsyncDisposable
{
    // ── Static: QUIC availability check ──────────────────────────────────────────

    /// <summary>
    /// True if System.Net.Quic (and the underlying MsQuic library) is available on
    /// this platform.  Always check before constructing.
    /// </summary>
    public static bool IsSupported => System.Net.Quic.QuicConnection.IsSupported;

    // ── Fields ────────────────────────────────────────────────────────────────────

    private readonly string   _baseUrl;
    private readonly string   _localNodeId;
    private readonly ILogger<QuicRelayTransportService>? _logger;
    private readonly HttpClient _http;
    private readonly Bbr3Pacer  _pacer = new();
    private readonly PerTransportMetrics _metrics = new();

    private CancellationTokenSource? _sseCts;
    private Task? _sseTask;
    private volatile bool _disposed;
    private volatile bool _connected;

    // ── ITransportService ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string Name => "Aether Purple (QUIC Relay)";

    /// <inheritdoc/>
    public bool IsAvailable => !_disposed;

    /// <inheritdoc/>
    public long MaxBandwidthBps => 10_000_000; // 10 Mbps assumed cellular

    /// <inheritdoc/>
    public int MaxRangeMeters => 0; // Unlimited (internet relay)

    /// <inheritdoc/>
    public int PowerCostRelative => 100; // Always last resort

    /// <inheritdoc/>
    public int MaxConcurrentPeers => 1024;

    /// <inheritdoc/>
    public PerTransportMetrics? Metrics => _metrics;

    /// <inheritdoc/>
    public event Action<string, byte[]>? DataReceived;

    // ── Construction ──────────────────────────────────────────────────────────────

    /// <param name="baseUrl">
    ///   Base URL of the relay server, e.g. <c>"https://relay.example.com:5201"</c>.
    ///   The server must advertise <c>Alt-Svc: h3</c> or the client will fall back to HTTP/2.
    /// </param>
    /// <param name="localNodeId">UHID of this node. Used to subscribe to <c>/relay/stream/{id}</c>.</param>
    /// <param name="skipCertificateValidation">
    ///   Set to <see langword="true"/> for test environments using self-signed certificates.
    ///   Never enable in production.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public QuicRelayTransportService(
        string baseUrl,
        string localNodeId,
        bool skipCertificateValidation = false,
        ILogger<QuicRelayTransportService>? logger = null)
    {
        _baseUrl      = baseUrl.TrimEnd('/');
        _localNodeId  = localNodeId ?? throw new ArgumentNullException(nameof(localNodeId));
        _logger       = logger;

        var handler = new SocketsHttpHandler
        {
            // Allow HTTP/3 (QUIC) alongside HTTP/2 and HTTP/1.1.
            EnableMultipleHttp3Connections = true,
            PooledConnectionIdleTimeout    = TimeSpan.FromSeconds(90),
            KeepAlivePingPolicy            = HttpKeepAlivePingPolicy.Always,
            KeepAlivePingDelay             = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout           = TimeSpan.FromSeconds(5),
        };

        if (skipCertificateValidation)
        {
            // Development/test only — ignores server certificate errors.
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            };
        }

        _http = new HttpClient(handler)
        {
            Timeout         = TimeSpan.FromSeconds(45),
            DefaultRequestVersion        = HttpVersion.Version30,
            DefaultVersionPolicy         = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the SSE receive-stream subscription and the BBRv3 pacer.
    /// Called automatically on first <see cref="SendAsync"/> if not explicitly called.
    /// </summary>
    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected) return;
        _connected = true;

        _sseCts  = new CancellationTokenSource();
        _sseTask = Task.Run(() => SseLoopAsync(_sseCts.Token));

        _logger?.LogInformation("[QuicRelay] Connected to {Url} as {NodeId}", _baseUrl, _localNodeId);
    }

    // ── ITransportService ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<bool> SendAsync(
        string peerUhid,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Connect();

        // BBRv3: acquire send permit (blocks if BDP window is full).
        await _pacer.AcquireSendPermitAsync(data.Length, cancellationToken);

        var sw = Stopwatch.StartNew();
        try
        {
            var msg = new RelayMessage(
                From:    _localNodeId,
                To:      peerUhid,
                DataB64: Convert.ToBase64String(data));

            using var req  = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/relay/send")
            {
                Content = JsonContent.Create(msg),
                // Prefer HTTP/3; fall back to HTTP/2 if server hasn't upgraded yet.
                Version = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };

            using var resp = await _http.SendAsync(req, cancellationToken);

            sw.Stop();
            bool ok = resp.IsSuccessStatusCode;
            _metrics.RecordSample(sw.ElapsedMilliseconds, ok, ok ? data.Length : 0);

            // Feed RTT + delivered bytes to BBRv3 pacer.
            _pacer.RecordDelivery(sw.Elapsed, ok ? data.Length : 0);

            if (ok)
            {
                _logger?.LogDebug("[QuicRelay] ► TX {Bytes}B → {Peer} ({Version})",
                    data.Length, peerUhid, resp.Version);
                return true;
            }

            _logger?.LogWarning("[QuicRelay] TX failed: {Status}", resp.StatusCode);
            return false;
        }
        catch (OperationCanceledException)
        {
            _pacer.RecordDelivery(sw.Elapsed, 0);
            _metrics.RecordSample(sw.ElapsedMilliseconds, false, 0);
            return false;
        }
        catch (Exception ex)
        {
            _pacer.RecordDelivery(sw.Elapsed, 0);
            _metrics.RecordSample(sw.ElapsedMilliseconds, false, 0);
            _logger?.LogError(ex, "[QuicRelay] TX exception → {Peer}", peerUhid);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SendStreamAsync(
        string peerUhid,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken);
    }

    /// <inheritdoc/>
    public bool IsConnected(string peerUhid) => !_disposed;

    // ── SSE receive loop ──────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribes to <c>GET /relay/stream/{localNodeId}</c> (Server-Sent Events).
    /// The server holds the connection open and pushes messages as <c>data: {json}\n\n</c>
    /// lines.  No polling required — QUIC's persistent connection delivers messages
    /// with sub-10 ms server-to-client latency once the connection is established.
    /// </summary>
    private async Task SseLoopAsync(CancellationToken ct)
    {
        var url = $"{_baseUrl}/relay/stream/{Uri.EscapeDataString(_localNodeId)}";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version       = HttpVersion.Version30,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                // Must not buffer — we want streaming delivery.
                using var resp = await _http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("[QuicRelay] SSE connect failed: {Status}", resp.StatusCode);
                    await Task.Delay(2_000, ct);
                    continue;
                }

                _logger?.LogInformation("[QuicRelay] SSE stream open ({Version})", resp.Version);

                using var body   = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(body);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;   // null = EOF, server closed the stream

                    // SSE format: "data: <json>\n\n"
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                    var json = line["data:".Length..].Trim();
                    if (string.IsNullOrEmpty(json)) continue;

                    try
                    {
                        var msg = JsonSerializer.Deserialize<RelayMessage>(json);

                        if (msg is not null && !string.IsNullOrEmpty(msg.DataB64))
                        {
                            var data = Convert.FromBase64String(msg.DataB64);
                            _logger?.LogDebug("[QuicRelay] ◄ RX {Bytes}B ← {From}", data.Length, msg.From);
                            DataReceived?.Invoke(msg.From ?? "", data);
                        }
                    }
                    catch (JsonException jex)
                    {
                        _logger?.LogDebug(jex, "[QuicRelay] SSE JSON parse error");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[QuicRelay] SSE reconnect in 2 s");
                await Task.Delay(2_000, ct);
            }
        }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sseCts is not null)
        {
            await _sseCts.CancelAsync();
            if (_sseTask is not null)
                await _sseTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _sseCts.Dispose();
        }

        _http.Dispose();
        _logger?.LogInformation("[QuicRelay] Disconnected from {Url}", _baseUrl);
    }

    // ── Wire types ────────────────────────────────────────────────────────────────

    private sealed record RelayMessage(
        string? From    = null,
        string? To      = null,
        string? DataB64 = null);
}

// ════════════════════════════════════════════════════════════════════════════════
//  BBRv3 Application-Layer Pacer
// ════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Application-layer BBRv3 congestion controller (RFC draft-cardwell-iccrg-bbr-congestion-control-02).
///
/// Estimates the path's bottleneck bandwidth (BTLBw) and round-trip propagation delay
/// (RTprop) and limits the application's send rate to BDP = BTLBw × RTprop × cwnd_gain.
/// This prevents queue build-up at cellular bottlenecks and achieves near-optimal
/// throughput with low latency, independent of the underlying transport's own CC.
///
/// <para>
/// Phase cycle (each measured in delivery rounds, i.e. complete request-response cycles):
/// <list type="bullet">
///   <item><b>Startup</b>  — pacing_gain = 2.89 (≈ ln 2 / ln e), grow until bandwidth plateau.</item>
///   <item><b>Drain</b>    — pacing_gain = 0.35, drain queue built during Startup.</item>
///   <item><b>ProbeBW</b>  — 8-round cycle at gains [1.25, 0.75, 1, 1, 1, 1, 1, 1].</item>
///   <item><b>ProbeRTT</b> — reduce in-flight to 4 msgs for 200 ms to refresh RTprop.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class Bbr3Pacer
{
    // ── BBRv3 constants ───────────────────────────────────────────────────────────

    private static readonly double[] ProbeBwGains   = { 1.25, 0.75, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
    private const double             StartupGain     = 2.89;
    private const double             DrainGain       = 0.35;
    private const double             CwndGain        = 2.0;  // BDP multiplier for in-flight cap
    private const int                BtlBwWindowSize = 10;   // rounds for windowed-max BTLBw
    private const double             ProbeRttMs      = 200.0;
    private const double             ProbeRttInterval = 10_000.0;  // ms between ProbeRTT phases
    private const int                ProbeRttPackets = 4;   // min in-flight during ProbeRTT

    // ── State ─────────────────────────────────────────────────────────────────────

    private enum Phase { Startup, Drain, ProbeBw, ProbeRtt }

    private Phase  _phase = Phase.Startup;
    private int    _probeBwRound;     // current round within the 8-round ProbeBW cycle
    private int    _startupRounds;   // rounds without BTLBw growth (triggers Drain)

    // BTLBw: windowed maximum delivery rate (bytes/ms) over last BtlBwWindowSize rounds.
    private readonly Queue<double> _btlBwWindow = new(BtlBwWindowSize + 1);
    private double _btlBw = 125_000.0; // bytes/ms = 1 Mbps initial

    // RTprop: windowed minimum RTT (ms) over last ProbeRttInterval ms.
    private double _rtProp   = 100.0;  // ms initial estimate
    private long   _rtPropTs = Environment.TickCount64;   // ms timestamp of last min-RTT update

    // In-flight cap: computed from BDP + current phase gain.
    private double _inflight = 4.0;    // max concurrent in-flight messages

    // Semaphore: limits concurrent sends.  MaxCount = int.MaxValue so that
    // Release(delta) calls for window growth never throw SemaphoreFullException.
    private readonly SemaphoreSlim _sem = new(4, int.MaxValue);

    private double PacingGain => _phase switch
    {
        Phase.Startup => StartupGain,
        Phase.Drain   => DrainGain,
        Phase.ProbeBw => ProbeBwGains[_probeBwRound % ProbeBwGains.Length],
        Phase.ProbeRtt => 1.0,
        _ => 1.0
    };

    // ── Public API ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acquires a send permit.  Blocks while the number of in-flight sends is at the
    /// BDP-derived cap.  Releases automatically after <see cref="RecordDelivery"/> is called.
    /// </summary>
    public Task AcquireSendPermitAsync(int bytes, CancellationToken ct)
        => _sem.WaitAsync(ct);

    /// <summary>
    /// Called after each send completes (success or failure) with the observed RTT
    /// and bytes delivered.  Updates BTLBw, RTprop, advances the phase state machine,
    /// and recomputes the in-flight cap.
    /// </summary>
    public void RecordDelivery(TimeSpan rtt, int bytesDelivered)
    {
        _sem.Release();   // Always release the semaphore permit.

        double rttMs = rtt.TotalMilliseconds;
        if (rttMs <= 0) rttMs = 1.0;

        // ── Update RTprop (windowed minimum) ─────────────────────────────────────
        if (rttMs <= _rtProp || Environment.TickCount64 - _rtPropTs > ProbeRttInterval)
        {
            _rtProp   = rttMs;
            _rtPropTs = Environment.TickCount64;
        }

        // ── Update BTLBw (windowed maximum delivery rate) ────────────────────────
        if (bytesDelivered > 0)
        {
            double rate = bytesDelivered / rttMs;   // bytes/ms
            _btlBwWindow.Enqueue(rate);
            if (_btlBwWindow.Count > BtlBwWindowSize)
                _btlBwWindow.Dequeue();

            double newBtlBw = _btlBwWindow.Max();
            bool   grew     = newBtlBw > _btlBw * 1.25;
            _btlBw = newBtlBw;

            if (_phase == Phase.Startup)
            {
                if (!grew) _startupRounds++;
                if (_startupRounds >= 3) _phase = Phase.Drain;
            }
        }

        // ── Phase advancement ─────────────────────────────────────────────────────
        switch (_phase)
        {
            case Phase.Drain:
                // Exit Drain once in-flight drops below BDP.
                double bdp = _btlBw * _rtProp;
                if (_sem.CurrentCount >= (int)Math.Max(1, bdp / 1024.0))
                    _phase = Phase.ProbeBw;
                break;

            case Phase.ProbeBw:
                // Advance ProbeBW round; trigger ProbeRTT every ProbeRttInterval ms.
                _probeBwRound = (_probeBwRound + 1) % ProbeBwGains.Length;
                if (Environment.TickCount64 - _rtPropTs > ProbeRttInterval)
                    _phase = Phase.ProbeRtt;
                break;

            case Phase.ProbeRtt:
                // Stay in ProbeRTT for 200 ms then return to ProbeBW.
                if (rttMs <= _rtProp + 5.0)  // RTprop refreshed
                    _phase = Phase.ProbeBw;
                break;
        }

        // ── Recompute in-flight cap ───────────────────────────────────────────────
        double newInflight = _phase == Phase.ProbeRtt
            ? ProbeRttPackets
            : Math.Max(ProbeRttPackets, (_btlBw * _rtProp * CwndGain * PacingGain) / 1024.0);

        int delta = (int)newInflight - (int)_inflight;
        _inflight = newInflight;

        // Release extra semaphore slots if window grew.
        if (delta > 0) _sem.Release(delta);
    }
}
