// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Streaming;
using AetherNet.Streaming.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Watching something together, over the same mesh everything else rides.
///
/// <para>
/// The protocol library owns the hard part — <see cref="WatchTogetherService"/> is the sync brain: the
/// host issues play / pause / seek, and every follower applies it with the round-trip correction so two
/// phones land on the same frame. This is only the phone's half: it hands that service the REAL mesh
/// (not the Lab's in-process one), feeds it the watch packets that arrive off the radio, and keeps the
/// one session a screen needs to draw.
/// </para>
///
/// <para>
/// Because it drives the shared <see cref="WatchTogetherService"/> and its standard wire format
/// (<see cref="PacketType.WatchSync"/> / <see cref="PacketType.WatchReaction"/>), a watch invite from
/// here reaches any app on the same mesh that speaks AetherNet — TxtMe included — as if it were the
/// same app. The content itself is shared by hash over the ordinary attachment transfer, so both ends
/// play from bytes they each already hold.
/// </para>
/// </summary>
public sealed class WatchService : IDisposable
{
    private readonly IIdentityService _me;
    private readonly IRadioMesh? _radio;
    private readonly ILogger _log;

    private WatchTogetherService? _watch;
    private bool _disposed;

    public WatchService(
        IIdentityService me, IRadioMesh? radio = null, ILoggerFactory? loggerFactory = null)
    {
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _radio = radio;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WatchService>();

        if (_radio is not null) _radio.PacketReceived += OnPacket;
    }

    /// <summary>Something changed a screen would want to redraw — a new session, a sync, a reaction.</summary>
    public event Action? Changed;

    /// <summary>Running commentary, for the radio log.</summary>
    public event Action<string>? Trace;

    /// <summary>Someone invited this phone to watch with them. Carries the session to draw an invite from.</summary>
    public event Action<WatchSession>? Invited;

    /// <summary>The host moved — play, pause or seek. The player follows this to the frame.</summary>
    public event Action<WatchSession>? Synced;

    /// <summary>Somebody in the room reacted.</summary>
    public event Action<WatchReactionPayload>? Reacted;

    /// <summary>The watch session in progress, or null when there is none.</summary>
    public WatchSession? Current { get; private set; }

    /// <summary>Whether this phone is the one driving playback.</summary>
    public bool IsHost => Current is { } c && string.Equals(c.HostUhid, _me.AetherTag, StringComparison.Ordinal);

    // ── Starting and joining ────────────────────────────────────────────────────

    /// <summary>
    /// Host a room around content this phone already holds — named by the same content hash the
    /// attachment store uses, so a follower who has been sent the file plays the very same bytes.
    /// </summary>
    public async Task<WatchSession?> HostAsync(string contentRootHash, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(contentRootHash) || string.IsNullOrEmpty(title)) return null;

        try
        {
            var session = await Watch().HostAsync(contentRootHash, title, WatchMode.SharedFile, cancellationToken)
                .ConfigureAwait(false);
            Current = session;
            T($"hosting “{title}” — the room is open");
            Raise();
            return session;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not host a watch session");
            return null;
        }
    }

    /// <summary>Follow a room somebody invited this phone to.</summary>
    public async Task FollowAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            await Watch().FollowAsync(sessionId, cancellationToken).ConfigureAwait(false);
            T("following the host");
            Raise();
        }
        catch (Exception ex) { _log.LogDebug(ex, "follow"); }
    }

    // ── Driving playback (host) ──────────────────────────────────────────────────

    public Task PlayAsync(long positionMs) => HostCommand(id => Watch().PlayAsync(id, positionMs), "play");
    public Task PauseAsync(long positionMs) => HostCommand(id => Watch().PauseAsync(id, positionMs), "pause");
    public Task SeekAsync(long positionMs) => HostCommand(id => Watch().SeekAsync(id, positionMs), "seek");

    private async Task HostCommand(Func<Guid, Task> command, string what)
    {
        if (Current is not { } session) return;
        try { await command(session.Id).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "watch {What}", what); }
    }

    /// <summary>React — a tap that everyone in the room sees, tied to where you are in the video.</summary>
    public async Task ReactAsync(string reaction, long positionMs)
    {
        if (Current is not { } session) return;
        try { await Watch().SendReactionAsync(session.Id, reaction, positionMs).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "watch react"); }
    }

    /// <summary>Leave the room. The session is over for this phone either way.</summary>
    public async Task LeaveAsync()
    {
        if (Current is not { } session) return;
        try { await Watch().EndAsync(session.Id).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "watch leave"); }
        Current = null;
        T("left the room");
        Raise();
    }

    // ── The wire ─────────────────────────────────────────────────────────────────

    private WatchTogetherService Watch()
    {
        if (_watch is not null) return _watch;

        IMeshSender sender = _radio is not null
            ? new RadioMeshSender(_me.AetherTag, _radio)
            : new NullMeshSender(_me.AetherTag);

        var watch = new WatchTogetherService(sender, new RoutingService(sender));

        watch.SessionInvited += (_, s) => { Current ??= s; Invited?.Invoke(s); T($"invited to watch “{s.Title}”"); Raise(); };
        watch.SyncApplied += (_, s) => { Current = s; Synced?.Invoke(s); Raise(); };
        watch.ReactionReceived += (_, r) => { Reacted?.Invoke(r); Raise(); };
        watch.SessionEnded += (_, s) => { if (Current?.Id == s.Id) { Current = null; T("the room closed"); Raise(); } };

        return _watch = watch;
    }

    private void OnPacket(byte[] bytes)
    {
        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return; }

        // Only the watch lanes reach the sync brain; everything else is somebody else's packet.
        if (packet.Type is not (PacketType.WatchSync or PacketType.WatchReaction or PacketType.WatchChunkRequest))
            return;

        _ = HandleAsync(packet);
    }

    private async Task HandleAsync(MeshPacket packet)
    {
        try { await Watch().HandleAsync(packet).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "handling a watch packet"); }
    }

    private void T(string message)
    {
        Trace?.Invoke(message);
        _log.LogInformation("{Message}", message);
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_radio is not null) _radio.PacketReceived -= OnPacket;
    }
}
