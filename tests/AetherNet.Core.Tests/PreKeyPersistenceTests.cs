// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Security.Services;
using AetherNet.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Verifies that long-term identity keys, the active signed-pre-key, and
/// the OPK pool all survive a process restart when wired up to an
/// <see cref="IPreKeyStore"/>. Without persistence, every restart would
/// regenerate identity keys and invalidate every outstanding bundle ever
/// published for this node.
/// </summary>
public class PreKeyPersistenceTests
{
    private const string LocalUhid = "uhid-prekey-persist";

    private static SignalProtocolService BuildService(IKeyValueStore preKeyKv) =>
        new(NullLogger<SignalProtocolService>.Instance,
            opkPoolSize: 16,
            sessionStore: null,
            preKeyStore: new KeyValuePreKeyStore(preKeyKv),
            rotationOptions: null,
            nowProvider: null);

    [Fact]
    public async Task Identity_SurvivesRestart()
    {
        var kv = new InMemoryKeyValueStore();

        var first = BuildService(kv);
        var ed25519PubBefore = first.GetPublicKey();
        var x25519PubBefore = first.GetX25519PublicKey();

        // Restart against the same store.
        var second = BuildService(kv);
        var ed25519PubAfter = second.GetPublicKey();
        var x25519PubAfter = second.GetX25519PublicKey();

        Assert.Equal(ed25519PubBefore, ed25519PubAfter);
        Assert.Equal(x25519PubBefore, x25519PubAfter);

        // Signature round-trip through the restored Ed25519 key works.
        var data = Encoding.UTF8.GetBytes("identity-persistence-check");
        var sig = await second.SignDataAsync(data);
        Assert.True(first.VerifySignature(ed25519PubBefore, data, sig));
    }

    [Fact]
    public async Task ActiveSignedPreKey_SurvivesRestart()
    {
        var kv = new InMemoryKeyValueStore();

        var first = BuildService(kv);
        var bundleBefore = await first.GeneratePreKeyBundleAsync(LocalUhid);

        var second = BuildService(kv);
        // Re-issuing a bundle should reuse the persisted active SPK
        // (no rotation: default RotationInterval is 7 days).
        var bundleAfter = await second.GeneratePreKeyBundleAsync(LocalUhid);

        Assert.Equal(bundleBefore.SignedPreKeyId, bundleAfter.SignedPreKeyId);
        Assert.Equal(bundleBefore.SignedPreKey, bundleAfter.SignedPreKey);
        Assert.Equal(bundleBefore.SignedPreKeySignature, bundleAfter.SignedPreKeySignature);
    }

    [Fact]
    public async Task OneTimePreKeyPool_SurvivesRestart()
    {
        var kv = new InMemoryKeyValueStore();

        var first = BuildService(kv);
        var bundle1 = await first.GeneratePreKeyBundleAsync(LocalUhid);
        var heldBefore = first.HeldOneTimePreKeyCount;
        var availableBefore = first.AvailableOneTimePreKeyCount;
        Assert.Equal(16, heldBefore);
        Assert.Equal(15, availableBefore); // one issued in bundle1

        // Restart. The pool size should match — the issued OPK should
        // still be marked issued, and the un-issued ones should still be
        // un-issued. Issuing another bundle must NOT reuse bundle1's id.
        var second = BuildService(kv);
        Assert.Equal(heldBefore, second.HeldOneTimePreKeyCount);
        Assert.Equal(availableBefore, second.AvailableOneTimePreKeyCount);

        var bundle2 = await second.GeneratePreKeyBundleAsync(LocalUhid);
        Assert.NotEqual(bundle1.PreKeyId, bundle2.PreKeyId);
    }

    [Fact]
    public async Task ResponderSession_AcrossRestart_ConsumesOpk()
    {
        // Bob persists. Alice initiates against Bob's bundle, then Bob
        // restarts BEFORE the PreKey message arrives. After restart, the
        // OPK that was reserved for Alice is still in Bob's pool — so
        // X3DH should still complete on Bob's side.
        var bobPreKeyKv = new InMemoryKeyValueStore();
        var bobSessionKv = new InMemoryKeyValueStore();

        var bob = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance,
            opkPoolSize: 16,
            sessionStore: new KeyValueSignalSessionStore(bobSessionKv),
            preKeyStore: new KeyValuePreKeyStore(bobPreKeyKv),
            rotationOptions: null, nowProvider: null);
        var bobBundle = await bob.GeneratePreKeyBundleAsync("bob-uhid-x3dh-restart");

        var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        await alice.GeneratePreKeyBundleAsync("alice-uhid-x3dh-restart");
        await alice.ProcessPreKeyBundleAsync(bobBundle);
        var msg = await alice.EncryptAsync("bob-uhid-x3dh-restart", Encoding.UTF8.GetBytes("hello-after-restart"));

        // Bob "restarts" — fresh service over the SAME stores. He should
        // pick up the un-consumed OPK + active SPK and complete X3DH.
        var bobRestarted = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance,
            opkPoolSize: 16,
            sessionStore: new KeyValueSignalSessionStore(bobSessionKv),
            preKeyStore: new KeyValuePreKeyStore(bobPreKeyKv),
            rotationOptions: null, nowProvider: null);

        var plain = await bobRestarted.DecryptAsync("alice-uhid-x3dh-restart", msg);
        Assert.Equal("hello-after-restart", Encoding.UTF8.GetString(plain));

        // Replay attempt: a second restart with the same stored OPK pool
        // should NOT have the consumed OPK any more — the OPK was deleted
        // from the persistent store on consumption.
        var bobRestartedAgain = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance,
            opkPoolSize: 16,
            sessionStore: new KeyValueSignalSessionStore(bobSessionKv),
            preKeyStore: new KeyValuePreKeyStore(bobPreKeyKv),
            rotationOptions: null, nowProvider: null);
        // The OPK count drops by 1 (one consumed by Alice's X3DH).
        Assert.Equal(15, bobRestartedAgain.HeldOneTimePreKeyCount);
    }
}
