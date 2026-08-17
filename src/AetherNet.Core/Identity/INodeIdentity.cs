// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;

namespace AetherNet.Identity;

/// <summary>
/// The device's identity on the mesh — one per device, for the life of the device.
///
/// <para>
/// A node's identity is its address, so <b>one device must be one node</b>. An application that mints
/// its own puts a second node on the same handset: a second presence beacon, a second entry in every
/// neighbour's routing table, a second reputation for one person, and a second radio stack contending
/// for hardware that has hard limits on both scanning and advertising. Install fifteen applications
/// built that way and the phone is fifteen peers pretending to be strangers to each other.
/// </para>
///
/// <para>
/// So an application never mints an identity and never holds a key. It asks the node for the device's
/// tag — which is minted on the first ask and adopted by every ask after — and when something must be
/// signed it hands over bytes and receives a signature. This interface deliberately offers no way to
/// obtain the private half; that is the whole point of it, not an oversight.
/// </para>
///
/// <para>
/// The comparison that makes it obvious: an application does not implement TCP/IP and does not own the
/// machine's address. It uses the one the machine already has.
/// </para>
/// </summary>
public interface INodeIdentity
{
    /// <summary>
    /// This device's AetherTag, minting one if the device does not have an identity yet.
    /// <para>
    /// Safe to call from anywhere, at any time, concurrently: a device that already has an identity
    /// always returns that one, and a device that does not gets exactly one no matter how many callers
    /// ask at once.
    /// </para>
    /// </summary>
    /// <exception cref="NodeIdentityUnavailableException">
    /// The device has an identity that cannot be opened right now — a locked screen, most often. Never
    /// a reason to mint a replacement.
    /// </exception>
    ValueTask<AetherNetTag> GetOrMintAsync(CancellationToken cancellationToken = default);

    /// <summary>The public half of this device's identity — publishable, and what the tag derives from.</summary>
    ValueTask<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sign bytes as this device. The caller supplies what is to be signed and receives the signature;
    /// the key stays with the node.
    /// </summary>
    ValueTask<byte[]> SignAsync(byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// A 32-byte key derived from this device's identity for one named purpose.
    ///
    /// <para>
    /// Some things genuinely need key material of their own — the rotating wire address is computed
    /// from one, and it must be secret or the address stops being unlinkable. Handing out the root to
    /// do that would defeat the entire arrangement, so the node hands out a key that is derived from
    /// the root, bound to a purpose, and useless for anything else: knowing the key for one purpose
    /// reveals nothing about the root or about any other purpose's key.
    /// </para>
    ///
    /// <para>Same device and same purpose always give the same key, so it survives restarts.</para>
    /// </summary>
    /// <param name="purpose">
    /// What the key is for, e.g. <c>"erid-routing"</c>. Two callers naming the same purpose share a
    /// key by definition — pick a name that is specific to the use, not to the caller.
    /// </param>
    ValueTask<byte[]> DeriveKeyAsync(string purpose, CancellationToken cancellationToken = default);
}

/// <summary>
/// The one place a device keeps its node identity.
///
/// <para>
/// Implementations are platform-specific and are expected to be gated by the device's own
/// authentication — the lock screen is what protects the identity, because the identity belongs to the
/// device rather than to any application on it. A store that is private to a single application is a
/// misuse of this interface: it produces exactly the per-application identities
/// <see cref="INodeIdentity"/> exists to prevent.
/// </para>
/// </summary>
public interface INodeIdentityStore
{
    /// <summary>
    /// Does this device already hold an identity? Answers "is something stored here", <b>not</b> "can it
    /// be opened right now" — confusing the two is how a device forgets who it is.
    /// </summary>
    bool Exists { get; }

    /// <summary>
    /// The stored private key, or null if this device has never had an identity.
    /// </summary>
    /// <exception cref="NodeIdentityUnavailableException">
    /// An identity is stored but cannot be opened at this moment. Must be thrown rather than returning
    /// null: a caller told "nothing here" will create a replacement, and for an identity that is
    /// destruction rather than recovery.
    /// </exception>
    byte[]? Load();

    /// <summary>Store a newly minted identity. Called once in the life of a device.</summary>
    void Save(byte[] privateKey);
}

/// <summary>
/// The device has an identity and it cannot be read at this moment — a locked screen, a key the
/// platform will not release yet. Always temporary, and never a reason to mint a new one.
/// </summary>
public sealed class NodeIdentityUnavailableException : Exception
{
    public NodeIdentityUnavailableException(string message) : base(message) { }

    public NodeIdentityUnavailableException(string message, Exception? innerException)
        : base(message, innerException) { }
}
