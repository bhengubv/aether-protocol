// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.ApiClients;
using AetherNet.Constants;
using AetherNet.Core.Tests.Fakes;
using AetherNet.DependencyInjection;
using AetherNet.Extensibility;
using AetherNet.Incentive;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Services;
using AetherNet.Tipping;
using AetherNet.Tipping.Incentives;
using AetherNet.Tipping.Models;
using AetherNet.Tipping.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Tests for the ported SDPKT-settlement tipping layer: the in-memory stores,
/// <see cref="TippingService"/>, <see cref="NodeReputationService"/>,
/// <see cref="TipperQoSService"/>, <see cref="AetherRewardService"/>,
/// <see cref="TipEventHandler"/>, and the <see cref="SdpktMeshTipSettlementProvider"/>
/// plug-in into the protocol-level <see cref="IMeshTipService"/> hook.
/// </summary>
public sealed class TippingLayerTests
{
    private const string LocalUhid = "aether:local:01";
    private const string OperatorUhid = "aether:operator:02";

    // ── fakes ────────────────────────────────────────────────────────────────────

    private sealed class FixedLocalNodeProvider(string? uhid) : ILocalNodeProvider
    {
        public Task<string?> GetLocalUhidAsync() => Task.FromResult(uhid);
    }

    /// <summary>Records every backend call and returns configurable results. No real HTTP.</summary>
    private sealed class FakeAetherApiClient : IAetherApiClient
    {
        public List<object> MeshSettleCalls { get; } = [];
        public List<object> BatchSyncTipCalls { get; } = [];
        public List<object> BatchSyncRewardCalls { get; } = [];
        public List<object> RegisterOperatorCalls { get; } = [];

        public int TipsSyncedResult { get; set; }
        public int RewardsSyncedResult { get; set; }
        public bool MeshSettleResult { get; set; } = true;
        public List<TipPolicy> PoliciesResult { get; set; } = [];
        public NodeReputation? NodeReputationResult { get; set; }
        public bool ThrowOnMeshSettle { get; set; }
        public bool ThrowOnGetNodeReputation { get; set; }

        public Task<T?> RecordTipAsync<T>(object request) => Task.FromResult<T?>(default);

        public Task<int> BatchSyncTipsAsync(object request)
        {
            BatchSyncTipCalls.Add(request);
            return Task.FromResult(TipsSyncedResult);
        }

        public Task<List<T>> GetTipPoliciesAsync<T>()
            => Task.FromResult(PoliciesResult.Cast<T>().ToList());

        public Task<bool> MeshSettleTipAsync(object request)
        {
            if (ThrowOnMeshSettle) throw new HttpRequestException("settle down");
            MeshSettleCalls.Add(request);
            return Task.FromResult(MeshSettleResult);
        }

        public Task<T?> GetTipperReputationAsync<T>(string uhid) => Task.FromResult<T?>(default);

        public Task<int> BatchSyncRewardsAsync(object request)
        {
            BatchSyncRewardCalls.Add(request);
            return Task.FromResult(RewardsSyncedResult);
        }

        public Task<bool> RegisterOperatorAsync(object request)
        {
            RegisterOperatorCalls.Add(request);
            return Task.FromResult(true);
        }

        public Task<T?> GetNodeReputationAsync<T>(string uhid)
        {
            if (ThrowOnGetNodeReputation) throw new HttpRequestException("offline");
            return Task.FromResult((T?)(object?)NodeReputationResult);
        }

        public Task<T?> CreateWatchSessionAsync<T>(object request) => Task.FromResult<T?>(default);
        public Task<T?> CreateChipInPoolAsync<T>(object request) => Task.FromResult<T?>(default);
        public Task<T?> ContributeChipInAsync<T>(object request) => Task.FromResult<T?>(default);
    }

    private static (TippingService Svc, InMemoryAetherTipStore Store, FakeAetherApiClient Api, AetherRewardService Rewards)
        BuildTipping(string? localUhid = LocalUhid)
    {
        var store = new InMemoryAetherTipStore();
        var rewardStore = new InMemoryAetherRewardStore();
        var api = new FakeAetherApiClient();
        var rewards = new AetherRewardService(rewardStore, api, NullLogger<AetherRewardService>.Instance);
        var svc = new TippingService(store, new FixedLocalNodeProvider(localUhid), api, rewards,
            NullLogger<TippingService>.Instance);
        return (svc, store, api, rewards);
    }

    private static async Task RegisterOperatorAsync(InMemoryAetherTipStore store, string uhid, bool acceptsTips = true)
        => await store.SaveNodeOperatorAsync(new NodeOperatorProfile
        {
            Uhid = uhid,
            SdpktWalletAddress = "sdpkt:wallet:zzz",
            IsRegistered = true,
            AcceptsTips = acceptsTips,
            OperatorSince = DateTimeOffset.UtcNow
        });

    // ── InMemoryAetherTipStore ─────────────────────────────────────────────────

    [Fact]
    public async Task TipStore_Queue_Then_GetUnsynced_Then_MarkSynced()
    {
        var store = new InMemoryAetherTipStore();
        await store.QueueTipAsync(new LocalTipTransaction { TipperUhid = LocalUhid, Amount = 1m, CreatedAt = DateTimeOffset.UtcNow });
        await store.QueueTipAsync(new LocalTipTransaction { TipperUhid = LocalUhid, Amount = 2m, CreatedAt = DateTimeOffset.UtcNow });

        var unsynced = await store.GetUnsyncedTipsAsync(50);
        Assert.Equal(2, unsynced.Count);
        // Ids auto-assigned and monotonic.
        Assert.Equal([1, 2], unsynced.Select(t => t.Id).ToArray());

        await store.MarkTipsSyncedAsync([1]);
        var afterMark = await store.GetUnsyncedTipsAsync(50);
        var remaining = Assert.Single(afterMark);
        Assert.Equal(2, remaining.Id);
    }

    [Fact]
    public async Task TipStore_DailyTotal_SumsTodayForTipperOnly()
    {
        var store = new InMemoryAetherTipStore();
        await store.QueueTipAsync(new LocalTipTransaction { TipperUhid = LocalUhid, Amount = 5m, CreatedAt = DateTimeOffset.UtcNow });
        await store.QueueTipAsync(new LocalTipTransaction { TipperUhid = LocalUhid, Amount = 3m, CreatedAt = DateTimeOffset.UtcNow });
        // Different tipper — must not count.
        await store.QueueTipAsync(new LocalTipTransaction { TipperUhid = "other", Amount = 100m, CreatedAt = DateTimeOffset.UtcNow });
        // Yesterday — must not count.
        await store.QueueTipAsync(new LocalTipTransaction { TipperUhid = LocalUhid, Amount = 50m, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) });

        var total = await store.GetDailyTipTotalAsync(LocalUhid);
        Assert.Equal(8m, total);
    }

    [Fact]
    public async Task TipStore_Policies_Operator_Reputation_RoundTrip()
    {
        var store = new InMemoryAetherTipStore();
        await store.SaveTipPoliciesAsync([new TipPolicy { TrafficType = TipTrafficType.StreamRelay, MinAmount = 1m, MaxAmount = 9m }]);
        var policies = await store.GetTipPoliciesAsync();
        Assert.Equal(TipTrafficType.StreamRelay, Assert.Single(policies).TrafficType);

        await store.SaveNodeOperatorAsync(new NodeOperatorProfile { Uhid = OperatorUhid, IsRegistered = true });
        Assert.True((await store.GetNodeOperatorAsync(OperatorUhid))!.IsRegistered);
        Assert.Null(await store.GetNodeOperatorAsync("missing"));

        await store.SaveTipperReputationAsync(new TipperReputation { TipperUhid = LocalUhid, ConsistencyScore = 80, Tier = QoSTier.Gold });
        Assert.Equal(QoSTier.Gold, (await store.GetTipperReputationAsync(LocalUhid))!.Tier);
    }

    // ── InMemoryAetherRewardStore + AetherRewardService ────────────────────────

    [Fact]
    public async Task RewardService_Queue_Count_Sync()
    {
        var store = new InMemoryAetherRewardStore();
        var api = new FakeAetherApiClient { RewardsSyncedResult = 2 };
        var svc = new AetherRewardService(store, api, NullLogger<AetherRewardService>.Instance);

        await svc.QueueRewardAsync(AetherRewardActions.MeshTip, 5, "tip", Guid.NewGuid());
        await svc.QueueRewardAsync(AetherRewardActions.RelayPacket, 5);
        Assert.Equal(2, await svc.GetPendingCountAsync());

        var synced = await svc.SyncToServerAsync();
        Assert.Equal(2, synced);
        Assert.Single(api.BatchSyncRewardCalls);
        // All marked synced.
        Assert.Equal(0, await svc.GetPendingCountAsync());
    }

    [Fact]
    public async Task RewardService_ServerReturnsZero_StopsAndLeavesQueued()
    {
        var store = new InMemoryAetherRewardStore();
        var api = new FakeAetherApiClient { RewardsSyncedResult = 0 };
        var svc = new AetherRewardService(store, api, NullLogger<AetherRewardService>.Instance);
        await svc.QueueRewardAsync(AetherRewardActions.MeshTip, 5);

        var synced = await svc.SyncToServerAsync();
        Assert.Equal(0, synced);
        // Still queued for retry.
        Assert.Equal(1, await svc.GetPendingCountAsync());
    }

    // ── TippingService ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPolicy_NoCachedPolicy_ReturnsRegulatedDefault()
    {
        var (svc, _, _, _) = BuildTipping();
        var policy = await svc.GetPolicyAsync(TipTrafficType.ChunkServe);

        Assert.NotNull(policy);
        Assert.Equal(TipPolicyConstants.DefaultTipMinZar, policy!.MinAmount);
        Assert.Equal(TipPolicyConstants.DefaultTipMaxZar, policy.MaxAmount);
        Assert.Equal(TipPolicyConstants.DefaultDailyCapZar, policy.DailyCapPerTipper);
        Assert.Equal(TipPolicyConstants.SuggestedTipChunkServe, policy.SuggestedAmount);
        Assert.True(policy.IsEnabled);
    }

    [Fact]
    public async Task TipNode_HappyPath_QueuesTip_AndXpReward()
    {
        var (svc, store, _, rewards) = BuildTipping();
        await RegisterOperatorAsync(store, OperatorUhid);

        var ok = await svc.TipNodeAsync(OperatorUhid, 0.10m, TipTrafficType.MessageRelay);

        Assert.True(ok);
        var queued = Assert.Single(await store.GetUnsyncedTipsAsync(50));
        Assert.Equal(LocalUhid, queued.TipperUhid);
        Assert.Equal(OperatorUhid, queued.RecipientUhid);
        Assert.Equal(0.10m, queued.Amount);
        Assert.Equal(TipTrafficType.MessageRelay, queued.TrafficType);
        // XP reward queued for the tip.
        Assert.Equal(1, await rewards.GetPendingCountAsync());
        // Daily total reflects the queued tip.
        Assert.Equal(0.10m, await svc.GetDailyTotalAsync());
    }

    [Fact]
    public async Task TipNode_LocalNodeUninitialized_ReturnsFalse_QueuesNothing()
    {
        var (svc, store, _, _) = BuildTipping(localUhid: null);
        await RegisterOperatorAsync(store, OperatorUhid);

        var ok = await svc.TipNodeAsync(OperatorUhid, 0.10m, TipTrafficType.MessageRelay);

        Assert.False(ok);
        Assert.Empty(await store.GetUnsyncedTipsAsync(50));
    }

    [Fact]
    public async Task TipNode_RecipientNotRegisteredOperator_ReturnsFalse()
    {
        var (svc, store, _, _) = BuildTipping();
        // No operator saved for OperatorUhid.

        var ok = await svc.TipNodeAsync(OperatorUhid, 0.10m, TipTrafficType.MessageRelay);

        Assert.False(ok);
        Assert.Empty(await store.GetUnsyncedTipsAsync(50));
    }

    [Fact]
    public async Task TipNode_OperatorDoesNotAcceptTips_ReturnsFalse()
    {
        var (svc, store, _, _) = BuildTipping();
        await RegisterOperatorAsync(store, OperatorUhid, acceptsTips: false);

        var ok = await svc.TipNodeAsync(OperatorUhid, 0.10m, TipTrafficType.MessageRelay);

        Assert.False(ok);
    }

    [Fact]
    public async Task TipNode_AmountOutsidePolicyBand_ReturnsFalse()
    {
        var (svc, store, _, _) = BuildTipping();
        await RegisterOperatorAsync(store, OperatorUhid);

        // Above DefaultTipMaxZar (50.00).
        Assert.False(await svc.TipNodeAsync(OperatorUhid, 9999m, TipTrafficType.MessageRelay));
        // Below DefaultTipMinZar (0.10).
        Assert.False(await svc.TipNodeAsync(OperatorUhid, 0.001m, TipTrafficType.MessageRelay));
        Assert.Empty(await store.GetUnsyncedTipsAsync(50));
    }

    [Fact]
    public async Task TipNode_DailyCapExceeded_ReturnsFalse()
    {
        var (svc, store, _, _) = BuildTipping();
        await RegisterOperatorAsync(store, OperatorUhid);

        // Pre-seed near the daily cap (100 ZAR): one big tip just under max repeated.
        for (var i = 0; i < 2; i++)
            Assert.True(await svc.TipNodeAsync(OperatorUhid, 50m, TipTrafficType.MessageRelay));

        // Now at 100; any further tip exceeds DefaultDailyCapZar.
        Assert.False(await svc.TipNodeAsync(OperatorUhid, 0.10m, TipTrafficType.MessageRelay));
    }

    [Fact]
    public async Task SyncTips_PushesBatch_MarksSynced()
    {
        var (svc, store, api, _) = BuildTipping();
        await RegisterOperatorAsync(store, OperatorUhid);
        await svc.TipNodeAsync(OperatorUhid, 1m, TipTrafficType.MessageRelay);
        api.TipsSyncedResult = 1;

        var synced = await svc.SyncTipsToServerAsync();

        Assert.Equal(1, synced);
        Assert.Single(api.BatchSyncTipCalls);
        Assert.Empty(await store.GetUnsyncedTipsAsync(50));
        Assert.Equal(0, await svc.GetPendingTipCountAsync());
    }

    // ── NodeReputationService ──────────────────────────────────────────────────

    [Fact]
    public async Task RegisterAsOperator_SavesLocallyAndCallsServer()
    {
        var store = new InMemoryAetherTipStore();
        var api = new FakeAetherApiClient();
        var svc = new NodeReputationService(store, new FixedLocalNodeProvider(LocalUhid), api,
            NullLogger<NodeReputationService>.Instance);

        await svc.RegisterAsOperatorAsync("sdpkt:wallet:abcdef123");

        var profile = await store.GetNodeOperatorAsync(LocalUhid);
        Assert.NotNull(profile);
        Assert.True(profile!.IsRegistered);
        Assert.Equal("sdpkt:wallet:abcdef123", profile.SdpktWalletAddress);
        Assert.True(await svc.IsOperatorRegisteredAsync(LocalUhid));
        Assert.Single(api.RegisterOperatorCalls);
    }

    [Fact]
    public async Task RegisterAsOperator_LocalNodeUninitialized_NoOp()
    {
        var store = new InMemoryAetherTipStore();
        var api = new FakeAetherApiClient();
        var svc = new NodeReputationService(store, new FixedLocalNodeProvider(null), api,
            NullLogger<NodeReputationService>.Instance);

        await svc.RegisterAsOperatorAsync("sdpkt:wallet:abc");

        Assert.Empty(api.RegisterOperatorCalls);
        Assert.False(await svc.IsOperatorRegisteredAsync(LocalUhid));
    }

    [Fact]
    public async Task GetReputation_ApiThrows_ReturnsNull()
    {
        var store = new InMemoryAetherTipStore();
        var api = new FakeAetherApiClient { ThrowOnGetNodeReputation = true };
        var svc = new NodeReputationService(store, new FixedLocalNodeProvider(LocalUhid), api,
            NullLogger<NodeReputationService>.Instance);

        Assert.Null(await svc.GetReputationAsync(OperatorUhid));
    }

    [Fact]
    public async Task RefreshReputations_CachesServerPolicies()
    {
        var store = new InMemoryAetherTipStore();
        var api = new FakeAetherApiClient
        {
            PoliciesResult = [new TipPolicy { TrafficType = TipTrafficType.VoiceRelay, MinAmount = 0.5m, MaxAmount = 5m }]
        };
        var svc = new NodeReputationService(store, new FixedLocalNodeProvider(LocalUhid), api,
            NullLogger<NodeReputationService>.Instance);

        await svc.RefreshReputationsAsync();

        var cached = await store.GetTipPoliciesAsync();
        Assert.Equal(TipTrafficType.VoiceRelay, Assert.Single(cached).TrafficType);
    }

    // ── TipperQoSService ───────────────────────────────────────────────────────

    [Theory]
    [InlineData((short)10, QoSTier.Standard, (short)0)]
    [InlineData((short)25, QoSTier.Bronze, TipPolicyConstants.QoSBoostBronze)]
    [InlineData((short)50, QoSTier.Silver, TipPolicyConstants.QoSBoostSilver)]
    [InlineData((short)90, QoSTier.Gold, TipPolicyConstants.QoSBoostGold)]
    public async Task QoS_RefreshComputesTier_AndBoostMatches(short consistency, QoSTier expectedTier, short expectedBoost)
    {
        var store = new InMemoryAetherTipStore();
        await store.SaveTipperReputationAsync(new TipperReputation { TipperUhid = LocalUhid, ConsistencyScore = consistency });
        var svc = new TipperQoSService(store, new FixedLocalNodeProvider(LocalUhid), NullLogger<TipperQoSService>.Instance);

        await svc.RefreshScoresAsync();

        Assert.Equal(expectedTier, svc.GetTier(LocalUhid));
        Assert.Equal(expectedBoost, svc.GetQoSBoost(LocalUhid));
    }

    [Fact]
    public void QoS_UnknownTipper_IsStandard_NoBoost()
    {
        var svc = new TipperQoSService(new InMemoryAetherTipStore(), new FixedLocalNodeProvider(LocalUhid),
            NullLogger<TipperQoSService>.Instance);

        Assert.Equal(QoSTier.Standard, svc.GetTier("nobody"));
        Assert.Equal((short)0, svc.GetQoSBoost("nobody"));
    }

    // ── TipEventHandler ────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public async Task TipEventHandler_Gateway_SettlesTipPacket_ViaMeshSettle()
    {
        var (tipping, _, api, _) = BuildTipping();
        var handler = new TipEventHandler(tipping, api, NullLogger<TipEventHandler>.Instance);

        var payload = new TipPacketPayload
        {
            TipperUhid = "aether:tipper:zz",
            RecipientUhid = OperatorUhid,
            Amount = 2.50m,
            TrafficType = "message-relay",
            ReferenceId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Signature = new byte[64],
        };
        var packet = new MeshPacket
        {
            Type = PacketType.TipPacket,
            SourceUhid = "aether:tipper:zz",
            DestinationUhid = OperatorUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts),
        };

        await handler.HandleTipPacketAsync(packet, isGateway: true);

        Assert.Single(api.MeshSettleCalls);
    }

    [Fact]
    public async Task TipEventHandler_NonGateway_DoesNotSettle()
    {
        var (tipping, _, api, _) = BuildTipping();
        var handler = new TipEventHandler(tipping, api, NullLogger<TipEventHandler>.Instance);

        var payload = new TipPacketPayload { TipperUhid = "t", RecipientUhid = "r", Amount = 1m, TrafficType = "x", Signature = new byte[64] };
        var packet = new MeshPacket { Type = PacketType.TipPacket, Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts) };

        await handler.HandleTipPacketAsync(packet, isGateway: false);

        Assert.Empty(api.MeshSettleCalls);
    }

    [Fact]
    public async Task TipEventHandler_SettlementThrows_SwallowedNoThrow()
    {
        var (tipping, _, api, _) = BuildTipping();
        api.ThrowOnMeshSettle = true;
        var handler = new TipEventHandler(tipping, api, NullLogger<TipEventHandler>.Instance);

        var payload = new TipPacketPayload { TipperUhid = "t", RecipientUhid = "r", Amount = 1m, TrafficType = "x", Signature = new byte[64] };
        var packet = new MeshPacket { Type = PacketType.TipPacket, Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts) };

        // Must not throw — failed settlement is logged and swallowed for retry.
        await handler.HandleTipPacketAsync(packet, isGateway: true);
        Assert.Empty(api.MeshSettleCalls);
    }

    [Fact]
    public async Task TipEventHandler_OnMessageRelayed_AutoTipsRelayNode()
    {
        var (tipping, store, _, _) = BuildTipping();
        await RegisterOperatorAsync(store, OperatorUhid);
        var handler = new TipEventHandler(tipping, new FakeAetherApiClient(), NullLogger<TipEventHandler>.Instance);

        await handler.OnMessageRelayedAsync(OperatorUhid);

        var queued = Assert.Single(await store.GetUnsyncedTipsAsync(50));
        Assert.Equal(OperatorUhid, queued.RecipientUhid);
        Assert.Equal(TipTrafficType.MessageRelay, queued.TrafficType);
        Assert.Equal(TipPolicyConstants.SuggestedTipMessageRelay, queued.Amount);
    }

    // ── SdpktMeshTipSettlementProvider (the protocol-hook plug-in) ─────────────

    [Fact]
    public async Task SdpktSettlementProvider_SettleMeshTip_ForwardsToBackend()
    {
        var api = new FakeAetherApiClient();
        var provider = new SdpktMeshTipSettlementProvider(api, NullLogger<SdpktMeshTipSettlementProvider>.Instance);

        var tip = new TipPacketPayload
        {
            TipperUhid = "aether:tipper:zz",
            RecipientUhid = OperatorUhid,
            Amount = 3.33m,
            TrafficType = "gateway-share",
            Timestamp = DateTimeOffset.UtcNow,
            Signature = new byte[64],
        };

        await provider.SettleMeshTipAsync(tip);

        Assert.Single(api.MeshSettleCalls);
    }

    [Fact]
    public async Task SdpktSettlementProvider_BackendThrows_Swallowed()
    {
        var api = new FakeAetherApiClient { ThrowOnMeshSettle = true };
        var provider = new SdpktMeshTipSettlementProvider(api, NullLogger<SdpktMeshTipSettlementProvider>.Instance);

        // Must not throw — settlement failure is logged so the inbound packet can still relay.
        await provider.SettleMeshTipAsync(new TipPacketPayload { TipperUhid = "t", RecipientUhid = "r", Amount = 1m, TrafficType = "x" });
    }

    /// <summary>
    /// End-to-end: a real <see cref="MeshTipService"/> receiving an inbound
    /// <see cref="PacketType.TipPacket"/> off the mesh settles it through the SDPKT
    /// provider's <see cref="IAetherNetIncentiveProvider.SettleMeshTipAsync"/>, which
    /// forwards it to the backend — exactly the wired settlement path.
    /// </summary>
    [Fact]
    public async Task MeshTipService_InboundTipPacket_SettlesThroughSdpktProvider()
    {
        var api = new FakeAetherApiClient();
        var sdpkt = new SdpktMeshTipSettlementProvider(api, NullLogger<SdpktMeshTipSettlementProvider>.Instance);

        var sender = new EndToEndMeshSender { LocalUhid = OperatorUhid };
        var signal = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var packetSigning = new PacketSigningService(signal, NullLogger<PacketSigningService>.Instance);
        var meshTip = new MeshTipService(sender, new NoRouteRouting(), packetSigning, signal, sdpkt,
            NullLogger<MeshTipService>.Instance);

        // Build a signed inbound tip addressed to this node.
        var payload = new TipPacketPayload
        {
            TipperUhid = "aether:tipper:zz",
            RecipientUhid = OperatorUhid,
            Amount = 7.77m,
            TrafficType = "message-relay",
            Timestamp = DateTimeOffset.UtcNow,
        };
        payload.Signature = await signal.SignDataAsync(payload.BuildCanonicalData());

        var packet = new MeshPacket
        {
            Type = PacketType.TipPacket,
            SourceUhid = "aether:tipper:zz",
            DestinationUhid = OperatorUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts),
        };

        await meshTip.HandleTipPacketAsync(packet);

        // The protocol hook fired and the SDPKT provider forwarded settlement to the backend.
        Assert.Single(api.MeshSettleCalls);
    }

    private sealed class EndToEndMeshSender : IMeshSender
    {
        public string LocalUhid { get; init; } = OperatorUhid;
        public string? LocalGeohash => null;
        public IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();
        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default) => Task.FromResult(true);
        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default) => Task.FromResult(1);
    }

    private sealed class NoRouteRouting : IRoutingService
    {
        public Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken ct = default)
            => Task.FromResult<RouteEntry?>(null);
        public RouteEntry? GetCachedRoute(string destinationUhid) => null;
        public IReadOnlyList<RouteEntry> GetAllRoutes() => Array.Empty<RouteEntry>();
        public Task HandleRouteRequestAsync(MeshPacket routeRequest, string? linkLayerSenderUhid = null, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken ct = default) => Task.CompletedTask;
        public Task PruneAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>
/// Verifies <c>AddTipping()</c> in the protocol builder: it registers the full
/// tipping layer (services, stores, backend bridge) and plugs the SDPKT settlement
/// provider into the protocol-level <see cref="IAetherNetIncentiveProvider"/> hook so
/// <c>AddMeshTip()</c>'s <see cref="IMeshTipService"/> settles inbound tips through it.
/// </summary>
public sealed class AddTippingDiTests
{
    private sealed class FixedLocalNodeProvider : ILocalNodeProvider
    {
        public Task<string?> GetLocalUhidAsync() => Task.FromResult<string?>("aether:di:01");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        // Host-supplied seams AddTipping depends on.
        services.AddSingleton<ILocalNodeProvider, FixedLocalNodeProvider>();
        services.AddHttpClient(AetherNet.Tipping.ApiClients.AetherApiClient.HttpClientName, c =>
            c.BaseAddress = new System.Uri("https://aether.example.test"));

        services.AddAetherNetProtocol(opts => opts.LocalUhid = "aether:di:01")
                .AddTipping();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddTipping_ResolvesEntireLayer()
    {
        using var sp = BuildProvider();

        Assert.IsType<TippingService>(sp.GetService<ITippingService>());
        Assert.IsType<NodeReputationService>(sp.GetService<AetherNet.Tipping.Services.INodeReputationService>());
        Assert.IsType<TipperQoSService>(sp.GetService<ITipperQoSService>());
        Assert.IsType<AetherRewardService>(sp.GetService<IAetherRewardService>());
        Assert.IsType<AetherNet.Tipping.ApiClients.AetherApiClient>(sp.GetService<IAetherApiClient>());
        Assert.NotNull(sp.GetService<TipEventHandler>());

        // Default in-memory stores wired.
        Assert.IsType<InMemoryAetherTipStore>(sp.GetService<IAetherTipStore>());
        Assert.IsType<InMemoryAetherRewardStore>(sp.GetService<IAetherRewardStore>());
    }

    [Fact]
    public void AddTipping_WiresSdpktProvider_AsIncentiveProvider()
    {
        using var sp = BuildProvider();

        var incentives = sp.GetService<IAetherNetIncentiveProvider>();
        Assert.NotNull(incentives);
        Assert.IsType<SdpktMeshTipSettlementProvider>(incentives);
    }

    [Fact]
    public void AddTipping_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalNodeProvider, FixedLocalNodeProvider>();
        services.AddHttpClient(AetherNet.Tipping.ApiClients.AetherApiClient.HttpClientName);

        services.AddAetherNetProtocol().AddTipping().AddTipping();

        using var sp = services.BuildServiceProvider();
        Assert.Single(sp.GetServices<ITippingService>());
    }

    [Fact]
    public void AddTipping_ThenAddMeshTip_MeshTipServicePicksUpSdpktProvider()
    {
        // The full wiring: AddMeshTip resolves IAetherNetIncentiveProvider, which AddTipping
        // supplied as the SDPKT settlement provider — so an inbound TipPacket settles via SDPKT.
        var services = new ServiceCollection();
        services.AddSingleton<ILocalNodeProvider, FixedLocalNodeProvider>();
        services.AddSingleton<IMeshSender>(new FakeMeshSender("aether:di:01"));
        services.AddHttpClient(AetherNet.Tipping.ApiClients.AetherApiClient.HttpClientName, c =>
            c.BaseAddress = new System.Uri("https://aether.example.test"));

        services.AddAetherNetProtocol(opts => opts.LocalUhid = "aether:di:01")
                .AddSignalProtocol()
                .AddRouting()
                .AddTipping()
                .AddMeshTip();

        using var sp = services.BuildServiceProvider();

        Assert.IsType<SdpktMeshTipSettlementProvider>(sp.GetService<IAetherNetIncentiveProvider>());
        Assert.IsType<MeshTipService>(sp.GetService<IMeshTipService>());
    }
}
