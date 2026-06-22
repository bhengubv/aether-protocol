// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using AetherNet.Transport.Abstractions;
using AetherNet.Transport.Models;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace AetherNet.Transport.WebRtc;

/// <summary>
/// Direct peer-to-peer transport over a WebRTC <c>RTCDataChannel</c> (SIPSorcery, pure C#).
///
/// <para>NAT traversal is handled by ICE/STUN, with WebRTC's own TURN as last resort. The initial
/// SDP/ICE handshake is carried by an injected <see cref="IWebRtcSignaling"/> channel (e.g. the
/// AetherNet relay), so no central signalling server is required. Implements
/// <see cref="ITransportService"/> so <c>TransportManager</c> ranks it between the radio mesh
/// (cheap, proximity) and the QUIC/HTTP relay (last resort) — a direct internet path is used when
/// one can be negotiated, otherwise the relay carries the traffic.</para>
/// </summary>
public sealed class WebRtcTransportService : ITransportService, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private static readonly RTCIceServer[] DefaultIceServers =
    {
        new() { urls = "stun:stun.l.google.com:19302" },
    };

    private readonly string _localUhid;
    private readonly IWebRtcSignaling _signaling;
    private readonly List<RTCIceServer> _iceServers;
    private readonly ILogger<WebRtcTransportService>? _logger;
    private readonly PerTransportMetrics _metrics = new();
    private readonly ConcurrentDictionary<string, WebRtcPeerLink> _peers = new();
    private volatile bool _disposed;

    public WebRtcTransportService(
        string localUhid,
        IWebRtcSignaling signaling,
        IReadOnlyList<RTCIceServer>? iceServers = null,
        ILogger<WebRtcTransportService>? logger = null)
    {
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        _logger = logger;
        // null => the STUN default; an explicit (even empty) list is respected verbatim, so a
        // caller can pass an empty list to force host-candidate-only ICE (e.g. same-LAN / tests).
        _iceServers = (iceServers ?? DefaultIceServers).ToList();
        _signaling.SignalReceived += OnSignalReceived;
    }

    // ── ITransportService ──────────────────────────────────────────────
    public string Name => "WebRTC P2P";
    public bool IsAvailable => !_disposed;
    public long MaxBandwidthBps => 100_000_000;   // direct link — bounded by the local NIC
    public int MaxRangeMeters => 0;               // internet — unlimited
    public int PowerCostRelative => 45;           // between radio mesh (low) and relay (100)
    public int MaxConcurrentPeers => 256;
    public PerTransportMetrics? Metrics => _metrics;

    public event Action<string, byte[]>? DataReceived;

    public async Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrEmpty(peerUhid)) return false;

        var link = await GetOrCreateLinkAsync(peerUhid, asInitiator: true, cancellationToken).ConfigureAwait(false);
        if (link is null) return false;

        var sw = Stopwatch.StartNew();
        var ok = await link.SendAsync(data, ConnectTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        _metrics.RecordSample(sw.ElapsedMilliseconds, ok, ok ? data.Length : 0);
        return ok;
    }

    public async Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public bool IsConnected(string peerUhid) =>
        _peers.TryGetValue(peerUhid, out var link) && link.IsOpen;

    // ── Signalling inbound ─────────────────────────────────────────────
    private async void OnSignalReceived(WebRtcSignal signal)
    {
        if (_disposed || signal.ToUhid != _localUhid) return;
        try
        {
            switch (signal.Type)
            {
                case WebRtcSignalType.Offer:
                {
                    var link = await GetOrCreateLinkAsync(signal.FromUhid, asInitiator: false, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (link is not null && signal.Sdp is not null)
                        await link.AcceptOfferAsync(signal.Sdp).ConfigureAwait(false);
                    break;
                }
                case WebRtcSignalType.Answer:
                    if (signal.Sdp is not null && _peers.TryGetValue(signal.FromUhid, out var answered))
                        answered.AcceptAnswer(signal.Sdp);
                    break;
                case WebRtcSignalType.IceCandidate:
                    if (_peers.TryGetValue(signal.FromUhid, out var cand))
                        cand.AddRemoteCandidate(signal);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[WebRTC] signal handling failed from {Peer}", signal.FromUhid);
        }
    }

    private async Task<WebRtcPeerLink?> GetOrCreateLinkAsync(string peerUhid, bool asInitiator, CancellationToken ct)
    {
        if (_peers.TryGetValue(peerUhid, out var existing) && !existing.IsClosed)
        {
            if (asInitiator) await existing.WaitOpenAsync(ConnectTimeout, ct).ConfigureAwait(false);
            return existing;
        }

        var link = new WebRtcPeerLink(_localUhid, peerUhid, _iceServers, _signaling, OnPeerData, _logger);
        if (!_peers.TryAdd(peerUhid, link))
        {
            // Lost a race — discard ours, use the winner.
            await link.DisposeAsync().ConfigureAwait(false);
            _peers.TryGetValue(peerUhid, out var winner);
            return winner;
        }

        link.Closed += () => _peers.TryRemove(peerUhid, out _);
        await link.StartAsync(asInitiator).ConfigureAwait(false);

        if (asInitiator)
            await link.WaitOpenAsync(ConnectTimeout, ct).ConfigureAwait(false);

        return link;
    }

    private void OnPeerData(string peerUhid, byte[] data) => DataReceived?.Invoke(peerUhid, data);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _signaling.SignalReceived -= OnSignalReceived;
        foreach (var link in _peers.Values)
            await link.DisposeAsync().ConfigureAwait(false);
        _peers.Clear();
        DataReceived = null;
    }
}
