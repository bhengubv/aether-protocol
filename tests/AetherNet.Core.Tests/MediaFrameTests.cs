// SPDX-License-Identifier: MIT

using AetherNet.Media;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for the VoicePtt(15) + ScreenShare(32) media-frame bindings. Binary frames sharing the
/// 29-byte header (call_id big-endian, sequence/timestamp little-endian, flag). Byte-identity gates +
/// send/handle behaviour.
/// </summary>
public sealed class MediaFrameTests
{
    private sealed class FakeMeshSender : IMeshSender
    {
        public string LocalUhid { get; set; } = "aether:local:01";
        public List<(MeshPacket Packet, string NextHop)> Sends { get; } = [];
        public IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();
        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default)
        {
            Sends.Add((packet, nextHopUhid));
            return Task.FromResult(true);
        }
        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static readonly Guid CallId = Guid.Parse("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f");
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // ── Byte-identity gates ─────────────────────────────────────────────────

    [Fact]
    public void VoicePtt_Frame_SerializesToCanonicalBytes()
    {
        var f = new VoicePttFrame { CallId = CallId, Sequence = 42, TimestampMs = 1700000000000L, IsSilence = false, EncodedPayload = [0xAA, 0xBB, 0xCC] };
        Assert.Equal("0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2a0000000068e5cf8b01000000aabbcc", Hex(MediaFrameCodec.SerializeVoicePtt(f)));
    }

    [Fact]
    public void VoicePtt_SilenceEmpty_SerializesToCanonicalBytes()
    {
        var f = new VoicePttFrame { CallId = CallId, Sequence = 43, TimestampMs = 1700000000020L, IsSilence = true, EncodedPayload = [] };
        Assert.Equal("0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2b0000001468e5cf8b01000001", Hex(MediaFrameCodec.SerializeVoicePtt(f)));
    }

    [Fact]
    public void ScreenShare_Keyframe_SerializesToCanonicalBytes()
    {
        var f = new ScreenShareFrame { CallId = CallId, Sequence = 7, TimestampMs = 1700000000000L, IsKeyframe = true, EncodedPayload = [0x11, 0x22, 0x33, 0x44] };
        Assert.Equal("0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f070000000068e5cf8b0100000111223344", Hex(MediaFrameCodec.SerializeScreenShare(f)));
    }

    [Fact]
    public void ScreenShare_DeltaEmpty_SerializesToCanonicalBytes()
    {
        var f = new ScreenShareFrame { CallId = Guid.Empty, Sequence = 0, TimestampMs = 0, IsKeyframe = false, EncodedPayload = [] };
        Assert.Equal("0000000000000000000000000000000000000000000000000000000000", Hex(MediaFrameCodec.SerializeScreenShare(f)));
    }

    [Fact]
    public void VoicePtt_RoundTrips()
    {
        var f = new VoicePttFrame { CallId = CallId, Sequence = 99, TimestampMs = 123456789L, IsSilence = true, EncodedPayload = [1, 2, 3, 4, 5] };
        var back = MediaFrameCodec.DeserializeVoicePtt(MediaFrameCodec.SerializeVoicePtt(f));
        Assert.Equal(CallId, back.CallId);
        Assert.Equal(99u, back.Sequence);
        Assert.Equal(123456789L, back.TimestampMs);
        Assert.True(back.IsSilence);
        Assert.Equal(f.EncodedPayload, back.EncodedPayload);
    }

    [Fact]
    public void ScreenShare_RoundTrips_KeyframeAndCallIdBigEndian()
    {
        var f = new ScreenShareFrame { CallId = CallId, Sequence = 5, TimestampMs = 999L, IsKeyframe = true, EncodedPayload = [0xFF] };
        var back = MediaFrameCodec.DeserializeScreenShare(MediaFrameCodec.SerializeScreenShare(f));
        Assert.Equal(CallId, back.CallId);
        Assert.True(back.IsKeyframe);
        Assert.Equal(new byte[] { 0xFF }, back.EncodedPayload);
    }

    // ── Behaviour ───────────────────────────────────────────────────────────

    [Fact]
    public async Task VoicePtt_Send_EmitsDirectedFrame_AndHandleRaisesEvent()
    {
        var s = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = new VoicePttService(s, NullLogger<VoicePttService>.Instance);
        var frame = new VoicePttFrame { CallId = CallId, Sequence = 42, TimestampMs = 1700000000000L, EncodedPayload = [0xAA, 0xBB, 0xCC] };

        Assert.True(await svc.SendFrameAsync("aether:bob:02", frame));
        var sent = Assert.Single(s.Sends);
        Assert.Equal(PacketType.VoicePtt, sent.Packet.Type);
        Assert.Equal("aether:bob:02", sent.NextHop);

        VoicePttFrameReceived? got = null;
        svc.FrameReceived += (_, e) => got = e;
        sent.Packet.SourceUhid = "aether:alice:01";
        Assert.True(await svc.HandleAsync(sent.Packet));
        Assert.NotNull(got);
        Assert.Equal(42u, got!.Frame.Sequence);
        Assert.Equal("aether:alice:01", got.FromUhid);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, got.Frame.EncodedPayload);
    }

    [Fact]
    public async Task ScreenShare_Send_EmitsDirectedFrame_AndHandleRaisesEvent()
    {
        var s = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = new ScreenShareService(s, NullLogger<ScreenShareService>.Instance);
        var frame = new ScreenShareFrame { CallId = CallId, Sequence = 7, TimestampMs = 1700000000000L, IsKeyframe = true, EncodedPayload = [0x11, 0x22, 0x33, 0x44] };

        Assert.True(await svc.SendFrameAsync("aether:bob:02", frame));
        var sent = Assert.Single(s.Sends);
        Assert.Equal(PacketType.ScreenShare, sent.Packet.Type);

        ScreenShareFrameReceived? got = null;
        svc.FrameReceived += (_, e) => got = e;
        Assert.True(await svc.HandleAsync(sent.Packet));
        Assert.NotNull(got);
        Assert.True(got!.Frame.IsKeyframe);
        Assert.Equal(7u, got.Frame.Sequence);
    }

    [Fact]
    public async Task Handle_WrongType_ReturnsFalse()
    {
        var vp = new VoicePttService(new FakeMeshSender(), NullLogger<VoicePttService>.Instance);
        var ss = new ScreenShareService(new FakeMeshSender(), NullLogger<ScreenShareService>.Instance);
        Assert.False(await vp.HandleAsync(new MeshPacket { Type = PacketType.Data, Payload = new byte[40] }));
        Assert.False(await ss.HandleAsync(new MeshPacket { Type = PacketType.Data, Payload = new byte[40] }));
    }

    [Fact]
    public async Task Handle_ShortFrame_ReturnsFalse()
    {
        var vp = new VoicePttService(new FakeMeshSender(), NullLogger<VoicePttService>.Instance);
        Assert.False(await vp.HandleAsync(new MeshPacket { Type = PacketType.VoicePtt, Payload = new byte[10] }));
    }
}
