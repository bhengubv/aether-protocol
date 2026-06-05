// SPDX-License-Identifier: MIT
//
// Integration test: biometric co-presence (HandshakeService.VerifyCoPresenceAsync)
// feeds directly into the aether-market PoV trust graph (InMemoryPoVService).
//
// Tests the canonical PoV wire flow:
//   1. Alice captures a camera frame and detects Bob's face in it.
//   2. Alice calls HandshakeService.VerifyCoPresenceAsync against Bob's reference
//      embedding — the biometric match confirms physical co-presence.
//   3. On success Alice calls IPoVService.IssueTokenAsync("alice", "bob").
//   4. Bob calls IPoVService.AcceptTokenAsync(token).
//   5. Bob's PoVScore.UniqueWitnesses increments to 1.
//
// This test is the only place in the repository that verifies the two-component
// integration end-to-end.  Individual unit tests for each component live in
// Aether.Core.Tests and Aether.Market.Tests respectively.

using AetherMesh.Extensibility;
using AetherMesh.Handshake;
using AetherMesh.Market;
using AetherMesh.Market.Models;
using AetherMesh.Protocol;
using AetherMesh.Routing;
using Xunit;

namespace AetherMesh.Market.Tests;

// ── Inline fakes ──────────────────────────────────────────────────────────────

/// <summary>Discards all outbound mesh packets — sufficient for PoV integration.</summary>
file sealed class NullMeshSender : IMeshSender
{
    public string LocalUhid => "test-node";
    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default)
        => Task.FromResult(true);
    public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default)
        => Task.FromResult(0);
}

/// <summary>
/// Configurable biometric fake:
/// • When <see cref="DetectionResult"/> is set, <see cref="DetectAsync"/> returns it.
/// • When <see cref="VerifyResult"/> is set, <see cref="VerifyAsync"/> returns it.
/// </summary>
file sealed class StubBiometricProvider : IBiometricProvider
{
    public bool IsAvailable { get; set; } = true;

    /// <summary>Set to control what DetectAsync returns (null → empty list).</summary>
    public FaceDetectionResult? DetectionResult { get; set; }

    /// <summary>Set to control what VerifyAsync returns.</summary>
    public BiometricVerificationResult VerifyResult { get; set; }
        = BiometricVerificationResult.Failed;

    public Task<IReadOnlyList<FaceDetectionResult>> DetectAsync(
        byte[] rgbHwc, int width, int height, int maxFaces = 1,
        CancellationToken ct = default)
    {
        IReadOnlyList<FaceDetectionResult> result = DetectionResult is null
            ? Array.Empty<FaceDetectionResult>()
            : [DetectionResult];
        return Task.FromResult(result);
    }

    public Task<BiometricVerificationResult> VerifyAsync(
        FaceEmbedding a, FaceEmbedding b,
        double threshold = 0.30, CancellationToken cancellationToken = default)
        => Task.FromResult(VerifyResult);
}

// ── Integration tests ─────────────────────────────────────────────────────────

public sealed class PoVIntegrationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Build a unit-length L2-normalised 512-dim embedding (all equal weights).</summary>
    private static FaceEmbedding MakeEmbedding()
    {
        var values = new float[512];
        float element = 1.0f / MathF.Sqrt(512f);
        for (int i = 0; i < 512; i++)
            values[i] = element;
        return new FaceEmbedding(values, DateTimeOffset.UtcNow);
    }

    private static HandshakeService BuildHandshakeService(IBiometricProvider biometrics)
        => new HandshakeService(new NullMeshSender(), biometricProvider: biometrics);

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full happy-path: biometric match → PoV token issued + accepted →
    /// Bob's UniqueWitnesses increments to 1.
    /// </summary>
    [Fact]
    public async Task VerifyCoPresence_Success_PoVTokenIncrementsScore()
    {
        // Arrange
        var referenceEmbedding = MakeEmbedding();
        var detectedFace = new FaceDetectionResult(
            X1: 0f, Y1: 0f, X2: 112f, Y2: 112f,
            DetectionScore: 0.95f, Embedding: referenceEmbedding);

        var biometrics = new StubBiometricProvider
        {
            DetectionResult = detectedFace,
            VerifyResult    = new BiometricVerificationResult(
                                  Verified: true, Similarity: 0.88, LivenessConfirmed: true),
        };

        var handshake = BuildHandshakeService(biometrics);
        var povSvc = new InMemoryPoVService();

        const string AliceUhid = "alice:01";
        const string BobUhid   = "bob:02";

        var dummyFrame = new byte[112 * 112 * 3]; // 112×112 RGB

        // Act — Step 1: biometric co-presence check
        var bioResult = await handshake.VerifyCoPresenceAsync(
            dummyFrame, width: 112, height: 112, referenceEmbedding);

        // Assert — biometric check passed
        Assert.True(bioResult.Verified,
            "VerifyCoPresenceAsync should return Verified=true with matching stub");

        // Act — Step 2: issue PoV token (conditional on biometric success)
        PoVToken token = bioResult.Verified
            ? await povSvc.IssueTokenAsync(AliceUhid, BobUhid, PoVTransportType.Ble)
            : throw new InvalidOperationException("Biometric check failed");

        // Assert — token carries correct UHIDs
        Assert.Equal(AliceUhid, token.WitnessUhid);
        Assert.Equal(BobUhid,   token.SubjectUhid);
        Assert.Equal(PoVTransportType.Ble, token.TransportUsed);

        // Act — Step 3: Bob accepts the token
        await povSvc.AcceptTokenAsync(token);

        // Assert — Bob's PoV score now shows 1 unique witness
        var bobScore = await povSvc.GetScoreAsync(BobUhid);
        Assert.Equal(1, bobScore.UniqueWitnesses);
        Assert.True(bobScore.WeightedScore > 0.0,
            $"Bob's WeightedScore should be positive; got {bobScore.WeightedScore}");
    }

    /// <summary>
    /// When biometric verification fails, the PoV flow must not proceed.
    /// Alice's code checks the result and skips IssueTokenAsync.
    /// Bob's score stays at zero.
    /// </summary>
    [Fact]
    public async Task VerifyCoPresence_Failure_PoVScoreRemainsZero()
    {
        // Arrange — face detected but similarity too low
        var refEmbedding = MakeEmbedding();
        var detectedFace = new FaceDetectionResult(
            X1: 0f, Y1: 0f, X2: 112f, Y2: 112f,
            DetectionScore: 0.91f, Embedding: refEmbedding);

        var biometrics = new StubBiometricProvider
        {
            DetectionResult = detectedFace,
            VerifyResult    = new BiometricVerificationResult(
                                  Verified: false, Similarity: 0.12), // below threshold
        };

        var handshake = BuildHandshakeService(biometrics);
        var povSvc = new InMemoryPoVService();

        const string AliceUhid = "alice:01";
        const string BobUhid   = "bob:02";
        var dummyFrame = new byte[112 * 112 * 3];

        // Act
        var bioResult = await handshake.VerifyCoPresenceAsync(
            dummyFrame, width: 112, height: 112, refEmbedding);

        // Alice's code would check bioResult.Verified before issuing a token.
        if (!bioResult.Verified)
        {
            // No token issued — Bob's score stays zero.
        }
        else
        {
            var token = await povSvc.IssueTokenAsync(AliceUhid, BobUhid);
            await povSvc.AcceptTokenAsync(token);
        }

        // Assert — biometric mismatch
        Assert.False(bioResult.Verified,
            "Stub with low similarity should return Verified=false");

        // Assert — Bob's score is zero (no token was accepted)
        var bobScore = await povSvc.GetScoreAsync(BobUhid);
        Assert.Equal(0, bobScore.UniqueWitnesses);
        Assert.Equal(0.0, bobScore.WeightedScore);
    }

    /// <summary>
    /// When no biometric provider is registered (NullBiometricProvider),
    /// VerifyCoPresenceAsync returns Failed and the PoV flow is never triggered.
    /// </summary>
    [Fact]
    public async Task VerifyCoPresence_NoBiometricProvider_ReturnsFailed_PoVUnchanged()
    {
        // Arrange — use default constructor (no biometric provider → Null)
        var handshake = new HandshakeService(new NullMeshSender());
        var povSvc = new InMemoryPoVService();

        const string SubjectUhid = "bob:02";

        var bioResult = await handshake.VerifyCoPresenceAsync(
            new byte[100], width: 10, height: 10, MakeEmbedding());

        // NullBiometricProvider.IsAvailable == false → immediate Failed
        Assert.False(bioResult.Verified);

        var score = await povSvc.GetScoreAsync(SubjectUhid);
        Assert.Equal(0, score.UniqueWitnesses);
    }

    /// <summary>
    /// Multiple distinct witnesses each contribute once.
    /// After Alice and Carol both verify Bob, Bob has 2 unique witnesses.
    /// </summary>
    [Fact]
    public async Task TwoWitnesses_BobScore_UniqueWitnessesIsTwo()
    {
        var refEmbedding = MakeEmbedding();
        var detectedFace = new FaceDetectionResult(
            X1: 0f, Y1: 0f, X2: 112f, Y2: 112f,
            DetectionScore: 0.95f, Embedding: refEmbedding);

        StubBiometricProvider MakeBiometrics() => new()
        {
            DetectionResult = detectedFace,
            VerifyResult    = new BiometricVerificationResult(true, 0.92),
        };

        var povSvc   = new InMemoryPoVService();
        var dummyFrame = new byte[112 * 112 * 3];

        foreach (var witnessUhid in new[] { "alice:01", "carol:03" })
        {
            var handshake  = BuildHandshakeService(MakeBiometrics());
            var bioResult  = await handshake.VerifyCoPresenceAsync(
                dummyFrame, 112, 112, refEmbedding);

            Assert.True(bioResult.Verified, $"{witnessUhid}: biometric should pass");

            var token = await povSvc.IssueTokenAsync(witnessUhid, "bob:02");
            await povSvc.AcceptTokenAsync(token);
        }

        var bobScore = await povSvc.GetScoreAsync("bob:02");
        Assert.Equal(2, bobScore.UniqueWitnesses);
    }

    /// <summary>
    /// Defection penalty after PoV fraud: Alice vouches for a defector,
    /// defection reported → Alice's score drops by 20%.
    /// </summary>
    [Fact]
    public async Task DefectionPenalty_ReducesWitnessScore_AfterPoVFraud()
    {
        var refEmbedding = MakeEmbedding();
        var detectedFace = new FaceDetectionResult(
            X1: 0f, Y1: 0f, X2: 112f, Y2: 112f,
            DetectionScore: 0.95f, Embedding: refEmbedding);

        var biometrics = new StubBiometricProvider
        {
            DetectionResult = detectedFace,
            VerifyResult    = new BiometricVerificationResult(true, 0.91),
        };

        var povSvc    = new InMemoryPoVService();
        var handshake = BuildHandshakeService(biometrics);
        var dummyFrame = new byte[112 * 112 * 3];

        const string AliceUhid   = "alice:01";
        const string BobUhid     = "bob:02";
        const string DefectorUhid = "defector:99";

        // Give Alice a base score: some third party vouches for Alice first.
        var boostToken = await povSvc.IssueTokenAsync("booster:00", AliceUhid);
        await povSvc.AcceptTokenAsync(boostToken);

        var aliceScoreBefore = await povSvc.GetScoreAsync(AliceUhid);

        // Alice biometrically verifies and vouches for defector.
        var bioResult = await handshake.VerifyCoPresenceAsync(dummyFrame, 112, 112, refEmbedding);
        Assert.True(bioResult.Verified);

        var defectorToken = await povSvc.IssueTokenAsync(AliceUhid, DefectorUhid);
        await povSvc.AcceptTokenAsync(defectorToken);

        // Fraud detected — defector reported, penalty applied to Alice.
        await povSvc.ReportDefectionAsync(AliceUhid, DefectorUhid);

        var aliceScoreAfter = await povSvc.GetScoreAsync(AliceUhid);

        // Alice's score should be 80% of what it was (20% penalty).
        Assert.Equal(aliceScoreBefore.WeightedScore * 0.8, aliceScoreAfter.WeightedScore,
                     precision: 10);

        // Bob (uninvolved) should be unaffected.
        var bobScore = await povSvc.GetScoreAsync(BobUhid);
        Assert.Equal(0, bobScore.UniqueWitnesses);
    }
}
