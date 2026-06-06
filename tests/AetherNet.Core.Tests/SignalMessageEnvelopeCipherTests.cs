// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Messaging;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Tests for the messaging↔Signal bridge. Validates the security rule
/// "messages without a Signal session are queued, never sent insecurely":
/// EncryptAsync returns null when no session exists; only when a Signal
/// session is established does it return real ciphertext.
/// </summary>
public class SignalMessageEnvelopeCipherTests
{
    [Fact]
    public async Task EncryptAsync_NoSession_ReturnsNull()
    {
        var signal = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var cipher = new SignalMessageEnvelopeCipher(signal);

        var result = await cipher.EncryptAsync("unknown-peer", Encoding.UTF8.GetBytes("hi"));
        Assert.Null(result);
    }

    [Fact]
    public async Task EncryptAsync_WithSession_ReturnsCiphertext()
    {
        var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bob = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bobBundle = await bob.GeneratePreKeyBundleAsync("bob");
        await alice.GeneratePreKeyBundleAsync("alice");
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var aliceCipher = new SignalMessageEnvelopeCipher(alice);
        var ciphertext = await aliceCipher.EncryptAsync("bob", Encoding.UTF8.GetBytes("hello"));

        Assert.NotNull(ciphertext);
        Assert.NotEqual(Encoding.UTF8.GetBytes("hello"), ciphertext);
    }

    [Fact]
    public async Task EncryptThenDecrypt_RoundTrips()
    {
        var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bob = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bobBundle = await bob.GeneratePreKeyBundleAsync("bob");
        await alice.GeneratePreKeyBundleAsync("alice");
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var aliceCipher = new SignalMessageEnvelopeCipher(alice);
        var bobCipher = new SignalMessageEnvelopeCipher(bob);

        var plaintext = Encoding.UTF8.GetBytes("the mesh is alive");
        var ciphertext = await aliceCipher.EncryptAsync("bob", plaintext);
        Assert.NotNull(ciphertext);

        var decrypted = await bobCipher.DecryptAsync("alice", ciphertext);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task DecryptAsync_TamperedCiphertext_ReturnsNull()
    {
        var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bob = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bobBundle = await bob.GeneratePreKeyBundleAsync("bob");
        await alice.GeneratePreKeyBundleAsync("alice");
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var aliceCipher = new SignalMessageEnvelopeCipher(alice);
        var bobCipher = new SignalMessageEnvelopeCipher(bob);

        var ciphertext = (await aliceCipher.EncryptAsync("bob", Encoding.UTF8.GetBytes("x")))!;

        // Tamper the GCM-authenticated ciphertext deterministically: decode the
        // envelope, flip a byte of the encrypted body, re-encode. Flipping a raw
        // JSON byte (as this test did before) was flaky — the midpoint can land on
        // non-authenticated metadata and still decrypt cleanly; corrupting the
        // ciphertext itself always trips the AEAD tag.
        var payload = EncryptedPayloadCodec.Deserialize(ciphertext);
        payload.Ciphertext[0] ^= 0xFF;
        var tampered = EncryptedPayloadCodec.Serialize(payload);

        var decrypted = await bobCipher.DecryptAsync("alice", tampered);
        Assert.Null(decrypted);
    }

    [Fact]
    public async Task DecryptAsync_MalformedEnvelope_ReturnsNull()
    {
        var bob = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bobCipher = new SignalMessageEnvelopeCipher(bob);

        // Garbage bytes — must drop (not throw) to keep the messaging layer flowing.
        var result = await bobCipher.DecryptAsync("alice", new byte[] { 0xDE, 0xAD });
        Assert.Null(result);
    }

    [Fact]
    public void HasSession_DelegatesToSignalService()
    {
        var signal = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var cipher = new SignalMessageEnvelopeCipher(signal);

        Assert.False(cipher.HasSession("anyone"));
    }

    [Fact]
    public async Task FullDoubleRatchetFlow_SurvivesEnvelopeCodec()
    {
        // Codec preserves all the Double Ratchet wire fields (SenderEphemeralKey,
        // PreviousChainCount, etc.) so a long bidirectional conversation works
        // through the messaging-layer envelope.
        var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bob = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bobBundle = await bob.GeneratePreKeyBundleAsync("bob");
        await alice.GeneratePreKeyBundleAsync("alice");
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var aliceCipher = new SignalMessageEnvelopeCipher(alice);
        var bobCipher = new SignalMessageEnvelopeCipher(bob);

        for (var i = 0; i < 5; i++)
        {
            var aOut = (await aliceCipher.EncryptAsync("bob", Encoding.UTF8.GetBytes($"a{i}")))!;
            Assert.Equal($"a{i}", Encoding.UTF8.GetString((await bobCipher.DecryptAsync("alice", aOut))!));
            var bOut = (await bobCipher.EncryptAsync("alice", Encoding.UTF8.GetBytes($"b{i}")))!;
            Assert.Equal($"b{i}", Encoding.UTF8.GetString((await aliceCipher.DecryptAsync("bob", bOut))!));
        }
    }
}
