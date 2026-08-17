// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using AetherNet.Security.Services;

namespace AetherNet.Identity;

/// <summary>
/// The reference <see cref="INodeIdentity"/> — mint once per device, adopt forever after.
///
/// <para>
/// Every caller on a device shares whatever the device's store holds, so two applications from two
/// vendors that know nothing about each other still arrive at the same node. That is the property the
/// mesh needs: one handset, one address, one presence, one reputation.
/// </para>
///
/// <para>
/// The private key is loaded into this instance so it can sign, and never leaves it. Callers hand over
/// bytes and get a signature; there is no accessor for the key and there should never be one.
/// </para>
/// </summary>
public sealed class NodeIdentity : INodeIdentity
{
    private readonly INodeIdentityStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private byte[]? _privateKey;
    private byte[]? _publicKey;
    private AetherNetTag _tag;

    public NodeIdentity(INodeIdentityStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public async ValueTask<AetherNetTag> GetOrMintAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return _tag;
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return (byte[])_publicKey!.Clone();
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> SignAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return Ed25519SigningService.Sign(_privateKey!, data);
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> DeriveKeyAsync(string purpose, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(purpose)) throw new ArgumentException("A derived key needs a purpose.", nameof(purpose));

        await EnsureAsync(cancellationToken).ConfigureAwait(false);

        // HKDF with the purpose as the info string: the root is the input keying material and never
        // appears in the output, and two purposes are computationally unrelated to each other. A caller
        // holding one derived key learns nothing about the root or about any other purpose's key.
        return System.Security.Cryptography.HKDF.DeriveKey(
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            ikm: _privateKey!,
            outputLength: DerivedKeyLength,
            salt: DerivationSalt,
            info: System.Text.Encoding.UTF8.GetBytes(purpose));
    }

    private const int DerivedKeyLength = 32;

    /// <summary>
    /// Domain separator, so a key derived here can never collide with one derived from the same secret
    /// by some other part of the system using its own scheme.
    /// </summary>
    private static readonly byte[] DerivationSalt = System.Text.Encoding.UTF8.GetBytes("aether-node-derived-v1");

    /// <summary>
    /// Load the device's identity, or mint it if the device has none.
    ///
    /// <para>
    /// Everything happens behind one gate so that applications starting together on a fresh device
    /// cannot each mint one and race to save — the loser's tag would already have been handed out and
    /// possibly put on the wire.
    /// </para>
    /// </summary>
    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (_privateKey is not null) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_privateKey is not null) return;

            // A store that throws here is saying "not now" — a locked device. That must travel: minting
            // over a sealed identity changes this device's address permanently, and everyone holding the
            // old one is left with an address that no longer answers.
            var stored = _store.Load();

            if (stored is not { Length: > 0 })
            {
                // Genuinely a device with no identity. Mint the one it will keep.
                var (privateKey, _) = Ed25519SigningService.GenerateKeyPair();
                _store.Save(privateKey);
                stored = privateKey;
            }

            // The public half is derived from the secret rather than read back from the store, so a
            // tampered or drifted record cannot swap this device's identity for someone else's.
            _publicKey = Ed25519SigningService.DerivePublicKey(stored);
            _tag = AetherNetTag.FromPublicKey(_publicKey);
            _privateKey = stored;
        }
        finally
        {
            _gate.Release();
        }
    }
}
