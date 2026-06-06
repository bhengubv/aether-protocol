// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Extensibility;
using AetherNet.Handshake;
using AetherNet.Protocol;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Coverage for the protocol-version + capability negotiation surface
/// (<see cref="HandshakeService"/>):
/// <list type="bullet">
///   <item>two services exchange Hello/HelloAck and both lock in the
///   intersection of capabilities,</item>
///   <item>negotiated version lands on the lower of the two max versions,</item>
///   <item>peers with no version overlap fire <c>IncompatiblePeer</c>,</item>
///   <item>no-reply backward-compat installs a v1 fallback,</item>
///   <item>capability-set intersection drops capabilities only one side
///   advertises.</item>
/// </list>
/// </summary>
public class HandshakeServiceTests
{
    private const string Local = "uhid:alice";
    private const string Remote = "uhid:bob";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static MeshPacket BuildHello(
        PacketType type,
        string source,
        string destination,
        byte minVersion,
        byte maxVersion,
        IEnumerable<string> capabilities,
        string implementation = "test/1")
    {
        var payload = new HelloPayload
        {
            MinVersion = minVersion,
            MaxVersion = maxVersion,
            Capabilities = capabilities.ToList(),
            Implementation = implementation,
        };
        return new MeshPacket
        {
            Type = type,
            SourceUhid = source,
            DestinationUhid = destination,
            Ttl = 1,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };
    }

    // ─── Hello → HelloAck round-trip ───────────────────────────────

    [Fact]
    public async Task TwoServices_ExchangeHelloAndHelloAck_BothLockInCapabilities()
    {
        var senderA = new FakeMeshSender(Local);
        var senderB = new FakeMeshSender(Remote);
        var serviceA = new HandshakeService(senderA);
        var serviceB = new HandshakeService(senderB);

        // A initiates a Hello to B.
        await serviceA.InitiateAsync(Remote);

        // The fake sender records what A sent — fish out the Hello packet
        // and feed it into B's HandleHelloAsync.
        var helloOnTheWire = senderA.Unicasts.Single(u => u.Packet.Type == PacketType.Hello).Packet;
        Assert.Equal(Local, helloOnTheWire.SourceUhid);
        Assert.Equal(Remote, helloOnTheWire.DestinationUhid);

        await serviceB.HandleHelloAsync(helloOnTheWire);

        // B's reply is a HelloAck — feed it back into A.
        var helloAckOnTheWire = senderB.Unicasts.Single(u => u.Packet.Type == PacketType.HelloAck).Packet;
        Assert.Equal(Remote, helloAckOnTheWire.SourceUhid);
        Assert.Equal(Local, helloAckOnTheWire.DestinationUhid);

        await serviceA.HandleHelloAckAsync(helloAckOnTheWire);

        // Both sides now have the peer recorded.
        var aSide = await serviceA.GetPeerCapabilitiesAsync(Remote);
        var bSide = await serviceB.GetPeerCapabilitiesAsync(Local);

        Assert.NotNull(aSide);
        Assert.NotNull(bSide);
        Assert.Equal(ProtocolConstants.CurrentProtocolVersion, aSide!.NegotiatedVersion);
        Assert.Equal(ProtocolConstants.CurrentProtocolVersion, bSide!.NegotiatedVersion);
        // Default capabilities match on both sides → full set intersects to itself.
        Assert.Equal(HandshakeService.DefaultCapabilities, aSide.Capabilities);
        Assert.Equal(HandshakeService.DefaultCapabilities, bSide.Capabilities);
    }

    // ─── Version selection ─────────────────────────────────────────

    [Fact]
    public async Task PeerWithHigherMaxVersion_NegotiatesOnOurMax()
    {
        var senderA = new FakeMeshSender(Local); // ours: 1..2
        var serviceA = new HandshakeService(senderA, ourMinVersion: 1, ourMaxVersion: 2);

        // Their range: 1..5 — overlap is 1..2; chosen is 2.
        var hello = BuildHello(PacketType.Hello, Remote, Local, 1, 5, HandshakeService.DefaultCapabilities);
        await serviceA.HandleHelloAsync(hello);

        var caps = await serviceA.GetPeerCapabilitiesAsync(Remote);
        Assert.NotNull(caps);
        Assert.Equal((byte)2, caps!.NegotiatedVersion);
    }

    [Fact]
    public async Task PeerWithLowerMaxVersion_NegotiatesOnTheirMax()
    {
        var senderA = new FakeMeshSender(Local); // ours: 1..2
        var serviceA = new HandshakeService(senderA, ourMinVersion: 1, ourMaxVersion: 2);

        // Their range: 1..1 — overlap is 1..1; chosen is 1.
        var hello = BuildHello(PacketType.Hello, Remote, Local, 1, 1, HandshakeService.DefaultCapabilities);
        await serviceA.HandleHelloAsync(hello);

        var caps = await serviceA.GetPeerCapabilitiesAsync(Remote);
        Assert.NotNull(caps);
        Assert.Equal((byte)1, caps!.NegotiatedVersion);
    }

    // ─── Incompatible peer ─────────────────────────────────────────

    [Fact]
    public async Task PeerWithNoOverlap_FiresIncompatiblePeer_AndIsNotRecorded()
    {
        var senderA = new FakeMeshSender(Local); // ours: 2..3
        var serviceA = new HandshakeService(senderA, ourMinVersion: 2, ourMaxVersion: 3);

        IncompatiblePeerEventArgs? captured = null;
        serviceA.IncompatiblePeer += (_, e) => captured = e;

        // Their range: 4..5 — completely above ours.
        var hello = BuildHello(PacketType.Hello, Remote, Local, 4, 5, HandshakeService.DefaultCapabilities);
        await serviceA.HandleHelloAsync(hello);

        Assert.NotNull(captured);
        Assert.Equal(Remote, captured!.PeerUhid);
        Assert.Equal((byte)4, captured.TheirMinVersion);
        Assert.Equal((byte)5, captured.TheirMaxVersion);
        Assert.Equal((byte)2, captured.OurMinVersion);
        Assert.Equal((byte)3, captured.OurMaxVersion);

        // No HelloAck should have been sent for the rejected peer.
        Assert.DoesNotContain(senderA.Unicasts, u => u.Packet.Type == PacketType.HelloAck);

        // No capabilities recorded.
        Assert.Null(await serviceA.GetPeerCapabilitiesAsync(Remote));
    }

    [Fact]
    public async Task PeerBelowOurMinVersion_FiresIncompatiblePeer()
    {
        var senderA = new FakeMeshSender(Local); // ours: 2..3
        var serviceA = new HandshakeService(senderA, ourMinVersion: 2, ourMaxVersion: 3);

        var fired = false;
        serviceA.IncompatiblePeer += (_, _) => fired = true;

        // Their range: 1..1 — completely below ours.
        var hello = BuildHello(PacketType.Hello, Remote, Local, 1, 1, HandshakeService.DefaultCapabilities);
        await serviceA.HandleHelloAsync(hello);

        Assert.True(fired);
        Assert.Null(await serviceA.GetPeerCapabilitiesAsync(Remote));
    }

    // ─── Backward-compat: peer never replies ───────────────────────

    [Fact]
    public async Task PeerNeverRepliesWithHelloAck_AssumeLegacyV1_LocksInV1Fallback()
    {
        var senderA = new FakeMeshSender(Local);
        var serviceA = new HandshakeService(senderA);

        await serviceA.InitiateAsync(Remote);

        // Simulate the timeout firing — host calls AssumeLegacyV1.
        serviceA.AssumeLegacyV1(Remote);

        var caps = await serviceA.GetPeerCapabilitiesAsync(Remote);
        Assert.NotNull(caps);
        Assert.Equal((byte)1, caps!.NegotiatedVersion);
        Assert.Empty(caps.Capabilities);
        Assert.Equal(string.Empty, caps.ImplementationVersion);
    }

    [Fact]
    public async Task AssumeLegacyV1_AfterRealHelloAck_DoesNotOverwrite()
    {
        var senderA = new FakeMeshSender(Local);
        var serviceA = new HandshakeService(senderA);

        var hello = BuildHello(PacketType.Hello, Remote, Local, 1, 2, HandshakeService.DefaultCapabilities);
        await serviceA.HandleHelloAsync(hello);

        var before = await serviceA.GetPeerCapabilitiesAsync(Remote);
        Assert.NotNull(before);

        // After: late timeout fires — the existing real record must win.
        serviceA.AssumeLegacyV1(Remote);

        var after = await serviceA.GetPeerCapabilitiesAsync(Remote);
        Assert.NotNull(after);
        Assert.Same(before, after);
        Assert.Equal((byte)2, after!.NegotiatedVersion);
        Assert.NotEmpty(after.Capabilities);
    }

    // ─── Capability intersection ──────────────────────────────────

    [Fact]
    public async Task CapabilityIntersection_DropsCapabilitiesOnlyOneSideClaims()
    {
        var senderA = new FakeMeshSender(Local);
        var ourCaps = new HashSet<string>(StringComparer.Ordinal)
        {
            "signal-x3dh", "dtn-custody", "sos",
        };
        var serviceA = new HandshakeService(senderA, ourCapabilities: ourCaps);

        // Peer claims [signal-x3dh, sos, voice]: intersection = [signal-x3dh, sos].
        var hello = BuildHello(
            PacketType.Hello, Remote, Local,
            minVersion: 1,
            maxVersion: ProtocolConstants.CurrentProtocolVersion,
            capabilities: new[] { "signal-x3dh", "sos", "voice" });
        await serviceA.HandleHelloAsync(hello);

        var caps = await serviceA.GetPeerCapabilitiesAsync(Remote);
        Assert.NotNull(caps);
        Assert.Equal(2, caps!.Capabilities.Count);
        Assert.Contains("signal-x3dh", caps.Capabilities);
        Assert.Contains("sos", caps.Capabilities);
        Assert.DoesNotContain("voice", caps.Capabilities);
        Assert.DoesNotContain("dtn-custody", caps.Capabilities);
    }

    // ─── Initiate semantics ────────────────────────────────────────

    [Fact]
    public async Task InitiateAsync_SendsExactlyOneHelloPerPeer()
    {
        var sender = new FakeMeshSender(Local);
        var service = new HandshakeService(sender);

        await service.InitiateAsync(Remote);
        await service.InitiateAsync(Remote);
        await service.InitiateAsync(Remote);

        Assert.Single(sender.Unicasts, u => u.Packet.Type == PacketType.Hello && u.NextHopUhid == Remote);
    }

    [Fact]
    public async Task InitiateAsync_SkipsLocalUhid()
    {
        var sender = new FakeMeshSender(Local);
        var service = new HandshakeService(sender);

        await service.InitiateAsync(Local);

        Assert.Empty(sender.Unicasts);
    }

    // ─── Renegotiate ───────────────────────────────────────────────

    [Fact]
    public async Task RenegotiateAsync_ClearsCachedCapabilities_AllowsNewHello()
    {
        var sender = new FakeMeshSender(Local);
        var service = new HandshakeService(sender);

        var hello = BuildHello(PacketType.Hello, Remote, Local, 1, 2, HandshakeService.DefaultCapabilities);
        await service.HandleHelloAsync(hello);
        Assert.NotNull(await service.GetPeerCapabilitiesAsync(Remote));

        await service.RenegotiateAsync(Remote);
        Assert.Null(await service.GetPeerCapabilitiesAsync(Remote));

        // After renegotiate, InitiateAsync must send a fresh Hello.
        sender.Clear();
        await service.InitiateAsync(Remote);
        Assert.Single(sender.Unicasts, u => u.Packet.Type == PacketType.Hello);
    }

    // ─── Malformed payload ─────────────────────────────────────────

    [Fact]
    public async Task MalformedPayload_IsIgnored_NoExceptionsThrown()
    {
        var sender = new FakeMeshSender(Local);
        var service = new HandshakeService(sender);

        var bad = new MeshPacket
        {
            Type = PacketType.Hello,
            SourceUhid = Remote,
            DestinationUhid = Local,
            Payload = new byte[] { 0x7B, 0xFF, 0xFE, 0x00 }, // not valid JSON after the `{`
        };

        // Must not throw.
        await service.HandleHelloAsync(bad);
        Assert.Null(await service.GetPeerCapabilitiesAsync(Remote));
    }

    // ─── Negotiation event ─────────────────────────────────────────

    [Fact]
    public async Task PeerNegotiated_EventFires_OnSuccessfulHello()
    {
        var sender = new FakeMeshSender(Local);
        var service = new HandshakeService(sender);

        PeerCapabilities? captured = null;
        service.PeerNegotiated += (_, caps) => captured = caps;

        var hello = BuildHello(PacketType.Hello, Remote, Local, 1, 2, HandshakeService.DefaultCapabilities);
        await service.HandleHelloAsync(hello);

        Assert.NotNull(captured);
        Assert.Equal(Remote, captured!.PeerUhid);
    }

    // ─── GetAllNegotiated ──────────────────────────────────────────

    [Fact]
    public async Task GetAllNegotiated_ReturnsEverySuccessfulPeer()
    {
        var sender = new FakeMeshSender(Local);
        var service = new HandshakeService(sender);

        var hello1 = BuildHello(PacketType.Hello, "uhid:bob", Local, 1, 2, HandshakeService.DefaultCapabilities);
        var hello2 = BuildHello(PacketType.Hello, "uhid:carol", Local, 1, 2, HandshakeService.DefaultCapabilities);
        await service.HandleHelloAsync(hello1);
        await service.HandleHelloAsync(hello2);

        var all = service.GetAllNegotiated();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, c => c.PeerUhid == "uhid:bob");
        Assert.Contains(all, c => c.PeerUhid == "uhid:carol");
    }

    // ─── Biometric co-presence verification ───────────────────────

    /// <summary>
    /// No biometric provider wired in → NullBiometricProvider.IsAvailable = false
    /// → always returns <see cref="BiometricVerificationResult.Failed"/>.
    /// </summary>
    [Fact]
    public async Task VerifyCoPresenceAsync_WithNullProvider_ReturnsFailed()
    {
        var sender  = new FakeMeshSender(Local);
        var service = new HandshakeService(sender); // biometricProvider defaults to Null

        var reference = MakeDummyEmbedding();
        var result = await service.VerifyCoPresenceAsync(
            new byte[112 * 112 * 3], 112, 112, reference);

        Assert.False(result.Verified);
        Assert.Equal(0.0, result.Similarity);
    }

    /// <summary>
    /// Provider is registered but unavailable (hardware absent / engine not loaded).
    /// </summary>
    [Fact]
    public async Task VerifyCoPresenceAsync_WithUnavailableProvider_ReturnsFailed()
    {
        var sender   = new FakeMeshSender(Local);
        var provider = new FakeBiometricProvider { IsAvailable = false };
        var service  = new HandshakeService(sender, biometricProvider: provider);

        var result = await service.VerifyCoPresenceAsync(
            new byte[112 * 112 * 3], 112, 112, MakeDummyEmbedding());

        Assert.False(result.Verified);
        Assert.Equal(0.0, result.Similarity);
    }

    /// <summary>
    /// Provider available but no face found in the live frame.
    /// </summary>
    [Fact]
    public async Task VerifyCoPresenceAsync_NoFaceDetected_ReturnsFailed()
    {
        var sender   = new FakeMeshSender(Local);
        var provider = new FakeBiometricProvider();
        provider.SetDetectionResult(null); // empty frame
        provider.SetVerifyResult(true, 0.95);
        var service  = new HandshakeService(sender, biometricProvider: provider);

        var result = await service.VerifyCoPresenceAsync(
            new byte[112 * 112 * 3], 112, 112, MakeDummyEmbedding());

        Assert.False(result.Verified);
    }

    /// <summary>
    /// A face is detected but its detection confidence is below 0.50 (FaceX default).
    /// The low-confidence detection must be rejected before VerifyAsync is even called.
    /// </summary>
    [Fact]
    public async Task VerifyCoPresenceAsync_LowConfidenceFace_ReturnsFailed()
    {
        var sender   = new FakeMeshSender(Local);
        var provider = new FakeBiometricProvider();
        provider.SetDetectionResult(new FaceDetectionResult(
            X1: 0f, Y1: 0f, X2: 50f, Y2: 50f,
            DetectionScore: 0.30f, // below IsConfident threshold of 0.50
            Embedding: MakeDummyEmbedding()));
        provider.SetVerifyResult(true, 0.95); // would pass if confidence weren't checked
        var service = new HandshakeService(sender, biometricProvider: provider);

        var result = await service.VerifyCoPresenceAsync(
            new byte[112 * 112 * 3], 112, 112, MakeDummyEmbedding());

        Assert.False(result.Verified);
    }

    /// <summary>
    /// High-confidence face detected, embeddings match → Verified = true.
    /// </summary>
    [Fact]
    public async Task VerifyCoPresenceAsync_MatchingFace_ReturnsVerified()
    {
        var sender   = new FakeMeshSender(Local);
        var provider = new FakeBiometricProvider();
        provider.SetDetectionResult(new FaceDetectionResult(
            X1: 10f, Y1: 10f, X2: 90f, Y2: 90f,
            DetectionScore: 0.97f,
            Embedding: MakeDummyEmbedding()));
        provider.SetVerifyResult(true, 0.82);
        var service = new HandshakeService(sender, biometricProvider: provider);

        var result = await service.VerifyCoPresenceAsync(
            new byte[112 * 112 * 3], 112, 112, MakeDummyEmbedding());

        Assert.True(result.Verified);
        Assert.Equal(0.82, result.Similarity, precision: 10);
    }

    /// <summary>
    /// High-confidence face detected but embeddings do not match the reference
    /// (different person). Verified = false; similarity value is surfaced.
    /// </summary>
    [Fact]
    public async Task VerifyCoPresenceAsync_NonMatchingFace_ReturnsNotVerified()
    {
        var sender   = new FakeMeshSender(Local);
        var provider = new FakeBiometricProvider();
        provider.SetDetectionResult(new FaceDetectionResult(
            X1: 10f, Y1: 10f, X2: 90f, Y2: 90f,
            DetectionScore: 0.91f,
            Embedding: MakeDummyEmbedding()));
        provider.SetVerifyResult(false, 0.10);
        var service = new HandshakeService(sender, biometricProvider: provider);

        var result = await service.VerifyCoPresenceAsync(
            new byte[112 * 112 * 3], 112, 112, MakeDummyEmbedding());

        Assert.False(result.Verified);
        Assert.Equal(0.10, result.Similarity, precision: 10);
    }

    // ── helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Produces a valid L2-normalised 512-dim embedding (all elements = 1/√512).
    /// </summary>
    private static FaceEmbedding MakeDummyEmbedding()
    {
        var v   = (float)(1.0 / Math.Sqrt(512));
        var vec = Enumerable.Repeat(v, 512).ToArray();
        return new FaceEmbedding(vec, DateTimeOffset.UtcNow);
    }
}
