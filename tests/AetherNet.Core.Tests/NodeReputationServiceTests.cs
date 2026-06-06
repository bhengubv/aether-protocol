// SPDX-License-Identifier: MIT

using AetherNet.Reputation;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for <see cref="InMemoryNodeReputationService"/>.
/// Covers: initial score, negative signal deltas, positive signal EWMA,
/// clamping to [0, 1], unknown-peer default, and snapshot correctness.
/// </summary>
public class NodeReputationServiceTests
{
    private const string Alice = "alice-uhid";
    private const string Bob   = "bob-uhid";

    private static InMemoryNodeReputationService NewService() => new();

    // ── Default score ────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownPeer_ReturnsOnePointZero()
    {
        var svc = NewService();
        var score = await svc.GetReputationScoreAsync("nobody");
        Assert.Equal(1.0, score);
    }

    // ── Negative signals ─────────────────────────────────────────────────────

    [Fact]
    public async Task RreqFlood_ReducesScore()
    {
        var svc = NewService();
        await svc.RecordRreqFloodAttemptAsync(Alice);
        var score = await svc.GetReputationScoreAsync(Alice);
        Assert.True(score < 1.0, $"Expected score < 1.0, got {score}");
        Assert.Equal(0.95, score, precision: 9);
    }

    [Fact]
    public async Task ReplayAttempt_ReducesScoreByFifteen()
    {
        var svc = NewService();
        await svc.RecordReplayAttemptAsync(Alice);
        var score = await svc.GetReputationScoreAsync(Alice);
        Assert.Equal(0.85, score, precision: 9);
    }

    [Fact]
    public async Task SignatureFailure_ReducesScoreByTwenty()
    {
        var svc = NewService();
        await svc.RecordSignatureFailureAsync(Alice);
        var score = await svc.GetReputationScoreAsync(Alice);
        Assert.Equal(0.80, score, precision: 9);
    }

    [Fact]
    public async Task CustodyRefusal_ReducesScoreByFive()
    {
        var svc = NewService();
        await svc.RecordCustodyRefusalAsync(Alice);
        var score = await svc.GetReputationScoreAsync(Alice);
        Assert.Equal(0.95, score, precision: 9);
    }

    [Fact]
    public async Task DeliveryFailure_ReducesScoreByTwo()
    {
        var svc = NewService();
        await svc.RecordDeliveryFailureAsync(Alice);
        var score = await svc.GetReputationScoreAsync(Alice);
        Assert.Equal(0.98, score, precision: 9);
    }

    // ── Clamping ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RepeatedSignatureFailures_ClampToZero()
    {
        var svc = NewService();
        // 5 × −0.20 = −1.0 → floor at 0.0 (epsilon-snapped by implementation)
        for (var i = 0; i < 5; i++)
            await svc.RecordSignatureFailureAsync(Alice);
        var score = await svc.GetReputationScoreAsync(Alice);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public async Task RepeatedDeliverySuccess_ClampToOne()
    {
        var svc = NewService();
        // 10 × positive = still capped at 1.0
        for (var i = 0; i < 10; i++)
            await svc.RecordDeliverySuccessAsync(Alice, roundTripMs: 50);
        var score = await svc.GetReputationScoreAsync(Alice);
        Assert.Equal(1.0, score);
    }

    // ── Multiple peers ───────────────────────────────────────────────────────

    [Fact]
    public async Task Signals_DoNotCrossContaminatePeers()
    {
        var svc = NewService();
        await svc.RecordSignatureFailureAsync(Alice);
        await svc.RecordSignatureFailureAsync(Alice);

        var alice = await svc.GetReputationScoreAsync(Alice);
        var bob   = await svc.GetReputationScoreAsync(Bob);

        Assert.True(alice < 1.0);
        Assert.Equal(1.0, bob); // Bob untouched
    }

    // ── GetAllScores ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllScores_ReturnsSnapshot()
    {
        var svc = NewService();
        await svc.RecordRreqFloodAttemptAsync(Alice);
        await svc.RecordReplayAttemptAsync(Bob);

        var all = await svc.GetAllScoresAsync();
        Assert.Equal(2, all.Count);
        Assert.True(all.ContainsKey(Alice));
        Assert.True(all.ContainsKey(Bob));
        Assert.True(all[Alice] < 1.0);
        Assert.True(all[Bob] < 1.0);
    }

    // ── Compound signals ─────────────────────────────────────────────────────

    [Fact]
    public async Task CompoundSignals_Accumulate()
    {
        var svc = NewService();
        await svc.RecordRreqFloodAttemptAsync(Alice);  // −0.05 → 0.95
        await svc.RecordReplayAttemptAsync(Alice);     // −0.15 → 0.80
        await svc.RecordSignatureFailureAsync(Alice);  // −0.20 → 0.60

        var score = await svc.GetReputationScoreAsync(Alice);
        Assert.Equal(0.60, score, precision: 9);
    }
}
