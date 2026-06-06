// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Extensibility;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Voice.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Voice;

/// <summary>
/// Default group voice service. Host-driven membership and key rotation; each
/// participant encrypts outbound frames once and unicasts to every other participant
/// (small fan-out is fine for the typical 3–8 participant target).
/// </summary>
public sealed class GroupVoiceCallService : IGroupVoiceCallService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IRoutingService _routing;
    private readonly IGroupKeyProvider _keys;
    private readonly IAetherNetIncentiveProvider _incentives;
    private readonly ILogger<GroupVoiceCallService> _logger;

    private readonly ConcurrentDictionary<Guid, GroupVoiceCallSession> _calls = new();
    private readonly ConcurrentDictionary<Guid, byte[]> _currentKey = new();

    public event EventHandler<GroupVoiceCallSession>? GroupCallInvited;
    public event EventHandler<GroupVoiceCallSession>? GroupCallActive;
    public event EventHandler<GroupVoiceCallSession>? GroupCallEnded;
    public event EventHandler<GroupVoiceCallSession>? MembershipChanged;
    public event EventHandler<VoiceFrame>? GroupFrameReceived;

    public GroupVoiceCallService(
        IMeshSender sender,
        IRoutingService routing,
        IGroupKeyProvider? keys = null,
        IAetherNetIncentiveProvider? incentives = null,
        ILogger<GroupVoiceCallService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _keys = keys ?? new NullGroupKeyProvider();
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _logger = logger ?? NullLogger<GroupVoiceCallService>.Instance;
    }

    public async Task<GroupVoiceCallSession> StartAsync(IReadOnlyList<string> initialParticipants, string codec, int sampleRateHz, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialParticipants);
        if (initialParticipants.Count == 0)
            throw new ArgumentException("At least one initial participant required", nameof(initialParticipants));
        ArgumentException.ThrowIfNullOrEmpty(codec);

        var session = new GroupVoiceCallSession
        {
            HostUhid = _sender.LocalUhid,
            Participants = new[] { _sender.LocalUhid }.Concat(initialParticipants).Distinct(StringComparer.Ordinal).ToArray(),
            State = GroupCallState.Pending,
            Codec = codec,
            SampleRateHz = sampleRateHz,
            KeyGeneration = 1,
        };
        _calls[session.Id] = session;
        _currentKey[session.Id] = _keys.GenerateSenderKey();

        foreach (var uhid in initialParticipants.Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(uhid, _sender.LocalUhid, StringComparison.Ordinal)) continue;
            await SendSignalingAsync(uhid, new GroupVoiceSignalingMessage
            {
                Kind = GroupSignalingKind.Invite,
                CallId = session.Id,
                FromUhid = _sender.LocalUhid,
                ToUhid = uhid,
                Codec = codec,
                SampleRateHz = sampleRateHz,
                AffectedUhid = uhid,
            }, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Group voice call {Id} started by {Host} with {Count} participants",
            session.Id, _sender.LocalUhid, session.Participants.Count);
        return session;
    }

    public async Task InviteAsync(Guid callId, string uhid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(uhid);
        if (!_calls.TryGetValue(callId, out var session) || session.HostUhid != _sender.LocalUhid) return;

        if (!session.Participants.Contains(uhid, StringComparer.Ordinal))
        {
            session.Participants = session.Participants.Append(uhid).ToArray();
            MembershipChanged?.Invoke(this, session);
        }

        await SendSignalingAsync(uhid, new GroupVoiceSignalingMessage
        {
            Kind = GroupSignalingKind.Invite,
            CallId = callId,
            FromUhid = _sender.LocalUhid,
            ToUhid = uhid,
            Codec = session.Codec,
            SampleRateHz = session.SampleRateHz,
            AffectedUhid = uhid,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task KickAsync(Guid callId, string uhid, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session) || session.HostUhid != _sender.LocalUhid) return;
        if (!session.Participants.Contains(uhid, StringComparer.Ordinal)) return;

        session.Participants = session.Participants.Where(p => !string.Equals(p, uhid, StringComparison.Ordinal)).ToArray();
        await BroadcastSignalingAsync(session, new GroupVoiceSignalingMessage
        {
            Kind = GroupSignalingKind.Kick,
            CallId = callId,
            FromUhid = _sender.LocalUhid,
            AffectedUhid = uhid,
        }, cancellationToken).ConfigureAwait(false);
        MembershipChanged?.Invoke(this, session);
        await RotateKeyAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task AcceptAsync(Guid callId, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session)) return;

        await SendSignalingAsync(session.HostUhid, new GroupVoiceSignalingMessage
        {
            Kind = GroupSignalingKind.Accept,
            CallId = callId,
            FromUhid = _sender.LocalUhid,
            ToUhid = session.HostUhid,
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task DeclineAsync(Guid callId, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session)) return Task.CompletedTask;
        return SendSignalingAsync(session.HostUhid, new GroupVoiceSignalingMessage
        {
            Kind = GroupSignalingKind.Decline,
            CallId = callId,
            FromUhid = _sender.LocalUhid,
            ToUhid = session.HostUhid,
        }, cancellationToken);
    }

    public async Task LeaveAsync(Guid callId, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session)) return;

        await BroadcastSignalingAsync(session, new GroupVoiceSignalingMessage
        {
            Kind = GroupSignalingKind.Leave,
            CallId = callId,
            FromUhid = _sender.LocalUhid,
            AffectedUhid = _sender.LocalUhid,
        }, cancellationToken).ConfigureAwait(false);

        session.Participants = session.Participants.Where(p => !string.Equals(p, _sender.LocalUhid, StringComparison.Ordinal)).ToArray();
        if (session.Participants.Count == 0)
        {
            session.State = GroupCallState.Ended;
            session.EndedAt = DateTime.UtcNow;
            GroupCallEnded?.Invoke(this, session);
        }
        else if (string.Equals(session.HostUhid, _sender.LocalUhid, StringComparison.Ordinal) == false)
        {
            MembershipChanged?.Invoke(this, session);
        }
    }

    public async Task EndAsync(Guid callId, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session) || session.HostUhid != _sender.LocalUhid) return;
        if (session.State == GroupCallState.Ended) return;

        await BroadcastSignalingAsync(session, new GroupVoiceSignalingMessage
        {
            Kind = GroupSignalingKind.End,
            CallId = callId,
            FromUhid = _sender.LocalUhid,
        }, cancellationToken).ConfigureAwait(false);

        session.State = GroupCallState.Ended;
        session.EndedAt = DateTime.UtcNow;
        GroupCallEnded?.Invoke(this, session);
    }

    public async Task SendFrameAsync(Guid callId, ReadOnlyMemory<byte> encodedPayload, uint sequence, bool isSilence = false, CancellationToken cancellationToken = default)
    {
        if (!_calls.TryGetValue(callId, out var session) || session.State != GroupCallState.Active) return;
        if (!_currentKey.TryGetValue(callId, out var key)) return;

        var encrypted = _keys.EncryptFrame(key, encodedPayload.Span);
        var payload = SerializeGroupFrame(callId, sequence, encrypted, isSilence, session.KeyGeneration);

        foreach (var peer in session.Participants)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (string.Equals(peer, _sender.LocalUhid, StringComparison.Ordinal)) continue;

            var packet = new MeshPacket
            {
                Type = PacketType.VoiceCall,
                SourceUhid = _sender.LocalUhid,
                DestinationUhid = peer,
                Ttl = ProtocolConstants.DefaultTtl,
                Priority = 64,
                Payload = payload,
            };
            var route = await _routing.FindRouteAsync(peer, cancellationToken).ConfigureAwait(false);
            if (route is not null)
                await _sender.SendAsync(packet, route.NextHopUhid, cancellationToken).ConfigureAwait(false);
            else
                await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type == PacketType.VoiceSignaling)
        {
            await HandleSignalingAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        else if (packet.Type == PacketType.VoiceCall)
        {
            HandleFrame(packet);
        }
    }

    public IReadOnlyList<GroupVoiceCallSession> GetActiveCalls()
        => _calls.Values.Where(c => c.State is GroupCallState.Pending or GroupCallState.Active).ToArray();

    private async Task HandleSignalingAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        // Group vs 1-to-1 signaling are discriminated by JSON shape (group has "key_generation" / "wrapped_key_for_recipient").
        // For simplicity and forward-compat: try group decode first; fall through silently if it looks like a 1-to-1 message.
        GroupVoiceSignalingMessage? body;
        try
        {
            body = JsonSerializer.Deserialize<GroupVoiceSignalingMessage>(packet.Payload, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }
        if (body is null) return;
        // Heuristic: must have a CallId and a non-default Kind. If the payload is a 1-to-1 VoiceSignalingMessage,
        // its Kind values overlap (Offer=0 etc.) — but the AffectedUhid / KeyGeneration / WrappedKeyForRecipient
        // fields will all be empty/zero. We let the outer Voice service process those instead.
        if (body.AffectedUhid.Length == 0 && body.KeyGeneration == 0 && body.WrappedKeyForRecipient.Length == 0
            && body.Kind is GroupSignalingKind.Invite or GroupSignalingKind.Accept or GroupSignalingKind.Decline)
            return;

        switch (body.Kind)
        {
            case GroupSignalingKind.Invite:
            {
                if (!string.Equals(body.ToUhid, _sender.LocalUhid, StringComparison.Ordinal)) return;
                var session = _calls.GetOrAdd(body.CallId, _ => new GroupVoiceCallSession
                {
                    Id = body.CallId,
                    HostUhid = body.FromUhid,
                    Participants = new[] { body.FromUhid, _sender.LocalUhid },
                    State = GroupCallState.Pending,
                    Codec = body.Codec,
                    SampleRateHz = body.SampleRateHz,
                    KeyGeneration = 0,
                });
                GroupCallInvited?.Invoke(this, session);
                break;
            }
            case GroupSignalingKind.Accept:
            {
                if (!_calls.TryGetValue(body.CallId, out var session)) return;
                if (session.HostUhid != _sender.LocalUhid) return;
                if (!session.Participants.Contains(body.FromUhid, StringComparer.Ordinal))
                    session.Participants = session.Participants.Append(body.FromUhid).ToArray();
                if (session.State != GroupCallState.Active)
                {
                    session.State = GroupCallState.Active;
                    session.StartedAt = DateTime.UtcNow;
                    GroupCallActive?.Invoke(this, session);
                }
                else
                {
                    MembershipChanged?.Invoke(this, session);
                }
                await RotateKeyAsync(session, cancellationToken).ConfigureAwait(false);
                break;
            }
            case GroupSignalingKind.Decline:
            case GroupSignalingKind.Leave:
            {
                if (!_calls.TryGetValue(body.CallId, out var session)) return;
                var leaver = body.AffectedUhid.Length > 0 ? body.AffectedUhid : body.FromUhid;
                session.Participants = session.Participants.Where(p => !string.Equals(p, leaver, StringComparison.Ordinal)).ToArray();
                if (session.HostUhid == _sender.LocalUhid)
                    await RotateKeyAsync(session, cancellationToken).ConfigureAwait(false);
                MembershipChanged?.Invoke(this, session);
                break;
            }
            case GroupSignalingKind.Kick:
            {
                if (!_calls.TryGetValue(body.CallId, out var session)) return;
                if (string.Equals(body.AffectedUhid, _sender.LocalUhid, StringComparison.Ordinal))
                {
                    session.State = GroupCallState.Ended;
                    session.EndedAt = DateTime.UtcNow;
                    _currentKey.TryRemove(body.CallId, out _);
                    GroupCallEnded?.Invoke(this, session);
                }
                else
                {
                    session.Participants = session.Participants.Where(p => !string.Equals(p, body.AffectedUhid, StringComparison.Ordinal)).ToArray();
                    MembershipChanged?.Invoke(this, session);
                }
                break;
            }
            case GroupSignalingKind.RotateKey:
            {
                if (!_calls.TryGetValue(body.CallId, out var session)) return;
                var unwrapped = await _keys.UnwrapAsync(body.FromUhid, body.WrappedKeyForRecipient, cancellationToken).ConfigureAwait(false);
                if (unwrapped is not null)
                {
                    _currentKey[body.CallId] = unwrapped;
                    session.KeyGeneration = body.KeyGeneration;
                    if (session.State != GroupCallState.Active)
                    {
                        session.State = GroupCallState.Active;
                        session.StartedAt = DateTime.UtcNow;
                        GroupCallActive?.Invoke(this, session);
                    }
                }
                break;
            }
            case GroupSignalingKind.End:
            {
                if (!_calls.TryGetValue(body.CallId, out var session)) return;
                session.State = GroupCallState.Ended;
                session.EndedAt = DateTime.UtcNow;
                _currentKey.TryRemove(body.CallId, out _);
                GroupCallEnded?.Invoke(this, session);
                break;
            }
        }
    }

    private async Task RotateKeyAsync(GroupVoiceCallSession session, CancellationToken cancellationToken)
    {
        if (session.HostUhid != _sender.LocalUhid) return;
        var newKey = _keys.GenerateSenderKey();
        _currentKey[session.Id] = newKey;
        session.KeyGeneration++;

        foreach (var peer in session.Participants)
        {
            if (string.Equals(peer, _sender.LocalUhid, StringComparison.Ordinal)) continue;
            var wrapped = await _keys.WrapForAsync(peer, newKey, cancellationToken).ConfigureAwait(false);
            if (wrapped.Length == 0) continue;

            await SendSignalingAsync(peer, new GroupVoiceSignalingMessage
            {
                Kind = GroupSignalingKind.RotateKey,
                CallId = session.Id,
                FromUhid = _sender.LocalUhid,
                ToUhid = peer,
                KeyGeneration = session.KeyGeneration,
                WrappedKeyForRecipient = wrapped,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandleFrame(MeshPacket packet)
    {
        var (callId, sequence, timestampMs, isSilence, _, encrypted) = TryParseGroupFrame(packet) ?? default;
        if (callId == Guid.Empty) return;
        if (!_calls.TryGetValue(callId, out var session) || session.State != GroupCallState.Active) return;
        if (!_currentKey.TryGetValue(callId, out var key)) return;

        var plaintext = _keys.DecryptFrame(key, encrypted);
        if (plaintext is null) return;

        GroupFrameReceived?.Invoke(this, new VoiceFrame
        {
            CallId = callId,
            SenderUhid = packet.SourceUhid,
            Sequence = sequence,
            TimestampMs = timestampMs,
            IsSilence = isSilence,
            EncodedPayload = plaintext,
        });
    }

    private async Task SendSignalingAsync(string toUhid, GroupVoiceSignalingMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var packet = new MeshPacket
        {
            Type = PacketType.VoiceSignaling,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = toUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 32,
            Payload = payload,
        };
        var route = await _routing.FindRouteAsync(toUhid, cancellationToken).ConfigureAwait(false);
        if (route is not null)
            await _sender.SendAsync(packet, route.NextHopUhid, cancellationToken).ConfigureAwait(false);
        else
            await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        await _incentives.RecordRelayAsync(_sender.LocalUhid, packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task BroadcastSignalingAsync(GroupVoiceCallSession session, GroupVoiceSignalingMessage message, CancellationToken cancellationToken)
    {
        foreach (var peer in session.Participants)
        {
            if (string.Equals(peer, _sender.LocalUhid, StringComparison.Ordinal)) continue;
            message.ToUhid = peer;
            await SendSignalingAsync(peer, message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Group voice frame payload format:
    ///   [16] CallId (RFC 4122 BE)
    ///   [4]  Sequence (uint32 LE)
    ///   [8]  TimestampMs (int64 LE)
    ///   [1]  IsSilence (0/1)
    ///   [4]  KeyGeneration (uint32 LE)
    ///   [N]  EncryptedPayload
    /// </summary>
    internal static byte[] SerializeGroupFrame(Guid callId, uint sequence, ReadOnlySpan<byte> encrypted, bool isSilence, uint keyGeneration)
    {
        var buf = new byte[16 + 4 + 8 + 1 + 4 + encrypted.Length];
        if (!callId.TryWriteBytes(buf.AsSpan(0, 16), bigEndian: true, out _))
            throw new InvalidOperationException("Failed to write call id");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(20, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        buf[28] = isSilence ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(29, 4), keyGeneration);
        encrypted.CopyTo(buf.AsSpan(33));
        return buf;
    }

    private static (Guid CallId, uint Sequence, long TimestampMs, bool IsSilence, uint KeyGeneration, byte[] Encrypted)? TryParseGroupFrame(MeshPacket packet)
    {
        if (packet.Payload.Length < 33) return null;
        try
        {
            var span = packet.Payload.AsSpan();
            var callId = new Guid(span[..16], bigEndian: true);
            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
            var timestampMs = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(20, 8));
            var isSilence = span[28] == 1;
            var keyGeneration = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(29, 4));
            var encrypted = span[33..].ToArray();
            return (callId, sequence, timestampMs, isSilence, keyGeneration, encrypted);
        }
        catch
        {
            return null;
        }
    }

    private sealed class DefaultIncentiveProvider : IAetherNetIncentiveProvider
    {
    }
}
