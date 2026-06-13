// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Extensibility;
using AetherNet.Incentive;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Tests for the generic mesh-tip wire surface: <see cref="TipPacketPayload"/>,
/// <see cref="IMeshTipService"/>/<see cref="MeshTipService"/>, and the inbound
/// <see cref="IAetherNetIncentiveProvider.SettleMeshTipAsync"/> dispatch.
///
/// These exercise the protocol mechanism ONLY — no settlement, no value semantics.
/// </summary>
public sealed class MeshTipServiceTests
{
    private const string LocalUhid = "aether:local:01";
    private const string RecipientUhid = "aether:recipient:02";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeMeshSender : IMeshSender
    {
        public string LocalUhid { get; init; } = MeshTipServiceTests.LocalUhid;
        public string? LocalGeohash => null;
        public List<(MeshPacket Packet, string NextHop)> Unicasts { get; } = [];
        public List<MeshPacket> Broadcasts { get; } = [];

        public IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();

        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default)
        {
            Unicasts.Add((packet, nextHopUhid));
            return Task.FromResult(true);
        }

        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default)
        {
            Broadcasts.Add(packet);
            return Task.FromResult(1);
        }
    }

    /// <summary>Routing fake — returns no route so sends/relays take the broadcast fallback.</summary>
    private sealed class NoRouteRoutingService : IRoutingService
    {
        public Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken ct = default)
            => Task.FromResult<RouteEntry?>(null);
        public RouteEntry? GetCachedRoute(string destinationUhid) => null;
        public IReadOnlyList<RouteEntry> GetAllRoutes() => Array.Empty<RouteEntry>();
        public Task HandleRouteRequestAsync(MeshPacket routeRequest, string? linkLayerSenderUhid = null, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task PruneAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Captures every <see cref="IAetherNetIncentiveProvider.SettleMeshTipAsync"/> call.</summary>
    private sealed class CapturingProvider : IAetherNetIncentiveProvider
    {
        public List<TipPacketPayload> Settled { get; } = [];

        public Task SettleMeshTipAsync(TipPacketPayload tip, CancellationToken cancellationToken = default)
        {
            Settled.Add(tip);
            return Task.CompletedTask;
        }
    }

    /// <summary>Uses every default-method on the interface — the bare-node case.</summary>
    private sealed class DefaultProvider : IAetherNetIncentiveProvider
    {
    }

    // ── factory ──────────────────────────────────────────────────────────────

    private static (MeshTipService Svc, FakeMeshSender Sender, SignalProtocolService Signal, CapturingProvider Provider)
        Build(string localUhid = LocalUhid)
    {
        var sender = new FakeMeshSender { LocalUhid = localUhid };
        var signal = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var packetSigning = new PacketSigningService(signal, NullLogger<PacketSigningService>.Instance);
        var routing = new NoRouteRoutingService();
        var provider = new CapturingProvider();
        var svc = new MeshTipService(sender, routing, packetSigning, signal, provider,
            NullLogger<MeshTipService>.Instance);
        return (svc, sender, signal, provider);
    }

    // ── (a) JSON round-trips byte-identically with the exact snake_case names ──

    [Fact]
    public void TipPacketPayload_SerializesWithExactSnakeCaseFieldNames()
    {
        var refId = Guid.Parse("11112222-3333-4444-5555-666677778888");
        var payload = new TipPacketPayload
        {
            TipperUhid    = "aether:tipper:aa",
            RecipientUhid = "aether:recipient:bb",
            Amount        = 12.50m,
            TrafficType   = "message-relay",
            ReferenceId   = refId,
            Timestamp     = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
            Signature     = [1, 2, 3, 4],
        };

        var json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));

        // Exact wire field names (snake_case).
        Assert.Contains("\"tipper_uhid\"", json);
        Assert.Contains("\"recipient_uhid\"", json);
        Assert.Contains("\"amount\"", json);
        Assert.Contains("\"traffic_type\"", json);
        Assert.Contains("\"reference_id\"", json);
        Assert.Contains("\"timestamp\"", json);
        Assert.Contains("\"signature\"", json);

        // No camelCase / PascalCase leakage.
        Assert.DoesNotContain("tipperUhid", json);
        Assert.DoesNotContain("TipperUhid", json);
        Assert.DoesNotContain("trafficType", json);
    }

    [Fact]
    public void TipPacketPayload_RoundTripsByteIdentically()
    {
        var payload = new TipPacketPayload
        {
            TipperUhid    = "aether:tipper:aa",
            RecipientUhid = "aether:recipient:bb",
            Amount        = 0.0001m,
            TrafficType   = "gateway-share",
            ReferenceId   = Guid.NewGuid(),
            Timestamp     = DateTimeOffset.FromUnixTimeMilliseconds(1_699_999_999_001),
            Signature     = [9, 8, 7, 6, 5],
        };

        var first = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        var back = JsonSerializer.Deserialize<TipPacketPayload>(first, JsonOpts)!;
        var second = JsonSerializer.SerializeToUtf8Bytes(back, JsonOpts);

        // Byte-identical re-serialisation.
        Assert.Equal(first, second);

        // Field-level fidelity.
        Assert.Equal(payload.TipperUhid, back.TipperUhid);
        Assert.Equal(payload.RecipientUhid, back.RecipientUhid);
        Assert.Equal(payload.Amount, back.Amount);
        Assert.Equal(payload.TrafficType, back.TrafficType);
        Assert.Equal(payload.ReferenceId, back.ReferenceId);
        Assert.Equal(payload.Timestamp.ToUnixTimeMilliseconds(), back.Timestamp.ToUnixTimeMilliseconds());
        Assert.Equal(payload.Signature, back.Signature);
    }

    [Fact]
    public void TipPacketPayload_NullReferenceId_RoundTrips()
    {
        var payload = new TipPacketPayload
        {
            TipperUhid    = "t",
            RecipientUhid = "r",
            Amount        = 1m,
            TrafficType   = "x",
            ReferenceId   = null,
            Signature     = [0],
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        var back = JsonSerializer.Deserialize<TipPacketPayload>(bytes, JsonOpts)!;
        Assert.Null(back.ReferenceId);
    }

    // ── (b) inbound TipPacket(24) invokes SettleMeshTipAsync with the right fields ──

    [Fact]
    public async Task HandleTipPacket_InvokesSettleMeshTip_WithVerbatimFields()
    {
        var (svc, _, signal, provider) = Build();
        var refId = Guid.NewGuid();

        var payload = new TipPacketPayload
        {
            TipperUhid    = "aether:tipper:zz",
            RecipientUhid = RecipientUhid,
            Amount        = 7.25m,
            TrafficType   = "message-relay",
            ReferenceId   = refId,
            Timestamp     = DateTimeOffset.UtcNow,
        };
        payload.Signature = await signal.SignDataAsync(payload.BuildCanonicalData());

        var packet = new MeshPacket
        {
            Type            = PacketType.TipPacket,
            SourceUhid      = "aether:tipper:zz",
            DestinationUhid = RecipientUhid,
            Ttl             = ProtocolConstants.DefaultTtl,
            Payload         = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts),
        };

        await svc.HandleTipPacketAsync(packet);

        var settled = Assert.Single(provider.Settled);
        Assert.Equal("aether:tipper:zz", settled.TipperUhid);
        Assert.Equal(RecipientUhid, settled.RecipientUhid);
        Assert.Equal(7.25m, settled.Amount);
        Assert.Equal("message-relay", settled.TrafficType);
        Assert.Equal(refId, settled.ReferenceId);
    }

    [Fact]
    public async Task HandleTipPacket_NotDestination_RelaysOnward()
    {
        // This node is the relay, not the addressed recipient → packet should be re-sent.
        var (svc, sender, signal, provider) = Build(localUhid: "aether:relay:99");

        var payload = new TipPacketPayload
        {
            TipperUhid    = "aether:tipper:zz",
            RecipientUhid = RecipientUhid,
            Amount        = 3m,
            TrafficType   = "gateway-share",
            Timestamp     = DateTimeOffset.UtcNow,
        };
        payload.Signature = await signal.SignDataAsync(payload.BuildCanonicalData());

        var packet = new MeshPacket
        {
            Type            = PacketType.TipPacket,
            SourceUhid      = "aether:tipper:zz",
            DestinationUhid = RecipientUhid, // not the local relay node
            Ttl             = ProtocolConstants.DefaultTtl,
            Payload         = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts),
        };

        await svc.HandleTipPacketAsync(packet);

        // Settled locally AND relayed onward (broadcast fallback since NoRouteRoutingService).
        Assert.Single(provider.Settled);
        Assert.Single(sender.Broadcasts);
        Assert.Equal(PacketType.TipPacket, sender.Broadcasts[0].Type);
    }

    [Fact]
    public async Task HandleTipPacket_MalformedJson_DropsWithoutThrow_NothingSettled()
    {
        var (svc, sender, _, provider) = Build();
        var packet = new MeshPacket
        {
            Type    = PacketType.TipPacket,
            Payload = Encoding.UTF8.GetBytes("{ this is not valid json"),
        };

        await svc.HandleTipPacketAsync(packet); // must not throw

        Assert.Empty(provider.Settled);
        Assert.Empty(sender.Broadcasts);
        Assert.Empty(sender.Unicasts);
    }

    [Fact]
    public async Task HandleTipPacket_MissingSignature_DropsWithoutThrow_NothingSettled()
    {
        var (svc, _, _, provider) = Build();
        var payload = new TipPacketPayload
        {
            TipperUhid    = "t",
            RecipientUhid = "r",
            Amount        = 1m,
            TrafficType   = "x",
            Signature     = null, // unverifiable
        };
        var packet = new MeshPacket
        {
            Type    = PacketType.TipPacket,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts),
        };

        await svc.HandleTipPacketAsync(packet);

        Assert.Empty(provider.Settled);
    }

    [Fact]
    public async Task HandleTipPacket_WrongPacketType_Ignored()
    {
        var (svc, _, _, provider) = Build();
        var packet = new MeshPacket { Type = PacketType.Data, Payload = [1, 2, 3] };

        await svc.HandleTipPacketAsync(packet);

        Assert.Empty(provider.Settled);
    }

    // ── (c) the DEFAULT provider no-ops (no throw, nothing settled) ────────────

    [Fact]
    public async Task SettleMeshTipAsync_DefaultImpl_IsNoOpAndReturnsCompleted()
    {
        IAetherNetIncentiveProvider provider = new DefaultProvider();
        var tip = new TipPacketPayload
        {
            TipperUhid    = "t",
            RecipientUhid = "r",
            Amount        = 99m,
            TrafficType   = "message-relay",
        };

        // No throw, returns immediately.
        await provider.SettleMeshTipAsync(tip);
    }

    [Fact]
    public async Task HandleTipPacket_WithDefaultProvider_SettlesNothing_StillRelays()
    {
        // A bare node: default no-op provider. It accepts and relays but settles nothing.
        var sender = new FakeMeshSender { LocalUhid = "aether:relay:99" };
        var signal = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var packetSigning = new PacketSigningService(signal, NullLogger<PacketSigningService>.Instance);
        var svc = new MeshTipService(sender, new NoRouteRoutingService(), packetSigning, signal,
            incentives: null, logger: NullLogger<MeshTipService>.Instance);

        var payload = new TipPacketPayload
        {
            TipperUhid    = "aether:tipper:zz",
            RecipientUhid = RecipientUhid,
            Amount        = 5m,
            TrafficType   = "gateway-share",
            Timestamp     = DateTimeOffset.UtcNow,
        };
        payload.Signature = await signal.SignDataAsync(payload.BuildCanonicalData());

        var packet = new MeshPacket
        {
            Type            = PacketType.TipPacket,
            SourceUhid      = "aether:tipper:zz",
            DestinationUhid = RecipientUhid,
            Ttl             = ProtocolConstants.DefaultTtl,
            Payload         = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts),
        };

        // No capturing provider to assert against — the point is simply that the bare
        // node neither throws nor blocks: it relays the addressed packet onward.
        await svc.HandleTipPacketAsync(packet);
        Assert.Single(sender.Broadcasts);
    }

    // ── (d) SendTipAsync produces a signed MeshPacket(24) that round-trips ──────

    [Fact]
    public async Task SendTipAsync_ProducesSignedTipPacket_PayloadRoundTripsToInput()
    {
        var (svc, sender, signal, _) = Build();
        var refId = Guid.NewGuid();

        var produced = await svc.SendTipAsync(
            RecipientUhid, amount: 42.42m, trafficType: "message-relay", referenceId: refId);

        // Type 24, addressed to the recipient, with a non-empty envelope signature.
        Assert.Equal(PacketType.TipPacket, produced.Type);
        Assert.Equal((byte)24, (byte)produced.Type);
        Assert.Equal(LocalUhid, produced.SourceUhid);
        Assert.Equal(RecipientUhid, produced.DestinationUhid);
        Assert.NotEmpty(produced.Signature);

        // It was routed onto the mesh (broadcast fallback, since NoRouteRoutingService).
        var routed = Assert.Single(sender.Broadcasts);
        Assert.Equal(PacketType.TipPacket, routed.Type);

        // Payload deserialises back to the exact input.
        var payload = JsonSerializer.Deserialize<TipPacketPayload>(produced.Payload, JsonOpts)!;
        Assert.Equal(LocalUhid, payload.TipperUhid);
        Assert.Equal(RecipientUhid, payload.RecipientUhid);
        Assert.Equal(42.42m, payload.Amount);
        Assert.Equal("message-relay", payload.TrafficType);
        Assert.Equal(refId, payload.ReferenceId);

        // The payload signature is a real Ed25519 signature over the canonical bytes —
        // verifiable with the tipper's public key.
        Assert.NotNull(payload.Signature);
        var ok = signal.VerifySignature(signal.GetPublicKey(), payload.BuildCanonicalData(), payload.Signature!);
        Assert.True(ok);
    }

    [Fact]
    public async Task SendTipAsync_RoundTripsThroughHandle_SettlesSameFields()
    {
        // End-to-end: send produces a packet; feeding it back into Handle settles the same fields.
        var (svc, sender, _, provider) = Build();

        var produced = await svc.SendTipAsync(RecipientUhid, amount: 1.23m, trafficType: "gateway-share");
        var onWire = Assert.Single(sender.Broadcasts);

        // The local node is the tipper, so it is not the recipient — Handle will settle + relay.
        await svc.HandleTipPacketAsync(onWire);

        var settled = Assert.Single(provider.Settled);
        Assert.Equal(LocalUhid, settled.TipperUhid);
        Assert.Equal(RecipientUhid, settled.RecipientUhid);
        Assert.Equal(1.23m, settled.Amount);
        Assert.Equal("gateway-share", settled.TrafficType);
        _ = produced;
    }
}
