// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace AetherNet.Identity;

/// <summary>
/// Moving a device's identity — out of this device to a recovery phrase, and onto a device from a phrase
/// or a raw seed. The portability half of <see cref="INodeIdentity"/>, kept apart from it on purpose.
///
/// <para>
/// <see cref="INodeIdentity"/> is deliberately closed: it never hands out the private half, because an
/// everyday caller has no business holding it. But an identity that can never leave the silicon it was
/// minted on is not portable — lose the phone and lose the person. These are the operations that move
/// it, and precisely because they touch key material they live behind their own interface: a platform
/// can gate them (a biometric prompt, a device-credential check) without touching the everyday path, and
/// reaching for them is always a deliberate act rather than an accident of calling the wrong method.
/// </para>
///
/// <para>
/// Every route here reproduces the SAME <see cref="AetherNetTag"/>. The tag is a function of the key and
/// these operations move the key unchanged, so the same 32-byte Ed25519 seed derives the same public key
/// and therefore the same tag on any device and in any application. That is what makes "one person, one
/// identity, across every device and app" true rather than aspirational — and it is why the derivation
/// lives in one place: an implementation that reproduces the tag its own way becomes a different person
/// on the mesh, which is exactly the drift this interface exists to end.
/// </para>
/// </summary>
public interface INodeIdentityRecovery
{
    /// <summary>
    /// Adopt a known 32-byte Ed25519 seed as this device's identity, returning the tag it reproduces.
    ///
    /// <para>
    /// The primitive behind every hand-off: a sibling application passing the device's seed to a freshly
    /// installed one, or a new phone receiving the old phone's seed. The transport is the caller's — a
    /// signature-guarded channel between same-signed apps, a scanned code between two phones — this only
    /// installs what it is given, deterministically, and says which tag that seed is.
    /// </para>
    /// </summary>
    /// <param name="seed">The 32-byte Ed25519 seed (the private key) to adopt.</param>
    /// <exception cref="ArgumentNullException"><paramref name="seed"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="seed"/> is not exactly 32 bytes.</exception>
    /// <exception cref="IdentityAlreadyExistsException">
    /// This device already holds an identity. Adoption never overwrites a live one — that would change the
    /// device's address out from under everyone holding the old tag. Replacing it is a separate, explicit
    /// act (clear the existing identity first).
    /// </exception>
    ValueTask<AetherNetTag> AdoptSeedAsync(byte[] seed, CancellationToken cancellationToken = default);

    /// <summary>
    /// This device's identity as a 24-word BIP-39 recovery phrase — the human-writable form a person can
    /// keep on paper and restore from on a new device.
    /// </summary>
    /// <remarks>
    /// Exposes key material by design, which is the whole reason this is not on <see cref="INodeIdentity"/>:
    /// a platform implementation is expected to gate the call behind a fresh device authentication.
    /// </remarks>
    /// <exception cref="NodeIdentityUnavailableException">
    /// The device has an identity that cannot be opened right now — a locked screen, most often.
    /// </exception>
    /// <exception cref="InvalidOperationException">This device has no identity to export yet.</exception>
    ValueTask<string> ExportRecoveryPhraseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restore this device's identity from a BIP-39 recovery phrase, returning the tag it reproduces. The
    /// phrase is decoded to its seed and adopted (see <see cref="AdoptSeedAsync"/>), so the restored tag is
    /// byte-for-byte the one the phrase was exported from.
    /// </summary>
    /// <param name="recoveryPhrase">
    /// A 24-word phrase (256-bit seed) over the BIP-39 English wordlist. Other valid BIP-39 lengths decode
    /// to a seed that is not 32 bytes and are refused — an Ed25519 identity is exactly 24 words.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The phrase is empty, or the decoded seed is not a 32-byte Ed25519 seed.
    /// </exception>
    /// <exception cref="FormatException">
    /// The phrase is not valid BIP-39 (unknown word, wrong word count, or failed checksum) — a mistyped
    /// phrase is refused rather than silently yielding the wrong identity.
    /// </exception>
    /// <exception cref="IdentityAlreadyExistsException">
    /// This device already holds an identity — see <see cref="AdoptSeedAsync"/>.
    /// </exception>
    ValueTask<AetherNetTag> RestoreFromPhraseAsync(string recoveryPhrase, CancellationToken cancellationToken = default);
}

/// <summary>
/// An adopt or restore was asked of a device that already holds an identity. Overwriting it would change
/// the device's address permanently, so it is refused; clearing the existing identity is a separate,
/// deliberate act.
/// </summary>
public sealed class IdentityAlreadyExistsException : Exception
{
    public IdentityAlreadyExistsException(string message) : base(message) { }

    public IdentityAlreadyExistsException(string message, Exception? innerException)
        : base(message, innerException) { }
}
