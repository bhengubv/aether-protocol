// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Protocol;
using AetherNet.Streaming;
using AetherNet.Streaming.Models;
using Xunit;

namespace AetherNet.Core.Tests;

public class GroupVideoServiceTests
{
    private const string Host = "host-uhid";
    private const string PeerA = "peer-a-uhid";
    private const string PeerB = "peer-b-uhid";
    private const string PeerC = "peer-c-uhid";
    private const string PeerD = "peer-d-uhid";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static (GroupVideoService svc, FakeMeshSender sender, FakeRoutingService routing) NewService(string localUhid)
    {
        var sender = new FakeMeshSender(localUhid);
        var routing = new FakeRoutingService();
        var svc = new GroupVideoService(sender, routing);
        return (svc, sender, routing);
    }

    /// <summary>Deserialise a GroupVideoSignalingMessage from any signaling packet in the sender.</summary>
    private static GroupVideoSignalingMessage TakeGroupSignaling(FakeMeshSender sender)
    {
        var pkt = sender.Unicasts.Select(u => u.Packet)
            .Concat(sender.Broadcasts)
            .First(p => p.Type == PacketType.GroupVideoSignaling);
        return JsonSerializer.Deserialize<GroupVideoSignalingMessage>(pkt.Payload, JsonOptions)!;
    }

    private static IEnumerable<GroupVideoSignalingMessage> AllGroupSignaling(FakeMeshSender sender)
        => sender.Unicasts.Select(u => u.Packet)
            .Concat(sender.Broadcasts)
            .Where(p => p.Type == PacketType.GroupVideoSignaling)
            .Select(p => JsonSerializer.Deserialize<GroupVideoSignalingMessage>(p.Payload, JsonOptions)!);

    private static MeshPacket BuildSignalingPacket(GroupVideoSignalingMessage msg)
    {
        return new MeshPacket
        {
            Type = PacketType.GroupVideoSignaling,
            SourceUhid = msg.FromUhid,
            DestinationUhid = msg.ToUhid,
            Payload = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions),
        };
    }

    // ─── CreateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_BuildsSessionAndBroadcastsInvite()
    {
        var (svc, sender, _) = NewService(Host);

        GroupVideoSession? created = null;
        svc.SessionCreated += (_, s) => created = s;

        var session = await svc.CreateAsync(
            new[] { PeerA, PeerB },
            VideoResolution.R720p,
            "H264",
            1500);

        Assert.NotNull(created);
        Assert.Equal(session.Id, created!.Id);
        Assert.Equal(Host, session.HostUhid);
        Assert.Single(session.Participants); // only the host at this point
        Assert.Equal(Host, session.Participants[0].Uhid);
        Assert.Equal(VideoTopology.FullMesh, session.Topology);

        // Exactly one broadcast with Create signaling.
        Assert.Single(sender.Broadcasts);
        var sig = TakeGroupSignaling(sender);
        Assert.Equal(GroupVideoSignalingKind.Create, sig.Kind);
        Assert.Equal(session.Id, sig.SessionId);
        Assert.Equal(Host, sig.FromUhid);
        Assert.Contains(PeerA, sig.InvitedUhids!);
        Assert.Contains(PeerB, sig.InvitedUhids!);
    }

    // ─── HandleCreate (invited participant side) ──────────────────────────────

    [Fact]
    public async Task HandleCreate_InvitedParticipant_StoresSessionAndFiresEvent()
    {
        var (hostSvc, hostSender, _) = NewService(Host);
        var (peerSvc, _, _) = NewService(PeerA);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA },
            VideoResolution.R720p, "H264", 1500);

        var createPkt = hostSender.Broadcasts.Single(p => p.Type == PacketType.GroupVideoSignaling);

        GroupVideoSession? received = null;
        peerSvc.SessionCreated += (_, s) => received = s;

        await peerSvc.HandleAsync(createPkt);

        Assert.NotNull(received);
        Assert.Equal(session.Id, received!.Id);
        Assert.Equal(Host, received.HostUhid);
        Assert.Single(peerSvc.GetActiveSessions(), s => s.Id == session.Id);

        // Peer session should have the host as a participant.
        Assert.Contains(received.Participants, p => string.Equals(p.Uhid, Host));
    }

    // ─── JoinAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task JoinAsync_SendsJoinToHost()
    {
        var (hostSvc, hostSender, hostRouting) = NewService(Host);
        var (peerSvc, peerSender, peerRouting) = NewService(PeerA);

        // Set up route so we can verify unicast vs broadcast.
        peerRouting.SetRoute(Host, Host);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA }, VideoResolution.R720p, "H264", 1500);
        var createPkt = hostSender.Broadcasts.Single(p => p.Type == PacketType.GroupVideoSignaling);
        await peerSvc.HandleAsync(createPkt);

        peerSender.Clear();
        var joined = await peerSvc.JoinAsync(session.Id, VideoResolution.R720p, "H264", 1500);

        Assert.True(joined);

        var sig = TakeGroupSignaling(peerSender);
        Assert.Equal(GroupVideoSignalingKind.Join, sig.Kind);
        Assert.Equal(session.Id, sig.SessionId);
        Assert.Equal(PeerA, sig.FromUhid);
        Assert.Equal(Host, sig.ToUhid);
    }

    [Fact]
    public async Task JoinAsync_UnknownSession_ReturnsFalse()
    {
        var (svc, _, _) = NewService(PeerA);
        var ok = await svc.JoinAsync(Guid.NewGuid(), VideoResolution.R720p, "H264", 1500);
        Assert.False(ok);
    }

    // ─── HandleJoin (host side) ───────────────────────────────────────────────

    [Fact]
    public async Task HandleJoin_HostReceives_AddsParticipantAndFiresEvent()
    {
        var (hostSvc, hostSender, _) = NewService(Host);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA }, VideoResolution.R720p, "H264", 1500);

        var joinMsg = new GroupVideoSignalingMessage
        {
            Kind = GroupVideoSignalingKind.Join,
            SessionId = session.Id,
            FromUhid = PeerA,
            ToUhid = Host,
            Resolution = VideoResolution.R720p,
            VideoCodec = "H264",
            BitrateKbps = 1500,
        };

        GroupVideoSession? joined = null;
        hostSvc.ParticipantJoined += (_, s) => joined = s;

        await hostSvc.HandleAsync(BuildSignalingPacket(joinMsg));

        Assert.NotNull(joined);
        Assert.Contains(joined!.Participants, p => string.Equals(p.Uhid, PeerA) && !p.HasLeft);
    }

    // ─── Topology ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Topology_StartsAsFullMesh()
    {
        var (svc, _, _) = NewService(Host);
        var session = await svc.CreateAsync(
            new[] { PeerA }, VideoResolution.R720p, "H264", 1500);

        Assert.Equal(VideoTopology.FullMesh, session.Topology);
        Assert.Null(session.SfuRelayUhid);
    }

    [Fact]
    public async Task Topology_SwitchesToSfu_AtThreshold()
    {
        // Threshold = 4.  We need 4 active participants.
        // Host = 1, PeerA/B/C join = 3 more = 4 total → Sfu.
        var (hostSvc, hostSender, _) = NewService(Host);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA, PeerB, PeerC }, VideoResolution.R720p, "H264", 1500);

        // Simulate host receiving Join from each peer.
        GroupVideoSession? topologySession = null;
        hostSvc.TopologyChanged += (_, s) => topologySession = s;

        foreach (var peer in new[] { PeerA, PeerB, PeerC })
        {
            var joinMsg = new GroupVideoSignalingMessage
            {
                Kind = GroupVideoSignalingKind.Join,
                SessionId = session.Id,
                FromUhid = peer,
                ToUhid = Host,
                Resolution = VideoResolution.R720p,
                VideoCodec = "H264",
                BitrateKbps = 1500,
            };
            await hostSvc.HandleAsync(BuildSignalingPacket(joinMsg));
        }

        // After all three joins there should be 4 active participants (host + 3).
        Assert.Equal(ProtocolConstants.SfuThresholdParticipants, session.Participants.Count(p => !p.HasLeft));
        Assert.Equal(VideoTopology.Sfu, session.Topology);
        Assert.NotNull(session.SfuRelayUhid);
        Assert.NotNull(topologySession);
        Assert.Equal(VideoTopology.Sfu, topologySession!.Topology);

        // Host should have sent SfuAssigned to each of the 3 peers.
        var sfuSignals = AllGroupSignaling(hostSender)
            .Where(m => m.Kind == GroupVideoSignalingKind.SfuAssigned)
            .ToList();
        Assert.Equal(3, sfuSignals.Count);
        Assert.All(sfuSignals, m => Assert.Equal(session.SfuRelayUhid, m.SfuRelayUhid));
    }

    [Fact]
    public async Task Topology_SwitchesBackToFullMesh_BelowThreshold()
    {
        var (hostSvc, hostSender, _) = NewService(Host);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA, PeerB, PeerC }, VideoResolution.R720p, "H264", 1500);

        // Add 3 peers to reach SFU threshold.
        foreach (var peer in new[] { PeerA, PeerB, PeerC })
        {
            var joinMsg = new GroupVideoSignalingMessage
            {
                Kind = GroupVideoSignalingKind.Join,
                SessionId = session.Id,
                FromUhid = peer,
                ToUhid = Host,
                Resolution = VideoResolution.R720p,
                VideoCodec = "H264",
                BitrateKbps = 1500,
            };
            await hostSvc.HandleAsync(BuildSignalingPacket(joinMsg));
        }

        Assert.Equal(VideoTopology.Sfu, session.Topology);

        // One participant leaves → back to 3 → FullMesh.
        var topologyEvents = new List<GroupVideoSession>();
        hostSvc.TopologyChanged += (_, s) => topologyEvents.Add(s);

        var leaveMsg = new GroupVideoSignalingMessage
        {
            Kind = GroupVideoSignalingKind.Leave,
            SessionId = session.Id,
            FromUhid = PeerA,
            ToUhid = Host,
        };
        await hostSvc.HandleAsync(BuildSignalingPacket(leaveMsg));

        Assert.Equal(VideoTopology.FullMesh, session.Topology);
        Assert.Null(session.SfuRelayUhid);
        Assert.NotEmpty(topologyEvents);
        Assert.Equal(VideoTopology.FullMesh, topologyEvents.Last().Topology);
    }

    // ─── SendFrameAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SendFrameAsync_FullMesh_FansOutToAllParticipants()
    {
        var (hostSvc, hostSender, hostRouting) = NewService(Host);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA, PeerB }, VideoResolution.R720p, "H264", 1500);

        // Add two participants.
        foreach (var peer in new[] { PeerA, PeerB })
        {
            var joinMsg = new GroupVideoSignalingMessage
            {
                Kind = GroupVideoSignalingKind.Join,
                SessionId = session.Id,
                FromUhid = peer,
                ToUhid = Host,
                Resolution = VideoResolution.R720p,
                VideoCodec = "H264",
                BitrateKbps = 1500,
            };
            await hostSvc.HandleAsync(BuildSignalingPacket(joinMsg));
        }

        Assert.Equal(VideoTopology.FullMesh, session.Topology);

        hostRouting.SetRoute(PeerA, PeerA);
        hostRouting.SetRoute(PeerB, PeerB);

        hostSender.Clear();
        var frame = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await hostSvc.SendFrameAsync(session.Id, frame, 1, isKeyframe: false);

        var framePackets = hostSender.Unicasts
            .Where(u => u.Packet.Type == PacketType.VideoFrame)
            .ToList();

        // Host should have sent one frame to PeerA and one to PeerB (not to itself).
        Assert.Equal(2, framePackets.Count);
        Assert.Contains(framePackets, u => string.Equals(u.NextHopUhid, PeerA));
        Assert.Contains(framePackets, u => string.Equals(u.NextHopUhid, PeerB));
    }

    [Fact]
    public async Task SendFrameAsync_Sfu_SendsOnlyToRelayNode()
    {
        var (hostSvc, hostSender, hostRouting) = NewService(Host);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA, PeerB, PeerC }, VideoResolution.R720p, "H264", 1500);

        foreach (var peer in new[] { PeerA, PeerB, PeerC })
        {
            var joinMsg = new GroupVideoSignalingMessage
            {
                Kind = GroupVideoSignalingKind.Join,
                SessionId = session.Id,
                FromUhid = peer,
                ToUhid = Host,
                Resolution = VideoResolution.R720p,
                VideoCodec = "H264",
                BitrateKbps = 1500,
            };
            await hostSvc.HandleAsync(BuildSignalingPacket(joinMsg));
        }

        Assert.Equal(VideoTopology.Sfu, session.Topology);
        var relay = session.SfuRelayUhid!;
        hostRouting.SetRoute(relay, relay);

        hostSender.Clear();
        await hostSvc.SendFrameAsync(session.Id, new byte[] { 0x01, 0x02 }, 5, isKeyframe: false);

        var framePackets = hostSender.Unicasts
            .Where(u => u.Packet.Type == PacketType.VideoFrame)
            .ToList();

        Assert.Single(framePackets);
        Assert.Equal(relay, framePackets[0].NextHopUhid);
    }

    [Fact]
    public async Task SendFrameAsync_Keyframe_HasHigherPriority()
    {
        var (hostSvc, hostSender, hostRouting) = NewService(Host);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA }, VideoResolution.R720p, "H264", 1500);

        var joinMsg = new GroupVideoSignalingMessage
        {
            Kind = GroupVideoSignalingKind.Join,
            SessionId = session.Id,
            FromUhid = PeerA,
            ToUhid = Host,
            Resolution = VideoResolution.R720p,
            VideoCodec = "H264",
            BitrateKbps = 1500,
        };
        await hostSvc.HandleAsync(BuildSignalingPacket(joinMsg));

        hostRouting.SetRoute(PeerA, PeerA);
        hostSender.Clear();

        await hostSvc.SendFrameAsync(session.Id, new byte[] { 1 }, 1, isKeyframe: true);
        var keyframePkt = hostSender.Unicasts.Single(u => u.Packet.Type == PacketType.VideoFrame).Packet;
        var keyframePriority = keyframePkt.Priority;

        hostSender.Clear();
        await hostSvc.SendFrameAsync(session.Id, new byte[] { 2 }, 2, isKeyframe: false);
        var pframePkt = hostSender.Unicasts.Single(u => u.Packet.Type == PacketType.VideoFrame).Packet;
        var pframePriority = pframePkt.Priority;

        Assert.True(keyframePriority > pframePriority,
            $"Keyframe priority {keyframePriority} should exceed P-frame priority {pframePriority}");
    }

    // ─── KickAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task KickAsync_MarksParticipantLeft_TriggersTopologyUpdate()
    {
        var (hostSvc, hostSender, hostRouting) = NewService(Host);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA, PeerB, PeerC }, VideoResolution.R720p, "H264", 1500);

        foreach (var peer in new[] { PeerA, PeerB, PeerC })
        {
            var joinMsg = new GroupVideoSignalingMessage
            {
                Kind = GroupVideoSignalingKind.Join,
                SessionId = session.Id,
                FromUhid = peer,
                ToUhid = Host,
                Resolution = VideoResolution.R720p,
                VideoCodec = "H264",
                BitrateKbps = 1500,
            };
            await hostSvc.HandleAsync(BuildSignalingPacket(joinMsg));
        }

        // 4 active → Sfu.
        Assert.Equal(VideoTopology.Sfu, session.Topology);

        hostRouting.SetRoute(PeerA, PeerA);
        hostSender.Clear();

        await hostSvc.KickAsync(session.Id, PeerA);

        // PeerA should be marked as left.
        var kickedParticipant = session.Participants.Single(p => string.Equals(p.Uhid, PeerA));
        Assert.True(kickedParticipant.HasLeft);

        // Topology should have dropped back to FullMesh (3 active).
        Assert.Equal(VideoTopology.FullMesh, session.Topology);

        // A Kick signaling packet should have been sent to PeerA.
        var kickSignal = AllGroupSignaling(hostSender)
            .FirstOrDefault(m => m.Kind == GroupVideoSignalingKind.Kick);
        Assert.NotNull(kickSignal);
        Assert.Equal(PeerA, kickSignal!.ToUhid);
    }

    // ─── HandleFrame ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleFrame_FiresFrameReceivedEvent()
    {
        var (hostSvc, hostSender, hostRouting) = NewService(Host);
        var (peerSvc, peerSender, peerRouting) = NewService(PeerA);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA }, VideoResolution.R720p, "H264", 1500);

        // Give PeerA knowledge of the session.
        var createPkt = hostSender.Broadcasts.Single(p => p.Type == PacketType.GroupVideoSignaling);
        await peerSvc.HandleAsync(createPkt);

        // Set route so JoinAsync unicasts rather than broadcasts.
        peerRouting.SetRoute(Host, Host);
        await peerSvc.JoinAsync(session.Id, VideoResolution.R720p, "H264", 1500);

        // Host receives the Join signaling so it knows PeerA is active.
        var joinPkt = peerSender.Unicasts
            .Where(u => u.Packet.Type == PacketType.GroupVideoSignaling)
            .Select(u => u.Packet)
            .First();
        await hostSvc.HandleAsync(joinPkt);

        // Host sends a frame — capture it from the mesh sender.
        hostRouting.SetRoute(PeerA, PeerA);
        hostSender.Clear();
        var encoded = new byte[] { 0x11, 0x22, 0x33 };
        await hostSvc.SendFrameAsync(session.Id, encoded, sequence: 7, isKeyframe: true);

        var framePkt = hostSender.Unicasts
            .Single(u => u.Packet.Type == PacketType.VideoFrame).Packet;

        // Peer receives the frame → FrameReceived fires.
        VideoFrame? received = null;
        peerSvc.FrameReceived += (_, f) => received = f;
        await peerSvc.HandleAsync(framePkt);

        Assert.NotNull(received);
        Assert.Equal(session.Id, received!.CallId);
        Assert.Equal(7u, received.Sequence);
        Assert.True(received.IsKeyframe);
        Assert.Equal(encoded, received.EncodedPayload);
    }

    // ─── LeaveAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LeaveAsync_SendsLeaveSignaling()
    {
        var (hostSvc, hostSender, _) = NewService(Host);
        var (peerSvc, peerSender, peerRouting) = NewService(PeerA);

        peerRouting.SetRoute(Host, Host);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA }, VideoResolution.R720p, "H264", 1500);
        var createPkt = hostSender.Broadcasts.Single(p => p.Type == PacketType.GroupVideoSignaling);
        await peerSvc.HandleAsync(createPkt);
        await peerSvc.JoinAsync(session.Id, VideoResolution.R720p, "H264", 1500);

        peerSender.Clear();
        await peerSvc.LeaveAsync(session.Id);

        var leaveSignals = AllGroupSignaling(peerSender)
            .Where(m => m.Kind == GroupVideoSignalingKind.Leave)
            .ToList();

        Assert.NotEmpty(leaveSignals);
        var leave = leaveSignals.First();
        Assert.Equal(GroupVideoSignalingKind.Leave, leave.Kind);
        Assert.Equal(session.Id, leave.SessionId);
        Assert.Equal(PeerA, leave.FromUhid);
    }

    // ─── GetActiveSessions ────────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveSessions_ReturnsOnlyActiveSessions()
    {
        var (svc, sender, _) = NewService(Host);

        var sessionA = await svc.CreateAsync(
            new[] { PeerA }, VideoResolution.R720p, "H264", 1500);
        var sessionB = await svc.CreateAsync(
            new[] { PeerB }, VideoResolution.R720p, "H264", 1500);

        // Both sessions active (host is a non-left participant in both).
        var active = svc.GetActiveSessions();
        Assert.Equal(2, active.Count);

        // Host leaves sessionA.
        await svc.LeaveAsync(sessionA.Id);

        active = svc.GetActiveSessions();
        Assert.Single(active);
        Assert.Equal(sessionB.Id, active[0].Id);
    }

    // ─── SfuAssigned received by participant ──────────────────────────────────

    [Fact]
    public async Task HandleSfuAssigned_Participant_UpdatesTopologyAndFiresEvent()
    {
        var (hostSvc, hostSender, _) = NewService(Host);
        var (peerSvc, _, _) = NewService(PeerA);

        var session = await hostSvc.CreateAsync(
            new[] { PeerA }, VideoResolution.R720p, "H264", 1500);
        var createPkt = hostSender.Broadcasts.Single(p => p.Type == PacketType.GroupVideoSignaling);
        await peerSvc.HandleAsync(createPkt);

        // Simulate receiving SfuAssigned from the host.
        var sfuMsg = new GroupVideoSignalingMessage
        {
            Kind = GroupVideoSignalingKind.SfuAssigned,
            SessionId = session.Id,
            FromUhid = Host,
            ToUhid = PeerA,
            SfuRelayUhid = PeerB,
        };

        GroupVideoSession? topologySession = null;
        peerSvc.TopologyChanged += (_, s) => topologySession = s;

        await peerSvc.HandleAsync(BuildSignalingPacket(sfuMsg));

        Assert.NotNull(topologySession);
        var peerSession = peerSvc.GetActiveSessions().Single(s => s.Id == session.Id);
        Assert.Equal(VideoTopology.Sfu, peerSession.Topology);
        Assert.Equal(PeerB, peerSession.SfuRelayUhid);
    }
}
