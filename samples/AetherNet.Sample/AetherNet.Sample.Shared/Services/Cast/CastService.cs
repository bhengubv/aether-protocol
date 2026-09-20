// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Cast;

/// <summary>
/// "Cast to a screen", over one picker, two roads:
/// <list type="bullet">
///   <item><b>Aether</b> — a reachable mesh node that can display. Today that is another phone; put the
///     Aether node service on an Android TV or a dongle and that screen joins AetherNet as a node, and
///     appears here exactly the same way. Casting to it reuses the Watch-Together engine: the content is
///     already addressed by hash, so the device plays the very bytes it can fetch off the mesh.</item>
///   <item><b>DLNA</b> — a smart TV on the LAN that does NOT run Aether, driven over the open UPnP
///     standard (no Google Cast). The bridge for today's TVs until Aether rides on them directly.</item>
/// </list>
/// A person picks a screen; the words "phone" and "TV" are all they need — which road it took is ours.
/// </summary>
public sealed class CastService
{
    private readonly ContactService _contacts;
    private readonly WatchService _watch;
    private readonly DlnaCastService _dlna;
    private readonly IRadioMesh? _radio;
    private readonly ILogger<CastService> _log;

    public CastService(
        ContactService contacts,
        WatchService watch,
        DlnaCastService dlna,
        IRadioMesh? radio = null,
        ILogger<CastService>? log = null)
    {
        _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        _watch = watch ?? throw new ArgumentNullException(nameof(watch));
        _dlna = dlna ?? throw new ArgumentNullException(nameof(dlna));
        _radio = radio;
        _log = log ?? NullLogger<CastService>.Instance;
    }

    /// <summary>
    /// The screens you can send to right now: the Aether devices within reach, then any TVs that answer
    /// on the LAN. Aether devices come back immediately; the TV search takes a couple of seconds.
    /// </summary>
    public async Task<IReadOnlyList<CastTarget>> FindTargetsAsync(CancellationToken cancellationToken = default)
    {
        var targets = new List<CastTarget>();

        if (_radio is not null)
            foreach (var c in _contacts.Contacts)
                if (_radio.IsReachable(c.Tag))
                    targets.Add(new CastTarget(c.Tag, _contacts.DisplayName(c.Tag), CastKind.Aether));

        try { targets.AddRange(await _dlna.DiscoverAsync(cancellationToken: cancellationToken).ConfigureAwait(false)); }
        catch (Exception ex) { _log.LogDebug(ex, "TV discovery failed — showing Aether devices only"); }

        return targets;
    }

    /// <summary>
    /// Send content to a screen. For an Aether device this hosts a watch session it plays and returns
    /// true so the caller can open the remote; for a TV it serves the bytes and presses play. Returns
    /// false if the screen refused or the bytes are not all here yet.
    /// </summary>
    public async Task<bool> CastAsync(CastTarget target, string hash, string contentType, string title,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        return target.Kind switch
        {
            CastKind.Aether => await _watch.HostAsync(hash, title).ConfigureAwait(false) is not null,
            CastKind.Dlna => await _dlna.CastAsync(target, hash, contentType, title, cancellationToken).ConfigureAwait(false),
            _ => false,
        };
    }

    // The remote for a TV. An Aether cast is driven from the /watch player (the host's own controls).
    public Task<bool> PlayAsync(CastTarget t) => _dlna.PlayAsync(t);
    public Task<bool> PauseAsync(CastTarget t) => _dlna.PauseAsync(t);
    public Task<bool> StopAsync(CastTarget t) => _dlna.StopAsync(t);
    public Task<bool> SeekAsync(CastTarget t, long positionMs) => _dlna.SeekAsync(t, positionMs);

    /// <summary>What the screen is doing right now, for the remote's live state + position. Null if it didn't answer.</summary>
    public Task<CastStatus?> StatusAsync(CastTarget t) => _dlna.StatusAsync(t);
}
