// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Forward secrecy / deletion-proof tests for the C# Signal Protocol
/// reference implementation.
///
/// Three properties are verified:
///
///  1. A ciphertext whose message key was consumed cannot be re-decrypted once
///     the Double Ratchet has advanced past that key epoch (replay fails).
///  2. A failed replay attempt does not corrupt session state; the next
///     legitimate message still decrypts correctly.
///  3. Encrypting identical plaintext twice always yields distinct ciphertexts
///     (AES-256-GCM fresh nonce per call); both ciphertexts decrypt correctly.
/// </summary>
public class ForwardSecrecyTests
{
    private const string AliceUhid = "alice-uhid";
    private const string BobUhid = "bob-uhid";

    private static SignalProtocolService NewService() =>
        new(NullLogger<SignalProtocolService>.Instance);

    /// <summary>
    /// Establishes a session between alice and bob. Bob generates the pre-key
    /// bundle; alice processes it. No messages are exchanged — the caller
    /// decides the first message to send.
    /// </summary>
    private static async Task EstablishSessionAsync(
        SignalProtocolService alice,
        SignalProtocolService bob)
    {
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);
    }

    // ─── Test 1 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaying a consumed message after the ratchet has advanced must throw.
    ///
    /// Once a message key is used its state is discarded. Re-presenting the
    /// same ciphertext to a session that has moved multiple DH-ratchet steps
    /// further cannot produce the original plaintext because the AES-GCM
    /// authentication tag was computed with a key that is no longer held.
    ///
    /// Advancement: 5 bidirectional round-trips so both sides have DH-ratcheted
    /// multiple times and are well past the first message's key epoch.
    /// </summary>
    [Fact]
    public async Task ForwardSecrecy_ReplayOfConsumedMessageFails()
    {
        var alice = NewService();
        var bob = NewService();
        await EstablishSessionAsync(alice, bob);

        // Alice sends M1 (PreKey message — establishes Bob's session).
        var plaintext = Encoding.UTF8.GetBytes("forward secret payload");
        var m1 = await alice.EncryptAsync(BobUhid, plaintext);

        // Bob decrypts M1 and confirms the plaintext.
        var decoded = await bob.DecryptAsync(AliceUhid, m1);
        Assert.Equal(plaintext, decoded);

        // Advance the ratchet: 5 bidirectional round-trips.
        for (var i = 0; i < 5; i++)
        {
            var aMsg = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes($"alice {i}"));
            await bob.DecryptAsync(AliceUhid, aMsg);

            var bMsg = await bob.EncryptAsync(AliceUhid, Encoding.UTF8.GetBytes($"bob {i}"));
            await alice.DecryptAsync(BobUhid, bMsg);
        }

        // Attempt to replay M1. The AES-GCM tag must fail to verify because
        // the message key it was encrypted under is gone.
        var replayThrew = false;
        try
        {
            await bob.DecryptAsync(AliceUhid, m1);
        }
        catch (Exception)
        {
            replayThrew = true;
        }

        Assert.True(replayThrew,
            "Replaying a consumed message after ratchet advancement must throw an exception.");
    }

    // ─── Test 2 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Session must remain healthy after a failed replay attempt.
    ///
    /// The decrypt path must be transactional with respect to session state:
    /// an authentication failure must not advance the receive counter, corrupt
    /// the receive chain key, or leave any other state that would prevent
    /// the next legitimate message from decrypting correctly.
    /// </summary>
    [Fact]
    public async Task ForwardSecrecy_SessionHealthyAfterReplayAttempt()
    {
        var alice = NewService();
        var bob = NewService();
        await EstablishSessionAsync(alice, bob);

        // M1: initial message, Bob establishes his session.
        var m1 = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("ephemeral secret"));
        await bob.DecryptAsync(AliceUhid, m1);

        // Advance ratchet: 3 bidirectional pairs.
        for (var i = 0; i < 3; i++)
        {
            var a = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes($"a{i}"));
            await bob.DecryptAsync(AliceUhid, a);
            var b = await bob.EncryptAsync(AliceUhid, Encoding.UTF8.GetBytes($"b{i}"));
            await alice.DecryptAsync(BobUhid, b);
        }

        // Replay M1 — must fail; swallow the exception.
        try
        {
            await bob.DecryptAsync(AliceUhid, m1);
        }
        catch
        {
            // Expected — the replay must throw. We swallow it here because
            // the purpose of this test is to verify that the session survives.
        }

        // Alice encrypts a fresh message after the failed replay attempt.
        var freshPlaintext = Encoding.UTF8.GetBytes("still alive");
        var freshEnc = await alice.EncryptAsync(BobUhid, freshPlaintext);

        // Bob must be able to decrypt it — session must not be corrupted.
        var result = await bob.DecryptAsync(AliceUhid, freshEnc);
        Assert.Equal(freshPlaintext, result);
    }

    // ─── Test 3 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Encrypting the same plaintext twice must yield distinct ciphertexts,
    /// and both must decrypt to the original plaintext.
    ///
    /// AES-256-GCM generates a fresh random nonce on every call. In addition,
    /// the symmetric ratchet advances the chain key after each message, so
    /// consecutive encrypts use both a different nonce AND a different message
    /// key. The distinctness assertion guards against accidental determinism
    /// (e.g. a seeded RNG, nonce counter reuse, or chain-key regression).
    /// </summary>
    [Fact]
    public async Task ForwardSecrecy_SamePlaintextYieldsDifferentCiphertexts()
    {
        var alice = NewService();
        var bob = NewService();
        await EstablishSessionAsync(alice, bob);

        var samePlaintext = Encoding.UTF8.GetBytes("hello");

        // Encrypt the same plaintext twice in succession.
        var enc1 = await alice.EncryptAsync(BobUhid, samePlaintext);
        var enc2 = await alice.EncryptAsync(BobUhid, samePlaintext);

        // The ciphertext bytes (which embed the GCM tag and are distinct from
        // the plaintext) must differ.
        Assert.False(
            enc1.Ciphertext.AsSpan().SequenceEqual(enc2.Ciphertext),
            "Two encryptions of the same plaintext must produce distinct ciphertexts.");

        // Deliver enc1 first so Bob can establish his responder session via
        // the PreKey message, then decrypt enc2 as a normal session message.
        var pt1 = await bob.DecryptAsync(AliceUhid, enc1);
        var pt2 = await bob.DecryptAsync(AliceUhid, enc2);

        Assert.Equal(samePlaintext, pt1);
        Assert.Equal(samePlaintext, pt2);
    }
}
