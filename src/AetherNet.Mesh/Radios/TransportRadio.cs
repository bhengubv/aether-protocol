// SPDX-License-Identifier: MIT
using AetherNet.Transport.Abstractions;

namespace AetherNet.Mesh;

/// <summary>
/// A transport that has to be told to start, as opposed to one that is ready as soon as it exists.
/// </summary>
public interface IStartableTransport
{
    void Connect();
}


/// <summary>
/// Lets anything the protocol library already implements join the mesh, without a bespoke wrapper for
/// each one.
///
/// <para>
/// There were three transport abstractions stacked on each other: <c>ITransportService</c>, which the
/// protocol defines and which HttpRelay, QuicRelay, CircuitRelay and InProcess already implement;
/// <c>IRadio</c>, which this app invented and which was Android-internal then; and <c>IRadioMesh</c>, which
/// existed in the shared project because the shared project could not see <c>IRadio</c>. The mesh only
/// routed the middle one, so none of the transports in <c>src/</c> could carry a byte of this app's
/// traffic — which is why wiring the internet leg needed an entire new class rather than one line.
/// </para>
///
/// <para>
/// This is that one line. A transport becomes a radio, and the ladder collapses to two: the protocol's
/// abstraction, and the mesh that routes across it.
/// </para>
/// </summary>
public sealed class TransportRadio : IRadio, IDisposable
{
    private readonly ITransportService _transport;
    private readonly string _localUhid;
    private readonly Func<bool> _available;
    private readonly string? _name;
    private readonly Action? _start;
    private readonly Func<string?>? _why;
    private readonly string? _unavailableReason;
    private string? _peer;

    /// <summary>
    /// Whether <see cref="_peer"/> was learned from the transport announcing a real connection
    /// (<see cref="ITransportService.PeerLinked"/>) rather than from the first datagram heard. A tracked
    /// peer's link is only live while the transport still says so, so it can drop the instant the socket
    /// does; an untracked one keeps the old rule — heard once, linked until disposed — because a relay
    /// has no connection to lose.
    /// </summary>
    private bool _tracked;
    private bool _disposed;

    /// <param name="transport">Any of the protocol's transports.</param>
    /// <param name="localUhid">This node's wire address, for the handshake the mesh expects.</param>
    /// <param name="available">
    ///   Whether this transport can be used right now. Asked rather than assumed, because a relay with
    ///   nobody to relay through is present but useless, and saying otherwise is how a radio ends up
    ///   taking traffic it cannot carry.
    /// </param>
    /// <param name="name">
    ///   What the mesh calls this radio, where the transport's own name is not one a peer would recognise ("BLE",
    ///   "Wi-Fi Direct" — see <c>TransportCapability.TagFor</c>). Null keeps the transport's name.
    /// </param>
    /// <param name="start">
    ///   How to bring the transport up, for one that starts with a call of its own (scanning, listening) rather than
    ///   <see cref="IStartableTransport"/>. Null when it is ready as soon as it exists.
    /// </param>
    /// <param name="why">
    ///   Why it cannot be used right now, asked each time, for a radio whose reason changes ("switched off" one minute,
    ///   fine the next). Takes the place of <paramref name="unavailableReason"/> when it answers.
    /// </param>
    public TransportRadio(ITransportService transport, string localUhid,
        Func<bool>? available = null, string? unavailableReason = null, string? name = null, Action? start = null,
        Func<string?>? why = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _available = available ?? (() => true);
        _unavailableReason = unavailableReason;
        _name = name;
        _start = start;
        _why = why;

        _transport.DataReceived += OnData;
        _transport.PeerLinked += OnPeerLinked;
    }

    public string Name => _name ?? _transport.Name;
    public bool IsAvailable => !_disposed && _transport.IsAvailable && _available();
    public string? UnavailableReason => IsAvailable ? null : _why?.Invoke() ?? _unavailableReason;
    public long MaxBandwidthBps => _transport.MaxBandwidthBps;

    /// <inheritdoc />
    public LinkQuality Quality { get; } = new();

    // Linked while there is a peer — and, when that peer came from a real connection the transport
    // announced, only while the transport still holds it. That liveness is what lets the traffic move
    // back off a Wi-Fi/LAN link the instant its socket drops, instead of RadioChoice preferring a fast
    // radio that is no longer there. A relay peer (untracked) has no socket to lose, so it stays linked
    // once heard, exactly as before.
    public bool IsLinked => _peer is { } p && (!_tracked || _transport.IsConnected(p));
    public string? PeerTag => _peer;

    /// <summary>
    /// Everyone this radio is holding a link to. A connection-based transport (Wi-Fi/LAN) can keep
    /// several at once, so surface all of them — not just the last one heard from — which is what lets
    /// the mesh answer "is THIS person reachable" now that a phone can be linked to more than one.
    /// </summary>
    public IReadOnlyCollection<string> Peers
    {
        get
        {
            var many = _transport.ConnectedPeers;
            if (many.Count > 0) return many;
            return _peer is { } only ? new[] { only } : [];
        }
    }

    public event Action<string>? PeerLinked;
    public event Action<string, byte[]>? DataReceived;
    public event Action<string>? Status;

    private void OnData(string from, byte[] data)
    {
        // The first thing heard from somebody IS the link, on a transport with no separate handshake.
        if (_peer is null && !string.IsNullOrEmpty(from))
        {
            _peer = from;
            Status?.Invoke($"linked with {from}");
            PeerLinked?.Invoke(from);
        }
        DataReceived?.Invoke(from, data);
    }

    /// <summary>
    /// The transport says a connection to a peer is up, before any data has crossed it. This is the
    /// link for a transport that has a handshake of its own — Wi-Fi/LAN, WebRTC — so the mesh can pick
    /// it the moment it exists rather than waiting for data it would never send over a radio it does not
    /// yet count as linked. Transports without a connection to announce never raise this.
    /// </summary>
    private void OnPeerLinked(string peer)
    {
        if (string.IsNullOrEmpty(peer)) return;

        // Every connection the transport announces is a link — the first peer, a second one, or the same
        // one back after the socket dropped — and the mesh is told each time. It used to be told only
        // about the FIRST peer this radio ever saw: after that, a peer coming back was never announced,
        // so a message held for them waited forever (P30 and Pixel on the same Wi-Fi, 2026-09-30: linked
        // every minute, nothing ever sent).
        _peer = peer;
        _tracked = true;
        Status?.Invoke($"linked with {peer}");
        PeerLinked?.Invoke(peer);
    }

    public void Link()
    {
        if (_disposed) return;
        if (!IsAvailable) { Status?.Invoke(UnavailableReason ?? "not available"); return; }

        // ITransportService has no Connect — the ones that need starting expose their own, and the
        // rest are ready the moment they exist. Reflection would be guessing; a named interface is
        // the transport saying so.
        if (_start is not null) _start();
        else (_transport as IStartableTransport)?.Connect();
        Status?.Invoke($"{Name} up");
    }

    public Task<bool> SendAsync(byte[] data) => SendAsync(data, SendLane.Interactive);

    /// <inheritdoc />
    /// <remarks>
    /// The lane is accepted and ignored: these transports queue in the operating system, where there
    /// is one pipe and no way to reorder what is already in it. Saying so plainly beats pretending to
    /// prioritise and quietly not doing it.
    /// </remarks>
    public async Task<bool> SendAsync(byte[] data, SendLane lane)
    {
        if (_peer is not { } peer) return false;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var ok = await _transport.SendAsync(peer, data).ConfigureAwait(false);
            Quality.Record(data.Length, System.Diagnostics.Stopwatch.GetElapsedTime(started), ok);
            return ok;
        }
        catch
        {
            Quality.Record(data.Length, System.Diagnostics.Stopwatch.GetElapsedTime(started), sent: false);
            return false;
        }
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.DataReceived -= OnData;
        _transport.PeerLinked -= OnPeerLinked;
        _peer = null;
        (_transport as IDisposable)?.Dispose();
    }
}
