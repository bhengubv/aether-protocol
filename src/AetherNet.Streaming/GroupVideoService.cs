// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Extensibility;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Streaming.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Streaming;

/// <summary>
/// Multi-party video call service. Manages FullMesh and SFU topologies,
/// auto-switching at <see cref="ProtocolConstants.SfuThresholdParticipants"/> active participants.
/// Video frames re-use the binary format defined by <see cref="VideoCallService.SerializeFrame"/>
/// (16B sessionId BE + 4B seq LE + 8B ts LE + 1B isKeyframe + payload).
/// Signaling travels as JSON over <see cref="PacketType.GroupVideoSignaling"/>.
/// </summary>
public sealed class GroupVideoService : IGroupVideoService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IRoutingService _routing;
    private readonly IAetherNetIncentiveProvider _incentives;
    private readonly ILogger<GroupVideoService> _logger;

    private readonly ConcurrentDictionary<Guid, GroupVideoSession> _sessions = new();

    public event EventHandler<GroupVideoSession>? SessionCreated;
    public event EventHandler<GroupVideoSession>? ParticipantJoined;
    public event EventHandler<GroupVideoSession>? ParticipantLeft;
    public event EventHandler<GroupVideoSession>? TopologyChanged;
    public event EventHandler<VideoFrame>? FrameReceived;

    public GroupVideoService(
        IMeshSender sender,
        IRoutingService routing,
        IAetherNetIncentiveProvider? incentives = null,
        ILogger<GroupVideoService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _logger = logger ?? NullLogger<GroupVideoService>.Instance;
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    public async Task<GroupVideoSession> CreateAsync(
        IReadOnlyList<string> invitedUhids,
        VideoResolution resolution,
        string videoCodec,
        int bitrateKbps,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invitedUhids);
        ArgumentException.ThrowIfNullOrEmpty(videoCodec);

        var session = new GroupVideoSession
        {
            HostUhid = _sender.LocalUhid,
        };

        // Host is the first participant.
        session.Participants.Add(new GroupVideoParticipant
        {
            Uhid = _sender.LocalUhid,
            Resolution = resolution,
            VideoCodec = videoCodec,
            BitrateKbps = bitrateKbps,
        });

        _sessions[session.Id] = session;

        // Broadcast Create invite to all invited peers.
        var msg = new GroupVideoSignalingMessage
        {
            Kind = GroupVideoSignalingKind.Create,
            SessionId = session.Id,
            FromUhid = _sender.LocalUhid,
            ToUhid = string.Empty, // broadcast
            InvitedUhids = invitedUhids.ToList(),
            Resolution = resolution,
            VideoCodec = videoCodec,
            BitrateKbps = bitrateKbps,
        };

        await BroadcastSignalingAsync(msg, ct).ConfigureAwait(false);

        _logger.LogInformation("Group video session {Id} created by {Host} with {N} invitees",
            session.Id, _sender.LocalUhid, invitedUhids.Count);

        SessionCreated?.Invoke(this, session);
        return session;
    }

    public async Task<bool> JoinAsync(
        Guid sessionId,
        VideoResolution resolution,
        string videoCodec,
        int bitrateKbps,
        CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            _logger.LogWarning("JoinAsync: session {Id} not found", sessionId);
            return false;
        }

        // Idempotent — add self if not already a participant.
        var existing = session.Participants.FirstOrDefault(p =>
            string.Equals(p.Uhid, _sender.LocalUhid, StringComparison.Ordinal));

        if (existing is null)
        {
            session.Participants.Add(new GroupVideoParticipant
            {
                Uhid = _sender.LocalUhid,
                Resolution = resolution,
                VideoCodec = videoCodec,
                BitrateKbps = bitrateKbps,
            });
        }
        else
        {
            existing.HasLeft = false;
            existing.Resolution = resolution;
            existing.VideoCodec = videoCodec;
            existing.BitrateKbps = bitrateKbps;
        }

        // Notify the host.
        var msg = new GroupVideoSignalingMessage
        {
            Kind = GroupVideoSignalingKind.Join,
            SessionId = sessionId,
            FromUhid = _sender.LocalUhid,
            ToUhid = session.HostUhid,
            Resolution = resolution,
            VideoCodec = videoCodec,
            BitrateKbps = bitrateKbps,
        };

        await UnicastSignalingAsync(msg, session.HostUhid, ct).ConfigureAwait(false);
        await UpdateTopologyAsync(session, ct).ConfigureAwait(false);

        ParticipantJoined?.Invoke(this, session);
        return true;
    }

    public async Task LeaveAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var self = session.Participants.FirstOrDefault(p =>
            string.Equals(p.Uhid, _sender.LocalUhid, StringComparison.Ordinal));

        if (self is not null)
            self.HasLeft = true;

        var msg = new GroupVideoSignalingMessage
        {
            Kind = GroupVideoSignalingKind.Leave,
            SessionId = sessionId,
            FromUhid = _sender.LocalUhid,
            ToUhid = session.HostUhid,
        };

        // Send to host; fall back to broadcast if no route.
        if (string.Equals(session.HostUhid, _sender.LocalUhid, StringComparison.Ordinal))
        {
            // We ARE the host — broadcast to remaining participants.
            msg.ToUhid = string.Empty;
            await BroadcastSignalingAsync(msg, ct).ConfigureAwait(false);
        }
        else
        {
            await UnicastSignalingAsync(msg, session.HostUhid, ct).ConfigureAwait(false);
        }

        await UpdateTopologyAsync(session, ct).ConfigureAwait(false);
        ParticipantLeft?.Invoke(this, session);

        _logger.LogInformation("Left group video session {Id}", sessionId);
    }

    public async Task KickAsync(Guid sessionId, string participantUhid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(participantUhid);

        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        // Only the host may kick.
        if (!string.Equals(session.HostUhid, _sender.LocalUhid, StringComparison.Ordinal))
        {
            _logger.LogWarning("KickAsync: {Local} is not the host of session {Id}", _sender.LocalUhid, sessionId);
            return;
        }

        var target = session.Participants.FirstOrDefault(p =>
            string.Equals(p.Uhid, participantUhid, StringComparison.Ordinal));

        if (target is not null)
            target.HasLeft = true;

        var msg = new GroupVideoSignalingMessage
        {
            Kind = GroupVideoSignalingKind.Kick,
            SessionId = sessionId,
            FromUhid = _sender.LocalUhid,
            ToUhid = participantUhid,
        };

        await UnicastSignalingAsync(msg, participantUhid, ct).ConfigureAwait(false);
        await UpdateTopologyAsync(session, ct).ConfigureAwait(false);

        _logger.LogInformation("Kicked {Uhid} from session {Id}", participantUhid, sessionId);
    }

    public async Task SendFrameAsync(
        Guid sessionId,
        ReadOnlyMemory<byte> encodedPayload,
        uint sequence,
        bool isKeyframe,
        CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        var payload = VideoCallService.SerializeFrame(sessionId, sequence, encodedPayload.Span, isKeyframe);
        byte priority = isKeyframe ? (byte)64 : (byte)32;

        if (session.Topology == VideoTopology.Sfu && session.SfuRelayUhid is not null)
        {
            // SFU mode — unicast only to the relay node.
            await SendVideoFramePacketAsync(payload, session.SfuRelayUhid, priority, ct).ConfigureAwait(false);
        }
        else
        {
            // FullMesh — fan out to every active non-self participant.
            var targets = session.Participants
                .Where(p => !p.HasLeft && !string.Equals(p.Uhid, _sender.LocalUhid, StringComparison.Ordinal))
                .ToList();

            foreach (var participant in targets)
            {
                await SendVideoFramePacketAsync(payload, participant.Uhid, priority, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task HandleAsync(MeshPacket packet, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        switch (packet.Type)
        {
            case PacketType.GroupVideoSignaling:
                await HandleSignalingAsync(packet, ct).ConfigureAwait(false);
                break;

            case PacketType.VideoFrame:
                HandleFrame(packet);
                break;

            default:
                _logger.LogDebug("GroupVideoService.HandleAsync ignoring packet type {Type}", packet.Type);
                break;
        }
    }

    public IReadOnlyList<GroupVideoSession> GetActiveSessions()
        => _sessions.Values.Where(s => s.IsActive).ToArray();

    // ─── Topology management ─────────────────────────────────────────────────

    private async Task UpdateTopologyAsync(GroupVideoSession session, CancellationToken ct)
    {
        var activeCount = session.Participants.Count(p => !p.HasLeft);
        var previousTopology = session.Topology;

        if (activeCount >= ProtocolConstants.SfuThresholdParticipants)
        {
            session.Topology = VideoTopology.Sfu;

            // Select relay: first active participant that is NOT the local UHID;
            // if none exists, fall back to self.
            var relay = session.Participants
                .Where(p => !p.HasLeft && !string.Equals(p.Uhid, _sender.LocalUhid, StringComparison.Ordinal))
                .Select(p => p.Uhid)
                .FirstOrDefault() ?? _sender.LocalUhid;

            session.SfuRelayUhid = relay;
        }
        else
        {
            session.Topology = VideoTopology.FullMesh;
            session.SfuRelayUhid = null;
        }

        if (session.Topology != previousTopology)
        {
            _logger.LogInformation(
                "Session {Id} topology changed: {Prev} → {Next} (relay={Relay})",
                session.Id, previousTopology, session.Topology, session.SfuRelayUhid ?? "none");

            TopologyChanged?.Invoke(this, session);

            // Only the host sends SfuAssigned to avoid duplicate notifications.
            if (string.Equals(session.HostUhid, _sender.LocalUhid, StringComparison.Ordinal)
                && session.Topology == VideoTopology.Sfu)
            {
                await BroadcastSfuAssignedAsync(session, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task BroadcastSfuAssignedAsync(GroupVideoSession session, CancellationToken ct)
    {
        var msg = new GroupVideoSignalingMessage
        {
            Kind = GroupVideoSignalingKind.SfuAssigned,
            SessionId = session.Id,
            FromUhid = _sender.LocalUhid,
            ToUhid = string.Empty, // broadcast
            SfuRelayUhid = session.SfuRelayUhid,
        };

        // Send to each active participant individually so routing is applied per-hop.
        var targets = session.Participants
            .Where(p => !p.HasLeft && !string.Equals(p.Uhid, _sender.LocalUhid, StringComparison.Ordinal))
            .ToList();

        foreach (var participant in targets)
        {
            var unicast = new GroupVideoSignalingMessage
            {
                Kind = GroupVideoSignalingKind.SfuAssigned,
                SessionId = session.Id,
                FromUhid = _sender.LocalUhid,
                ToUhid = participant.Uhid,
                SfuRelayUhid = session.SfuRelayUhid,
            };
            await UnicastSignalingAsync(unicast, participant.Uhid, ct).ConfigureAwait(false);
        }
    }

    // ─── Signaling dispatch ──────────────────────────────────────────────────

    private async Task HandleSignalingAsync(MeshPacket packet, CancellationToken ct)
    {
        GroupVideoSignalingMessage? body;
        try
        {
            body = JsonSerializer.Deserialize<GroupVideoSignalingMessage>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "GroupVideo: failed to deserialize signaling from {Id}", packet.Id);
            return;
        }

        if (body is null) return;

        // Accept if addressed to us or broadcast (empty ToUhid).
        var toUs = string.IsNullOrEmpty(body.ToUhid) ||
                   string.Equals(body.ToUhid, _sender.LocalUhid, StringComparison.Ordinal);
        if (!toUs) return;

        switch (body.Kind)
        {
            case GroupVideoSignalingKind.Create:
                await HandleCreateAsync(body, ct).ConfigureAwait(false);
                break;

            case GroupVideoSignalingKind.Join:
                await HandleJoinAsync(body, ct).ConfigureAwait(false);
                break;

            case GroupVideoSignalingKind.Leave:
                await HandleLeaveAsync(body, ct).ConfigureAwait(false);
                break;

            case GroupVideoSignalingKind.Kick:
                await HandleKickAsync(body, ct).ConfigureAwait(false);
                break;

            case GroupVideoSignalingKind.SfuAssigned:
                await HandleSfuAssignedAsync(body, ct).ConfigureAwait(false);
                break;
        }
    }

    private Task HandleCreateAsync(GroupVideoSignalingMessage body, CancellationToken ct)
    {
        // Ignore our own broadcasts.
        if (string.Equals(body.FromUhid, _sender.LocalUhid, StringComparison.Ordinal))
            return Task.CompletedTask;

        // Already known — idempotent.
        if (_sessions.ContainsKey(body.SessionId))
            return Task.CompletedTask;

        var session = new GroupVideoSession
        {
            Id = body.SessionId,
            HostUhid = body.FromUhid,
        };

        // Add the host as the first participant.
        session.Participants.Add(new GroupVideoParticipant
        {
            Uhid = body.FromUhid,
            Resolution = body.Resolution,
            VideoCodec = body.VideoCodec,
            BitrateKbps = body.BitrateKbps,
        });

        _sessions[session.Id] = session;

        _logger.LogInformation(
            "Received group video Create for session {Id} from host {Host}",
            session.Id, body.FromUhid);

        SessionCreated?.Invoke(this, session);
        return Task.CompletedTask;
    }

    private async Task HandleJoinAsync(GroupVideoSignalingMessage body, CancellationToken ct)
    {
        // We are the host receiving a Join from a participant.
        if (!_sessions.TryGetValue(body.SessionId, out var session)) return;

        if (!string.Equals(session.HostUhid, _sender.LocalUhid, StringComparison.Ordinal))
            return; // Only the host processes incoming joins.

        var existing = session.Participants.FirstOrDefault(p =>
            string.Equals(p.Uhid, body.FromUhid, StringComparison.Ordinal));

        if (existing is null)
        {
            session.Participants.Add(new GroupVideoParticipant
            {
                Uhid = body.FromUhid,
                Resolution = body.Resolution,
                VideoCodec = body.VideoCodec,
                BitrateKbps = body.BitrateKbps,
            });
        }
        else
        {
            existing.HasLeft = false;
            existing.Resolution = body.Resolution;
            existing.VideoCodec = body.VideoCodec;
            existing.BitrateKbps = body.BitrateKbps;
        }

        await UpdateTopologyAsync(session, ct).ConfigureAwait(false);

        _logger.LogInformation("{Uhid} joined session {Id}", body.FromUhid, body.SessionId);
        ParticipantJoined?.Invoke(this, session);
    }

    private async Task HandleLeaveAsync(GroupVideoSignalingMessage body, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(body.SessionId, out var session)) return;

        var participant = session.Participants.FirstOrDefault(p =>
            string.Equals(p.Uhid, body.FromUhid, StringComparison.Ordinal));

        if (participant is not null)
            participant.HasLeft = true;

        await UpdateTopologyAsync(session, ct).ConfigureAwait(false);

        _logger.LogInformation("{Uhid} left session {Id}", body.FromUhid, body.SessionId);
        ParticipantLeft?.Invoke(this, session);
    }

    private async Task HandleKickAsync(GroupVideoSignalingMessage body, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(body.SessionId, out var session)) return;

        // Mark the kicked participant (could be us).
        var participant = session.Participants.FirstOrDefault(p =>
            string.Equals(p.Uhid, body.ToUhid, StringComparison.Ordinal));

        if (participant is not null)
            participant.HasLeft = true;

        await UpdateTopologyAsync(session, ct).ConfigureAwait(false);

        _logger.LogInformation("{Uhid} was kicked from session {Id}", body.ToUhid, body.SessionId);
        ParticipantLeft?.Invoke(this, session);
    }

    private Task HandleSfuAssignedAsync(GroupVideoSignalingMessage body, CancellationToken _ct)
    {
        if (!_sessions.TryGetValue(body.SessionId, out var session)) return Task.CompletedTask;

        session.SfuRelayUhid = body.SfuRelayUhid;
        session.Topology = VideoTopology.Sfu;

        _logger.LogInformation(
            "Session {Id} SFU relay assigned: {Relay}", body.SessionId, body.SfuRelayUhid ?? "(none)");

        TopologyChanged?.Invoke(this, session);
        return Task.CompletedTask;
    }

    // ─── Frame handling ──────────────────────────────────────────────────────

    private void HandleFrame(MeshPacket packet)
    {
        var frame = VideoCallService.TryDeserializeFrame(packet);
        if (frame is null) return;

        // The CallId field carries the group session ID.
        if (!_sessions.ContainsKey(frame.CallId)) return;

        FrameReceived?.Invoke(this, frame);
    }

    // ─── Sending helpers ─────────────────────────────────────────────────────

    private async Task UnicastSignalingAsync(
        GroupVideoSignalingMessage message,
        string destinationUhid,
        CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var packet = new MeshPacket
        {
            Type = PacketType.GroupVideoSignaling,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = destinationUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 32,
            Payload = payload,
        };

        var route = await _routing.FindRouteAsync(destinationUhid, ct).ConfigureAwait(false);
        if (route is not null)
            await _sender.SendAsync(packet, route.NextHopUhid, ct).ConfigureAwait(false);
        else
            await _sender.BroadcastAsync(packet, ct).ConfigureAwait(false);

        await _incentives.RecordRelayAsync(_sender.LocalUhid, packet, ct).ConfigureAwait(false);
    }

    private async Task BroadcastSignalingAsync(GroupVideoSignalingMessage message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var packet = new MeshPacket
        {
            Type = PacketType.GroupVideoSignaling,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = string.Empty,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 32,
            Payload = payload,
        };

        await _sender.BroadcastAsync(packet, ct).ConfigureAwait(false);
        await _incentives.RecordRelayAsync(_sender.LocalUhid, packet, ct).ConfigureAwait(false);
    }

    private async Task SendVideoFramePacketAsync(
        byte[] framePayload,
        string destinationUhid,
        byte priority,
        CancellationToken ct)
    {
        var packet = new MeshPacket
        {
            Type = PacketType.VideoFrame,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = destinationUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = priority,
            Payload = framePayload,
        };

        var route = await _routing.FindRouteAsync(destinationUhid, ct).ConfigureAwait(false);
        if (route is not null)
            await _sender.SendAsync(packet, route.NextHopUhid, ct).ConfigureAwait(false);
        else
            await _sender.BroadcastAsync(packet, ct).ConfigureAwait(false);
    }

    // ─── Private default implementations ────────────────────────────────────

    private sealed class DefaultIncentiveProvider : IAetherNetIncentiveProvider
    {
    }
}
