// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using AetherNet.Security.Backup;
using AetherNet.Security.Services;

namespace AetherNet.Identity;

/// <summary>
/// The reference <see cref="INodeIdentityRecovery"/> — moves the device's identity in and out of the one
/// <see cref="INodeIdentityStore"/> the device keeps, using the standard <see cref="Bip39Mnemonic"/> codec
/// so a phrase written here restores anywhere and every AetherNet language SDK agrees byte-for-byte.
/// </summary>
/// <remarks>
/// Shares the store with <see cref="NodeIdentity"/>. Because adopt and restore refuse when the store
/// already holds an identity, they only ever run on a device with nothing minted yet — so there is no
/// live <see cref="NodeIdentity"/> cache to invalidate, and the two need no coordination beyond the store.
/// </remarks>
public sealed class NodeIdentityRecovery : INodeIdentityRecovery
{
    /// <summary>An Ed25519 seed, which is also the whole private key, is exactly this many bytes.</summary>
    private const int SeedLength = 32;

    private readonly INodeIdentityStore _store;

    public NodeIdentityRecovery(INodeIdentityStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public ValueTask<AetherNetTag> AdoptSeedAsync(byte[] seed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Length != SeedLength)
            throw new ArgumentException($"An Ed25519 seed must be {SeedLength} bytes.", nameof(seed));

        // Asked without opening the identity: "is something stored", never "can it be opened". A device
        // with a sealed (locked) identity still HAS one, and adopting over it would be destruction.
        if (_store.Exists)
            throw new IdentityAlreadyExistsException(
                "This device already holds an identity; adoption does not overwrite it.");

        // Derive the public half from the seed itself, so the tag returned is the one this seed will
        // always produce — not a value read back from a record that could have drifted.
        var publicKey = Ed25519SigningService.DerivePublicKey(seed);
        var tag = AetherNetTag.FromPublicKey(publicKey);

        // Store a copy: the caller still owns the array it handed in and is free to clear it.
        _store.Save((byte[])seed.Clone());

        return ValueTask.FromResult(tag);
    }

    /// <inheritdoc />
    public ValueTask<string> ExportRecoveryPhraseAsync(CancellationToken cancellationToken = default)
    {
        // Load throws NodeIdentityUnavailableException on a locked device — the honest "not now", which
        // must travel rather than be flattened into "nothing here".
        var seed = _store.Load();
        if (seed is null)
            throw new InvalidOperationException("This device has no identity to export.");
        if (seed.Length != SeedLength)
            throw new InvalidOperationException(
                $"Stored identity is not a {SeedLength}-byte Ed25519 seed.");

        // The 32-byte seed IS the BIP-39 entropy — 256 bits, 24 words.
        return ValueTask.FromResult(Bip39Mnemonic.EntropyToMnemonic(seed));
    }

    /// <inheritdoc />
    public ValueTask<AetherNetTag> RestoreFromPhraseAsync(
        string recoveryPhrase, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPhrase);

        // Throws FormatException on an unknown word / wrong count / bad checksum — a mistyped phrase is
        // refused, never silently turned into the wrong identity. A valid non-24-word phrase decodes to a
        // seed that is not 32 bytes, which AdoptSeedAsync then rejects.
        var seed = Bip39Mnemonic.MnemonicToEntropy(recoveryPhrase);
        return AdoptSeedAsync(seed, cancellationToken);
    }
}
