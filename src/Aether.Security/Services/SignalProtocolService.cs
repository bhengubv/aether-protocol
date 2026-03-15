// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Aether.Security.Models;
using Microsoft.Extensions.Logging;

namespace Aether.Security.Services;

/// <summary>
/// Tracks the state of a Signal Protocol session with a single peer.
/// Contains root key, chain keys, counters, and skipped message keys.
/// </summary>
internal sealed class SignalSession
{
    public byte[] RootKey { get; set; } = [];
    public byte[] SendChainKey { get; set; } = [];
    public byte[] RecvChainKey { get; set; } = [];
    public int SendCounter { get; set; }
    public int RecvCounter { get; set; }
    public byte[] RemotePublicKey { get; set; } = [];

    /// <summary>
    /// Skipped message keys indexed by (counter) for out-of-order decryption.
    /// </summary>
    public Dictionary<int, byte[]> SkippedMessageKeys { get; } = new();
}

/// <summary>
/// Signal Protocol implementation providing end-to-end encryption for Aether mesh messaging.
///
/// Key agreement: X3DH with ECDH P-256.
/// Key derivation: HKDF-SHA256 with unique info strings per derivation context.
/// Encryption: AES-256-GCM with 12-byte nonce and 16-byte authentication tag.
/// Signing: Ed25519 via <see cref="Ed25519SigningService"/>.
///
/// The symmetric ratchet advances the chain key with each message sent or received.
/// Out-of-order messages are handled by caching skipped keys (up to MaxSkippedKeys).
/// </summary>
public sealed class SignalProtocolService : ISignalProtocolService
{
    /// <summary>
    /// Maximum number of skipped message keys to retain per session.
    /// If a counter gap exceeds this, the session must be re-established.
    /// </summary>
    public const int MaxSkippedKeys = 1000;

    private const int AesKeySize = 32;
    private const int AesNonceSize = 12;
    private const int AesTagSize = 16;

    private static readonly byte[] HkdfRootInfo = "aether-root-v1"u8.ToArray();
    private static readonly byte[] HkdfChainSendInfo = "aether-chain-send-v1"u8.ToArray();
    private static readonly byte[] HkdfChainRecvInfo = "aether-chain-recv-v1"u8.ToArray();

    private readonly ConcurrentDictionary<string, SignalSession> _sessions = new();
    private readonly ILogger<SignalProtocolService> _logger;

    private byte[] _identityPrivateKey = [];
    private byte[] _identityPublicKey = [];
    private byte[] _ed25519PrivateKey = [];
    private byte[] _ed25519PublicKey = [];

    public SignalProtocolService(ILogger<SignalProtocolService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeIdentityKeys();
    }

    private void InitializeIdentityKeys()
    {
        // ECDH identity key pair (P-256) for key agreement
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ecParams = ecdh.ExportParameters(true);
        _identityPrivateKey = ExportEcdhPrivateKey(ecParams);
        _identityPublicKey = ExportEcdhPublicKey(ecParams);

        // Ed25519 key pair for signing
        (_ed25519PrivateKey, _ed25519PublicKey) = Ed25519SigningService.GenerateKeyPair();
    }

    /// <inheritdoc />
    public bool HasSession(string peerUhid)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        return _sessions.ContainsKey(peerUhid);
    }

    /// <inheritdoc />
    public Task<EncryptedPayload> EncryptAsync(string peerUhid, byte[] plaintext, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(plaintext);

        if (!_sessions.TryGetValue(peerUhid, out var session))
            throw new InvalidOperationException($"No session established with peer {LogSanitizer.SanitizeUhid(peerUhid)}");

        byte[]? messageKey = null;
        try
        {
            // Ratchet the sending chain to derive a message key
            (session.SendChainKey, messageKey) = RatchetChainKey(session.SendChainKey, HkdfChainSendInfo);

            // Encrypt with AES-GCM
            var nonce = RandomNumberGenerator.GetBytes(AesNonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[AesTagSize];

            using var aes = new AesGcm(messageKey, AesTagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            // Combine ciphertext + tag
            var combined = new byte[ciphertext.Length + AesTagSize];
            Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, AesTagSize);

            var counter = session.SendCounter++;

            _logger.LogDebug("Encrypted message for {Peer}, counter={Counter}",
                LogSanitizer.SanitizeUhid(peerUhid), counter);

            return Task.FromResult(new EncryptedPayload(
                Ciphertext: combined,
                Nonce: nonce,
                MessageType: 0,
                SenderUhid: peerUhid,
                Counter: counter));
        }
        finally
        {
            if (messageKey != null)
                CryptographicOperations.ZeroMemory(messageKey);
        }
    }

    /// <inheritdoc />
    public Task<byte[]> DecryptAsync(string peerUhid, EncryptedPayload payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(payload);

        if (!_sessions.TryGetValue(peerUhid, out var session))
            throw new InvalidOperationException($"No session established with peer {LogSanitizer.SanitizeUhid(peerUhid)}");

        byte[]? messageKey = null;
        try
        {
            // Check if this is a skipped message
            if (session.SkippedMessageKeys.TryGetValue(payload.Counter, out var skippedKey))
            {
                session.SkippedMessageKeys.Remove(payload.Counter);
                messageKey = skippedKey;
            }
            else
            {
                // Check for excessive counter gap
                var gap = payload.Counter - session.RecvCounter;
                if (gap > MaxSkippedKeys)
                    throw new CryptographicException(
                        $"Message counter gap ({gap}) exceeds maximum ({MaxSkippedKeys}). Session must be re-established.");

                // Skip ahead and cache intermediate keys
                while (session.RecvCounter < payload.Counter)
                {
                    byte[]? skipKey = null;
                    try
                    {
                        (session.RecvChainKey, skipKey) = RatchetChainKey(session.RecvChainKey, HkdfChainRecvInfo);
                        session.SkippedMessageKeys[session.RecvCounter] = skipKey;
                        skipKey = null; // Ownership transferred to dictionary
                        session.RecvCounter++;
                    }
                    finally
                    {
                        if (skipKey != null)
                            CryptographicOperations.ZeroMemory(skipKey);
                    }
                }

                // Derive the actual message key
                (session.RecvChainKey, messageKey) = RatchetChainKey(session.RecvChainKey, HkdfChainRecvInfo);
                session.RecvCounter++;
            }

            // Decrypt with AES-GCM
            if (payload.Ciphertext.Length < AesTagSize)
                throw new CryptographicException("Ciphertext too short.");

            var ciphertextLength = payload.Ciphertext.Length - AesTagSize;
            var ciphertext = payload.Ciphertext.AsSpan(0, ciphertextLength);
            var tag = payload.Ciphertext.AsSpan(ciphertextLength, AesTagSize);
            var plaintext = new byte[ciphertextLength];

            using var aes = new AesGcm(messageKey, AesTagSize);
            aes.Decrypt(payload.Nonce, ciphertext, tag, plaintext);

            _logger.LogDebug("Decrypted message from {Peer}, counter={Counter}",
                LogSanitizer.SanitizeUhid(peerUhid), payload.Counter);

            return Task.FromResult(plaintext);
        }
        finally
        {
            if (messageKey != null)
                CryptographicOperations.ZeroMemory(messageKey);
        }
    }

    /// <inheritdoc />
    public Task<PreKeyBundle> GeneratePreKeyBundleAsync(string localUhid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(localUhid);

        // Generate one-time pre-key (ECDH P-256)
        using var preKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var preKeyPublic = ExportEcdhPublicKey(preKeyEcdh.ExportParameters(false));
        var preKeyId = RandomNumberGenerator.GetInt32(1, int.MaxValue);

        // Generate signed pre-key (ECDH P-256)
        using var signedPreKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signedPreKeyPublic = ExportEcdhPublicKey(signedPreKeyEcdh.ExportParameters(false));
        var signedPreKeyId = RandomNumberGenerator.GetInt32(1, int.MaxValue);

        // Sign the signed pre-key with our Ed25519 identity key
        var signature = Ed25519SigningService.Sign(_ed25519PrivateKey, signedPreKeyPublic);

        var bundle = new PreKeyBundle(
            Uhid: localUhid,
            IdentityKey: (byte[])_ed25519PublicKey.Clone(),
            PreKeyId: preKeyId,
            PreKey: preKeyPublic,
            SignedPreKeyId: signedPreKeyId,
            SignedPreKey: signedPreKeyPublic,
            SignedPreKeySignature: signature);

        _logger.LogDebug("Generated pre-key bundle for {Uhid}", LogSanitizer.SanitizeUhid(localUhid));

        return Task.FromResult(bundle);
    }

    /// <inheritdoc />
    public Task ProcessPreKeyBundleAsync(PreKeyBundle bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        // Verify the signed pre-key signature
        if (!Ed25519SigningService.Verify(bundle.IdentityKey, bundle.SignedPreKey, bundle.SignedPreKeySignature))
            throw new CryptographicException("Signed pre-key signature verification failed.");

        byte[]? sharedSecret = null;
        byte[]? rootKey = null;
        byte[]? sendChainKey = null;
        byte[]? recvChainKey = null;

        try
        {
            // X3DH key agreement: DH(our identity, their signed pre-key) || DH(our identity, their pre-key)
            sharedSecret = PerformX3DH(bundle.SignedPreKey, bundle.PreKey);

            // Derive root key and initial chain keys using HKDF
            rootKey = DeriveKey(sharedSecret, HkdfRootInfo);
            sendChainKey = DeriveKey(rootKey, HkdfChainSendInfo);
            recvChainKey = DeriveKey(rootKey, HkdfChainRecvInfo);

            var session = new SignalSession
            {
                RootKey = rootKey,
                SendChainKey = sendChainKey,
                RecvChainKey = recvChainKey,
                RemotePublicKey = (byte[])bundle.IdentityKey.Clone()
            };

            // Ownership of key arrays transferred to session
            rootKey = null;
            sendChainKey = null;
            recvChainKey = null;

            _sessions[bundle.Uhid] = session;

            _logger.LogDebug("Established session with {Peer}", LogSanitizer.SanitizeUhid(bundle.Uhid));

            return Task.CompletedTask;
        }
        finally
        {
            if (sharedSecret != null) CryptographicOperations.ZeroMemory(sharedSecret);
            if (rootKey != null) CryptographicOperations.ZeroMemory(rootKey);
            if (sendChainKey != null) CryptographicOperations.ZeroMemory(sendChainKey);
            if (recvChainKey != null) CryptographicOperations.ZeroMemory(recvChainKey);
        }
    }

    /// <inheritdoc />
    public Task<byte[]> SignDataAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var signature = Ed25519SigningService.Sign(_ed25519PrivateKey, data);
        return Task.FromResult(signature);
    }

    /// <inheritdoc />
    public bool VerifySignature(byte[] publicKey, byte[] data, byte[] signature)
    {
        return Ed25519SigningService.Verify(publicKey, data, signature);
    }

    /// <summary>
    /// Gets a copy of the Ed25519 public key for this node.
    /// </summary>
    public byte[] GetPublicKey() => (byte[])_ed25519PublicKey.Clone();

    /// <summary>
    /// Performs X3DH key agreement using our identity key against
    /// the remote signed pre-key and one-time pre-key.
    /// </summary>
    private byte[] PerformX3DH(byte[] remoteSignedPreKey, byte[] remotePreKey)
    {
        using var localEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        localEcdh.ImportParameters(ImportEcdhPrivateKey(_identityPrivateKey, _identityPublicKey));

        // DH1: identity <-> signed pre-key
        byte[] dh1;
        using (var remotePk1 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        {
            remotePk1.ImportParameters(ImportEcdhPublicKey(remoteSignedPreKey));
            dh1 = localEcdh.DeriveRawSecretAgreement(remotePk1.PublicKey);
        }

        // DH2: identity <-> one-time pre-key
        byte[] dh2;
        using (var remotePk2 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
        {
            remotePk2.ImportParameters(ImportEcdhPublicKey(remotePreKey));
            dh2 = localEcdh.DeriveRawSecretAgreement(remotePk2.PublicKey);
        }

        // Concatenate DH results
        var combined = new byte[dh1.Length + dh2.Length];
        try
        {
            Buffer.BlockCopy(dh1, 0, combined, 0, dh1.Length);
            Buffer.BlockCopy(dh2, 0, combined, dh1.Length, dh2.Length);
            return combined;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dh1);
            CryptographicOperations.ZeroMemory(dh2);
        }
    }

    /// <summary>
    /// Derives a 32-byte key from input key material using HKDF-SHA256.
    /// </summary>
    private static byte[] DeriveKey(byte[] inputKeyMaterial, byte[] info)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, AesKeySize, info: info);
    }

    /// <summary>
    /// Advances a chain key by one step, returning the new chain key and message key.
    /// Uses HKDF with the appropriate info string.
    /// </summary>
    private static (byte[] NewChainKey, byte[] MessageKey) RatchetChainKey(byte[] chainKey, byte[] info)
    {
        // Derive message key from current chain key
        var messageKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, chainKey, AesKeySize, info: info, salt: [0x01]);

        // Advance chain key
        var newChainKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, chainKey, AesKeySize, info: info, salt: [0x02]);

        return (newChainKey, messageKey);
    }

    /// <summary>
    /// Exports an ECDH P-256 public key as uncompressed point (65 bytes: 0x04 || X || Y).
    /// </summary>
    private static byte[] ExportEcdhPublicKey(ECParameters ecParams)
    {
        var result = new byte[65];
        result[0] = 0x04; // Uncompressed point marker
        Buffer.BlockCopy(ecParams.Q.X!, 0, result, 1, 32);
        Buffer.BlockCopy(ecParams.Q.Y!, 0, result, 33, 32);
        return result;
    }

    /// <summary>
    /// Exports an ECDH P-256 private key as raw D parameter (32 bytes).
    /// </summary>
    private static byte[] ExportEcdhPrivateKey(ECParameters ecParams)
    {
        return (byte[])ecParams.D!.Clone();
    }

    /// <summary>
    /// Imports an uncompressed P-256 public key (65 bytes) into ECParameters.
    /// </summary>
    private static ECParameters ImportEcdhPublicKey(byte[] publicKey)
    {
        if (publicKey.Length != 65 || publicKey[0] != 0x04)
            throw new CryptographicException("Invalid uncompressed P-256 public key format.");

        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = publicKey[1..33],
                Y = publicKey[33..65]
            }
        };
    }

    /// <summary>
    /// Imports P-256 private + public key material into ECParameters.
    /// </summary>
    private static ECParameters ImportEcdhPrivateKey(byte[] privateKey, byte[] publicKey)
    {
        var ecParams = ImportEcdhPublicKey(publicKey);
        ecParams.D = (byte[])privateKey.Clone();
        return ecParams;
    }
}
