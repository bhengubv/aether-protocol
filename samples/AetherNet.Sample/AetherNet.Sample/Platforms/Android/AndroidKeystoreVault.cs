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
    private readonly bool _hardware;
    private readonly FileSecretVault? _fallback;

    public AndroidKeystoreVault(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        Directory.CreateDirectory(_directory);
        _hardware = TryEnsureKey();
        // A phone with no usable Keystore (or one that later invalidates the key) must still be able to
        // keep an identity — degrade to the encrypted-file vault rather than losing the tag on restart.
        _fallback = _hardware ? null : new FileSecretVault(Path.Combine(_directory, "fallback"));
    }

    public bool IsHardwareBacked => _hardware;

    public string ProtectionDescription => _hardware
        ? "Sealed by this phone's secure hardware"
        : "Encrypted on this device";

    public byte[]? Get(string name)
    {
        if (_fallback is not null) return _fallback.Get(name);

        var path = PathFor(name);
        if (!File.Exists(path)) return null;

        var blob = File.ReadAllBytes(path);
        if (blob.Length <= NonceSize) return null;

        try
        {
            var key = LoadKey();
            if (key is null) return null;

            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, new GCMParameterSpec(TagBits, blob[..NonceSize]));
            return cipher.DoFinal(blob[NonceSize..]);
        }
        catch (Java.Lang.Exception)
        {
            // Key invalidated (e.g. the user removed their lock screen) or blob tampered.
            return null;
        }
    }

    public void Set(string name, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (_fallback is not null)
        {
            _fallback.Set(name, secret);
            return;
        }

        var key = LoadKey() ?? throw new InvalidOperationException("Keystore identity key unavailable.");
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);

        var nonce = cipher.GetIV() ?? throw new InvalidOperationException("Keystore produced no IV.");
        var sealedBytes = cipher.DoFinal(secret) ?? throw new InvalidOperationException("Keystore encryption failed.");

        var blob = new byte[nonce.Length + sealedBytes.Length];
        nonce.CopyTo(blob.AsSpan(0));
        sealedBytes.CopyTo(blob.AsSpan(nonce.Length));

        var path = PathFor(name);
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

            // API 28+: the key refuses to operate while the screen is locked, so the identity is only
            // usable once the human has actually unlocked the phone.
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.P)
                builder = builder.SetUnlockedDeviceRequired(true)!;

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

    private string PathFor(string name) =>
        Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))) + ".sealed");
}
#endif
