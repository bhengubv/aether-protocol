// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text.Json;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Voice;
using AetherNet.Voice.Models;
using Xunit;

namespace AetherNet.Core.Tests;

public class GroupVoiceCallServiceTests
{
    private const string Host = "host-uhid";
    private const string Alice = "alice-uhid";
    private const string Bob = "bob-uhid";
    private const string Carol = "carol-uhid";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static (GroupVoiceCallService svc, FakeMeshSender sender, IRoutingService routing) NewService(string localUhid)
    {
        var sender = new FakeMeshSender(localUhid);
        var routing = new RoutingService(sender);
        var svc = new GroupVoiceCallService(sender, routing, new NullGroupKeyProvider());
        return (svc, sender, routing);
    }

    private static GroupVoiceSignalingMessage Decode(MeshPacket p)
        => JsonSerializer.Deserialize<GroupVoiceSignalingMessage>(p.Payload, JsonOptions)!;

    /// <summary>Builds a group voice frame payload in the format GroupVoiceCallService emits.</summary>
    private static byte[] BuildGroupFramePayload(Guid callId, uint sequence, byte[] encrypted, bool isSilence, uint keyGeneration)
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

    private static MeshPacket BuildSignalingPacket(GroupSignalingKind kind, Guid callId, string from, string to,
        string codec = "opus", int rate = 16_000, string? affected = null,
        uint keyGen = 0, byte[]? wrapped = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new GroupVoiceSignalingMessage
        {
            Kind = kind,
            CallId = callId,
            FromUhid = from,
            ToUhid = to,
            Codec = codec,
            SampleRateHz = rate,
            AffectedUhid = affected ?? string.Empty,
            KeyGeneration = keyGen,
            WrappedKeyForRecipient = wrapped ?? Array.Empty<byte>(),
        }, JsonOptions);
        return new MeshPacket
        {
            Type = PacketType.VoiceSignaling,
            SourceUhid = from,
            DestinationUhid = to,
            Payload = body,
        };
    }

    // ── StartAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_HostCreatesSessionAndInvitesParticipants()
    {
        var (svc, sender, _) = NewService(Host);

        var session = await svc.StartAsync(new[] { Alice, Bob }, "opus", 16_000);

        Assert.Equal(Host, session.HostUhid);
        Assert.Equal(GroupCallState.Pending, session.State);
        Assert.Equal("opus", session.Codec);
        Assert.Equal(16_000, session.SampleRateHz);
        Assert.Contains(Host, session.Participants);
        Assert.Contains(Alice, session.Participants);
        Assert.Contains(Bob, session.Participants);

        // One invite per non-host participant. With no peers, every signaling packet broadcasts.
        var invites = sender.Broadcasts.Where(p => p.Type == PacketType.VoiceSignaling).ToArray();
        Assert.Equal(2, invites.Length);
        Assert.All(invites, p => Assert.Equal(GroupSignalingKind.Invite, Decode(p).Kind));
    }

    [Fact]
    public async Task StartAsync_RejectsEmptyParticipants()
    {
        var (svc, _, _) = NewService(Host);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.StartAsync(Array.Empty<string>(), "opus", 16_000));
    }

    [Fact]
    public async Task StartAsync_RejectsEmptyCodec()
    {
        var (svc, _, _) = NewService(Host);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.StartAsync(new[] { Alice }, string.Empty, 16_000));
    }

    // ── Incoming Invite ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_Invite_RaisesGroupCallInvited()
    {
        var (svc, _, _) = NewService(Alice);

        GroupVoiceCallSession? raised = null;
        svc.GroupCallInvited += (_, s) => raised = s;

        var callId = Guid.NewGuid();
        // The participant-discriminator hint requires AffectedUhid != empty so this path is taken.
        await svc.HandleAsync(BuildSignalingPacket(GroupSignalingKind.Invite, callId, Host, Alice, affected: Alice));

        Assert.NotNull(raised);
        Assert.Equal(callId, raised!.Id);
        Assert.Equal(Host, raised.HostUhid);
        Assert.Equal(GroupCallState.Pending, raised.State);
        Assert.Contains(Host, raised.Participants);
        Assert.Contains(Alice, raised.Participants);
    }

    [Fact]
    public async Task HandleAsync_InviteForOtherRecipient_Ignored()
    {
        var (svc, _, _) = NewService(Alice);

        var raisedCount = 0;
        svc.GroupCallInvited += (_, _) => raisedCount++;

        await svc.HandleAsync(BuildSignalingPacket(GroupSignalingKind.Invite, Guid.NewGuid(), Host, Bob, affected: Bob));

        Assert.Equal(0, raisedCount);
    }

    // ── Accept (host-side handling) ─────────────────────────────────

    [Fact]
    public async Task HandleAsync_Accept_TransitionsToActiveAndRotatesKey()
    {
        var (svc, sender, _) = NewService(Host);
        var session = await svc.StartAsync(new[] { Alice }, "opus", 16_000);
        sender.Clear();

        GroupVoiceCallSession? activeRaised = null;
        svc.GroupCallActive += (_, s) => activeRaised = s;

        // Use AffectedUhid to disambiguate from a 1:1 signaling envelope (per GroupVoice heuristic).
        await svc.HandleAsync(BuildSignalingPacket(GroupSignalingKind.Accept, session.Id, Alice, Host, affected: Alice));

        Assert.NotNull(activeRaised);
        Assert.Equal(GroupCallState.Active, activeRaised!.State);
        Assert.NotNull(activeRaised.StartedAt);
        Assert.Contains(Alice, activeRaised.Participants);

        // Host should have rotated the key — at least one RotateKey signaling packet emitted.
        var rotates = sender.Broadcasts
            .Where(p => p.Type == PacketType.VoiceSignaling)
            .Select(Decode)
            .Where(m => m.Kind == GroupSignalingKind.RotateKey)
            .ToArray();
        Assert.NotEmpty(rotates);
        Assert.True(rotates.All(m => m.KeyGeneration >= 2));
    }

    // ── Frame fan-out ──────────────────────────────────────────────

    [Fact]
    public async Task SendFrameAsync_Active_FansOutToEveryNonHostParticipant()
    {
        var (svc, sender, _) = NewService(Host);
        var session = await svc.StartAsync(new[] { Alice, Bob, Carol }, "opus", 16_000);

        // Activate the session by simulating one acceptor.
        await svc.HandleAsync(BuildSignalingPacket(GroupSignalingKind.Accept, session.Id, Alice, Host, affected: Alice));
        sender.Clear();

        var payload = new byte[] { 0xAA, 0xBB, 0xCC };
        await svc.SendFrameAsync(session.Id, payload, sequence: 1, isSilence: false);

        var voiceFrames = sender.Broadcasts.Where(p => p.Type == PacketType.VoiceCall).ToArray();
        // 3 non-host participants → 3 outbound packets.
        Assert.Equal(3, voiceFrames.Length);
        Assert.All(voiceFrames, p => Assert.Equal(Host, p.SourceUhid));
        var destinations = voiceFrames.Select(p => p.DestinationUhid).ToHashSet();
        Assert.Contains(Alice, destinations);
        Assert.Contains(Bob, destinations);
        Assert.Contains(Carol, destinations);
        Assert.DoesNotContain(Host, destinations);
    }

    [Fact]
    public async Task SendFrameAsync_BeforeActive_NoPacketEmitted()
    {
        var (svc, sender, _) = NewService(Host);
        var session = await svc.StartAsync(new[] { Alice }, "opus", 16_000);
        sender.Clear();

        // Session is still Pending — frames should not flow.
        await svc.SendFrameAsync(session.Id, new byte[] { 1, 2, 3 }, sequence: 1);

        Assert.DoesNotContain(sender.Broadcasts, p => p.Type == PacketType.VoiceCall);
        Assert.DoesNotContain(sender.Unicasts, u => u.Packet.Type == PacketType.VoiceCall);
    }

    // ── Group frame round-trip on receive side ──────────────────────

    [Fact]
    public async Task HandleAsync_GroupFrame_DecryptsAndRaisesGroupFrameReceived()
    {
        // Alice is invited by Host, accepts, and receives a key-rotation. Then a frame comes in.
        var (svc, _, _) = NewService(Alice);

        var callId = Guid.NewGuid();
        await svc.HandleAsync(BuildSignalingPacket(GroupSignalingKind.Invite, callId, Host, Alice, affected: Alice));

        // Host rotates key — NullGroupKeyProvider.UnwrapAsync returns the wrapped bytes verbatim.
        var key = new byte[32];
        await svc.HandleAsync(BuildSignalingPacket(
            GroupSignalingKind.RotateKey, callId, Host, Alice,
            affected: string.Empty, keyGen: 1, wrapped: key));

        VoiceFrame? received = null;
        svc.GroupFrameReceived += (_, f) => received = f;

        // Build a group frame packet from Bob (another participant). With NullGroupKeyProvider,
        // encryption is identity, so the encrypted bytes equal the plaintext bytes.
        var plaintext = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var framePayload = BuildGroupFramePayload(callId, sequence: 13, plaintext, isSilence: false, keyGeneration: 1);
        var packet = new MeshPacket
        {
            Type = PacketType.VoiceCall,
            SourceUhid = Bob,
            DestinationUhid = Alice,
            Payload = framePayload,
        };
        await svc.HandleAsync(packet);

        Assert.NotNull(received);
        Assert.Equal(callId, received!.CallId);
        Assert.Equal(13u, received.Sequence);
        Assert.Equal(Bob, received.SenderUhid);
        Assert.Equal(plaintext, received.EncodedPayload);
    }

    // ── Participant Leave ───────────────────────────────────────────

    [Fact]
    public async Task LeaveAsync_BroadcastsLeaveAndRemovesSelfFromParticipants()
    {
        // Alice was invited and her session is Pending locally.
        var (svc, sender, _) = NewService(Alice);

        var callId = Guid.NewGuid();
        await svc.HandleAsync(BuildSignalingPacket(GroupSignalingKind.Invite, callId, Host, Alice, affected: Alice));
        sender.Clear();

        await svc.LeaveAsync(callId);

        var leaves = sender.Broadcasts
            .Where(p => p.Type == PacketType.VoiceSignaling)
            .Select(Decode)
            .Where(m => m.Kind == GroupSignalingKind.Leave)
            .ToArray();
        Assert.NotEmpty(leaves);
        Assert.All(leaves, m => Assert.Equal(Alice, m.AffectedUhid));

        // Alice has removed herself from her local view of the participants.
        var session = svc.GetActiveCalls().FirstOrDefault(c => c.Id == callId);
        if (session is not null)
            Assert.DoesNotContain(Alice, session.Participants);
    }

    [Fact]
    public async Task HandleAsync_LeaveFromParticipant_HostRemovesAndRotatesKey()
    {
        var (svc, sender, _) = NewService(Host);
        var session = await svc.StartAsync(new[] { Alice, Bob }, "opus", 16_000);
        // Activate via Alice's accept.
        await svc.HandleAsync(BuildSignalingPacket(GroupSignalingKind.Accept, session.Id, Alice, Host, affected: Alice));
        sender.Clear();

        var membershipChangedFired = false;
        svc.MembershipChanged += (_, _) => membershipChangedFired = true;

        // Bob leaves.
        await svc.HandleAsync(BuildSignalingPacket(GroupSignalingKind.Leave, session.Id, Bob, Host, affected: Bob));

        Assert.True(membershipChangedFired);
        var refreshed = svc.GetActiveCalls().Single(c => c.Id == session.Id);
        Assert.DoesNotContain(Bob, refreshed.Participants);

        // Host should have triggered another key rotation.
        var rotates = sender.Broadcasts
            .Where(p => p.Type == PacketType.VoiceSignaling)
            .Select(Decode)
            .Where(m => m.Kind == GroupSignalingKind.RotateKey)
            .ToArray();
        Assert.NotEmpty(rotates);
    }

    // ── Host EndAsync ───────────────────────────────────────────────

    [Fact]
    public async Task EndAsync_HostBroadcastsEndAndMarksSessionEnded()
    {
        var (svc, sender, _) = NewService(Host);
        var session = await svc.StartAsync(new[] { Alice, Bob }, "opus", 16_000);
        sender.Clear();

        GroupVoiceCallSession? endedRaised = null;
        svc.GroupCallEnded += (_, s) => endedRaised = s;

        await svc.EndAsync(session.Id);

        Assert.NotNull(endedRaised);
        Assert.Equal(GroupCallState.Ended, endedRaised!.State);
        Assert.NotNull(endedRaised.EndedAt);
        Assert.DoesNotContain(svc.GetActiveCalls(), c => c.Id == session.Id);

        var ends = sender.Broadcasts
            .Where(p => p.Type == PacketType.VoiceSignaling)
            .Select(Decode)
            .Where(m => m.Kind == GroupSignalingKind.End)
            .ToArray();
        Assert.NotEmpty(ends);
    }

    [Fact]
    public async Task EndAsync_NonHost_NoOp()
    {
        // Alice receives an invite (so the session exists locally), but she is not the host.
        var (svc, sender, _) = NewService(Alice);
        var callId = Guid.NewGuid();
        await svc.HandleAsync(BuildSignalingPacket(GroupSignalingKind.Invite, callId, Host, Alice, affected: Alice));
        sender.Clear();

        await svc.EndAsync(callId);

        Assert.Empty(sender.Broadcasts);
        Assert.Empty(sender.Unicasts);
    }

    // ── Remote End delivered to participant ─────────────────────────

    [Fact]
    public async Task HandleAsync_RemoteEnd_EndsSessionLocally()
    {
        var (svc, _, _) = NewService(Alice);
        var callId = Guid.NewGuid();
        await svc.HandleAsync(BuildSignalingPacket(GroupSignalingKind.Invite, callId, Host, Alice, affected: Alice));

        GroupVoiceCallSession? endedRaised = null;
        svc.GroupCallEnded += (_, s) => endedRaised = s;

        await svc.HandleAsync(BuildSignalingPacket(GroupSignalingKind.End, callId, Host, Alice));

        Assert.NotNull(endedRaised);
        Assert.Equal(GroupCallState.Ended, endedRaised!.State);
    }
}
