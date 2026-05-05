// SPDX-License-Identifier: MIT

using System.Text;
using Aether.Security.Models;
using Aether.Security.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aether.Core.Tests;

/// <summary>
/// End-to-end exercises of the X3DH + Double-Ratchet flow on the C# reference
/// implementation. These cover the asymmetric initiator/responder split: the
/// initiator processes a pre-key bundle, the responder establishes its session
/// from the first PreKey message it receives.
/// </summary>
public class SignalProtocolServiceTests
{
    private const string AliceUhid = "alice-uhid";
    private const string BobUhid = "bob-uhid";

    private static SignalProtocolService NewService() =>
        new(NullLogger<SignalProtocolService>.Instance);

    [Fact]
    public async Task X3DH_AliceInitiator_BobResponder_FirstMessage_RoundTrips()
    {
        // Arrange: Bob publishes a bundle, Alice processes it.
        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid); // sets Alice's local UHID
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        // Act: Alice's first send is a PreKey message; Bob auto-establishes
        // his responder-side session on receipt.
        var plaintext = Encoding.UTF8.GetBytes("the mesh is alive");
        var encrypted = await alice.EncryptAsync(BobUhid, plaintext);

        Assert.Equal(1, encrypted.MessageType); // PreKey flag set
        Assert.NotNull(encrypted.InitiatorIdentityKeyX25519);
        Assert.NotNull(encrypted.InitiatorEphemeralKeyX25519);
        Assert.Equal(32, encrypted.InitiatorIdentityKeyX25519!.Length);
        Assert.Equal(32, encrypted.InitiatorEphemeralKeyX25519!.Length);
        Assert.Equal(bobBundle.SignedPreKeyId, encrypted.UsedSignedPreKeyId);
        Assert.Equal(bobBundle.PreKeyId, encrypted.UsedOneTimePreKeyId);

        var decrypted = await bob.DecryptAsync(AliceUhid, encrypted);
        Assert.Equal(plaintext, decrypted);
        Assert.True(bob.HasSession(AliceUhid));
    }

    [Fact]
    public async Task X3DH_SubsequentMessages_AreNormalNotPreKey()
    {
        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        // First message: PreKey (consumed by responder).
        var first = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("hello"));
        await bob.DecryptAsync(AliceUhid, first);

        // Second message: should be a normal session message (no PreKey flag).
        var second = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("world"));
        Assert.Equal(0, second.MessageType);
        Assert.Null(second.InitiatorIdentityKeyX25519);
        Assert.Null(second.InitiatorEphemeralKeyX25519);
        Assert.Equal(0, second.UsedSignedPreKeyId);
        Assert.Equal(0, second.UsedOneTimePreKeyId);

        var plaintext = await bob.DecryptAsync(AliceUhid, second);
        Assert.Equal("world", Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public async Task X3DH_BidirectionalAfterFirstMessage()
    {
        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        // Alice -> Bob (PreKey).
        var aToB = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("ping"));
        var ping = await bob.DecryptAsync(AliceUhid, aToB);
        Assert.Equal("ping", Encoding.UTF8.GetString(ping));

        // Bob -> Alice (normal: Bob already has a responder session).
        var bToA = await bob.EncryptAsync(AliceUhid, Encoding.UTF8.GetBytes("pong"));
        Assert.Equal(0, bToA.MessageType);
        var pong = await alice.DecryptAsync(BobUhid, bToA);
        Assert.Equal("pong", Encoding.UTF8.GetString(pong));
    }

    [Fact]
    public async Task RatchetForwardSecrecy_FiveSequentialMessages()
    {
        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        for (var i = 0; i < 5; i++)
        {
            var msg = $"chain message {i}";
            var enc = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes(msg));
            Assert.Equal(i, enc.Counter);

            var dec = await bob.DecryptAsync(AliceUhid, enc);
            Assert.Equal(msg, Encoding.UTF8.GetString(dec));
        }
    }

    [Fact]
    public async Task OutOfOrderDelivery_SkippedKeysCached()
    {
        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        // Encrypt four in order (first one carries the PreKey flag).
        var enc0 = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("zero"));
        var enc1 = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("one"));
        var enc2 = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("two"));
        var enc3 = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("three"));

        // The PreKey message (enc0) MUST arrive first so Bob can establish
        // his responder session. Subsequent normal messages can then arrive
        // in any order.
        var dec0 = await bob.DecryptAsync(AliceUhid, enc0);
        Assert.Equal("zero", Encoding.UTF8.GetString(dec0));

        // Deliver remaining out of order: 3, 1, 2.
        var dec3 = await bob.DecryptAsync(AliceUhid, enc3);
        Assert.Equal("three", Encoding.UTF8.GetString(dec3));

        var dec1 = await bob.DecryptAsync(AliceUhid, enc1);
        Assert.Equal("one", Encoding.UTF8.GetString(dec1));

        var dec2 = await bob.DecryptAsync(AliceUhid, enc2);
        Assert.Equal("two", Encoding.UTF8.GetString(dec2));
    }

    [Fact]
    public async Task OneTimePreKey_ConsumedAfterResponderEstablishes()
    {
        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        // Alice sends, Bob auto-establishes and consumes the OPK.
        var first = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("first"));
        await bob.DecryptAsync(AliceUhid, first);

        // A second initiator using the same bundle (and therefore the same
        // OPK id) should fail on Bob's side because the OPK was consumed.
        var alice2 = NewService();
        await alice2.GeneratePreKeyBundleAsync("alice2-uhid");
        await alice2.ProcessPreKeyBundleAsync(bobBundle);
        var replay = await alice2.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("replay"));

        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(
            () => bob.DecryptAsync("alice2-uhid", replay));
    }

    [Fact]
    public async Task Encrypt_SetsLocalUhid_AsSender()
    {
        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var encrypted = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("x"));
        Assert.Equal(AliceUhid, encrypted.SenderUhid);
    }

    [Fact]
    public async Task Encrypt_WithoutLocalUhid_Throws()
    {
        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        // Note: no GeneratePreKeyBundleAsync / SetLocalUhid on Alice.
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("x")));
    }

    [Fact]
    public async Task SetLocalUhid_AlternativeToBundleGeneration()
    {
        var alice = NewService();
        var bob = NewService();

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        alice.SetLocalUhid(AliceUhid); // explicit set instead of generating a bundle
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var encrypted = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("hi"));
        Assert.Equal(AliceUhid, encrypted.SenderUhid);
        var decrypted = await bob.DecryptAsync(AliceUhid, encrypted);
        Assert.Equal("hi", Encoding.UTF8.GetString(decrypted));
    }

    [Fact]
    public async Task PreKeyBundle_HasBothEd25519AndX25519IdentityKeys()
    {
        var node = NewService();
        var bundle = await node.GeneratePreKeyBundleAsync(AliceUhid);

        Assert.Equal(32, bundle.IdentityKey.Length);          // Ed25519
        Assert.Equal(32, bundle.IdentityKeyX25519.Length);    // X25519
        Assert.NotEqual(bundle.IdentityKey, bundle.IdentityKeyX25519);
        Assert.Equal(32, bundle.SignedPreKey.Length);
        Assert.Equal(32, bundle.PreKey.Length);
        Assert.Equal(64, bundle.SignedPreKeySignature.Length); // Ed25519 sig
    }

    [Fact]
    public async Task PreKeyBundle_SignedPreKeySignature_VerifiesAgainstEd25519IdentityKey()
    {
        var node = NewService();
        var bundle = await node.GeneratePreKeyBundleAsync(AliceUhid);

        var ok = Ed25519SigningService.Verify(
            bundle.IdentityKey,
            bundle.SignedPreKey,
            bundle.SignedPreKeySignature);

        Assert.True(ok);
    }
}
