// SPDX-License-Identifier: MIT

using System.Text;
using Aether.Security.Models;
using Aether.Security.Services;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aether.Benchmarks;

/// <summary>
/// X3DH session establishment + Double-Ratchet send/receive hot paths.
///
/// The expensive operations here are:
///
///   * <see cref="X3DH_ProcessPreKeyBundle"/>   — 4 X25519 DHs + HKDF (one-shot per peer)
///   * <see cref="DhRatchet_Step"/>             — 2 X25519 DHs + 2 KDF_RKs (every roundtrip)
///   * subsequent encrypts/decrypts            — 1 HMAC chain step + AES-GCM
///
/// Pin baselines so swaps of the X25519 backend (BouncyCastle today, libsodium
/// or BCL X25519 in the future) are visible to whoever owns the change.
/// </summary>
[MemoryDiagnoser]
public class SignalProtocolBenchmarks
{
    private const string AliceUhid = "alice-uhid";
    private const string BobUhid = "bob-uhid";

    private static readonly byte[] PlaintextSmall = Encoding.UTF8.GetBytes("hello, mesh");
    private byte[] _plaintextBlock = null!;

    // Pre-warmed pair for normal-message send/recv. Re-created per iteration
    // is too expensive (X3DH dominates the timing); set up once and re-use.
    private SignalProtocolService _alice = null!;
    private SignalProtocolService _bob = null!;
    private PreKeyBundle _bobBundle = null!;

    // For Encrypt_SubsequentMessage — established session, no PreKey flag.
    private SignalProtocolService _aliceWarm = null!;
    private SignalProtocolService _bobWarm = null!;

    // For Decrypt_NormalMessage — payload Bob will decrypt repeatedly. We
    // can't actually decrypt the same message twice (the receive ratchet
    // advances) so we capture the state needed and create a fresh
    // session+payload in [IterationSetup] for that one method.
    private SignalProtocolService _decryptAlice = null!;
    private SignalProtocolService _decryptBob = null!;

    [GlobalSetup]
    public void Setup()
    {
        _plaintextBlock = new byte[256];
        new Random(42).NextBytes(_plaintextBlock);

        // Fresh pair for the X3DH benchmark — re-built per iteration in [IterationSetup].
        _alice = NewService();
        _bob = NewService();
        _bobBundle = _bob.GeneratePreKeyBundleAsync(BobUhid).GetAwaiter().GetResult();
        _alice.GeneratePreKeyBundleAsync(AliceUhid).GetAwaiter().GetResult();

        // Warm pair: PreKey message sent + decrypted, ratchets primed both sides.
        // Subsequent benchmarks use this pair so they're measuring the steady-state
        // chain step + AES-GCM, not the one-time X3DH cost.
        _aliceWarm = NewService();
        _bobWarm = NewService();
        var bobWarmBundle = _bobWarm.GeneratePreKeyBundleAsync(BobUhid).GetAwaiter().GetResult();
        _aliceWarm.GeneratePreKeyBundleAsync(AliceUhid).GetAwaiter().GetResult();
        _aliceWarm.ProcessPreKeyBundleAsync(bobWarmBundle).GetAwaiter().GetResult();

        var firstFromAlice = _aliceWarm.EncryptAsync(BobUhid, PlaintextSmall).GetAwaiter().GetResult();
        _bobWarm.DecryptAsync(AliceUhid, firstFromAlice).GetAwaiter().GetResult();
    }

    [IterationSetup(Target = nameof(X3DH_ProcessPreKeyBundle))]
    public void Setup_X3DH()
    {
        // ProcessPreKeyBundle stores session state in the dictionary keyed by peer
        // UHID. Re-running on the same SignalProtocolService just overwrites the
        // entry, so the per-iteration setup is simply re-generating Bob's bundle
        // (a fresh OPK each time).
        _bobBundle = _bob.GeneratePreKeyBundleAsync(BobUhid).GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task X3DH_ProcessPreKeyBundle()
        => _alice.ProcessPreKeyBundleAsync(_bobBundle);

    [IterationSetup(Target = nameof(Encrypt_FirstMessage))]
    public void Setup_FirstMessage()
    {
        // Each iteration needs a fresh initiator-side session because the
        // PendingPreKeyMessage flag flips on first encrypt. Re-establish.
        _alice = NewService();
        _bob = NewService();
        _bobBundle = _bob.GeneratePreKeyBundleAsync(BobUhid).GetAwaiter().GetResult();
        _alice.GeneratePreKeyBundleAsync(AliceUhid).GetAwaiter().GetResult();
        _alice.ProcessPreKeyBundleAsync(_bobBundle).GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task<EncryptedPayload> Encrypt_FirstMessage()
        => _alice.EncryptAsync(BobUhid, PlaintextSmall);

    [Benchmark]
    public Task<EncryptedPayload> Encrypt_SubsequentMessage()
        => _aliceWarm.EncryptAsync(BobUhid, _plaintextBlock);

    [IterationSetup(Target = nameof(Decrypt_NormalMessage))]
    public void Setup_DecryptNormal()
    {
        // Same warm-pair pattern but with throwaway services so the
        // ratchet counters can advance per iteration without polluting
        // the other benchmarks.
        _decryptAlice = NewService();
        _decryptBob = NewService();
        var bundle = _decryptBob.GeneratePreKeyBundleAsync(BobUhid).GetAwaiter().GetResult();
        _decryptAlice.GeneratePreKeyBundleAsync(AliceUhid).GetAwaiter().GetResult();
        _decryptAlice.ProcessPreKeyBundleAsync(bundle).GetAwaiter().GetResult();
        var first = _decryptAlice.EncryptAsync(BobUhid, PlaintextSmall).GetAwaiter().GetResult();
        _decryptBob.DecryptAsync(AliceUhid, first).GetAwaiter().GetResult();

        // The payload that Decrypt_NormalMessage will time: Alice -> Bob,
        // normal (non-PreKey) message, no DH-ratchet on receive (Alice's
        // ratchet pubkey hasn't changed since the warm-up message).
        _decryptPayload = _decryptAlice.EncryptAsync(BobUhid, _plaintextBlock).GetAwaiter().GetResult();
    }

    private EncryptedPayload _decryptPayload = null!;

    [Benchmark]
    public Task<byte[]> Decrypt_NormalMessage()
        => _decryptBob.DecryptAsync(AliceUhid, _decryptPayload);

    [IterationSetup(Target = nameof(DhRatchet_Step))]
    public void Setup_DhRatchet()
    {
        // To measure a DH-ratchet step on receive we need to (a) get Alice
        // and Bob both warmed up, (b) have Bob send a message Alice decrypts
        // (so Bob now has a ratchet pub Alice has seen), (c) have Bob send a
        // SECOND message — but with a NEW ratchet keypair triggered by his
        // own incoming receive of one of Alice's messages.
        //
        // Easier path: full roundtrip warm-up, then have Bob encrypt — that
        // forces Bob to lazy-init his SendChainKey (DhRatchetSendOnly), which
        // is the receive-side cost we want to measure approximately. This is
        // a soft-proxy for the full DhRatchetReceive cost; the exact full
        // step is what Decrypt_NormalMessage measures when SenderEphemeralKey
        // changes between messages.
        _ratchetAlice = NewService();
        _ratchetBob = NewService();
        var bobBundle = _ratchetBob.GeneratePreKeyBundleAsync(BobUhid).GetAwaiter().GetResult();
        _ratchetAlice.GeneratePreKeyBundleAsync(AliceUhid).GetAwaiter().GetResult();
        _ratchetAlice.ProcessPreKeyBundleAsync(bobBundle).GetAwaiter().GetResult();
        var first = _ratchetAlice.EncryptAsync(BobUhid, PlaintextSmall).GetAwaiter().GetResult();
        _ratchetBob.DecryptAsync(AliceUhid, first).GetAwaiter().GetResult();
    }

    private SignalProtocolService _ratchetAlice = null!;
    private SignalProtocolService _ratchetBob = null!;

    [Benchmark]
    public async Task<byte[]> DhRatchet_Step()
    {
        // Bob encrypts -> Alice decrypts. Since Bob's ratchet pubkey is
        // different from anything Alice has seen (Bob rotates DHs after his
        // first decrypt), this triggers a full DhRatchetReceive on Alice.
        var fromBob = await _ratchetBob.EncryptAsync(AliceUhid, _plaintextBlock);
        return await _ratchetAlice.DecryptAsync(BobUhid, fromBob);
    }

    private static SignalProtocolService NewService() =>
        new(NullLogger<SignalProtocolService>.Instance);
}
