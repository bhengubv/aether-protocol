// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using AetherMesh.Protocol;
using AetherMesh.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherMesh.Core.Tests;

/// <summary>
/// PacketSigningService tests — focused on the parts of the contract the
/// audit flagged: nonce dedup keyed by (source, nonce), freshness window,
/// and tamper detection via canonical signable-data layout.
/// </summary>
public class PacketSigningServiceTests
{
    private const string AliceUhid = "alice-uhid";
    private const string BobUhid = "bob-uhid";
    private const string MalloryUhid = "mallory-uhid";

    private static (PacketSigningService Signer, SignalProtocolService Signal) NewService()
    {
        var signal = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var signer = new PacketSigningService(signal, NullLogger<PacketSigningService>.Instance);
        return (signer, signal);
    }

    private static MeshPacket NewPacket(string source = AliceUhid, string dest = BobUhid)
    {
        return new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = source,
            DestinationUhid = dest,
            Payload = Encoding.UTF8.GetBytes("hello"),
            Ttl = 7,
            Priority = 1,
        };
    }

    [Fact]
    public async Task SignAndVerify_HappyPath_ReturnsTrue()
    {
        var (signer, signal) = NewService();
        var publicKey = signal.GetPublicKey();
        var packet = NewPacket();

        await signer.SignPacketAsync(packet);
        var ok = await signer.VerifyPacketAsync(packet, publicKey);

        Assert.True(ok);
    }

    [Fact]
    public async Task Verify_TamperedPacket_ReturnsFalse()
    {
        var (signer, signal) = NewService();
        var publicKey = signal.GetPublicKey();
        var packet = NewPacket();
        await signer.SignPacketAsync(packet);

        // Flip a payload byte after signing — should fail signature verification.
        packet.Payload[0] ^= 0xFF;
        var ok = await signer.VerifyPacketAsync(packet, publicKey);

        Assert.False(ok);
    }

    [Fact]
    public async Task Verify_OldTimestamp_RejectedAsStale()
    {
        var (signer, signal) = NewService();
        var publicKey = signal.GetPublicKey();
        var packet = NewPacket();
        await signer.SignPacketAsync(packet);

        // Bump TimestampMs back 6 minutes — past the 5-minute freshness window.
        packet.TimestampMs -= 6 * 60 * 1000;
        var ok = await signer.VerifyPacketAsync(packet, publicKey);

        Assert.False(ok);
    }

    [Fact]
    public async Task Verify_DuplicateNonceFromSameSender_RejectedAsReplay()
    {
        var (signer, signal) = NewService();
        var publicKey = signal.GetPublicKey();
        var first = NewPacket();
        await signer.SignPacketAsync(first);

        Assert.True(await signer.VerifyPacketAsync(first, publicKey));
        // Second verification of the same packet (same source, same nonce)
        // must fail — that's the point of the dedup cache.
        Assert.False(await signer.VerifyPacketAsync(first, publicKey));
    }

    [Fact]
    public async Task Verify_SameNonceDifferentSenders_BothAccepted()
    {
        // Audit fix: dedup keyed by (source, nonce). A nonce collision across
        // *different* senders must NOT drop legitimate traffic. (Pre-2026-05-05
        // the cache was keyed by nonce alone, which would silently drop the
        // second sender's packet.)
        var (signer, signal) = NewService();
        var publicKey = signal.GetPublicKey();

        var fromAlice = NewPacket(source: AliceUhid);
        await signer.SignPacketAsync(fromAlice);

        // Construct a separate packet from Mallory with the SAME nonce. Sign
        // it independently — same Ed25519 key (single test signer) but the
        // signed_data includes SourceUhid so the signatures differ.
        var fromMallory = NewPacket(source: MalloryUhid);
        fromMallory.PacketNonce = (byte[])fromAlice.PacketNonce.Clone();
        fromMallory.TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        fromMallory.ProtocolVersion = 2;
        fromMallory.Signature = await signal.SignDataAsync(
            PacketSigningService.BuildSignableData(fromMallory));

        Assert.True(await signer.VerifyPacketAsync(fromAlice, publicKey));
        Assert.True(await signer.VerifyPacketAsync(fromMallory, publicKey));
    }

    [Fact]
    public async Task Verify_AttackerPrePoisonsNonce_LegitimateSenderUnaffected()
    {
        // Audit scenario: attacker (Mallory) registers a nonce against a
        // recipient before the legitimate sender (Alice) can. With the old
        // nonce-only key, Alice's first packet would be silently dropped as
        // "duplicate". With the (source, nonce) key, Alice is unaffected.
        var (signer, signal) = NewService();
        var publicKey = signal.GetPublicKey();

        var attackerNonce = RandomNumberGenerator.GetBytes(8);

        // Mallory crafts a packet with the chosen nonce and "registers" it.
        var malloryPkt = NewPacket(source: MalloryUhid);
        malloryPkt.PacketNonce = (byte[])attackerNonce.Clone();
        malloryPkt.TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        malloryPkt.ProtocolVersion = 2;
        malloryPkt.Signature = await signal.SignDataAsync(
            PacketSigningService.BuildSignableData(malloryPkt));
        await signer.VerifyPacketAsync(malloryPkt, publicKey); // Mallory's verify caches her key.

        // Alice now legitimately uses the same nonce by chance. Her packet
        // must still verify — the cache is per (source, nonce), not nonce.
        var alicePkt = NewPacket(source: AliceUhid);
        alicePkt.PacketNonce = (byte[])attackerNonce.Clone();
        alicePkt.TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        alicePkt.ProtocolVersion = 2;
        alicePkt.Signature = await signal.SignDataAsync(
            PacketSigningService.BuildSignableData(alicePkt));

        Assert.True(await signer.VerifyPacketAsync(alicePkt, publicKey));
    }

    [Fact]
    public void BuildSignableData_DependsOnSourceUhid()
    {
        // Sanity check that the signable layout binds SourceUhid — without
        // this, the (source, nonce) dedup wouldn't add real security: the
        // attacker could just claim Alice's UHID for their pre-poison.
        var pktA = NewPacket(source: AliceUhid);
        var pktB = NewPacket(source: BobUhid);
        pktA.PacketNonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        pktB.PacketNonce = (byte[])pktA.PacketNonce.Clone();
        pktA.TimestampMs = pktB.TimestampMs = 1735689600000;

        var dataA = PacketSigningService.BuildSignableData(pktA);
        var dataB = PacketSigningService.BuildSignableData(pktB);

        Assert.NotEqual(dataA, dataB);
    }
}
