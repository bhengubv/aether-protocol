// SPDX-License-Identifier: MIT
#if ANDROID
using System.Security.Cryptography;
using Android.Security.Keystore;
using AetherNet.Sample.Shared.Services;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Hardware-backed vault for the device's identity key. An AES-256-GCM key is generated inside the
/// <b>Android Keystore</b> and never leaves it — the OS will not export it, so even a full copy of the
/// app's data directory yields a sealed blob and nothing else. That key wraps the Ed25519 private key
/// behind your AetherTag.
///
/// On API 28+ the key is marked <c>setUnlockedDeviceRequired(true)</c>: it will not decrypt while the
/// phone is locked, so the identity is released only after the user has unlocked the device with
/// whatever they use — fingerprint, face, or PIN. (A per-open biometric *prompt* is the separate
/// app-lock surface; this is the key-level gate.)
/// </summary>
public sealed class AndroidKeystoreVault : ISecretVault
{
    private const string AndroidKeyStore = "AndroidKeyStore";
    private const string KeyAlias = "aether.identity.v1";
    private const int NonceSize = 12;
    private const int TagBits = 128;

    private readonly string _directory;
    private readonly ResilientSecretVault _vault;

    public AndroidKeystoreVault(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        Directory.CreateDirectory(_directory);
        TryEnsureKey();

        // A phone with no usable Keystore must still be able to keep an identity, so an encrypted-file
        // vault sits behind this one. Both are always consulted: whether the Keystore works is a fact
        // about this run, not about the device, and a vault that only looks where it can write today
        // will report "no identity here" about a phone that has one — after which the caller mints a
        // replacement and the AetherTag changes for good.
        _vault = new ResilientSecretVault(
            new KeystoreSealedStore(_directory),
            new FileSecretVault(Path.Combine(_directory, "fallback")));
    }

    public bool IsHardwareBacked => _vault.IsHardwareBacked;

    public string ProtectionDescription => _vault.ProtectionDescription;

    /// <inheritdoc />
    public byte[]? Get(string name) => _vault.Get(name);

    /// <inheritdoc />
    public bool Has(string name) => _vault.Has(name);

    /// <inheritdoc />
    public void Set(string name, byte[] secret) => _vault.Set(name, secret);

    /// <inheritdoc />
    public void Remove(string name) => _vault.Remove(name);

    /// <summary>
    /// The Keystore half on its own: a sealed blob on disk, opened by a key the OS will not export.
    /// </summary>
    private sealed class KeystoreSealedStore(string directory) : ISecretVault
    {
        public bool IsHardwareBacked => true;
        public string ProtectionDescription => "Sealed by this phone's secure hardware";

        /// <summary>
        /// Is a sealed blob stored here — <b>not</b> "can it be opened right now". Those are different
        /// questions, and answering the second one here is what lets a phone forget who it is.
        /// </summary>
        public bool Has(string name) => File.Exists(PathFor(directory, name));

        public byte[]? Get(string name) => ReadSealed(directory, name);

        public void Set(string name, byte[] secret) => WriteSealed(directory, name, secret);

        public void Remove(string name)
        {
            var path = PathFor(directory, name);
            if (!File.Exists(path)) return;
            // Overwrite the sealed blob before unlinking; the Keystore wrapping key stays (it seals
            // nothing now) so other entries keep working — the identity blob itself is what's destroyed.
            try { var len = (int)new FileInfo(path).Length; if (len > 0) File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(len)); }
            catch { /* best-effort scrub */ }
            try { File.Delete(path); } catch { /* the wipe carries on */ }
        }
    }

    private static byte[]? ReadSealed(string directory, string name)
    {
        var path = PathFor(directory, name);
        if (!File.Exists(path)) return null;

        var blob = File.ReadAllBytes(path);
        if (blob.Length <= NonceSize) return null;

        try
        {
            // The blob is right here, so an identity exists. A missing key means it cannot be opened
            // this run — never that there is nothing to open.
            var key = LoadKey()
                ?? throw new SecretUnavailableException("The key that seals this phone's identity is not available.");

            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, new GCMParameterSpec(TagBits, blob[..NonceSize]));
            return cipher.DoFinal(blob[NonceSize..]);
        }
        catch (Java.Lang.Exception ex)
        {
            // A sealed identity exists — we simply cannot open it this instant. The commonest reason
            // is the screen being locked, because the key is deliberately marked unusable until the
            // phone is unlocked. Returning null here would be a lie with permanent consequences: the
            // caller reads "no identity yet", mints a new keypair, writes it over this one, and the
            // person's AetherTag changes for good. Say "not now", never "not there".
            throw new SecretUnavailableException(
                "The identity is sealed and cannot be opened while the phone is locked.", ex);
        }
    }

    private static void WriteSealed(string directory, string name, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var key = LoadKey() ?? throw new InvalidOperationException("Keystore identity key unavailable.");
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);

        var nonce = cipher.GetIV() ?? throw new InvalidOperationException("Keystore produced no IV.");
        var sealedBytes = cipher.DoFinal(secret) ?? throw new InvalidOperationException("Keystore encryption failed.");

        var blob = new byte[nonce.Length + sealedBytes.Length];
        nonce.CopyTo(blob.AsSpan(0));
        sealedBytes.CopyTo(blob.AsSpan(nonce.Length));

        var path = PathFor(directory, name);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, blob);
        File.Move(temp, path, overwrite: true);
    }

    // ── Keystore plumbing ───────────────────────────────────────────────────────

    private static IKey? LoadKey()
    {
        var store = KeyStore.GetInstance(AndroidKeyStore)!;
        store.Load(null);
        return store.GetKey(KeyAlias, null);
    }

    private static bool TryEnsureKey()
    {
        try
        {
            var store = KeyStore.GetInstance(AndroidKeyStore)!;
            store.Load(null);
            if (store.ContainsAlias(KeyAlias)) return true;

            var builder = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm!)!
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone!)!
                .SetKeySize(256)!;

            // Deliberately NOT SetUnlockedDeviceRequired / SetUserAuthenticationRequired.
            //
            // The identity is read the instant the app starts, before there is an Activity to prompt
            // with — and on a mesh it is read again by background work holding a radio with the screen
            // off. A key that refuses to operate unless the device is unlocked turns both of those into
            // a failure that cannot be recovered from in place: the node correctly refuses to mint over
            // a sealed identity it cannot open, so the app simply never starts. Watched on a P30 Lite
            // on 2026-08-15 — first run sealed the identity, every run after that could not open it.
            //
            // What still protects it: the key is generated inside the Keystore and never leaves, so a
            // copy of the app's data directory yields a sealed blob and nothing else. The device's own
            // lock screen protects the device, which is the boundary the identity belongs to anyway.

            var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes!, AndroidKeyStore)!;
            generator.Init(builder.Build());
            generator.GenerateKey();
            return true;
        }
        catch (Java.Lang.Exception ex)
        {
            global::Android.Util.Log.Warn("AetherVault", $"Keystore unavailable, falling back: {ex.Message}");
            return false;
        }
    }

    private static string PathFor(string directory, string name) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))) + ".sealed");
}
#endif
