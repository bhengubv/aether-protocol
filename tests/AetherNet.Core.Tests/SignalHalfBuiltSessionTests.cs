// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using AetherNet.Security.Services;
using AetherNet.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// A session that has been established but has never opened a message cannot send anything: it has
/// no DHr and no sending chain, so there is no key to encrypt under.
///
/// <para>
/// That state is fine as a moment in time — it lasts until the message that created it is opened.
/// It is not fine written to disk. A node that stored it came back after every restart holding a
/// session it could receive on and never speak on, and because the failure was reported as an
/// <see cref="InvalidOperationException"/> rather than a cryptographic one, nothing in the stack
/// recognised it as a session worth rebuilding. On two phones this looked like a call that would
/// not dial, with the ratchet apparently healthy on both sides.
/// </para>
/// </summary>
public class SignalHalfBuiltSessionTests
{
    private const string Alice = "alice-uhid-halfbuilt";
    private const string Bob = "bob-uhid-halfbuilt";

    private static SignalProtocolService Build(string localUhid, IKeyValueStore? sessions = null)
    {
        var service = new SignalProtocolService(
            NullLogger<SignalProtocolService>.Instance,
            opkPoolSize: SignalProtocolService.DefaultOpkPoolSize,
            sessionStore: sessions is null ? null : new KeyValueSignalSessionStore(sessions),
            preKeyStore: null,
            rotationOptions: null,
            nowProvider: null);
        service.SetLocalUhid(localUhid);
        return service;
    }

    // ── what must not be written down ──────────────────────────────────────

    /// <summary>
    /// The responder establishes its side while opening the first message. If that message then
    /// fails, nothing may be left in the store — a session that cannot send is worse than no session
    /// at all, because no session at least gets rebuilt.
    /// </summary>
    [Fact]
    public async Task A_session_that_cannot_send_yet_is_never_stored()
    {
        var store = new InMemoryKeyValueStore();
        var bob = Build(Bob, store);
        var alice = Build(Alice);

        await alice.ProcessPreKeyBundleAsync(await bob.GeneratePreKeyBundleAsync(Bob));
        var opening = await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("hello"));

        // The radio corrupted it in flight: Bob establishes his side from the header, then the body
        // will not open.
        var ciphertext = (byte[])opening.Ciphertext.Clone();
        ciphertext[^1] ^= 0xFF;
        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => bob.DecryptAsync(Alice, opening with { Ciphertext = ciphertext }));

        // Whatever Bob holds in memory, nothing unusable may have reached the disk.
        var restarted = Build(Bob, store);
        Assert.False(restarted.HasSession(Alice),
            "a session that has never opened a message was written to the store");
    }

    /// <summary>The good path still stores: a session that has opened a message survives a restart.</summary>
    [Fact]
    public async Task A_session_that_has_opened_a_message_is_stored()
    {
        var store = new InMemoryKeyValueStore();
        var bob = Build(Bob, store);
        var alice = Build(Alice);

        await alice.ProcessPreKeyBundleAsync(await bob.GeneratePreKeyBundleAsync(Bob));
        await bob.DecryptAsync(Alice, await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("hello")));

        var restarted = Build(Bob, store);
        Assert.True(restarted.HasSession(Alice), "a working session did not survive the restart");

        // And it can still speak, which is the whole point of keeping it.
        var reply = await restarted.EncryptAsync(Alice, Encoding.UTF8.GetBytes("hi back"));
        Assert.Equal("hi back", Encoding.UTF8.GetString(await alice.DecryptAsync(Bob, reply)));
    }

    // ── how it must fail when it does happen ───────────────────────────────

    /// <summary>
    /// Sending on a session that never completed must read as a dead session, so the repair paths —
    /// which all key off a cryptographic failure — actually rebuild it.
    /// </summary>
    [Fact]
    public async Task Sending_on_a_session_that_never_completed_reports_a_dead_session()
    {
        var bob = Build(Bob);
        var alice = Build(Alice);

        await alice.ProcessPreKeyBundleAsync(await bob.GeneratePreKeyBundleAsync(Bob));
        var opening = await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("hello"));

        var ciphertext = (byte[])opening.Ciphertext.Clone();
        ciphertext[^1] ^= 0xFF;
        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => bob.DecryptAsync(Alice, opening with { Ciphertext = ciphertext }));

        if (!bob.HasSession(Alice)) return;   // nothing half-built survived, which is also correct

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => bob.EncryptAsync(Alice, Encoding.UTF8.GetBytes("can I speak?")));
    }

    /// <summary>
    /// And having failed that way, the pair must be able to get going again — that is the difference
    /// between a bad moment and a wedged node.
    /// </summary>
    [Fact]
    public async Task A_pair_recovers_after_a_send_on_a_session_that_never_completed()
    {
        var bob = Build(Bob);
        var alice = Build(Alice);

        await alice.ProcessPreKeyBundleAsync(await bob.GeneratePreKeyBundleAsync(Bob));
        var opening = await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("hello"));

        var ciphertext = (byte[])opening.Ciphertext.Clone();
        ciphertext[^1] ^= 0xFF;
        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => bob.DecryptAsync(Alice, opening with { Ciphertext = ciphertext }));

        // What a repair does: drop what will not work and take a fresh bundle.
        bob.DropSession(Alice);
        await bob.ProcessPreKeyBundleAsync(await alice.GeneratePreKeyBundleAsync(Alice));

        var hello = await bob.EncryptAsync(Alice, Encoding.UTF8.GetBytes("try again"));
        Assert.Equal("try again", Encoding.UTF8.GetString(await alice.DecryptAsync(Bob, hello)));
    }
}
