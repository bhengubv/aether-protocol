// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Security.Services;
using AetherNet.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Verifies that <see cref="SignalProtocolService"/> sessions survive a
/// process restart when wired up to an <see cref="ISignalSessionStore"/>.
///
/// The "restart" is simulated by tearing down the in-memory service and
/// constructing a brand new one against the SAME persistent stores. The
/// new service must come up with every previously-active session in place
/// and able to encrypt / decrypt against the peer.
/// </summary>
public class SignalSessionPersistenceTests
{
    private const string AliceUhid = "alice-uhid-persist";
    private const string BobUhid = "bob-uhid-persist";

    /// <summary>
    /// Build a fresh service against the shared persistent stores. Calling
    /// this twice with the same <paramref name="kv"/> instances simulates
    /// a process restart — the second instance must hydrate every previously
    /// stored session from disk.
    /// </summary>
    private static SignalProtocolService BuildService(
        IKeyValueStore sessionKv,
        IKeyValueStore preKeyKv) =>
        new(NullLogger<SignalProtocolService>.Instance,
            opkPoolSize: SignalProtocolService.DefaultOpkPoolSize,
            sessionStore: new KeyValueSignalSessionStore(sessionKv),
            preKeyStore: new KeyValuePreKeyStore(preKeyKv),
            rotationOptions: null,
            nowProvider: null);

    [Fact]
    public async Task EstablishedSession_SurvivesRestart_OnInitiatorSide()
    {
        // Shared in-memory KV stores for Alice (initiator) — Bob can be
        // ephemeral since the test only verifies Alice resumes correctly.
        var aliceSessionKv = new InMemoryKeyValueStore();
        var alicePreKeyKv = new InMemoryKeyValueStore();
        var bobSessionKv = new InMemoryKeyValueStore();
        var bobPreKeyKv = new InMemoryKeyValueStore();

        // Round 1: Alice + Bob run X3DH and exchange a few messages.
        var alice = BuildService(aliceSessionKv, alicePreKeyKv);
        var bob = BuildService(bobSessionKv, bobPreKeyKv);

        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var first = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("pre-restart-1"));
        await bob.DecryptAsync(AliceUhid, first);

        var second = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("pre-restart-2"));
        var secondPlain = await bob.DecryptAsync(AliceUhid, second);
        Assert.Equal("pre-restart-2", Encoding.UTF8.GetString(secondPlain));

        // Round 2: Alice "restarts" — fresh service against the same KV
        // stores. The session map must be hydrated with the previous
        // Double-Ratchet state.
        var aliceRestarted = BuildService(aliceSessionKv, alicePreKeyKv);
        Assert.True(aliceRestarted.HasSession(BobUhid));

        var third = await aliceRestarted.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("post-restart"));
        var thirdPlain = await bob.DecryptAsync(AliceUhid, third);
        Assert.Equal("post-restart", Encoding.UTF8.GetString(thirdPlain));
    }

    [Fact]
    public async Task EstablishedSession_SurvivesRestart_OnResponderSide()
    {
        // Symmetric to the initiator test: Bob restarts, then receives a
        // new message from Alice using the previously-established session.
        var aliceSessionKv = new InMemoryKeyValueStore();
        var alicePreKeyKv = new InMemoryKeyValueStore();
        var bobSessionKv = new InMemoryKeyValueStore();
        var bobPreKeyKv = new InMemoryKeyValueStore();

        var alice = BuildService(aliceSessionKv, alicePreKeyKv);
        var bob = BuildService(bobSessionKv, bobPreKeyKv);

        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var first = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("ping-1"));
        await bob.DecryptAsync(AliceUhid, first);

        // Bob sends a reply to ensure his session has both send + recv chains.
        var reply = await bob.EncryptAsync(AliceUhid, Encoding.UTF8.GetBytes("pong-1"));
        await alice.DecryptAsync(BobUhid, reply);

        // Bob restarts. Alice's existing session continues from where it left off.
        var bobRestarted = BuildService(bobSessionKv, bobPreKeyKv);
        Assert.True(bobRestarted.HasSession(AliceUhid));

        var aliceMsg = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("post-bob-restart"));
        var plain = await bobRestarted.DecryptAsync(AliceUhid, aliceMsg);
        Assert.Equal("post-bob-restart", Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public async Task NoStore_BehaviorUnchanged_SessionsAreInMemoryOnly()
    {
        // Sanity check: the persistence path must not affect behavior
        // when no stores are wired up. Two services sharing nothing
        // should NOT see each other's sessions.
        var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bob = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var msg = await alice.EncryptAsync(BobUhid, Encoding.UTF8.GetBytes("hello"));
        await bob.DecryptAsync(AliceUhid, msg);

        // A second Alice constructed with no stores must NOT see the session.
        var aliceFresh = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        Assert.False(aliceFresh.HasSession(BobUhid));
    }

    [Fact]
    public async Task ListPeers_EnumeratesOnlyStoredSessions()
    {
        var sessionKv = new InMemoryKeyValueStore();
        var preKeyKv = new InMemoryKeyValueStore();

        var alice = BuildService(sessionKv, preKeyKv);
        await alice.GeneratePreKeyBundleAsync(AliceUhid);

        // Establish sessions to two distinct peers.
        var bob = BuildService(new InMemoryKeyValueStore(), new InMemoryKeyValueStore());
        var carol = BuildService(new InMemoryKeyValueStore(), new InMemoryKeyValueStore());
        var bobBundle = await bob.GeneratePreKeyBundleAsync(BobUhid);
        var carolBundle = await carol.GeneratePreKeyBundleAsync("carol-uhid-persist");

        await alice.ProcessPreKeyBundleAsync(bobBundle);
        await alice.ProcessPreKeyBundleAsync(carolBundle);

        // The session store should reflect both peers — implicitly
        // verified by hydrating a fresh Alice and checking HasSession.
        var aliceRestarted = BuildService(sessionKv, preKeyKv);
        Assert.True(aliceRestarted.HasSession(BobUhid));
        Assert.True(aliceRestarted.HasSession("carol-uhid-persist"));
    }
}
