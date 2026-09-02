// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Diagnostics;
using AetherNet.Extensibility;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Streaming.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Streaming;

/// <summary>
/// Default watch-together implementation. Host-driven model: the host emits
/// authoritative sync commands; followers apply with RTT compensation
/// (<c>local_position ← position_ms + (now − sent_at_ms) × playback_speed</c>).
///
/// Wire packets:
///   <see cref="PacketType.WatchSync"/> — sync commands and join announces (discriminated by JSON shape)
///   <see cref="PacketType.WatchReaction"/> — fire-and-forget reactions
/// </summary>
public sealed class WatchTogetherService : IWatchTogetherService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IRoutingService _routing;
    private readonly IAetherNetIncentiveProvider _incentives;
    private readonly ILogger<WatchTogetherService> _logger;

    private readonly ConcurrentDictionary<Guid, WatchSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, ChipInPool> _chipIns = new();

    public event EventHandler<WatchSession>? SessionInvited;
    public event EventHandler<WatchSession>? SyncApplied;
    public event EventHandler<WatchReactionPayload>? ReactionReceived;
    public event EventHandler<WatchSession>? SessionEnded;
    public event EventHandler<(Guid SessionId, TorrentInfo Torrent)>? TorrentReceived;
    public event EventHandler<ChipInPool>? ChipInUpdated;

    public WatchTogetherService(
        IMeshSender sender,
        IRoutingService routing,
        IAetherNetIncentiveProvider? incentives = null,
        ILogger<WatchTogetherService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _logger = logger ?? NullLogger<WatchTogetherService>.Instance;
    }

    public async Task<WatchSession> HostAsync(string contentRootHash, string title, WatchMode mode = WatchMode.SharedFile, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentRootHash);
        ArgumentException.ThrowIfNullOrEmpty(title);

        var session = new WatchSession
        {
            HostUhid = _sender.LocalUhid,
            State = WatchState.Hosting,
            ContentRootHash = contentRootHash,
            Title = title,
            Mode = mode,
            IsPlaying = false,
        };
        _sessions[session.Id] = session;

        await BroadcastJoinAsync(session, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Watch session {Id} hosted: {Title} (root={Root})", session.Id, title, contentRootHash);
        return session;
    }

    public async Task FollowAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State == WatchState.Hosting) return;
        session.State = WatchState.Following;
        await Task.CompletedTask;
    }

    public Task PlayAsync(Guid sessionId, long positionMs, CancellationToken cancellationToken = default)
        => SendSyncAsync(sessionId, WatchSyncType.Play, positionMs, cancellationToken: cancellationToken);

    public Task PauseAsync(Guid sessionId, long positionMs, CancellationToken cancellationToken = default)
        => SendSyncAsync(sessionId, WatchSyncType.Pause, positionMs, cancellationToken: cancellationToken);

    public Task SeekAsync(Guid sessionId, long positionMs, CancellationToken cancellationToken = default)
        => SendSyncAsync(sessionId, WatchSyncType.Seek, positionMs, cancellationToken: cancellationToken);

    public Task SetSpeedAsync(Guid sessionId, double playbackSpeed, long positionMs, CancellationToken cancellationToken = default)
        => SendSyncAsync(sessionId, WatchSyncType.Speed, positionMs, playbackSpeed, cancellationToken);

    public async Task SendReactionAsync(Guid sessionId, string reaction, long positionMs, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        ArgumentException.ThrowIfNullOrEmpty(reaction);

        var body = JsonSerializer.SerializeToUtf8Bytes(new WatchReactionPayload
        {
            SessionId = sessionId,
            Reaction = reaction,
            SenderUhid = _sender.LocalUhid,
            PositionMs = positionMs,
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.WatchReaction,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = string.Empty,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 0,
            Payload = body,
        };
        await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public async Task EndAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.State != WatchState.Hosting) return;

        session.State = WatchState.Ended;
        session.EndedAt = DateTime.UtcNow;
        await SendSyncAsync(sessionId, WatchSyncType.Pause, session.PositionMs, cancellationToken: cancellationToken).ConfigureAwait(false);
        SessionEnded?.Invoke(this, session);
    }

    public async Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        switch (packet.Type)
        {
            case PacketType.WatchSync:
                HandleSync(packet);
                break;
            case PacketType.WatchReaction:
                HandleReaction(packet);
                break;
            case PacketType.TorrentMetadata:
                HandleTorrentMetadata(packet);
                break;
            case PacketType.WatchChunkRequest:
                // Chunk serving is handled by the content layer (AetherNet.Content).
                // WatchTogetherService records the request in telemetry but does not serve bytes.
                _logger.LogDebug("WatchChunkRequest from {Source} — delegate to content layer", packet.SourceUhid);
                break;
            default:
                _logger.LogDebug("WatchTogetherService.HandleAsync ignoring non-watch packet type {Type}", packet.Type);
                break;
        }
        await Task.CompletedTask;
    }

    public IReadOnlyList<WatchSession> GetActiveSessions()
        => _sessions.Values.Where(s => s.State is WatchState.Hosting or WatchState.Following).ToArray();

    public async Task BroadcastTorrentAsync(Guid sessionId, TorrentInfo torrent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        var payload = JsonSerializer.SerializeToUtf8Bytes(torrent, JsonOptions);
        var packet = new MeshPacket
        {
            Type = PacketType.TorrentMetadata,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = string.Empty,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 8,
            Payload = payload,
        };
        await _sender.BroadcastAsync(packet, ct).ConfigureAwait(false);
        _logger.LogInformation("Watch session {Id}: TorrentMetadata broadcast (hash={Hash})", sessionId, torrent.InfoHash);
    }

    public async Task<ChipInPool> StartChipInAsync(Guid sessionId, decimal targetAmountZar, string? contentDescription, string? torrentInfoHash, string? magnetLink, CancellationToken ct = default)
    {
        var pool = new ChipInPool
        {
            SessionId = sessionId,
            InitiatorUhid = _sender.LocalUhid,
            TargetAmountZar = targetAmountZar,
            ContentDescription = contentDescription,
            TorrentInfoHash = torrentInfoHash,
            MagnetLink = magnetLink,
        };
        _chipIns[pool.Id] = pool;

        // Broadcast a WatchSync packet with a "chip_in" discriminator field so followers
        // learn about the new pool and can store it in their own _chipIns dict.
        var envelope = new ChipInBroadcastEnvelope { ChipIn = pool };
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        var packet = new MeshPacket
        {
            Type = PacketType.WatchSync,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = string.Empty,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 8,
            Payload = body,
        };
        await _sender.BroadcastAsync(packet, ct).ConfigureAwait(false);
        ChipInUpdated?.Invoke(this, pool);
        _logger.LogInformation("Watch session {SessionId}: ChipIn pool {PoolId} started (target={Target} ZAR)", sessionId, pool.Id, targetAmountZar);
        return pool;
    }

    public Task<ChipInPool?> ContributeAsync(Guid chipInId, string contributorUhid, decimal amountZar, CancellationToken ct = default)
    {
        if (!_chipIns.TryGetValue(chipInId, out var pool))
            return Task.FromResult<ChipInPool?>(null);

        pool.Contributions.Add(new ChipInContribution
        {
            ContributorUhid = contributorUhid,
            AmountZar = amountZar,
        });
        pool.CollectedAmountZar += amountZar;

        if (pool.IsFunded && pool.State == ChipInState.Collecting)
            pool.State = ChipInState.Funded;

        ChipInUpdated?.Invoke(this, pool);
        _logger.LogDebug("ChipIn {PoolId}: contribution {Amount} ZAR from {Uhid} — total={Total}/{Target}",
            chipInId, amountZar, contributorUhid, pool.CollectedAmountZar, pool.TargetAmountZar);
        return Task.FromResult<ChipInPool?>(pool);
    }

    public ChipInPool? GetChipIn(Guid chipInId)
        => _chipIns.TryGetValue(chipInId, out var pool) ? pool : null;

    private async Task SendSyncAsync(Guid sessionId, WatchSyncType kind, long positionMs, double playbackSpeed = 1.0, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.State != WatchState.Hosting) return;

        session.PositionMs = positionMs;
        if (kind == WatchSyncType.Speed) session.PlaybackSpeed = playbackSpeed;
        if (kind == WatchSyncType.Play) session.IsPlaying = true;
        if (kind == WatchSyncType.Pause) session.IsPlaying = false;

        var body = JsonSerializer.SerializeToUtf8Bytes(new WatchSyncCommand
        {
            SessionId = sessionId,
            Kind = kind,
            PositionMs = positionMs,
            PlaybackSpeed = playbackSpeed,
            SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.WatchSync,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = string.Empty,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 16,
            Payload = body,
        };
        await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task BroadcastJoinAsync(WatchSession session, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new WatchJoinPayload
        {
            SessionId = session.Id,
            HostUhid = session.HostUhid,
            ContentRootHash = session.ContentRootHash,
            Title = session.Title,
            Mode = session.Mode,
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.WatchSync,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = string.Empty,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 0,
            Payload = body,
        };
        await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private void HandleTorrentMetadata(MeshPacket packet)
    {
        TorrentInfo? torrent;
        try
        {
            torrent = JsonSerializer.Deserialize<TorrentInfo>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "WatchTogether: failed to deserialize TorrentMetadata from {Source}", packet.SourceUhid);
            return;
        }
        if (torrent is null) return;

        // Determine which session this belongs to — use the first hosting/following session
        // (the host broadcasts without a session envelope in the packet itself).
        var sessionId = _sessions.Keys.FirstOrDefault();
        TorrentReceived?.Invoke(this, (sessionId, torrent));
    }

    private void HandleSync(MeshPacket packet)
    {
        // Discriminate join (from host_uhid presence) vs chip_in pool (from chip_in presence)
        // vs sync command (from kind presence).
        var doc = TryParseJsonObject(packet.Payload);
        if (doc is null) return;

        if (doc.RootElement.TryGetProperty("chip_in", out _))
        {
            // Follower receiving a ChipIn pool broadcast from the host.
            var envelope = JsonSerializer.Deserialize<ChipInBroadcastEnvelope>(packet.Payload, JsonOptions);
            if (envelope?.ChipIn is null) return;
            var pool = envelope.ChipIn;
            _chipIns.AddOrUpdate(pool.Id, pool, (_, _) => pool);
            ChipInUpdated?.Invoke(this, pool);
            return;
        }

        if (doc.RootElement.TryGetProperty("host_uhid", out _))
        {
            var join = JsonSerializer.Deserialize<WatchJoinPayload>(packet.Payload, JsonOptions);
            if (join is null) return;
            var existing = _sessions.GetOrAdd(join.SessionId, _ => new WatchSession
            {
                Id = join.SessionId,
                HostUhid = join.HostUhid,
                State = WatchState.Idle,
                ContentRootHash = join.ContentRootHash,
                Title = join.Title,
                Mode = join.Mode,
            });
            SessionInvited?.Invoke(this, existing);
            return;
        }

        var command = JsonSerializer.Deserialize<WatchSyncCommand>(packet.Payload, JsonOptions);
        if (command is null) return;
        if (!_sessions.TryGetValue(command.SessionId, out var session)) return;
        if (string.Equals(session.HostUhid, _sender.LocalUhid, StringComparison.Ordinal)) return; // host ignores its own commands
        if (!string.Equals(packet.SourceUhid, session.HostUhid, StringComparison.Ordinal))
        {
            _logger.LogDebug("Watch sync from {Source} dropped — not the host of session {Id}", packet.SourceUhid, command.SessionId);
            return;
        }

        // RTT compensation: where the host's clock implies playback should be NOW.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var elapsed = Math.Max(0, nowMs - command.SentAtMs);
        var compensated = command.Kind == WatchSyncType.Play
            ? command.PositionMs + (long)(elapsed * command.PlaybackSpeed)
            : command.PositionMs;

        // Runtime quality monitor (formal/watch-together-timed): an established, playing follower whose
        // position has drifted beyond tolerance from the host's authoritative position is out of sync;
        // flag it before we snap to the host. The predicate keeps this check and the formal model coupled.
        if (session.IsPlaying &&
            !MeshInvariants.WatchTogetherBoundedLatency(compensated, [session.PositionMs]))
        {
            _logger.LogWarning(
                "Watch-together follower drift {DriftMs}ms exceeds tolerance for session {SessionId}; re-syncing to host {HostMs}ms",
                Math.Abs(session.PositionMs - compensated), command.SessionId, compensated);
        }

        session.PositionMs = compensated;
        session.PlaybackSpeed = command.PlaybackSpeed;
        session.IsPlaying = command.Kind switch
        {
            WatchSyncType.Play => true,
            WatchSyncType.Pause => false,
            _ => session.IsPlaying,
        };
        SyncApplied?.Invoke(this, session);
    }

    private void HandleReaction(MeshPacket packet)
    {
        WatchReactionPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<WatchReactionPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }
        if (body is null) return;
        if (string.IsNullOrEmpty(body.SenderUhid)) body.SenderUhid = packet.SourceUhid;
        ReactionReceived?.Invoke(this, body);
    }

    private static JsonDocument? TryParseJsonObject(byte[] payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Internal envelope used to discriminate ChipIn pool broadcasts inside WatchSync packets.
    /// Serializes with the snake_case "chip_in" property so HandleSync can discriminate by shape.
    /// </summary>
    private sealed class ChipInBroadcastEnvelope
    {
        public ChipInPool? ChipIn { get; set; }
    }

    private sealed class DefaultIncentiveProvider : IAetherNetIncentiveProvider
    {
    }
}
