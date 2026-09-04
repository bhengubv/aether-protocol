// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Security.Privacy;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The panic-wipe feature, wired to this device's real storage.
///
/// <para>
/// A duress PIN is a PIN that, instead of opening the app, quietly destroys it: entering it erases the
/// identity key and every trace of local data, and the phone is left looking freshly installed — for the
/// moment someone forces you to hand it over. The crypto core is <see cref="PanicWipe"/> (recognise the
/// PIN, the manifest of key names); this service owns the app's own storage — the vault entries and the
/// local database — and joins the two.
/// </para>
///
/// <para>
/// <b>How to trigger it.</b> There is deliberately no lock screen — this app has no PIN prompt, and
/// adding one is a product decision (a per-open lock fights the always-on background mesh). The feature
/// does not need one. Whatever surface eventually reads a PIN — a settings lock, a hidden gesture, a
/// test — calls <see cref="TrySubmit"/> with what the person typed: if it is the duress PIN the wipe
/// happens and it returns <c>true</c>, otherwise nothing happens and it returns <c>false</c> so the
/// caller falls through to its normal unlock. That one call is the whole trigger.
/// </para>
/// </summary>
public sealed class PanicWipeService
{
    /// <summary>Where the duress-PIN hash lives — a setting, since the PIN itself is never stored raw.</summary>
    private const string DuressHashKey = "panic.duress_pin_hash";

    private readonly AetherStore _store;
    private readonly ISecretVault _vault;

    public PanicWipeService(AetherStore store, ISecretVault vault)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(vault);
        _store = store;
        _vault = vault;
    }

    /// <summary>Whether a duress PIN has been armed on this device.</summary>
    public bool IsArmed => !string.IsNullOrEmpty(_store.GetSetting(DuressHashKey));

    /// <summary>
    /// Arm the panic wipe: store the SHA-256 of the duress PIN. The PIN is only ever kept as this hash
    /// (see <see cref="PanicWipe.DuressPinHash"/>) — never written in the clear.
    /// </summary>
    public void SetDuressPin(string pin)
    {
        ArgumentException.ThrowIfNullOrEmpty(pin);
        _store.SetSetting(DuressHashKey, Convert.ToHexString(PanicWipe.DuressPinHash(pin)));
    }

    /// <summary>Disarm the panic wipe — forget the duress PIN.</summary>
    public void Disarm() => _store.SetSetting(DuressHashKey, string.Empty);

    /// <summary>
    /// The trigger. Give it whatever PIN the person typed. If a duress PIN is armed and this matches it
    /// (constant-time), the device is wiped and this returns <c>true</c>. Otherwise nothing happens and
    /// it returns <c>false</c> — so the caller can fall through to its normal unlock without a tell.
    /// </summary>
    public bool TrySubmit(string pin)
    {
        if (string.IsNullOrEmpty(pin)) return false;

        var hex = _store.GetSetting(DuressHashKey);
        if (string.IsNullOrEmpty(hex)) return false;

        byte[] stored;
        try { stored = Convert.FromHexString(hex); } catch { return false; }
        if (!PanicWipe.VerifyDuressPin(pin, stored)) return false;

        Wipe();
        return true;
    }

    /// <summary>
    /// Destroy this device's identity and all its local data — the wipe itself. Exposed so a real panic
    /// <i>button</i> (not only a duress PIN) can call it directly. Irreversible.
    /// </summary>
    public void Wipe()
    {
        // The identity key first — the one secret whose loss is the point. Its real name is the app's,
        // not the protocol's canonical list, so both are swept (removing an absent name is a no-op).
        foreach (var name in VaultNodeIdentityStore.IdentityVaultNames) SafeRemove(name);
        foreach (var name in PanicWipe.IdentityKeyNames) SafeRemove(name);
        for (var i = 0; i < PanicWipe.MaxPreKeys; i++)
        {
            SafeRemove(PanicWipe.PreKeyName(i));
            SafeRemove(PanicWipe.SignedPreKeyName(i));
        }

        // Then everything else this phone holds — messages, contacts, sessions, the lot. This also
        // clears the setup flag, so the next launch is the wizard: a fresh install, as promised.
        _store.WipeAll();
    }

    private void SafeRemove(string name)
    {
        try { _vault.Remove(name); }
        catch { /* one stubborn entry must not stop the wipe */ }
    }
}
