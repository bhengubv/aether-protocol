// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Security.Models;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

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

    // ─── Double Ratchet (Signal §5) tests ───────────────────────────────────

    [Fact]
    public async Task DoubleRatchet_EveryMessageCarriesSenderEphemeralKey()
    {
        var alice = NewService();
        var bob = NewService();
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var first = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("a"));
        Assert.NotNull(first.SenderEphemeralKeyX25519);
        Assert.Equal(32, first.SenderEphemeralKeyX25519!.Length);

        await bob.DecryptAsync(AliceUhid, first);

        // Subsequent message also carries SenderEphemeralKeyX25519 (same
        // value — Alice hasn't ratcheted because Bob hasn't responded yet).
        var second = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("b"));
        Assert.NotNull(second.SenderEphemeralKeyX25519);
        Assert.Equal(first.SenderEphemeralKeyX25519, second.SenderEphemeralKeyX25519);
    }

    [Fact]
    public async Task DoubleRatchet_SenderEphemeralKey_RotatesAfterRoundtrip()
    {
        var alice = NewService();
        var bob = NewService();
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        // Alice → Bob: Alice's first ratchet pub.
        var aliceFirst = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("ping"));
        await bob.DecryptAsync(AliceUhid, aliceFirst);

        // Bob → Alice: Bob's first ratchet pub (rotated by responder-side DH ratchet).
        var bobReply = await bob.EncryptAsync(AliceUhid, Encoding.UTF8.GetBytes("pong"));
        Assert.NotNull(bobReply.SenderEphemeralKeyX25519);
        // Bob's ratchet pub should be DIFFERENT from Alice's (Bob generated
        // fresh DHs on his DH-ratchet step).
        Assert.NotEqual(aliceFirst.SenderEphemeralKeyX25519, bobReply.SenderEphemeralKeyX25519);

        await alice.DecryptAsync(BobUhid, bobReply);

        // Alice → Bob (after roundtrip): Alice should now use a NEW ratchet pub
        // (rotated on her DH-ratchet step when she received Bob's reply).
        var aliceSecond = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("ping2"));
        Assert.NotEqual(aliceFirst.SenderEphemeralKeyX25519, aliceSecond.SenderEphemeralKeyX25519);
        Assert.NotEqual(bobReply.SenderEphemeralKeyX25519, aliceSecond.SenderEphemeralKeyX25519);

        // Bob can still decrypt Alice's new message.
        var dec = await bob.DecryptAsync(AliceUhid, aliceSecond);
        Assert.Equal("ping2", Encoding.UTF8.GetString(dec));
    }

    [Fact]
    public async Task DoubleRatchet_PreviousChainCount_TracksMessagesPerChain()
    {
        var alice = NewService();
        var bob = NewService();
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        // Alice sends 3 messages without a roundtrip.
        for (var i = 0; i < 3; i++)
        {
            var enc = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes($"a{i}"));
            // PN is 0 because this IS Alice's first chain.
            Assert.Equal(0, enc.PreviousChainCount);
            await bob.DecryptAsync(AliceUhid, enc);
        }

        // Bob sends a reply, triggering his DH-ratchet step.
        var bobReply = await bob.EncryptAsync(AliceUhid, Encoding.UTF8.GetBytes("hi"));
        // Bob's PN reflects however many messages Bob sent in his previous
        // sending chain — which was 0 (Bob hadn't sent anything yet before
        // his DH-ratchet step rotated his chain).
        Assert.Equal(0, bobReply.PreviousChainCount);
        await alice.DecryptAsync(BobUhid, bobReply);

        // Alice's next message after her DH-ratchet step. Her PN should be
        // 3 — that's how many messages she sent on her previous chain
        // before Bob's reply triggered her ratchet.
        var aliceNew = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("a3"));
        Assert.Equal(3, aliceNew.PreviousChainCount);
    }

    [Fact]
    public async Task DoubleRatchet_OutOfOrderAcrossDhRatchetBoundary_StillDecrypts()
    {
        // Alice sends 3 messages on chain 1. Bob receives only the first 2,
        // then Alice does a DH-ratchet (because Bob replied) and sends a 4th
        // on chain 2. The 3rd message (from chain 1) arrives last —
        // Bob must still be able to decrypt it via the skipped-keys cache
        // keyed by (Alice's old DHs pub, counter=2).
        var alice = NewService();
        var bob = NewService();
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var a0 = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("a0"));
        var a1 = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("a1"));
        var a2 = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("a2"));

        // Bob receives a0, a1 only.
        Assert.Equal("a0", Encoding.UTF8.GetString(await bob.DecryptAsync(AliceUhid, a0)));
        Assert.Equal("a1", Encoding.UTF8.GetString(await bob.DecryptAsync(AliceUhid, a1)));

        // Bob replies — triggers his DH-ratchet step.
        var bReply = await bob.EncryptAsync(AliceUhid, Encoding.UTF8.GetBytes("hi"));
        await alice.DecryptAsync(BobUhid, bReply);

        // Alice sends a4 on her new chain (after her DH-ratchet step).
        var a4 = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("a4"));
        // Bob receives a4 — triggers his second DH-ratchet step. He must
        // skip-derive a key for Alice's old chain counter=2 because PN=3.
        Assert.Equal("a4", Encoding.UTF8.GetString(await bob.DecryptAsync(AliceUhid, a4)));

        // Now the missing a2 (from Alice's OLD chain) finally arrives. Bob
        // should pull the skipped key from cache.
        Assert.Equal("a2", Encoding.UTF8.GetString(await bob.DecryptAsync(AliceUhid, a2)));
    }

    [Fact]
    public async Task DoubleRatchet_LongConversation_AllMessagesDecrypt()
    {
        var alice = NewService();
        var bob = NewService();
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        // 10 alternating messages — each side ratchets at every roundtrip.
        for (var i = 0; i < 10; i++)
        {
            var aMsg = $"alice {i}";
            var aEnc = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes(aMsg));
            Assert.Equal(aMsg, Encoding.UTF8.GetString(await bob.DecryptAsync(AliceUhid, aEnc)));

            var bMsg = $"bob {i}";
            var bEnc = await bob.EncryptAsync(AliceUhid, Encoding.UTF8.GetBytes(bMsg));
            Assert.Equal(bMsg, Encoding.UTF8.GetString(await alice.DecryptAsync(BobUhid, bEnc)));
        }
    }
}
