// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using AetherNet.Content;
using AetherNet.Content.Models;
using AetherNet.Identity;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Cards.Tests;

/// <summary>
/// The hardened directory: a signed binding is accepted only if the signature verifies and the version
/// strictly increases, and it is filed under a slot derived from the signing key — so two authors that
/// publish the same name get independent slots (no squatting, no lock-out).
/// </summary>
public class DirectoryHardeningTests
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static (byte[] Priv, byte[] Pub) NewKey() => Ed25519SigningService.GenerateKeyPair();

    private static string Scope(byte[] pub) => AetherNetTag.FromPublicKey(pub).Value;

    private static ContentDescriptor Blob(string content)
        => ContentDescriptor.FromBytes("card", Encoding.UTF8.GetBytes(content), "text/html");

    private static byte[] Sign(string name, ContentDescriptor descriptor, byte[] priv, byte[] pub, long version)
    {
        var body = NameBindingCodec.BuildSignableBody(NameHashing.Hash(name), pub, version, descriptor.RootHash);
        return Ed25519SigningService.Sign(priv, body);
    }

    // Produce the exact NamePublish wire packet a publisher would broadcast for a signed binding.
    private static async Task<MeshPacket> SignedPublishAsync(string name, ContentDescriptor descriptor, byte[] priv, byte[] pub, long version)
    {
        var sender = new CapturingMeshSender("publisher");
        var dir = new DirectoryService(sender, new Ed25519NameBindingVerifier());
        await dir.PublishSignedAsync(name, descriptor, pub, version, Sign(name, descriptor, priv, pub, version));
        var packet = sender.Broadcasts[^1];
        packet.SourceUhid = "publisher";
        return packet;
    }

    private static DirectoryService Receiver()
        => new(new CapturingMeshSender("receiver"), new Ed25519NameBindingVerifier());

    [Fact]
    public async Task ValidSignedBinding_IsAccepted()
    {
        var (priv, pub) = NewKey();
        var descriptor = Blob("<h1>hi</h1>");
        var packet = await SignedPublishAsync("menu", descriptor, priv, pub, 1);

        var recv = Receiver();
        await recv.HandleAsync(packet);

        var binding = await recv.ResolveBindingByScopeAsync(Scope(pub), "menu", TimeSpan.FromMilliseconds(50));
        Assert.NotNull(binding);
        Assert.True(binding!.Authenticated);
        Assert.Equal(1, binding.Version);
        Assert.Equal(descriptor.RootHash, binding.Descriptor.RootHash);
    }

    [Fact]
    public async Task SubstitutedContentUnderSameName_IsRejectedBySignature()
    {
        var (priv, pub) = NewKey();
        var packet = await SignedPublishAsync("menu", Blob("<h1>real</h1>"), priv, pub, 1);

        // Attacker swaps in different (self-consistent) content but keeps the author's signature.
        var payload = JsonSerializer.Deserialize<NamePublishPayload>(packet.Payload, JsonOpts)!;
        payload.Descriptor = Blob("<script>evil()</script>");
        packet.Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);

        var recv = Receiver();
        await recv.HandleAsync(packet);

        var binding = await recv.ResolveBindingByScopeAsync(Scope(pub), "menu", TimeSpan.FromMilliseconds(50));
        Assert.Null(binding); // signature no longer matches the swapped root hash → nothing stored
    }

    [Fact]
    public async Task ReplayedOldVersion_IsRejected()
    {
        var (priv, pub) = NewKey();
        var v1 = await SignedPublishAsync("menu", Blob("v1"), priv, pub, 1);
        var v2Descriptor = Blob("v2");
        var v2 = await SignedPublishAsync("menu", v2Descriptor, priv, pub, 2);

        var recv = Receiver();
        await recv.HandleAsync(v2); // hold v2
        await recv.HandleAsync(v1); // replay the older, validly-signed v1

        var binding = await recv.ResolveBindingByScopeAsync(Scope(pub), "menu", TimeSpan.FromMilliseconds(50));
        Assert.NotNull(binding);
        Assert.Equal(2, binding!.Version);
        Assert.Equal(v2Descriptor.RootHash, binding.Descriptor.RootHash);
    }

    [Fact]
    public async Task NewerVersionFromSameAuthor_IsAccepted()
    {
        var (priv, pub) = NewKey();
        var recv = Receiver();

        await recv.HandleAsync(await SignedPublishAsync("menu", Blob("v1"), priv, pub, 1));
        var v2Descriptor = Blob("v2");
        await recv.HandleAsync(await SignedPublishAsync("menu", v2Descriptor, priv, pub, 2));

        var binding = await recv.ResolveBindingByScopeAsync(Scope(pub), "menu", TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, binding!.Version);
        Assert.Equal(v2Descriptor.RootHash, binding.Descriptor.RootHash);
    }

    [Fact]
    public async Task DifferentAuthorsForSameName_GetIndependentSlots_NoLockout()
    {
        var (privA, pubA) = NewKey();
        var (privB, pubB) = NewKey();
        var recv = Receiver();

        var aDescriptor = Blob("A's menu");
        var bDescriptor = Blob("B's menu");

        // B (the would-be squatter) even publishes FIRST, then the real owner A publishes the same name.
        await recv.HandleAsync(await SignedPublishAsync("menu", bDescriptor, privB, pubB, 9));
        await recv.HandleAsync(await SignedPublishAsync("menu", aDescriptor, privA, pubA, 1));

        // Neither locks the other out — each resolves under its own owner scope, to its own content.
        var a = await recv.ResolveBindingByScopeAsync(Scope(pubA), "menu", TimeSpan.FromMilliseconds(50));
        var b = await recv.ResolveBindingByScopeAsync(Scope(pubB), "menu", TimeSpan.FromMilliseconds(50));

        Assert.NotNull(a);
        Assert.True(a!.AuthorPublicKey.AsSpan().SequenceEqual(pubA));
        Assert.Equal(aDescriptor.RootHash, a.Descriptor.RootHash);

        Assert.NotNull(b);
        Assert.True(b!.AuthorPublicKey.AsSpan().SequenceEqual(pubB));
        Assert.Equal(bDescriptor.RootHash, b.Descriptor.RootHash);
        Assert.NotEqual(a.Descriptor.RootHash, b.Descriptor.RootHash);
    }

    [Fact]
    public async Task UnsignedPublish_DoesNotAffectScopedBinding()
    {
        var (priv, pub) = NewKey();
        var real = Blob("real");
        var recv = Receiver();
        await recv.HandleAsync(await SignedPublishAsync("menu", real, priv, pub, 1));

        // Unsigned attacker publish for the same name lands in a different (unsigned) store, never the slot.
        var attackerSender = new CapturingMeshSender("attacker");
        var attackerDir = new DirectoryService(attackerSender);
        await attackerDir.PublishAsync("menu", Blob("spoof"));
        var unsigned = attackerSender.Broadcasts[^1];
        unsigned.SourceUhid = "attacker";
        await recv.HandleAsync(unsigned);

        var binding = await recv.ResolveBindingByScopeAsync(Scope(pub), "menu", TimeSpan.FromMilliseconds(50));
        Assert.True(binding!.Authenticated);
        Assert.Equal(1, binding.Version);
        Assert.Equal(real.RootHash, binding.Descriptor.RootHash);
    }

    [Fact]
    public async Task SignedBinding_WithoutVerifier_IsDropped()
    {
        var (priv, pub) = NewKey();
        var packet = await SignedPublishAsync("menu", Blob("x"), priv, pub, 1);

        // Receiver with NO verifier configured — cannot authenticate or scope, so must not cache.
        var recv = new DirectoryService(new CapturingMeshSender("receiver"));
        await recv.HandleAsync(packet);

        var binding = await recv.ResolveBindingByScopeAsync(Scope(pub), "menu", TimeSpan.FromMilliseconds(50));
        Assert.Null(binding);
    }
}
