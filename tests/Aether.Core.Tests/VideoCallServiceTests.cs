// SPDX-License-Identifier: MIT

using System.Text.Json;
using Aether.Core.Tests.Fakes;
using Aether.Protocol;
using Aether.Streaming;
using Aether.Streaming.Models;
using Xunit;

namespace Aether.Core.Tests;

public class VideoCallServiceTests
{
    private const string Caller = "caller-uhid";
    private const string Callee = "callee-uhid";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static (VideoCallService svc, FakeMeshSender sender, FakeRoutingService routing) NewService(string localUhid)
    {
        var sender = new FakeMeshSender(localUhid);
        var routing = new FakeRoutingService();
        var svc = new VideoCallService(sender, routing);
        return (svc, sender, routing);
    }

    private static MeshPacket TakeSignaling(FakeMeshSender sender)
    {
        return sender.Unicasts.Select(u => u.Packet)
            .Concat(sender.Broadcasts)
            .Single(p => p.Type == PacketType.VideoSignaling);
    }

    private static IEnumerable<MeshPacket> AllSignaling(FakeMeshSender sender)
        => sender.Unicasts.Select(u => u.Packet)
            .Concat(sender.Broadcasts)
            .Where(p => p.Type == PacketType.VideoSignaling);

    // ─── PlaceAsync ──────────────────────────────────────────────

    [Fact]
    public async Task PlaceAsync_CreatesOutgoingSessionAndSendsOffer()
    {
        var (svc, sender, _) = NewService(Caller);

        var session = await svc.PlaceAsync(
            Callee,
            new[] { "h264", "vp8" },
            new[] { "opus" },
            VideoResolution.R720p,
            targetFps: 30,
            targetBitrateKbps: 1200);

        Assert.Equal(VideoCallState.Outgoing, session.State);
        Assert.Equal(Caller, session.CallerUhid);
        Assert.Equal(Callee, session.CalleeUhid);
        Assert.Single(svc.GetActiveCalls());

        var sig = TakeSignaling(sender);
        var body = JsonSerializer.Deserialize<VideoSignalingMessage>(sig.Payload, JsonOptions)!;
        Assert.Equal(VideoSignalingKind.Offer, body.Kind);
        Assert.Equal(session.Id, body.CallId);
        Assert.Equal(Caller, body.FromUhid);
        Assert.Equal(Callee, body.ToUhid);
        Assert.Equal(new[] { "h264", "vp8" }, body.ProposedVideoCodecs);
        Assert.Equal(VideoResolution.R720p, body.Resolution);
    }

    [Fact]
    public async Task PlaceAsync_RejectsEmptyCallee()
    {
        var (svc, _, _) = NewService(Caller);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            svc.PlaceAsync(
                string.Empty,
                new[] { "h264" },
                Array.Empty<string>(),
                VideoResolution.R480p, 30, 500));
    }

    [Fact]
    public async Task PlaceAsync_RejectsEmptyCodecList()
    {
        var (svc, _, _) = NewService(Caller);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            svc.PlaceAsync(
                Callee,
                Array.Empty<string>(),
                Array.Empty<string>(),
                VideoResolution.R480p, 30, 500));
    }

    // ─── Two-service Offer/Answer flow ───────────────────────────

    [Fact]
    public async Task PlaceThenAnswer_TransitionsBothSidesToConnected()
    {
        var (callerSvc, callerSender, _) = NewService(Caller);
        var (calleeSvc, calleeSender, _) = NewService(Callee);

        // Caller places → produces an Offer.
        var session = await callerSvc.PlaceAsync(
            Callee,
            new[] { "h264" },
            new[] { "opus" },
            VideoResolution.R480p, 30, 500);
        var offer = TakeSignaling(callerSender);

        // Callee receives the Offer → IncomingCall fires and call is recorded.
        VideoCallSession? incoming = null;
        calleeSvc.IncomingCall += (_, s) => incoming = s;
        await calleeSvc.HandleAsync(offer);

        Assert.NotNull(incoming);
        Assert.Equal(VideoCallState.Incoming, incoming!.State);
        Assert.Equal(session.Id, incoming.Id);

        // Callee answers → produces an Answer signal.
        calleeSender.Clear();
        var ok = await calleeSvc.AnswerAsync(session.Id, "h264", "opus", VideoResolution.R480p, 30, 500);
        Assert.True(ok);
        var answer = TakeSignaling(calleeSender);

        // Caller receives the Answer → CallConnected fires and codec is recorded.
        VideoCallSession? connected = null;
        callerSvc.CallConnected += (_, s) => connected = s;
        await callerSvc.HandleAsync(answer);

        Assert.NotNull(connected);
        Assert.Equal(VideoCallState.Connected, connected!.State);
        Assert.Equal("h264", connected.VideoCodec);
        Assert.Equal("opus", connected.AudioCodec);
        Assert.NotNull(connected.ConnectedAt);
    }

    [Fact]
    public async Task AnswerAsync_UnknownCallId_ReturnsFalse()
    {
        var (svc, _, _) = NewService(Callee);

        var ok = await svc.AnswerAsync(Guid.NewGuid(), "h264", "opus", VideoResolution.R480p, 30, 500);

        Assert.False(ok);
    }

    [Fact]
    public async Task AnswerAsync_OnOutgoingState_ReturnsFalse()
    {
        // Caller can't answer its own outgoing call.
        var (svc, _, _) = NewService(Caller);
        var session = await svc.PlaceAsync(
            Callee, new[] { "h264" }, new[] { "opus" }, VideoResolution.R480p, 30, 500);

        var ok = await svc.AnswerAsync(session.Id, "h264", "opus", VideoResolution.R480p, 30, 500);

        Assert.False(ok);
    }

    // ─── Hangup / Decline ────────────────────────────────────────

    [Fact]
    public async Task HangupAsync_OnConnectedCall_NotifiesRemoteAndEndsBothSides()
    {
        var (callerSvc, callerSender, _) = NewService(Caller);
        var (calleeSvc, calleeSender, _) = NewService(Callee);

        var session = await callerSvc.PlaceAsync(
            Callee, new[] { "h264" }, new[] { "opus" }, VideoResolution.R480p, 30, 500);
        var offer = TakeSignaling(callerSender);
        await calleeSvc.HandleAsync(offer);

        calleeSender.Clear();
        await calleeSvc.AnswerAsync(session.Id, "h264", "opus", VideoResolution.R480p, 30, 500);
        var answer = TakeSignaling(calleeSender);
        await callerSvc.HandleAsync(answer);

        // Wire callback BEFORE hangup so we observe CallEnded on the local side too.
        VideoCallSession? endedLocal = null;
        callerSvc.CallEnded += (_, s) => endedLocal = s;
        callerSender.Clear();

        await callerSvc.HangupAsync(session.Id);

        Assert.NotNull(endedLocal);
        Assert.Equal(VideoCallState.Ended, endedLocal!.State);

        // Remote receives the Hangup signal → CallEnded fires on callee too.
        var hangup = TakeSignaling(callerSender);
        VideoCallSession? endedRemote = null;
        calleeSvc.CallEnded += (_, s) => endedRemote = s;
        await calleeSvc.HandleAsync(hangup);
        Assert.NotNull(endedRemote);
        Assert.Equal(VideoCallState.Ended, endedRemote!.State);
    }

    [Fact]
    public async Task DeclineAsync_OnIncomingCall_PropagatesDeclinedReason()
    {
        var (callerSvc, callerSender, _) = NewService(Caller);
        var (calleeSvc, calleeSender, _) = NewService(Callee);

        var session = await callerSvc.PlaceAsync(
            Callee, new[] { "h264" }, new[] { "opus" }, VideoResolution.R480p, 30, 500);
        await calleeSvc.HandleAsync(TakeSignaling(callerSender));

        calleeSender.Clear();
        await calleeSvc.DeclineAsync(session.Id);

        var hangup = AllSignaling(calleeSender).Single();
        var body = JsonSerializer.Deserialize<VideoSignalingMessage>(hangup.Payload, JsonOptions)!;
        Assert.Equal(VideoSignalingKind.Hangup, body.Kind);
        Assert.Equal(VideoHangupReason.Declined, body.Reason);
    }

    // ─── Frames ──────────────────────────────────────────────────

    [Fact]
    public async Task SendFrameAsync_FromCaller_ToCallee_DeliversBytes()
    {
        var (callerSvc, callerSender, callerRouting) = NewService(Caller);
        var (calleeSvc, calleeSender, calleeRouting) = NewService(Callee);

        // Establish connected call.
        var session = await callerSvc.PlaceAsync(
            Callee, new[] { "h264" }, new[] { "opus" }, VideoResolution.R480p, 30, 500);
        await calleeSvc.HandleAsync(TakeSignaling(callerSender));
        calleeSender.Clear();
        await calleeSvc.AnswerAsync(session.Id, "h264", "opus", VideoResolution.R480p, 30, 500);
        await callerSvc.HandleAsync(TakeSignaling(calleeSender));

        callerRouting.SetRoute(Callee, Callee);
        calleeRouting.SetRoute(Caller, Caller);

        // Caller → Callee
        callerSender.Clear();
        var encoded = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        await callerSvc.SendFrameAsync(session.Id, encoded, sequence: 7, isKeyframe: true);
        var framePkt = callerSender.Unicasts
            .Single(u => u.Packet.Type == PacketType.VideoFrame).Packet;

        VideoFrame? received = null;
        calleeSvc.FrameReceived += (_, f) => received = f;
        await calleeSvc.HandleAsync(framePkt);

        Assert.NotNull(received);
        Assert.Equal(session.Id, received!.CallId);
        Assert.Equal(7u, received.Sequence);
        Assert.True(received.IsKeyframe);
        Assert.Equal(encoded, received.EncodedPayload);

        // Callee → Caller (return direction)
        calleeSender.Clear();
        var encodedBack = new byte[] { 0x09, 0x08, 0x07 };
        await calleeSvc.SendFrameAsync(session.Id, encodedBack, sequence: 11, isKeyframe: false);
        var returnPkt = calleeSender.Unicasts
            .Single(u => u.Packet.Type == PacketType.VideoFrame).Packet;

        VideoFrame? back = null;
        callerSvc.FrameReceived += (_, f) => back = f;
        await callerSvc.HandleAsync(returnPkt);

        Assert.NotNull(back);
        Assert.Equal(11u, back!.Sequence);
        Assert.False(back.IsKeyframe);
        Assert.Equal(encodedBack, back.EncodedPayload);
    }

    [Fact]
    public async Task SendFrameAsync_OnNonConnectedCall_IsNoOp()
    {
        var (svc, sender, _) = NewService(Caller);
        var session = await svc.PlaceAsync(
            Callee, new[] { "h264" }, new[] { "opus" }, VideoResolution.R480p, 30, 500);
        sender.Clear();

        // Session is still Outgoing — frame send must be silently ignored.
        await svc.SendFrameAsync(session.Id, new byte[] { 1 }, 1, false);

        Assert.DoesNotContain(sender.Unicasts, u => u.Packet.Type == PacketType.VideoFrame);
        Assert.DoesNotContain(sender.Broadcasts, p => p.Type == PacketType.VideoFrame);
    }

    // ─── Quality / keyframe signaling ────────────────────────────

    [Fact]
    public async Task RequestKeyframeAsync_RaisesKeyframeRequestedOnRemote()
    {
        var (callerSvc, callerSender, _) = NewService(Caller);
        var (calleeSvc, calleeSender, _) = NewService(Callee);
        var session = await callerSvc.PlaceAsync(
            Callee, new[] { "h264" }, new[] { "opus" }, VideoResolution.R480p, 30, 500);
        await calleeSvc.HandleAsync(TakeSignaling(callerSender));
        calleeSender.Clear();
        await calleeSvc.AnswerAsync(session.Id, "h264", "opus", VideoResolution.R480p, 30, 500);
        await callerSvc.HandleAsync(TakeSignaling(calleeSender));

        callerSender.Clear();
        await callerSvc.RequestKeyframeAsync(session.Id);
        var req = TakeSignaling(callerSender);

        Guid? observed = null;
        calleeSvc.KeyframeRequested += (_, id) => observed = id;
        await calleeSvc.HandleAsync(req);

        Assert.Equal(session.Id, observed);
    }

    [Fact]
    public async Task NotifyQualityChangeAsync_PropagatesNewParametersAndRaisesEvent()
    {
        var (callerSvc, callerSender, _) = NewService(Caller);
        var (calleeSvc, calleeSender, _) = NewService(Callee);
        var session = await callerSvc.PlaceAsync(
            Callee, new[] { "h264" }, new[] { "opus" }, VideoResolution.R480p, 30, 500);
        await calleeSvc.HandleAsync(TakeSignaling(callerSender));
        calleeSender.Clear();
        await calleeSvc.AnswerAsync(session.Id, "h264", "opus", VideoResolution.R480p, 30, 500);
        await callerSvc.HandleAsync(TakeSignaling(calleeSender));

        callerSender.Clear();
        await callerSvc.NotifyQualityChangeAsync(session.Id, VideoResolution.R360p, 15, 250);
        var qc = TakeSignaling(callerSender);

        VideoCallSession? observed = null;
        calleeSvc.QualityChanged += (_, s) => observed = s;
        await calleeSvc.HandleAsync(qc);

        Assert.NotNull(observed);
        Assert.Equal(VideoResolution.R360p, observed!.Resolution);
        Assert.Equal(15, observed.TargetFps);
        Assert.Equal(250, observed.TargetBitrateKbps);
    }

    [Fact]
    public async Task HandleAsync_SignalingNotAddressedToUs_IsIgnored()
    {
        var (svc, _, _) = NewService("local");

        // Build an Offer message addressed to a different node.
        var msg = new VideoSignalingMessage
        {
            Kind = VideoSignalingKind.Offer,
            CallId = Guid.NewGuid(),
            FromUhid = "alice",
            ToUhid = "bob",
            Resolution = VideoResolution.R480p,
            TargetFps = 30,
            TargetBitrateKbps = 500,
        };
        var pkt = new MeshPacket
        {
            Type = PacketType.VideoSignaling,
            SourceUhid = "alice",
            DestinationUhid = "bob",
            Payload = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions),
        };

        var fired = false;
        svc.IncomingCall += (_, _) => fired = true;
        await svc.HandleAsync(pkt);

        Assert.False(fired);
        Assert.Empty(svc.GetActiveCalls());
    }
}
