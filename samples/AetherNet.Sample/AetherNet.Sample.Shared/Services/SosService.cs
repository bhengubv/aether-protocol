// SPDX-License-Identifier: MIT

using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Sos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Emergency SOS, over the same mesh everything else rides.
///
/// <para>
/// The protocol library owns the hard part — <see cref="SosBroadcastService"/> floods an alert with an
/// extended TTL at maximum priority, every node re-broadcasts until the TTL is spent, duplicates are
/// suppressed, and signed acknowledgements flow back so the sender learns its true reach. This is only
/// the phone's half: it hands that service the REAL mesh (not the Lab's in-process one), feeds it the
/// SOS and ack packets that arrive off the radio, and keeps the one alert a screen needs to draw.
/// </para>
///
/// <para>
/// Deliberately honest: a mesh is best-effort, so the screen shows how many devices have actually
/// acknowledged rather than promising the message got through. Marking safe is the only thing that
/// stops an alert — an acknowledgement never does.
/// </para>
/// </summary>
public sealed class SosService : IDisposable
{
    private readonly IIdentityService _me;
    private readonly IRadioMesh? _radio;
    private readonly ILogger _log;

    private SosBroadcastService? _sos;
    private bool _disposed;

    public SosService(IIdentityService me, IRadioMesh? radio = null, ILoggerFactory? loggerFactory = null)
    {
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _radio = radio;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SosService>();

        if (_radio is not null) _radio.PacketReceived += OnPacket;
    }

    /// <summary>Something a screen would redraw — a new alert, an acknowledgement, a resolution.</summary>
    public event Action? Changed;

    /// <summary>An SOS arrived from somebody nearby.</summary>
    public event Action<SosAlert>? Received;

    /// <summary>A device confirmed it received one of OUR alerts — proof the emergency reached someone.</summary>
    public event Action<SosAcknowledgement>? Acknowledged;

    /// <summary>An alert was marked resolved — the source is safe.</summary>
    public event Action<Guid>? Resolved;

    /// <summary>Every alert this phone currently considers active (ours and others').</summary>
    public IReadOnlyList<SosAlert> Active => _sos?.GetActiveAlerts() ?? Array.Empty<SosAlert>();

    /// <summary>Whether this phone can actually put an SOS on the air.</summary>
    public bool CanSend => _radio is not null;

    /// <summary>
    /// Broadcast "I need help" to everyone in range. Floods the mesh; returns false only if the abuse
    /// rate-limit is spent.
    /// </summary>
    public async Task<bool> SendNearbyAsync(string? message, CancellationToken cancellationToken = default)
    {
        try
        {
            // No GPS is wired here yet, so location goes as unknown (0,0) — the flood does not depend on
            // it; it is metadata a responder can use if the sender fills it later.
            var ok = await Sos().BroadcastSosAsync("sos", message, 0, 0, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            T(ok ? "SOS sent to everyone nearby" : "SOS refused — too many in the last hour");
            Raise();
            return ok;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not send an SOS"); return false; }
    }

    /// <summary>Mark one of our alerts resolved — the only thing that stops it.</summary>
    public async Task MarkSafeAsync(Guid id)
    {
        try { await Sos().ResolveAsync(id).ConfigureAwait(false); T("marked safe"); Raise(); }
        catch (Exception ex) { _log.LogDebug(ex, "resolve sos"); }
    }

    /// <summary>Bring the service up now, so it hears alerts before a screen is ever opened.</summary>
    public void Prime() => _ = Sos();

    private SosBroadcastService Sos()
    {
        if (_sos is not null) return _sos;

        IMeshSender sender = _radio is not null
            ? new RadioMeshSender(_me.AetherTag, _radio)
            : new NullMeshSender(_me.AetherTag);

        var sos = new SosBroadcastService(sender);
        sos.SosReceived += (_, a) => { Received?.Invoke(a); Raise(); };
        sos.SosAcknowledged += (_, ack) => { Acknowledged?.Invoke(ack); Raise(); };
        sos.SosResolved += (_, id) => { Resolved?.Invoke(id); Raise(); };
        return _sos = sos;
    }

    private void OnPacket(byte[] bytes)
    {
        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return; }

        if (packet.Type == PacketType.SosBroadcast) _ = Pump(packet, ack: false);
        else if (packet.Type == PacketType.SosAck) _ = Pump(packet, ack: true);
    }

    private async Task Pump(MeshPacket packet, bool ack)
    {
        try
        {
            if (ack) await Sos().HandleAckAsync(packet).ConfigureAwait(false);
            else await Sos().HandleAsync(packet).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.LogDebug(ex, "handling an SOS packet"); }
    }

    private void T(string message) => _log.LogInformation("[SOS] {Message}", message);
    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_radio is not null) _radio.PacketReceived -= OnPacket;
        _sos?.Dispose();
    }
}
