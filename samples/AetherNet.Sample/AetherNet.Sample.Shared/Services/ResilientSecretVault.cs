// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// One vault over two stores: the best one this phone has (secure hardware), and the one that always
/// works (an encrypted file).
///
/// <para>
/// Which store is usable is not a fact about the device, it is a fact about <i>this run</i>. A Keystore
/// key can be invalidated by a lock-screen change or an OS update; a reinstall can land somewhere the
/// old key no longer opens. A vault that only looks where it can write today will report that the phone
/// has never had an identity — and the caller, told that, mints a new one. The AetherTag changes, and
/// everyone who added this phone is left holding an address that no longer answers.
/// </para>
///
/// <para>
/// So both stores are always consulted, the secret is read from whichever actually holds it, and a
/// refusal from the store that holds it is passed on rather than papered over. Degrading is allowed;
/// forgetting is not.
/// </para>
/// </summary>
public sealed class ResilientSecretVault : ISecretVault
{
    private readonly ISecretVault _preferred;
    private readonly ISecretVault _durable;

    /// <param name="preferred">The stronger store — hardware-backed where the phone has it.</param>
    /// <param name="durable">The store that works everywhere, and the one that catches what falls.</param>
    public ResilientSecretVault(ISecretVault preferred, ISecretVault durable)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(durable);
        _preferred = preferred;
        _durable = durable;
    }

    /// <summary>
    /// True only while the secret really is in hardware. Once anything has fallen back to the file
    /// store, saying "sealed by secure hardware" would be telling the person something about their own
    /// phone that is not true.
    /// </summary>
    public bool IsHardwareBacked => !HeldByDurable() && _preferred.IsHardwareBacked;

    public string ProtectionDescription =>
        HeldByDurable() ? _durable.ProtectionDescription : _preferred.ProtectionDescription;

    /// <inheritdoc />
    public bool Has(string name) => Holds(_preferred, name) || Holds(_durable, name);

    /// <inheritdoc />
    public byte[]? Get(string name)
    {
        if (Holds(_preferred, name))
        {
            try
            {
                var secret = _preferred.Get(name);
                if (secret is { Length: > 0 }) return secret;
            }
            catch (SecretUnavailableException)
            {
                // The secret is there and sealed shut for now — a locked phone, most often. This has to
                // reach the caller: answering from the other store, or with null, is how a temporary
                // refusal turns into a permanent replacement.
                throw;
            }
            catch (Exception)
            {
                // The store itself is broken rather than merely closed. Fall through to the one that
                // still works — it may be holding the same secret from an earlier run.
            }
        }

        return Holds(_durable, name) ? _durable.Get(name) : null;
    }

    /// <inheritdoc />
    public void Set(string name, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        // Never leave the same name in both stores holding different bytes — that is two identities on
        // one phone, and which one answers depends on which store happens to work that morning.
        if (Holds(_durable, name) && !Holds(_preferred, name))
        {
            _durable.Set(name, secret);
            return;
        }

        try { _preferred.Set(name, secret); }
        catch (Exception) { _durable.Set(name, secret); }
    }

    private bool HeldByDurable() => !Holds(_preferred, DefaultName) && Holds(_durable, DefaultName);

    /// <summary>
    /// The identity key. <see cref="IsHardwareBacked"/> and <see cref="ProtectionDescription"/> are
    /// asked about the device rather than about a named secret, and this is the secret they mean.
    /// </summary>
    private const string DefaultName = "aether.identity.ed25519";

    /// <summary>
    /// Does this store hold it? A store that cannot answer is not a store that says no — an exception
    /// here means "unusable right now", which is the one case where the other store must be tried.
    /// </summary>
    private static bool Holds(ISecretVault vault, string name)
    {
        try { return vault.Has(name); }
        catch (Exception) { return false; }
    }
}
