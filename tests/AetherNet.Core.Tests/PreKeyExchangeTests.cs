// SPDX-License-Identifier: MIT

using System.Linq;
using System.Text.Json;
using AetherNet.Models;
using AetherNet.PreKeys;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for <see cref="PreKeyExchangeService"/> (PacketType.PreKeyRequest 25 / PreKeyResponse 26).
/// Directed request/response transport of a <see cref="PreKeyBundle"/> over the mesh.
/// </summary>
public sealed class PreKeyExchangeTests
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

    private static readonly JsonSerializerOptions JsonOpts = new();

    private static PreKeyExchangeService Build(FakeMeshSender sender)
        => new(sender, NullLogger<PreKeyExchangeService>.Instance);

    private static PreKeyBundle SampleBundle(string uhid = "aether:bob:02") => new(
        uhid,
        Enumerable.Repeat((byte)0x11, 32).ToArray(),
        Enumerable.Repeat((byte)0x22, 32).ToArray(),
        4242,
        Enumerable.Repeat((byte)0x33, 32).ToArray(),
        77,
        Enumerable.Repeat((byte)0x44, 32).ToArray(),
        Enumerable.Repeat((byte)0x55, 64).ToArray());

    // ── Byte-identity gate ──────────────────────────────────────────────────

    [Fact]
    public void RequestPayload_SerializesToCanonicalBytes()
    {
        var p = new PreKeyRequestPayload
        {
            RequestId = Guid.Parse("11112222-3333-4444-5555-666677778888"),
            RequesterUhid = "aether:alice:01",
        };
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(p, JsonOpts));
        Assert.Equal(
            "{\"request_id\":\"11112222-3333-4444-5555-666677778888\",\"requester_uhid\":\"aether:alice:01\"}",
            json);
    }

    [Fact]
    public void ResponsePayload_SerializesToCanonicalBytes()
    {
        var p = PreKeyResponsePayload.FromBundle(Guid.Parse("7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a"), SampleBundle());
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(p, JsonOpts));
        Assert.Equal(
            "{\"request_id\":\"7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a\",\"uhid\":\"aether:bob:02\"," +
            "\"identity_key\":\"ERERERERERERERERERERERERERERERERERERERERERE=\"," +
            "\"identity_key_x25519\":\"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=\"," +
            "\"pre_key_id\":4242,\"pre_key\":\"MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM=\"," +
            "\"signed_pre_key_id\":77,\"signed_pre_key\":\"REREREREREREREREREREREREREREREREREREREREREQ=\"," +
            "\"signed_pre_key_signature\":\"VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ==\"}",
            json);
    }

    [Fact]
    public void ResponsePayload_RoundTripsThroughBundle()
    {
        var original = SampleBundle();
        var payload = PreKeyResponsePayload.FromBundle(Guid.NewGuid(), original);
        var back = payload.ToBundle();
        Assert.Equal(original.Uhid, back.Uhid);
        Assert.Equal(original.PreKeyId, back.PreKeyId);
        Assert.Equal(original.SignedPreKeyId, back.SignedPreKeyId);
        Assert.Equal(original.IdentityKey, back.IdentityKey);
        Assert.Equal(original.SignedPreKeySignature, back.SignedPreKeySignature);
    }

    // ── Behaviour ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_SendsDirectedPreKeyRequest_AndReturnsId()
    {
        var sender = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = Build(sender);

        var reqId = await svc.RequestBundleAsync("aether:bob:02");

        Assert.NotEqual(Guid.Empty, reqId);
        var sent = Assert.Single(sender.Sends);
        Assert.Equal(PacketType.PreKeyRequest, sent.Packet.Type);
        Assert.Equal("aether:bob:02", sent.NextHop);
        var body = JsonSerializer.Deserialize<PreKeyRequestPayload>(sent.Packet.Payload, JsonOpts)!;
        Assert.Equal(reqId, body.RequestId);
        Assert.Equal("aether:alice:01", body.RequesterUhid);
    }

    [Fact]
    public async Task HandleRequest_WithLocalBundle_SendsDirectedResponseToRequester()
    {
        var sender = new FakeMeshSender { LocalUhid = "aether:bob:02" };
        var svc = Build(sender);
        svc.SetLocalBundle(SampleBundle("aether:bob:02"));

        var reqId = Guid.NewGuid();
        var reqPkt = new MeshPacket
        {
            Type = PacketType.PreKeyRequest,
            SourceUhid = "aether:alice:01",
            DestinationUhid = "aether:bob:02",
            Payload = JsonSerializer.SerializeToUtf8Bytes(
                new PreKeyRequestPayload { RequestId = reqId, RequesterUhid = "aether:alice:01" }, JsonOpts),
        };

        Assert.True(await svc.HandleAsync(reqPkt));
        var sent = Assert.Single(sender.Sends);
        Assert.Equal(PacketType.PreKeyResponse, sent.Packet.Type);
        Assert.Equal("aether:alice:01", sent.NextHop);
        var body = JsonSerializer.Deserialize<PreKeyResponsePayload>(sent.Packet.Payload, JsonOpts)!;
        Assert.Equal(reqId, body.RequestId);
        Assert.Equal("aether:bob:02", body.Uhid);
        Assert.Equal(4242, body.PreKeyId);
        Assert.Equal(64, body.SignedPreKeySignature.Length);
    }

    [Fact]
    public async Task HandleRequest_NoLocalBundle_ReturnsFalse_AndSendsNothing()
    {
        var sender = new FakeMeshSender();
        var svc = Build(sender);
        var reqPkt = new MeshPacket
        {
            Type = PacketType.PreKeyRequest,
            SourceUhid = "aether:alice:01",
            Payload = JsonSerializer.SerializeToUtf8Bytes(
                new PreKeyRequestPayload { RequestId = Guid.NewGuid(), RequesterUhid = "aether:alice:01" }, JsonOpts),
        };

        Assert.False(await svc.HandleAsync(reqPkt));
        Assert.Empty(sender.Sends);
    }

    [Fact]
    public async Task HandleResponse_CachesBundle_AndRaisesEvent()
    {
        var sender = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = Build(sender);
        PreKeyBundleReceivedEventArgs? got = null;
        svc.BundleReceived += (_, e) => got = e;

        var reqId = Guid.NewGuid();
        var respPkt = new MeshPacket
        {
            Type = PacketType.PreKeyResponse,
            SourceUhid = "aether:bob:02",
            DestinationUhid = "aether:alice:01",
            Payload = JsonSerializer.SerializeToUtf8Bytes(
                PreKeyResponsePayload.FromBundle(reqId, SampleBundle("aether:bob:02")), JsonOpts),
        };

        Assert.True(await svc.HandleAsync(respPkt));
        Assert.NotNull(got);
        Assert.Equal(reqId, got!.RequestId);
        Assert.Equal("aether:bob:02", got.FromUhid);
        Assert.Equal("aether:bob:02", got.Bundle.Uhid);

        var cached = svc.GetReceivedBundle("aether:bob:02");
        Assert.NotNull(cached);
        Assert.Equal(4242, cached!.PreKeyId);
    }

    [Fact]
    public async Task Handle_WrongPacketType_ReturnsFalse()
    {
        var svc = Build(new FakeMeshSender());
        var pkt = new MeshPacket { Type = PacketType.Data, SourceUhid = "aether:x:01", Payload = Array.Empty<byte>() };
        Assert.False(await svc.HandleAsync(pkt));
    }
}
