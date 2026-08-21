// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Sample.Shared.Services;
using AetherNet.Transport.Relay;
using Android.Content;
using Android.Net;
using Microsoft.Extensions.Logging;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// The second leg: reaching somebody who is not in the room.
///
/// <para>
/// Every other radio here is line-of-sight. Wi-Fi Direct carries a call beautifully and carries it
/// exactly as far as the far side of the building; walk out of range and the network is not degraded,
/// it is gone. One leg is not uptime — it is a single point of failure with good throughput.
/// </para>
///
/// <para>
/// So this one goes the other way, through whatever internet the phone has. It reaches a proxy: a
/// phone in somebody's Circle that put its hand up and is running <see cref="RelayServer"/>. Not a
/// service, not an operator, not an account — a peer, whose address arrived from a contact inside
/// their session, and which stops being a proxy the moment they say so.
/// </para>
///
/// <para>
/// It is deliberately last in the ladder. Wi-Fi Direct is free, private and fast; this costs the
/// person data and puts their traffic through somebody else's phone. It is what you use when the
/// alternative is nothing at all — which, for a network meant to hold up at ninety-nine percent, is a
/// case that has to be built rather than hoped about.
/// </para>
/// </summary>
internal sealed class AndroidInternetTransportService : IRadio, IDisposable
{
    private readonly Context _context;
    private readonly string _localUhid;
    private readonly ILogger _logger;
    private readonly ProxyDirectory? _proxies;

    private HttpRelayTransportService? _client;
    private string? _connectedTo;
    private bool _disposed;

    public AndroidInternetTransportService(Context context, string localUhid, ILogger logger,
        ProxyDirectory? proxies = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _proxies = proxies;

        if (_proxies is not null) _proxies.Changed += OnProxiesChanged;
    }

    public string Name => "Internet";

    /// <summary>
    /// Two things have to be true, and they fail for completely different reasons — so they are
    /// reported separately rather than as one unhelpful "unavailable".
    /// </summary>
    public bool IsAvailable => !_disposed && HasNetwork && _proxies?.Best is not null;

    /// <inheritdoc />
    public string? UnavailableReason =>
        !HasNetwork ? "no internet on this phone"
        : _proxies?.Best is null ? "nobody in your Circle is offering to relay yet"
        : null;

    /// <summary>Both are things a person can change — one by turning data on, one by asking a friend.</summary>
    public bool IsFixable => true;

    /// <summary>
    /// Not measured, and not claimed to be. Real throughput here is whatever the phone's data
    /// connection and the proxy's uplink happen to be, which changes by the minute and by the street.
    /// This is the figure the codec sizes itself against, so it is set low enough to be safe on a
    /// bad connection rather than flattering on a good one.
    /// </summary>
    public long MaxBandwidthBps => 256_000;

    public bool IsLinked => _client is not null && _connectedTo is not null;

    /// <summary>
    /// Nobody in particular. A proxy is not a peer — it carries for everyone at once, so there is no
    /// single phone on the other end of this link the way there is on Wi-Fi Direct.
    /// </summary>
    public string? PeerTag => null;

    public event Action<string>? PeerLinked;
    public event Action<string, byte[]>? DataReceived;
    public event Action<string>? Status;

    private void L(string message)
    {
        global::Android.Util.Log.Info("AetherNet", message);
        Status?.Invoke(message);
    }

    private bool HasNetwork
    {
        get
        {
            try
            {
                if (_context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager cm)
                    return false;

                var caps = cm.GetNetworkCapabilities(cm.ActiveNetwork);
                return caps is not null && caps.HasCapability(NetCapability.Internet);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read connectivity");
                return false;
            }
        }
    }

    public void Link()
    {
        if (_disposed) return;

        if (!HasNetwork) { L("no internet on this phone — nothing to reach a proxy through"); return; }

        var proxy = _proxies?.Best;
        if (proxy is null) { L("nobody in your Circle is offering to relay yet"); return; }
        if (string.Equals(proxy, _connectedTo, StringComparison.Ordinal)) return;

        Stop();

        try
        {
            _client = new HttpRelayTransportService(proxy, _localUhid);
            _client.DataReceived += OnRelayData;
            _client.Connect();
            _connectedTo = proxy;
            L($"relaying through {proxy}");
            PeerLinked?.Invoke(proxy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach the proxy at {Proxy}", proxy);
            L($"could not reach the proxy at {proxy}");
            Stop();
        }
    }

    /// <summary>
    /// A proxy came or went. Re-linking here is what makes the leg self-healing: the phone offering to
    /// relay can change without anyone touching anything.
    /// </summary>
    private void OnProxiesChanged()
    {
        if (_disposed) return;
        if (_proxies?.Best is null) { Stop(); return; }
        Link();
    }

    private void OnRelayData(string from, byte[] data)
    {
        // The proxy names who sent it. What it says is sealed in their session, so the proxy carried
        // it without being able to read a word of it.
        DataReceived?.Invoke(from, data);
    }

    /// <summary>
    /// Broadcast has no meaning through a relay — there is no air to shout into, only a queue per
    /// node. So a packet goes to every contact who could be on the far side, which is the closest
    /// honest equivalent.
    /// </summary>
    public async Task<bool> SendAsync(byte[] data)
    {
        if (_client is null || _proxies is null) return false;

        var sent = false;
        foreach (var peer in _proxies.Reachable)
        {
            if (string.Equals(peer, _localUhid, StringComparison.Ordinal)) continue;
            try { sent |= await _client.SendAsync(peer, data).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Relay send to {Peer} failed", peer); }
        }
        return sent;
    }

    public void Stop()
    {
        var client = _client;
        _client = null;
        _connectedTo = null;

        if (client is null) return;
        client.DataReceived -= OnRelayData;
        _ = Task.Run(async () =>
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Relay client teardown"); }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_proxies is not null) _proxies.Changed -= OnProxiesChanged;
        Stop();
    }
}
#endif
