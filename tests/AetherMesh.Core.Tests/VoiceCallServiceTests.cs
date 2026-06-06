// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text.Json;
using AetherMesh.Core.Tests.Fakes;
using AetherMesh.Protocol;
using AetherMesh.Routing;
using AetherMesh.Voice;
using AetherMesh.Voice.Models;
using Xunit;

namespace AetherMesh.Core.Tests;

public class VoiceCallServiceTests
{
    private const string Local = "local-uhid";
    private const string Remote = "remote-uhid";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Builds a 1:1 voice frame payload in the format VoiceCallService emits.</summary>
    private static byte[] BuildFramePayload(Guid callId, uint sequence, byte[] encoded, bool isSilence)
    {
        var buf = new byte[16 + 4 + 8 + 1 + encoded.Length];
        if (!callId.TryWriteBytes(buf.AsSpan(0, 16), bigEndian: true, out _))
            throw new InvalidOperationException("Failed to write call id");
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(20, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        buf[28] = isSilence ? (byte)1 : (byte)0;
        encoded.CopyTo(buf.AsSpan(29));
        return buf;
    }

    /// <summary>Parses a voice-frame payload back into its fields, mirroring the wire format.</summary>
    private static (Guid CallId, uint Sequence, bool IsSilence, byte[] Encoded) ParseFramePayload(byte[] payload)
    {
        if (payload.Length < 29) throw new ArgumentException("payload too short", nameof(payload));
        var span = payload.AsSpan();
        var callId = new Guid(span[..16], bigEndian: true);
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
        var isSilence = span[28] == 1;
        var encoded = span[29..].ToArray();
        return (callId, sequence, isSilence, encoded);
    }

    private static (VoiceCallService svc, FakeMeshSender sender, IRoutingService routing) NewService(string localUhid = Local)
    {
        var sender = new FakeMeshSender(localUhid);
        var routing = new RoutingService(sender);
        var svc = new VoiceCallService(sender, routing);
        return (svc, sender, routing);
    }

    /// <summary>Builds an Offer signaling packet as it would arrive from the wire.</summary>
    private static MeshPacket BuildOfferPacket(string fromUhid, string toUhid, Guid callId, params string[] codecs)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new VoiceSignalingMessage
        {
            Kind = SignalingKind.Offer,
            CallId = callId,
            FromUhid = fromUhid,
            ToUhid = toUhid,
            ProposedCodecs = codecs,
        }, JsonOptions);
        return new MeshPacket
        {
            Type = PacketType.VoiceSignaling,
            SourceUhid = fromUhid,
            DestinationUhid = toUhid,
            Payload = body,
        };
    }

    private static MeshPacket BuildAnswerPacket(string fromUhid, string toUhid, Guid callId, string codec, int sampleRateHz)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new VoiceSignalingMessage
        {
            Kind = SignalingKind.Answer,
            CallId = callId,
            FromUhid = fromUhid,
            ToUhid = toUhid,
            SelectedCodec = codec,
            SampleRateHz = sampleRateHz,
        }, JsonOptions);
        return new MeshPacket
        {
            Type = PacketType.VoiceSignaling,
            SourceUhid = fromUhid,
            DestinationUhid = toUhid,
            Payload = body,
        };
    }

    private static MeshPacket BuildHangupPacket(string fromUhid, string toUhid, Guid callId, HangupReason reason)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new VoiceSignalingMessage
        {
            Kind = SignalingKind.Hangup,
            CallId = callId,
            FromUhid = fromUhid,
            ToUhid = toUhid,
            Reason = reason,
        }, JsonOptions);
        return new MeshPacket
        {
            Type = PacketType.VoiceSignaling,
            SourceUhid = fromUhid,
            DestinationUhid = toUhid,
            Payload = body,
        };
    }

    // ── PlaceAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task PlaceAsync_BroadcastsOfferAndCreatesOutgoingSession()
    {
        var (svc, sender, _) = NewService();

        var session = await svc.PlaceAsync(Remote, new[] { "opus", "speex" });

        Assert.Equal(CallState.Outgoing, session.State);
        Assert.Equal(Local, session.CallerUhid);
        Assert.Equal(Remote, session.CalleeUhid);

        // No connected peers, so route discovery falls back to broadcast for the offer
        // (the routing service also emits an RREQ in parallel — we filter for the signaling packet).
        var signaling = sender.Broadcasts.Single(p => p.Type == PacketType.VoiceSignaling);

        var msg = JsonSerializer.Deserialize<VoiceSignalingMessage>(signaling.Payload, JsonOptions)!;
        Assert.Equal(SignalingKind.Offer, msg.Kind);
        Assert.Equal(session.Id, msg.CallId);
        Assert.Contains("opus", msg.ProposedCodecs);
    }

    [Fact]
    public async Task PlaceAsync_RejectsEmptyCodecs()
    {
        var (svc, _, _) = NewService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.PlaceAsync(Remote, Array.Empty<string>()));
    }

    [Fact]
    public async Task PlaceAsync_RejectsEmptyCallee()
    {
        var (svc, _, _) = NewService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.PlaceAsync(string.Empty, new[] { "opus" }));
    }

    // ── Incoming Offer via HandleAsync ──────────────────────────────

    [Fact]
    public async Task HandleAsync_IncomingOffer_RaisesIncomingCallEvent()
    {
        var (svc, _, _) = NewService();

        VoiceCallSession? raised = null;
        svc.IncomingCall += (_, s) => raised = s;

        var callId = Guid.NewGuid();
        await svc.HandleAsync(BuildOfferPacket("alice", Local, callId, "opus"));

        Assert.NotNull(raised);
        Assert.Equal(CallState.Incoming, raised!.State);
        Assert.Equal(callId, raised.Id);
        Assert.Equal("alice", raised.CallerUhid);
        Assert.Equal(Local, raised.CalleeUhid);

        var active = svc.GetActiveCalls();
        Assert.Single(active);
        Assert.Equal(callId, active[0].Id);
    }

    [Fact]
    public async Task HandleAsync_OfferToWrongRecipient_Ignored()
    {
        var (svc, _, _) = NewService();

        var raisedCount = 0;
        svc.IncomingCall += (_, _) => raisedCount++;

        // Note: ToUhid is "someone-else", not Local.
        await svc.HandleAsync(BuildOfferPacket("alice", "someone-else", Guid.NewGuid(), "opus"));

        Assert.Equal(0, raisedCount);
        Assert.Empty(svc.GetActiveCalls());
    }

    [Fact]
    public async Task HandleAsync_MalformedSignalingPayload_DoesNotThrow()
    {
        var (svc, _, _) = NewService();

        var bogus = new MeshPacket
        {
            Type = PacketType.VoiceSignaling,
            SourceUhid = "alice",
            DestinationUhid = Local,
            Payload = new byte[] { 0x7B, 0x01, 0x02 }, // not valid JSON
        };

        await svc.HandleAsync(bogus); // must not throw
        Assert.Empty(svc.GetActiveCalls());
    }

    // ── AnswerAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task AnswerAsync_TransitionsIncomingToConnected_AndSendsAnswer()
    {
        var (svc, sender, _) = NewService();
        var callId = Guid.NewGuid();
        await svc.HandleAsync(BuildOfferPacket("alice", Local, callId, "opus"));
        sender.Clear();

        var ok = await svc.AnswerAsync(callId, "opus", 16_000);

        Assert.True(ok);
        var session = svc.GetActiveCalls().Single(c => c.Id == callId);
        Assert.Equal(CallState.Connected, session.State);
        Assert.Equal("opus", session.Codec);
        Assert.Equal(16_000, session.SampleRateHz);
        Assert.NotNull(session.ConnectedAt);

        // Answer signaling packet is now on the wire (filter out the routing RREQ).
        var signaling = sender.Broadcasts.Single(p => p.Type == PacketType.VoiceSignaling);
        var msg = JsonSerializer.Deserialize<VoiceSignalingMessage>(signaling.Payload, JsonOptions)!;
        Assert.Equal(SignalingKind.Answer, msg.Kind);
        Assert.Equal("opus", msg.SelectedCodec);
    }

    [Fact]
    public async Task AnswerAsync_UnknownCallId_ReturnsFalse()
    {
        var (svc, _, _) = NewService();
        var ok = await svc.AnswerAsync(Guid.NewGuid(), "opus", 16_000);
        Assert.False(ok);
    }

    // ── HandleAsync Answer (caller-side connection) ─────────────────

    [Fact]
    public async Task HandleAsync_AnswerForOutgoingCall_TransitionsToConnected()
    {
        var (svc, sender, _) = NewService();

        var session = await svc.PlaceAsync(Remote, new[] { "opus" });
        sender.Clear();

        VoiceCallSession? connectedRaised = null;
        svc.CallConnected += (_, s) => connectedRaised = s;

        await svc.HandleAsync(BuildAnswerPacket(Remote, Local, session.Id, "opus", 16_000));

        Assert.NotNull(connectedRaised);
        Assert.Equal(CallState.Connected, connectedRaised!.State);
        Assert.Equal("opus", connectedRaised.Codec);
        Assert.Equal(16_000, connectedRaised.SampleRateHz);
    }

    // ── DeclineAsync ────────────────────────────────────────────────

    [Fact]
    public async Task DeclineAsync_EndsCallAndSendsHangup()
    {
        var (svc, sender, _) = NewService();
        var callId = Guid.NewGuid();
        await svc.HandleAsync(BuildOfferPacket("alice", Local, callId, "opus"));
        sender.Clear();

        VoiceCallSession? endedRaised = null;
        svc.CallEnded += (_, s) => endedRaised = s;

        await svc.DeclineAsync(callId);

        Assert.NotNull(endedRaised);
        Assert.Equal(CallState.Ended, endedRaised!.State);
        Assert.Equal(HangupReason.Declined, endedRaised.HangupReason);
        var signaling = sender.Broadcasts.Single(p => p.Type == PacketType.VoiceSignaling);
        var msg = JsonSerializer.Deserialize<VoiceSignalingMessage>(signaling.Payload, JsonOptions)!;
        Assert.Equal(SignalingKind.Hangup, msg.Kind);
    }

    // ── HangupAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task HangupAsync_ConnectedCall_SendsHangupAndEndsSession()
    {
        var (svc, sender, _) = NewService();
        var session = await svc.PlaceAsync(Remote, new[] { "opus" });
        // Connect the call from the caller's side.
        await svc.HandleAsync(BuildAnswerPacket(Remote, Local, session.Id, "opus", 16_000));
        sender.Clear();

        await svc.HangupAsync(session.Id);

        // Active calls excludes ended sessions, so the session is no longer active.
        Assert.DoesNotContain(svc.GetActiveCalls(), c => c.Id == session.Id);
        var signaling = sender.Broadcasts.Single(p => p.Type == PacketType.VoiceSignaling);
        var msg = JsonSerializer.Deserialize<VoiceSignalingMessage>(signaling.Payload, JsonOptions)!;
        Assert.Equal(SignalingKind.Hangup, msg.Kind);
    }

    [Fact]
    public async Task HangupAsync_NetworkFailureReason_MarksSessionFailed()
    {
        var (svc, _, _) = NewService();
        var session = await svc.PlaceAsync(Remote, new[] { "opus" });
        await svc.HandleAsync(BuildAnswerPacket(Remote, Local, session.Id, "opus", 16_000));

        VoiceCallSession? endedRaised = null;
        svc.CallEnded += (_, s) => endedRaised = s;

        await svc.HangupAsync(session.Id, HangupReason.NetworkFailure);

        Assert.NotNull(endedRaised);
        Assert.Equal(CallState.Failed, endedRaised!.State);
        Assert.Equal(HangupReason.NetworkFailure, endedRaised.HangupReason);
    }

    [Fact]
    public async Task HandleAsync_RemoteHangup_EndsLocalSession()
    {
        var (svc, _, _) = NewService();
        var session = await svc.PlaceAsync(Remote, new[] { "opus" });
        await svc.HandleAsync(BuildAnswerPacket(Remote, Local, session.Id, "opus", 16_000));

        VoiceCallSession? endedRaised = null;
        svc.CallEnded += (_, s) => endedRaised = s;

        await svc.HandleAsync(BuildHangupPacket(Remote, Local, session.Id, HangupReason.Normal));

        Assert.NotNull(endedRaised);
        Assert.Equal(CallState.Ended, endedRaised!.State);
        Assert.DoesNotContain(svc.GetActiveCalls(), c => c.Id == session.Id);
    }

    // ── Frame I/O ───────────────────────────────────────────────────

    [Fact]
    public async Task SendFrameAsync_ConnectedCall_EmitsVoiceCallPacket()
    {
        var (svc, sender, _) = NewService();
        var session = await svc.PlaceAsync(Remote, new[] { "opus" });
        await svc.HandleAsync(BuildAnswerPacket(Remote, Local, session.Id, "opus", 16_000));
        sender.Clear();

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await svc.SendFrameAsync(session.Id, payload, sequence: 42, isSilence: false);

        // Filter out the routing RREQ — we want the voice frame.
        var packet = sender.Broadcasts.Single(p => p.Type == PacketType.VoiceCall);
        Assert.Equal(Local, packet.SourceUhid);
        Assert.Equal(Remote, packet.DestinationUhid);

        // Round-trip the wire-format payload back to its fields.
        var parsed = ParseFramePayload(packet.Payload);
        Assert.Equal(session.Id, parsed.CallId);
        Assert.Equal(42u, parsed.Sequence);
        Assert.Equal(payload, parsed.Encoded);
        Assert.False(parsed.IsSilence);
    }

    [Fact]
    public async Task SendFrameAsync_BeforeConnected_NoPacketEmitted()
    {
        var (svc, sender, _) = NewService();
        // Call is in Outgoing state, not Connected.
        var session = await svc.PlaceAsync(Remote, new[] { "opus" });
        sender.Clear();

        await svc.SendFrameAsync(session.Id, new byte[] { 1 }, sequence: 1);

        Assert.Empty(sender.Broadcasts);
        Assert.Empty(sender.Unicasts);
    }

    [Fact]
    public async Task HandleAsync_VoiceCallFrame_RaisesFrameReceived()
    {
        var (svc, _, _) = NewService();
        var session = await svc.PlaceAsync(Remote, new[] { "opus" });
        await svc.HandleAsync(BuildAnswerPacket(Remote, Local, session.Id, "opus", 16_000));

        VoiceFrame? received = null;
        svc.FrameReceived += (_, f) => received = f;

        var encoded = new byte[] { 9, 9, 9 };
        var framePayload = BuildFramePayload(session.Id, sequence: 7, encoded, isSilence: true);
        var packet = new MeshPacket
        {
            Type = PacketType.VoiceCall,
            SourceUhid = Remote,
            DestinationUhid = Local,
            Payload = framePayload,
        };
        await svc.HandleAsync(packet);

        Assert.NotNull(received);
        Assert.Equal(session.Id, received!.CallId);
        Assert.Equal(7u, received.Sequence);
        Assert.Equal(encoded, received.EncodedPayload);
        Assert.True(received.IsSilence);
    }

    [Fact]
    public async Task HandleAsync_VoiceCallFrame_UnknownCall_DoesNotRaise()
    {
        var (svc, _, _) = NewService();

        var raisedCount = 0;
        svc.FrameReceived += (_, _) => raisedCount++;

        var framePayload = BuildFramePayload(Guid.NewGuid(), 1, new byte[] { 1 }, isSilence: false);
        var packet = new MeshPacket
        {
            Type = PacketType.VoiceCall,
            SourceUhid = Remote,
            DestinationUhid = Local,
            Payload = framePayload,
        };
        await svc.HandleAsync(packet);

        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public async Task HandleAsync_NonVoicePacket_Ignored()
    {
        var (svc, _, _) = NewService();
        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.Heartbeat,
            SourceUhid = "alice",
            Payload = new byte[] { 1, 2, 3 },
        });
        // Nothing to assert beyond "did not throw".
    }
}
