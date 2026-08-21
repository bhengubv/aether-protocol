// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Sample.Shared.Services;
using AetherNet.Transport.Abstractions;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// A transport that has to be told to start, as opposed to one that is ready as soon as it exists.
/// </summary>
internal interface IStartableTransport
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
/// <c>IRadio</c>, which this app invented and which is Android-internal; and <c>IRadioMesh</c>, which
/// exists in the shared project because the shared project cannot see <c>IRadio</c>. The mesh only
/// routed the middle one, so none of the transports in <c>src/</c> could carry a byte of this app's
/// traffic — which is why wiring the internet leg needed an entire new class rather than one line.
/// </para>
///
/// <para>
/// This is that one line. A transport becomes a radio, and the ladder collapses to two: the protocol's
/// abstraction, and the mesh that routes across it.
/// </para>
/// </summary>
internal sealed class TransportRadio : IRadio, IDisposable
{
    private readonly ITransportService _transport;
    private readonly string _localUhid;
    private readonly Func<bool> _available;
    private readonly string? _unavailableReason;
    private string? _peer;
    private bool _disposed;

    /// <param name="transport">Any of the protocol's transports.</param>
    /// <param name="localUhid">This node's wire address, for the handshake the mesh expects.</param>
    /// <param name="available">
    ///   Whether this transport can be used right now. Asked rather than assumed, because a relay with
    ///   nobody to relay through is present but useless, and saying otherwise is how a radio ends up
    ///   taking traffic it cannot carry.
    /// </param>
    public TransportRadio(ITransportService transport, string localUhid,
        Func<bool>? available = null, string? unavailableReason = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _available = available ?? (() => true);
        _unavailableReason = unavailableReason;

        _transport.DataReceived += OnData;
    }

    public string Name => _transport.Name;
    public bool IsAvailable => !_disposed && _transport.IsAvailable && _available();
    public string? UnavailableReason => IsAvailable ? null : _unavailableReason;
    public long MaxBandwidthBps => _transport.MaxBandwidthBps;

    /// <inheritdoc />
    public LinkQuality Quality { get; } = new();

    public bool IsLinked => _peer is not null;
    public string? PeerTag => _peer;

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

    public void Link()
    {
        if (_disposed) return;
        if (!IsAvailable) { Status?.Invoke(_unavailableReason ?? "not available"); return; }

        // ITransportService has no Connect — the ones that need starting expose their own, and the
        // rest are ready the moment they exist. Reflection would be guessing; a named interface is
        // the transport saying so.
        (_transport as IStartableTransport)?.Connect();
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
        _peer = null;
        (_transport as IDisposable)?.Dispose();
    }
}
#endif
