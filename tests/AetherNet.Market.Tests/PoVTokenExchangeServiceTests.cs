// SPDX-License-Identifier: MIT
//
// Unit tests for the on-mesh Proof-of-Vicinity exchange (PacketType.PoVTokenExchange = 43): real
// Ed25519 witness signing + subject counter-signing across two distinct node identities, packet-level
// freshness + nonce replay-dedup, self-echo / not-for-us rejection, and tamper detection.
//
// Each node has its own SignalProtocolService identity (real Ed25519 key pair) and its own
// PacketSigningService. The witness (Alice) issues+sends; the subject (Bob) handles the packet using
// Alice's published public key — the AetherNet handler idiom (sender key passed in), mirroring
// ReputationGossipService.HandleGossipPacketAsync.

using AetherNet.Market;
using AetherNet.Market.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Market.Tests;

// ── Inline fakes ──────────────────────────────────────────────────────────────

/// <summary>Captures outbound packets and reports a fixed LocalUhid.</summary>
internal sealed class CapturingMeshSender : IMeshSender
{
    public CapturingMeshSender(string localUhid) => LocalUhid = localUhid;

    public string LocalUhid { get; }
    public List<(MeshPacket Packet, string NextHop)> Sent { get; } = new();

    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default)
    {
        Sent.Add((packet, nextHopUhid));
        return Task.FromResult(true);
    }

    public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default)
        => Task.FromResult(0);
}

/// <summary>A simulated node: identity key, packet-signing service, capturing sender, exchange service.</summary>
internal sealed record Node(
    SignalProtocolService Identity,
    PacketSigningService Signing,
    CapturingMeshSender Sender,
    PoVTokenExchangeService Exchange);

public sealed class PoVTokenExchangeServiceTests
{
    private const string AliceUhid = "alice:01";
    private const string BobUhid   = "bob:02";

    private static Node NewNode(string uhid)
    {
        var identity = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        identity.SetLocalUhid(uhid);
        var signing  = new PacketSigningService(identity, NullLogger<PacketSigningService>.Instance);
        var sender   = new CapturingMeshSender(uhid);
        var exchange = new PoVTokenExchangeService(sender, signing, identity,
            NullLogger<PoVTokenExchangeService>.Instance);
        return new Node(identity, signing, sender, exchange);
    }

    // ── Happy path: real sign → send → countersign → recorded ────────────────

    [Fact]
    public async Task IssueThenHandle_RealEd25519_RoundTrip_DrivesVerifiedToken()
    {
        var alice = NewNode(AliceUhid);
        var bob   = NewNode(BobUhid);

        // Alice (witness) issues + sends a token for Bob over the mesh.
        var issued = await alice.Exchange.IssueTokenAsync(BobUhid, PoVTransportType.Ble);

        Assert.NotNull(issued);
        Assert.Equal(AliceUhid, issued!.WitnessUhid);
        Assert.Equal(BobUhid, issued.SubjectUhid);
        Assert.Equal(64, issued.WitnessSignature.Length); // real Ed25519
        Assert.Empty(issued.SubjectSignature);            // subject signs on receipt
        Assert.Single(alice.Sender.Sent);

        // The packet Alice put on the wire.
        var (packet, nextHop) = alice.Sender.Sent[0];
        Assert.Equal(PacketType.PoVTokenExchange, packet.Type);
        Assert.Equal(BobUhid, nextHop);
        Assert.Equal(1, packet.Ttl); // co-present: one short-range hop

        // Bob handles it using Alice's published public key.
        var accepted = await bob.Exchange.HandleTokenExchangeAsync(packet, alice.Identity.GetPublicKey());

        Assert.True(accepted);

        // Bob's score now shows Alice as a unique witness.
        var bobScore = await bob.Exchange.GetScoreAsync(BobUhid);
        Assert.Equal(1, bobScore.UniqueWitnesses);
        Assert.True(bobScore.WeightedScore > 0.0);
    }

    [Fact]
    public async Task Handle_FiresTokenReceived_WithBothSignatures()
    {
        var alice = NewNode(AliceUhid);
        var bob   = NewNode(BobUhid);

        PoVToken? received = null;
        ((IPoVTokenExchangeService)bob.Exchange).TokenReceived += (_, t) => received = t;

        await alice.Exchange.IssueTokenAsync(BobUhid);
        var packet = alice.Sender.Sent[0].Packet;

        await bob.Exchange.HandleTokenExchangeAsync(packet, alice.Identity.GetPublicKey());

        Assert.NotNull(received);
        Assert.Equal(64, received!.WitnessSignature.Length);
        Assert.Equal(64, received.SubjectSignature.Length); // Bob counter-signed for real
    }

    // ── Tamper: wrong sender key ⇒ packet signature fails ────────────────────

    [Fact]
    public async Task Handle_WrongSenderKey_DropsPacket()
    {
        var alice   = NewNode(AliceUhid);
        var bob     = NewNode(BobUhid);
        var mallory = NewNode("mallory:66");

        await alice.Exchange.IssueTokenAsync(BobUhid);
        var packet = alice.Sender.Sent[0].Packet;

        // Verify against the WRONG public key (Mallory's, not Alice's) → packet signature fails.
        var accepted = await bob.Exchange.HandleTokenExchangeAsync(packet, mallory.Identity.GetPublicKey());

        Assert.False(accepted);
        var bobScore = await bob.Exchange.GetScoreAsync(BobUhid);
        Assert.Equal(0, bobScore.UniqueWitnesses);
    }

    // ── Tamper: forged witness signature in payload ⇒ dropped ────────────────

    [Fact]
    public async Task Handle_ForgedWitnessSignature_DropsPacket()
    {
        var alice = NewNode(AliceUhid);
        var bob   = NewNode(BobUhid);

        await alice.Exchange.IssueTokenAsync(BobUhid);
        var packet = alice.Sender.Sent[0].Packet;

        // Corrupt the witness signature inside the JSON payload, then RE-SIGN the envelope as Alice so the
        // packet signature still passes — isolating the token-body Ed25519 check.
        var json = System.Text.Encoding.UTF8.GetString(packet.Payload);
        var token = System.Text.Json.JsonSerializer.Deserialize<PoVToken>(json,
            new System.Text.Json.JsonSerializerOptions
            { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower })!;
        token.WitnessSignature = new byte[64]; // garbage 64-byte sig
        packet.Payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(token,
            new System.Text.Json.JsonSerializerOptions
            { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
        await alice.Signing.SignPacketAsync(packet); // re-sign envelope over the new payload

        var accepted = await bob.Exchange.HandleTokenExchangeAsync(packet, alice.Identity.GetPublicKey());

        Assert.False(accepted, "a forged witness signature must fail the token-body Ed25519 verification");
    }

    // ── Replay: same packet handled twice ⇒ nonce dedup drops the second ─────

    [Fact]
    public async Task Handle_ReplayedPacket_IsDeduped()
    {
        var alice = NewNode(AliceUhid);
        var bob   = NewNode(BobUhid);

        await alice.Exchange.IssueTokenAsync(BobUhid);
        var packet = alice.Sender.Sent[0].Packet;
        var aliceKey = alice.Identity.GetPublicKey();

        var first  = await bob.Exchange.HandleTokenExchangeAsync(packet, aliceKey);
        var second = await bob.Exchange.HandleTokenExchangeAsync(packet, aliceKey); // replay

        Assert.True(first);
        Assert.False(second, "the replayed packet must be dropped by nonce deduplication");

        // Still only one unique witness recorded.
        var bobScore = await bob.Exchange.GetScoreAsync(BobUhid);
        Assert.Equal(1, bobScore.UniqueWitnesses);
    }

    // ── Self-echo: our own token echoed back ⇒ ignored ───────────────────────

    [Fact]
    public async Task Handle_OwnTokenEchoedBack_IsIgnored()
    {
        var alice = NewNode(AliceUhid);

        // Alice issues for Bob; the packet has witness == Alice.
        await alice.Exchange.IssueTokenAsync(BobUhid);
        var packet = alice.Sender.Sent[0].Packet;

        // Alice handles her OWN packet (witness == local) → ignored.
        var accepted = await alice.Exchange.HandleTokenExchangeAsync(packet, alice.Identity.GetPublicKey());

        Assert.False(accepted);
    }

    // ── Not for us: token addressed to a different subject ⇒ ignored ─────────

    [Fact]
    public async Task Handle_TokenNotAddressedToUs_IsIgnored()
    {
        var alice   = NewNode(AliceUhid);
        var carol   = NewNode("carol:03");
        var bob     = NewNode(BobUhid);

        // Alice issues for Carol, not Bob.
        await alice.Exchange.IssueTokenAsync("carol:03");
        var packet = alice.Sender.Sent[0].Packet;

        // Bob receives it but is not the subject → ignored.
        var accepted = await bob.Exchange.HandleTokenExchangeAsync(packet, alice.Identity.GetPublicKey());

        Assert.False(accepted);
    }

    // ── Issue guards ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Issue_SelfVouch_IsRefused()
    {
        var alice = NewNode(AliceUhid);

        var issued = await alice.Exchange.IssueTokenAsync(AliceUhid); // witness == subject

        Assert.Null(issued);
        Assert.Empty(alice.Sender.Sent);
    }

    [Fact]
    public async Task Issue_NonShortRangeTransport_IsRefused()
    {
        var alice = NewNode(AliceUhid);

        // Only Ble/Nfc/NearLink are short-range. Cast an out-of-range value to force the refusal path.
        var issued = await alice.Exchange.IssueTokenAsync(BobUhid, (PoVTransportType)99);

        Assert.Null(issued);
        Assert.Empty(alice.Sender.Sent);
    }

    [Fact]
    public async Task Issue_EmptySubject_IsRefused()
    {
        var alice = NewNode(AliceUhid);

        var issued = await alice.Exchange.IssueTokenAsync(string.Empty);

        Assert.Null(issued);
        Assert.Empty(alice.Sender.Sent);
    }

    // ── Two distinct witnesses → score = 2 ───────────────────────────────────

    [Fact]
    public async Task TwoWitnesses_OverMesh_BobScoreIsTwo()
    {
        var alice = NewNode(AliceUhid);
        var carol = NewNode("carol:03");
        var bob   = NewNode(BobUhid);

        await alice.Exchange.IssueTokenAsync(BobUhid);
        await bob.Exchange.HandleTokenExchangeAsync(alice.Sender.Sent[0].Packet, alice.Identity.GetPublicKey());

        await carol.Exchange.IssueTokenAsync(BobUhid);
        await bob.Exchange.HandleTokenExchangeAsync(carol.Sender.Sent[0].Packet, carol.Identity.GetPublicKey());

        var bobScore = await bob.Exchange.GetScoreAsync(BobUhid);
        Assert.Equal(2, bobScore.UniqueWitnesses);
    }
}
