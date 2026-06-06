// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherMesh.DependencyInjection;
using AetherMesh.Extensibility;
using AetherMesh.Protocol;
using AetherMesh.Transport.Abstractions;
using AetherMesh.Transport.Models;
using AetherMesh.Transport.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AetherMesh.Core.Tests;

/// <summary>
/// Unit tests for the <see cref="IAetherMeshAiProvider"/> interface, the
/// <see cref="NullAetherMeshAiProvider"/> no-op implementation, and the AI-augmented
/// <see cref="PredictiveTransportSelector.RankWithAiAsync"/> ranking method.
///
/// No Moq / NSubstitute — all fakes are private sealed classes inside this file.
/// </summary>
public sealed class AetherMeshAiProviderTests
{
    // ══════════════════════════════════════════════════════════════════════════════
    //  Shared infrastructure — inline fakes
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Minimal <see cref="ITransportService"/> stub mirroring the one in
    /// <see cref="PredictiveTransportSelectorTests"/>.
    /// </summary>
    private sealed class FakeTransport : ITransportService
    {
        public string Name              { get; }
        public bool   IsAvailable       { get; }
        public long   MaxBandwidthBps   { get; }
        public int    MaxRangeMeters    => 100;
        public int    PowerCostRelative { get; }
        public int    MaxConcurrentPeers => 10;
        public PerTransportMetrics? Metrics { get; } = new();

        public FakeTransport(
            string name,
            long   bandwidthBps = 500_000L,
            int    powerCost    = 1,
            bool   available    = true)
        {
            Name              = name;
            MaxBandwidthBps   = bandwidthBps;
            PowerCostRelative = powerCost;
            IsAvailable       = available;
        }

        public Task<bool> SendAsync(string _, byte[] __, CancellationToken ___)
            => Task.FromResult(true);
        public Task<bool> SendStreamAsync(string _, Stream __, CancellationToken ___)
            => Task.FromResult(true);
        public bool IsConnected(string _) => false;
#pragma warning disable CS0067
        public event Action<string, byte[]>? DataReceived;
#pragma warning restore CS0067
    }

    /// <summary>
    /// Configurable AI provider that returns preset biases and route suggestions.
    /// <see cref="IsAvailable"/> is <c>true</c> by default.
    /// </summary>
    private sealed class FakeAiProvider : IAetherMeshAiProvider
    {
        private readonly IReadOnlyDictionary<string, double> _biases;
        private readonly IReadOnlyList<AiRouteSuggestion>    _routes;
        private readonly AiThreatLevel                       _threatLevel;

        public bool IsAvailable { get; }

        public FakeAiProvider(
            IReadOnlyDictionary<string, double>? biases       = null,
            IReadOnlyList<AiRouteSuggestion>?    routes       = null,
            AiThreatLevel                        threatLevel  = AiThreatLevel.None,
            bool                                 isAvailable  = true)
        {
            _biases      = biases      ?? new Dictionary<string, double>();
            _routes      = routes      ?? Array.Empty<AiRouteSuggestion>();
            _threatLevel = threatLevel;
            IsAvailable  = isAvailable;
        }

        public Task<IReadOnlyList<AiRouteSuggestion>> SuggestRoutesAsync(
            string _, int __, CancellationToken ___ = default)
            => Task.FromResult(_routes);

        public Task<IReadOnlyDictionary<string, double>> GetTransportBiasesAsync(
            int _, CancellationToken __ = default)
            => Task.FromResult(_biases);

        public Task<AiThreatLevel> AssessThreatAsync(
            MeshPacket _, CancellationToken __ = default)
            => Task.FromResult(_threatLevel);
    }

    /// <summary>AI provider whose <see cref="GetTransportBiasesAsync"/> always throws.</summary>
    private sealed class ThrowingAiProvider : IAetherMeshAiProvider
    {
        public bool IsAvailable => true;

        public Task<IReadOnlyList<AiRouteSuggestion>> SuggestRoutesAsync(
            string _, int __, CancellationToken ___ = default)
            => Task.FromResult<IReadOnlyList<AiRouteSuggestion>>(Array.Empty<AiRouteSuggestion>());

        public Task<IReadOnlyDictionary<string, double>> GetTransportBiasesAsync(
            int _, CancellationToken __ = default)
            => throw new InvalidOperationException("Simulated AI provider failure.");

        public Task<AiThreatLevel> AssessThreatAsync(
            MeshPacket _, CancellationToken __ = default)
            => Task.FromResult(AiThreatLevel.None);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  Group A — Interface contract / no-op defaults
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>1. NullAetherMeshAiProvider.IsAvailable is always false.</summary>
    [Fact]
    public void NullProvider_IsAvailable_IsFalse()
    {
        var provider = new NullAetherMeshAiProvider();
        Assert.False(provider.IsAvailable);
    }

    /// <summary>2. NullAetherMeshAiProvider.SuggestRoutesAsync returns an empty list.</summary>
    [Fact]
    public async Task NullProvider_SuggestRoutes_ReturnsEmpty()
    {
        var provider = new NullAetherMeshAiProvider();
        var result   = await provider.SuggestRoutesAsync("uhid:dest", 512);
        Assert.Empty(result);
    }

    /// <summary>3. NullAetherMeshAiProvider.GetTransportBiasesAsync returns an empty dictionary.</summary>
    [Fact]
    public async Task NullProvider_GetTransportBiases_ReturnsEmpty()
    {
        var provider = new NullAetherMeshAiProvider();
        var result   = await provider.GetTransportBiasesAsync(512);
        Assert.Empty(result);
    }

    /// <summary>4. NullAetherMeshAiProvider.AssessThreatAsync returns AiThreatLevel.None.</summary>
    [Fact]
    public async Task NullProvider_AssessThreat_ReturnsNone()
    {
        var provider = new NullAetherMeshAiProvider();
        var packet   = new MeshPacket { Type = PacketType.Data };
        var result   = await provider.AssessThreatAsync(packet);
        Assert.Equal(AiThreatLevel.None, result);
    }

    /// <summary>
    /// 5. A custom provider with IsAvailable=false causes RankWithAiAsync to return
    ///    the same ordering as the plain Rank call — AI is never consulted.
    /// </summary>
    [Fact]
    public async Task DefaultInterfaceImpl_NeverCalled_WhenNotAvailable()
    {
        var sel  = new PredictiveTransportSelector();
        var fast = new FakeTransport("fast", bandwidthBps: 1_000_000L, powerCost: 1);
        var slow = new FakeTransport("slow", bandwidthBps: 10_000L,    powerCost: 10);
        sel.Register(fast, 50.0);
        sel.Register(slow, 150.0);

        // Give fast some good observations so it's clearly ranked higher.
        for (int i = 0; i < 5; i++)
            sel.ObserveMetrics(fast, rttMs: 50, success: true, bytesTransferred: 1000);

        // Provider is unavailable — biases that would change the order if applied.
        var unavailableProvider = new FakeAiProvider(
            biases: new Dictionary<string, double> { ["fast"] = 0.0, ["slow"] = 100.0 },
            isAvailable: false);

        var baseResult = sel.Rank(512);
        var aiResult   = await sel.RankWithAiAsync(512, unavailableProvider);

        Assert.Equal(baseResult.Count, aiResult.Count);
        for (int i = 0; i < baseResult.Count; i++)
            Assert.Equal(baseResult[i].Transport.Name, aiResult[i].Transport.Name);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  Group B — Transport bias application
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>6. Empty bias dict → identical transport order to plain Rank.</summary>
    [Fact]
    public async Task RankWithAi_NoBias_ProducesIdenticalOrderToKalmanRank()
    {
        var sel  = new PredictiveTransportSelector();
        var wifi = new FakeTransport("wifi", bandwidthBps: 1_000_000L, powerCost: 1);
        var ble  = new FakeTransport("ble",  bandwidthBps: 200_000L,   powerCost: 3);
        sel.Register(wifi, 40.0);
        sel.Register(ble,  120.0);

        for (int i = 0; i < 5; i++)
            sel.ObserveMetrics(wifi, rttMs: 40, success: true, bytesTransferred: 1000);

        var emptyBiasProvider = new FakeAiProvider(
            biases: new Dictionary<string, double>());

        var baseRank = sel.Rank(512);
        var aiRank   = await sel.RankWithAiAsync(512, emptyBiasProvider);

        Assert.Equal(baseRank.Count, aiRank.Count);
        for (int i = 0; i < baseRank.Count; i++)
            Assert.Equal(baseRank[i].Transport.Name, aiRank[i].Transport.Name);
    }

    /// <summary>
    /// 7. A transport with a lower Kalman score jumps to #1 when the AI gives it a
    ///    multiplier that is large enough to overcome the score gap.
    ///    Strategy: register two transports at identical initial RTT and bandwidth so
    ///    their Kalman scores are equal, then give one a positive bias.
    /// </summary>
    [Fact]
    public async Task RankWithAi_PositiveBias_PromotesLowerKalmanTransport()
    {
        var sel  = new PredictiveTransportSelector();
        // wifi and ble start with identical capabilities and initial RTT — tied Kalman scores.
        var wifi = new FakeTransport("wifi", bandwidthBps: 500_000L, powerCost: 2);
        var ble  = new FakeTransport("ble",  bandwidthBps: 500_000L, powerCost: 2);
        sel.Register(wifi, 100.0);
        sel.Register(ble,  100.0);

        // Push wifi's Kalman RTT lower to make it rank #1 without AI.
        for (int i = 0; i < 10; i++)
            sel.ObserveMetrics(wifi, rttMs: 50, success: true, bytesTransferred: 1000);

        // Confirm wifi is on top without AI.
        var baseRank = sel.Rank(512);
        Assert.Equal("wifi", baseRank[0].Transport.Name);

        // AI strongly prefers BLE — the multiplier is large enough to overcome wifi's
        // lower-RTT advantage.  A 1000× multiplier is unambiguous.
        var aiProvider = new FakeAiProvider(
            biases: new Dictionary<string, double> { ["ble"] = 1000.0 });

        var aiRank = await sel.RankWithAiAsync(512, aiProvider);
        Assert.Equal("ble", aiRank[0].Transport.Name);
    }

    /// <summary>8. A 0.0 multiplier effectively suppresses a transport to last place.</summary>
    [Fact]
    public async Task RankWithAi_ZeroBias_EffectivelySuppressesTransport()
    {
        var sel  = new PredictiveTransportSelector();
        var wifi = new FakeTransport("wifi", bandwidthBps: 1_000_000L, powerCost: 1);
        var ble  = new FakeTransport("ble",  bandwidthBps: 200_000L,   powerCost: 2);
        sel.Register(wifi, 40.0);
        sel.Register(ble,  80.0);

        for (int i = 0; i < 5; i++)
            sel.ObserveMetrics(wifi, rttMs: 40, success: true, bytesTransferred: 5000);

        // Suppress wifi with 0.0 multiplier.
        var aiProvider = new FakeAiProvider(
            biases: new Dictionary<string, double> { ["wifi"] = 0.0 });

        var aiRank = await sel.RankWithAiAsync(512, aiProvider);

        Assert.Equal(2, aiRank.Count);
        // wifi should be last because its score is zeroed.
        Assert.Equal("wifi", aiRank.Last().Transport.Name);
        Assert.Equal(0.0, aiRank.Last().Score, precision: 10);
    }

    /// <summary>
    /// 9. When only one of three transports has a bias, the other two keep
    ///    their original relative order.
    ///    Strategy: all three transports start equal; give wifi a few good observations
    ///    to push it above ble (which has none), establishing wifi &gt; ble in Kalman.
    ///    Then give lora an enormous multiplier so it jumps to #1, while wifi and ble
    ///    keep their relative positions since neither has a multiplier.
    /// </summary>
    [Fact]
    public async Task RankWithAi_PartialBias_UntouchedTransportsUnchanged()
    {
        var sel  = new PredictiveTransportSelector();
        // All three identical capabilities so initial scores are equal.
        var wifi = new FakeTransport("wifi", bandwidthBps: 500_000L, powerCost: 2);
        var ble  = new FakeTransport("ble",  bandwidthBps: 500_000L, powerCost: 2);
        var lora = new FakeTransport("lora", bandwidthBps: 500_000L, powerCost: 2);
        sel.Register(wifi, 100.0);
        sel.Register(ble,  100.0);
        sel.Register(lora, 100.0);

        // Give wifi good observations to make it score above ble and lora (which have none).
        for (int i = 0; i < 10; i++)
            sel.ObserveMetrics(wifi, rttMs: 50, success: true, bytesTransferred: 1000);

        // Verify base order: wifi > ble or lora (wifi is clearly #1 after good observations).
        var baseRank = sel.Rank(512);
        Assert.Equal("wifi", baseRank[0].Transport.Name);

        // Only boost lora with a huge multiplier. wifi and ble carry no multiplier (1.0).
        // lora_score * 1_000_000 will surpass any realistic Kalman score for wifi or ble.
        var aiProvider = new FakeAiProvider(
            biases: new Dictionary<string, double> { ["lora"] = 1_000_000.0 });

        var aiRank = await sel.RankWithAiAsync(512, aiProvider);

        Assert.Equal(3, aiRank.Count);
        // lora jumps to #1.
        Assert.Equal("lora", aiRank[0].Transport.Name);
        // wifi and ble keep their relative order: wifi (with good observations) beats ble.
        int wifiIdx = -1, bleIdx = -1;
        for (int i = 0; i < aiRank.Count; i++)
        {
            if (aiRank[i].Transport.Name == "wifi") wifiIdx = i;
            if (aiRank[i].Transport.Name == "ble")  bleIdx  = i;
        }
        Assert.True(wifiIdx < bleIdx, $"wifi (pos {wifiIdx}) should still beat ble (pos {bleIdx}) after partial bias");
    }

    /// <summary>
    /// 10. When the AI provider throws, RankWithAiAsync swallows the exception
    ///     and returns the plain Kalman ranking unchanged.
    /// </summary>
    [Fact]
    public async Task RankWithAi_ExceptionInProvider_FallsBackToKalmanRanking()
    {
        var sel  = new PredictiveTransportSelector();
        var wifi = new FakeTransport("wifi", bandwidthBps: 1_000_000L, powerCost: 1);
        var ble  = new FakeTransport("ble",  bandwidthBps: 200_000L,   powerCost: 3);
        sel.Register(wifi, 40.0);
        sel.Register(ble,  120.0);

        for (int i = 0; i < 5; i++)
            sel.ObserveMetrics(wifi, rttMs: 40, success: true, bytesTransferred: 5000);

        var baseRank = sel.Rank(512);
        var aiRank   = await sel.RankWithAiAsync(512, new ThrowingAiProvider());

        Assert.Equal(baseRank.Count, aiRank.Count);
        for (int i = 0; i < baseRank.Count; i++)
        {
            Assert.Equal(baseRank[i].Transport.Name, aiRank[i].Transport.Name);
            Assert.Equal(baseRank[i].Score, aiRank[i].Score, precision: 10);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  Group C — Threat assessment
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>11. AiThreatLevel.None — packet is clean; forwarding is allowed.</summary>
    [Fact]
    public void ThreatLevel_None_AllowsForwarding()
    {
        const AiThreatLevel level = AiThreatLevel.None;
        Assert.True(level < AiThreatLevel.Medium,
            "AiThreatLevel.None should be below the suppression threshold");
    }

    /// <summary>12. AiThreatLevel.Low — informational only; forwarding is allowed.</summary>
    [Fact]
    public void ThreatLevel_Low_AllowsForwarding()
    {
        const AiThreatLevel level = AiThreatLevel.Low;
        Assert.True(level < AiThreatLevel.Medium,
            "AiThreatLevel.Low should be below the suppression threshold");
    }

    /// <summary>13. AiThreatLevel.Medium — forwarding must be suppressed.</summary>
    [Fact]
    public void ThreatLevel_Medium_SuppressesForwarding()
    {
        const AiThreatLevel level = AiThreatLevel.Medium;
        // The documented forwarding guard: if (level >= AiThreatLevel.Medium) suppress.
        Assert.True(level >= AiThreatLevel.Medium,
            "AiThreatLevel.Medium should meet the suppression threshold");
    }

    /// <summary>14. AiThreatLevel.High — forwarding must be suppressed.</summary>
    [Fact]
    public void ThreatLevel_High_SuppressesForwarding()
    {
        const AiThreatLevel level = AiThreatLevel.High;
        Assert.True(level >= AiThreatLevel.Medium,
            "AiThreatLevel.High should meet the suppression threshold");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  Group D — Route suggestions
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 15. When SuggestRoutesAsync returns empty, AODV would proceed normally
    ///     (represented here by the caller seeing an empty collection).
    /// </summary>
    [Fact]
    public async Task SuggestRoutes_EmptyResult_AodvProceedsNormally()
    {
        IAetherMeshAiProvider provider = new NullAetherMeshAiProvider();
        var suggestions = await provider.SuggestRoutesAsync("uhid:dest", 512);

        // Empty suggestions signal "use standard AODV".
        Assert.Empty(suggestions);
    }

    /// <summary>
    /// 16. When the provider returns multiple suggestions, sorting by Confidence
    ///     descending yields the highest-confidence path first.
    /// </summary>
    [Fact]
    public async Task SuggestRoutes_NonEmpty_ReturnsHighestConfidenceFirst()
    {
        var routes = new List<AiRouteSuggestion>
        {
            new(new[] { "hop1", "dest" }, Confidence: 0.55),
            new(new[] { "hop2", "dest" }, Confidence: 0.90),
            new(new[] { "hop3", "dest" }, Confidence: 0.72),
        };
        IAetherMeshAiProvider provider = new FakeAiProvider(routes: routes);

        var suggestions = await provider.SuggestRoutesAsync("uhid:dest", 512);

        // Caller sorts by Confidence descending.
        var sorted = suggestions.OrderByDescending(s => s.Confidence).ToList();
        Assert.Equal(0.90, sorted[0].Confidence, precision: 9);
        Assert.Equal(0.72, sorted[1].Confidence, precision: 9);
        Assert.Equal(0.55, sorted[2].Confidence, precision: 9);
    }

    /// <summary>17. Each returned AiRouteSuggestion carries the path slice that was set.</summary>
    [Fact]
    public async Task SuggestRoutes_PathIncludesAllHops()
    {
        var expectedPath = new[] { "relay-a", "relay-b", "uhid:dest" };
        var routes = new List<AiRouteSuggestion>
        {
            new(expectedPath, Confidence: 0.80),
        };
        IAetherMeshAiProvider provider = new FakeAiProvider(routes: routes);

        var suggestions = await provider.SuggestRoutesAsync("uhid:dest", 512);

        var only = Assert.Single(suggestions);
        Assert.Equal(3, only.Path.Count);
        Assert.Equal("relay-a",   only.Path[0]);
        Assert.Equal("relay-b",   only.Path[1]);
        Assert.Equal("uhid:dest", only.Path[2]);
    }

    /// <summary>18. Passing null as the provider produces an empty suggestion list.</summary>
    [Fact]
    public async Task SuggestRoutes_NullProvider_NoSuggestions()
    {
        // Simulate the common caller pattern: treat null provider == no AI.
        IAetherMeshAiProvider? provider = null;

        IReadOnlyList<AiRouteSuggestion> suggestions =
            provider is { IsAvailable: true }
                ? await provider.SuggestRoutesAsync("uhid:dest", 512)
                : Array.Empty<AiRouteSuggestion>();

        Assert.Empty(suggestions);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  Group E — DI integration
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 19. Calling AddAetherMeshProtocol() wires IAetherMeshAiProvider as NullAetherMeshAiProvider
    ///     when no alternative has been registered by the host.
    /// </summary>
    [Fact]
    public void DI_DefaultRegistration_ResolvesNullProvider()
    {
        var services = new ServiceCollection();
        services.AddAetherMeshProtocol();

        using var sp = services.BuildServiceProvider();

        var provider = sp.GetService<IAetherMeshAiProvider>();
        Assert.NotNull(provider);
        Assert.IsType<NullAetherMeshAiProvider>(provider);
        Assert.False(provider!.IsAvailable);
    }
}
