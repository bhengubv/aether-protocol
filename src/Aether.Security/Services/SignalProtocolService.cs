// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Aether.Security.Models;
using Microsoft.Extensions.Logging;

namespace Aether.Security.Services;

/// <summary>
/// Tracks the state of a Signal Protocol session with a single peer.
/// Contains root key, chain keys, counters, and skipped message keys.
///
/// On the initiator side (we processed the peer's pre-key bundle), the
/// pending PreKey-message metadata is retained until the first message
/// is sent — that first message carries our X25519 identity key, our fresh
/// ephemeral public key, and the bundle ids we consumed, so the responder
/// can run X3DH on its side to derive the same root key.
/// </summary>
internal sealed class SignalSession
{
    public byte[] RootKey { get; set; } = [];
    public byte[] SendChainKey { get; set; } = [];
    public byte[] RecvChainKey { get; set; } = [];
    public int SendCounter { get; set; }
    public int RecvCounter { get; set; }

    /// <summary>
    /// Skipped message keys indexed by counter for out-of-order decryption.
    /// </summary>
    public Dictionary<int, byte[]> SkippedMessageKeys { get; } = new();

    /// <summary>
    /// True iff this session was established in the initiator role and the
    /// first outbound message has not yet been sent. While true, the next
    /// <see cref="SignalProtocolService.EncryptAsync"/> emits a PreKey
    /// message (MessageType=1) carrying the fields below.
    /// </summary>
    public bool PendingPreKeyMessage { get; set; }
    public byte[] InitiatorIdentityKeyX25519 { get; set; } = [];
    public byte[] InitiatorEphemeralKeyX25519 { get; set; } = [];
    public int UsedSignedPreKeyId { get; set; }
    public int UsedOneTimePreKeyId { get; set; }
}

/// <summary>
/// Pre-key state held by the responder side: signed pre-key (rotated
/// periodically) and a pool of one-time pre-keys (each consumed exactly
/// once). The private halves stay on the responder so that when a
/// PreKey message arrives, the matching X3DH DHs can be computed.
/// </summary>
internal sealed class PreKeyState
{
    public int SignedPreKeyId { get; set; }
    public byte[] SignedPreKeyPriv { get; set; } = [];
    public byte[] SignedPreKeyPub { get; set; } = [];
    public byte[] SignedPreKeySignature { get; set; } = [];

    /// <summary>One-time pre-keys keyed by id. Removed and zeroed on consumption.</summary>
    public Dictionary<int, (byte[] Priv, byte[] Pub)> OneTimePreKeys { get; } = new();
}

/// <summary>
/// Signal Protocol implementation providing end-to-end encryption for Aether mesh messaging.
///
/// Key agreement: X3DH (Signal Protocol §3) over X25519 (RFC 7748). Four DHs:
///   <list type="bullet">
///     <item>DH1 = DH(IK_A, SPK_B) — long-term mutual authentication</item>
///     <item>DH2 = DH(EK_A, IK_B) — initiator ephemeral binds to responder identity</item>
///     <item>DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key</item>
///     <item>DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key (forward secrecy)</item>
///   </list>
/// Root-key derivation: HKDF-SHA256 over the concatenation DH1||DH2||DH3||DH4.
/// Symmetric ratchet: HMAC-SHA256 with single-byte domain separation
///   (0x01 → message key, 0x02 → next chain key) per Signal Double-Ratchet §5.1.
/// Encryption: AES-256-GCM with 12-byte nonce and 16-byte authentication tag.
/// Identity signing: Ed25519 via <see cref="Ed25519SigningService"/>.
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

    /// <summary>
    /// Ratchet domain-separation bytes per the Signal Double-Ratchet spec
    /// (§5.1). 0x01 yields a per-message key, 0x02 yields the next chain key.
    /// </summary>
    private static readonly byte[] RatchetMessageKeyInput = [0x01];
    private static readonly byte[] RatchetChainKeyInput = [0x02];

    /// <summary>
    /// HKDF info strings for X3DH session establishment. The SAME info
    /// strings are used on initiator and responder sides; the responder
    /// SWAPS send/recv assignment so the initiator's send chain matches
    /// the responder's recv chain (and vice versa).
    /// </summary>
    private static readonly byte[] HkdfRootInfo = "aether-x3dh-root-v1"u8.ToArray();
    private static readonly byte[] HkdfChainInitiatorSendInfo = "aether-chain-initiator-send-v1"u8.ToArray();
    private static readonly byte[] HkdfChainInitiatorRecvInfo = "aether-chain-initiator-recv-v1"u8.ToArray();

    private readonly ConcurrentDictionary<string, SignalSession> _sessions = new();
    private readonly ILogger<SignalProtocolService> _logger;

    // Long-term identity keys — two distinct keypairs per node.
    // X25519 for ECDH (X3DH); Ed25519 for signing. We keep them separate
    // rather than using XEdDSA, which adds complexity without standard-library
    // support across the 8-language family.
    private byte[] _identityX25519Priv = [];
    private byte[] _identityX25519Pub = [];
    private byte[] _ed25519PrivateKey = [];
    private byte[] _ed25519PublicKey = [];

    // Local UHID — captured when GeneratePreKeyBundleAsync is called or set
    // explicitly via SetLocalUhid. Used as the SenderUhid on outbound
    // EncryptedPayloads so the receiver can attribute the message correctly.
    private string? _localUhid;

    // Pre-key state held for responder-side X3DH. When a PreKey message
    // arrives, EstablishResponderSession looks up the SPK + OPK private
    // halves by id, runs the mirrored DHs, and consumes the OPK.
    private readonly PreKeyState _preKeyState = new();

    public SignalProtocolService(ILogger<SignalProtocolService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeIdentityKeys();
    }

    private void InitializeIdentityKeys()
    {
        // X25519 long-term identity for X3DH ECDH.
        (_identityX25519Priv, _identityX25519Pub) = X25519Service.GenerateKeyPair();

        // Ed25519 long-term identity for signing.
        (_ed25519PrivateKey, _ed25519PublicKey) = Ed25519SigningService.GenerateKeyPair();
    }

    /// <summary>
    /// Sets the local node's UHID. Required before any
    /// <see cref="EncryptAsync"/> call so the SenderUhid is correctly
    /// stamped. Called automatically by <see cref="GeneratePreKeyBundleAsync"/>;
    /// expose this for nodes that initiate without first publishing a bundle.
    /// </summary>
    public void SetLocalUhid(string localUhid)
    {
        ArgumentException.ThrowIfNullOrEmpty(localUhid);
        _localUhid = localUhid;
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
            throw new InvalidOperationException(
                $"No session established with peer {LogSanitizer.SanitizeUhid(peerUhid)}");

        var senderUhid = _localUhid ?? throw new InvalidOperationException(
            "Local UHID is not set. Call GeneratePreKeyBundleAsync(localUhid) " +
            "or SetLocalUhid(localUhid) before encrypting.");

        byte[]? messageKey = null;
        try
        {
            // Advance the sending chain to derive a fresh per-message key.
            (session.SendChainKey, messageKey) = RatchetChainKey(session.SendChainKey);

            var nonce = RandomNumberGenerator.GetBytes(AesNonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[AesTagSize];

            using var aes = new AesGcm(messageKey, AesTagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            // Wire format: ciphertext || tag (combined). The tag is split
            // back out on decryption.
            var combined = new byte[ciphertext.Length + AesTagSize];
            Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, AesTagSize);

            var counter = session.SendCounter++;

            // PreKey message? First message after initiator-side X3DH —
            // carries our X3DH inputs so the responder can mirror the DHs
            // and arrive at the same root key.
            if (session.PendingPreKeyMessage)
            {
                var preKeyPayload = new EncryptedPayload(
                    Ciphertext: combined,
                    Nonce: nonce,
                    MessageType: 1,
                    SenderUhid: senderUhid,
                    Counter: counter,
                    InitiatorIdentityKeyX25519: (byte[])session.InitiatorIdentityKeyX25519.Clone(),
                    InitiatorEphemeralKeyX25519: (byte[])session.InitiatorEphemeralKeyX25519.Clone(),
                    UsedSignedPreKeyId: session.UsedSignedPreKeyId,
                    UsedOneTimePreKeyId: session.UsedOneTimePreKeyId);

                session.PendingPreKeyMessage = false;

                _logger.LogDebug("Encrypted PreKey message for {Peer}, counter={Counter}",
                    LogSanitizer.SanitizeUhid(peerUhid), counter);

                return Task.FromResult(preKeyPayload);
            }

            _logger.LogDebug("Encrypted message for {Peer}, counter={Counter}",
                LogSanitizer.SanitizeUhid(peerUhid), counter);

            return Task.FromResult(new EncryptedPayload(
                Ciphertext: combined,
                Nonce: nonce,
                MessageType: 0,
                SenderUhid: senderUhid,
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

        // PreKey message? Establish (or replace) the responder-side session
        // BEFORE attempting decryption. The same byte stream that the
        // initiator encrypted is now decryptable because the mirrored X3DH
        // produces the same root key.
        if (payload.MessageType == 1)
        {
            if (payload.InitiatorIdentityKeyX25519 == null
                || payload.InitiatorEphemeralKeyX25519 == null)
                throw new CryptographicException(
                    "PreKey message missing initiator key material " +
                    "(InitiatorIdentityKeyX25519 / InitiatorEphemeralKeyX25519).");
            EstablishResponderSession(peerUhid, payload);
        }

        if (!_sessions.TryGetValue(peerUhid, out var session))
            throw new InvalidOperationException(
                $"No session established with peer {LogSanitizer.SanitizeUhid(peerUhid)}");

        byte[]? messageKey = null;
        try
        {
            // Out-of-order? Pull the cached key.
            if (session.SkippedMessageKeys.TryGetValue(payload.Counter, out var skippedKey))
            {
                session.SkippedMessageKeys.Remove(payload.Counter);
                messageKey = skippedKey;
            }
            else
            {
                // Counter ahead of expected? Cache intermediate keys (up to
                // the bound) so a later out-of-order delivery of one of
                // those still works.
                var gap = payload.Counter - session.RecvCounter;
                if (gap > MaxSkippedKeys)
                    throw new CryptographicException(
                        $"Message counter gap ({gap}) exceeds maximum ({MaxSkippedKeys}). " +
                        "Session must be re-established.");

                while (session.RecvCounter < payload.Counter)
                {
                    byte[]? skipKey = null;
                    try
                    {
                        (session.RecvChainKey, skipKey) = RatchetChainKey(session.RecvChainKey);
                        session.SkippedMessageKeys[session.RecvCounter] = skipKey;
                        skipKey = null; // Ownership transferred to dictionary.
                        session.RecvCounter++;
                    }
                    finally
                    {
                        if (skipKey != null)
                            CryptographicOperations.ZeroMemory(skipKey);
                    }
                }

                // Derive the message key for the expected counter.
                (session.RecvChainKey, messageKey) = RatchetChainKey(session.RecvChainKey);
                session.RecvCounter++;
            }

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
        _localUhid = localUhid;

        // One-time pre-key (X25519). We retain the private half so we can
        // run our side of X3DH when an initiator consumes this id.
        var (otpkPriv, otpkPub) = X25519Service.GenerateKeyPair();
        var preKeyId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        _preKeyState.OneTimePreKeys[preKeyId] = (otpkPriv, otpkPub);

        // Signed pre-key (X25519) — also keep the private half. The
        // signature is over the X25519 public key bytes, signed by our
        // long-term Ed25519 identity key.
        var (spkPriv, spkPub) = X25519Service.GenerateKeyPair();
        var signedPreKeyId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var signature = Ed25519SigningService.Sign(_ed25519PrivateKey, spkPub);

        _preKeyState.SignedPreKeyId = signedPreKeyId;
        _preKeyState.SignedPreKeyPriv = spkPriv;
        _preKeyState.SignedPreKeyPub = spkPub;
        _preKeyState.SignedPreKeySignature = signature;

        var bundle = new PreKeyBundle(
            Uhid: localUhid,
            IdentityKey: (byte[])_ed25519PublicKey.Clone(),
            IdentityKeyX25519: (byte[])_identityX25519Pub.Clone(),
            PreKeyId: preKeyId,
            PreKey: (byte[])otpkPub.Clone(),
            SignedPreKeyId: signedPreKeyId,
            SignedPreKey: (byte[])spkPub.Clone(),
            SignedPreKeySignature: signature);

        _logger.LogDebug("Generated pre-key bundle for {Uhid} (SPK id {Spk}, OPK id {Opk})",
            LogSanitizer.SanitizeUhid(localUhid), signedPreKeyId, preKeyId);

        return Task.FromResult(bundle);
    }

    /// <summary>
    /// Establishes an initiator-side session against the supplied pre-key
    /// bundle via X3DH (Signal §3): generates a fresh ephemeral X25519
    /// keypair, computes the four DH operations, derives the root key
    /// via HKDF-SHA256, and primes the symmetric ratchet.
    ///
    /// The first <see cref="EncryptAsync"/> after this returns a PreKey
    /// message (MessageType=1) carrying the initiator's X25519 identity
    /// key, ephemeral public key, and the bundle ids consumed — the
    /// responder uses these to compute the same root key on its side.
    /// </summary>
    public Task ProcessPreKeyBundleAsync(PreKeyBundle bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        // The signed pre-key signature is over the SPK *X25519* public key
        // bytes, signed by the bundle owner's *Ed25519* identity key.
        if (!Ed25519SigningService.Verify(bundle.IdentityKey, bundle.SignedPreKey, bundle.SignedPreKeySignature))
            throw new CryptographicException("Signed pre-key signature verification failed.");

        if (bundle.IdentityKeyX25519.Length != X25519Service.PublicKeySize)
            throw new CryptographicException(
                $"Pre-key bundle has malformed X25519 identity key (length {bundle.IdentityKeyX25519.Length}, expected {X25519Service.PublicKeySize}).");
        if (bundle.SignedPreKey.Length != X25519Service.PublicKeySize)
            throw new CryptographicException(
                $"Pre-key bundle has malformed signed pre-key (length {bundle.SignedPreKey.Length}, expected {X25519Service.PublicKeySize}).");
        if (bundle.PreKey.Length != X25519Service.PublicKeySize)
            throw new CryptographicException(
                $"Pre-key bundle has malformed one-time pre-key (length {bundle.PreKey.Length}, expected {X25519Service.PublicKeySize}).");

        // Fresh ephemeral X25519 keypair, generated per-session per Signal §3.3.
        var (ephemeralPriv, ephemeralPub) = X25519Service.GenerateKeyPair();

        byte[]? dh1 = null, dh2 = null, dh3 = null, dh4 = null;
        byte[]? sharedSecret = null;
        byte[]? rootKey = null;
        byte[]? sendChainKey = null;
        byte[]? recvChainKey = null;

        try
        {
            // X3DH 4-DH key agreement (Signal §3.3 — initiator side):
            //   DH1 = DH(IK_A, SPK_B)  binds initiator's long-term identity to peer's signed pre-key (mutual auth)
            //   DH2 = DH(EK_A, IK_B)   binds initiator's ephemeral to peer's long-term identity (auth)
            //   DH3 = DH(EK_A, SPK_B)  binds initiator's ephemeral to peer's signed pre-key (FS)
            //   DH4 = DH(EK_A, OPK_B)  binds initiator's ephemeral to peer's one-time pre-key (FS)
            dh1 = X25519Service.Agree(_identityX25519Priv, bundle.SignedPreKey);
            dh2 = X25519Service.Agree(ephemeralPriv, bundle.IdentityKeyX25519);
            dh3 = X25519Service.Agree(ephemeralPriv, bundle.SignedPreKey);
            dh4 = X25519Service.Agree(ephemeralPriv, bundle.PreKey);

            sharedSecret = ConcatBytes(dh1, dh2, dh3, dh4);
            rootKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, AesKeySize, info: HkdfRootInfo);
            sendChainKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, rootKey, AesKeySize, info: HkdfChainInitiatorSendInfo);
            recvChainKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, rootKey, AesKeySize, info: HkdfChainInitiatorRecvInfo);

            var session = new SignalSession
            {
                RootKey = rootKey,
                SendChainKey = sendChainKey,
                RecvChainKey = recvChainKey,
                PendingPreKeyMessage = true,
                InitiatorIdentityKeyX25519 = (byte[])_identityX25519Pub.Clone(),
                InitiatorEphemeralKeyX25519 = ephemeralPub,
                UsedSignedPreKeyId = bundle.SignedPreKeyId,
                UsedOneTimePreKeyId = bundle.PreKeyId
            };

            // Ownership transferred to session — null out so finally doesn't zero them.
            rootKey = null;
            sendChainKey = null;
            recvChainKey = null;

            _sessions[bundle.Uhid] = session;

            _logger.LogDebug("Established initiator session with {Peer} via X3DH (4 DHs, X25519)",
                LogSanitizer.SanitizeUhid(bundle.Uhid));

            return Task.CompletedTask;
        }
        finally
        {
            if (dh1 != null) CryptographicOperations.ZeroMemory(dh1);
            if (dh2 != null) CryptographicOperations.ZeroMemory(dh2);
            if (dh3 != null) CryptographicOperations.ZeroMemory(dh3);
            if (dh4 != null) CryptographicOperations.ZeroMemory(dh4);
            if (sharedSecret != null) CryptographicOperations.ZeroMemory(sharedSecret);
            if (rootKey != null) CryptographicOperations.ZeroMemory(rootKey);
            if (sendChainKey != null) CryptographicOperations.ZeroMemory(sendChainKey);
            if (recvChainKey != null) CryptographicOperations.ZeroMemory(recvChainKey);
            CryptographicOperations.ZeroMemory(ephemeralPriv);
        }
    }

    /// <summary>
    /// Establishes the responder-side session when a PreKey message arrives.
    /// Mirrors the initiator's 4 X3DH DHs (X25519 ECDH is commutative, so
    /// each pair of mirrored DHs yields the same shared secret) and derives
    /// the same root key. The chain-key info strings are SWAPPED relative
    /// to the initiator so initiator-send-chain == responder-recv-chain.
    ///
    /// The one-time pre-key is consumed (zeroed and removed) — replay
    /// protection at the bundle layer.
    /// </summary>
    private void EstablishResponderSession(string peerUhid, EncryptedPayload payload)
    {
        var initiatorIK = payload.InitiatorIdentityKeyX25519
            ?? throw new CryptographicException("PreKey message missing initiator identity key.");
        var initiatorEK = payload.InitiatorEphemeralKeyX25519
            ?? throw new CryptographicException("PreKey message missing initiator ephemeral key.");

        if (initiatorIK.Length != X25519Service.PublicKeySize)
            throw new CryptographicException(
                $"Initiator IK_X25519 has wrong size: {initiatorIK.Length} (expected {X25519Service.PublicKeySize}).");
        if (initiatorEK.Length != X25519Service.PublicKeySize)
            throw new CryptographicException(
                $"Initiator EK_X25519 has wrong size: {initiatorEK.Length} (expected {X25519Service.PublicKeySize}).");

        // Look up the SPK + OPK private halves the initiator consumed.
        if (_preKeyState.SignedPreKeyId != payload.UsedSignedPreKeyId
            || _preKeyState.SignedPreKeyPriv.Length == 0)
            throw new CryptographicException(
                $"PreKey message references signed pre-key id {payload.UsedSignedPreKeyId} " +
                "which is not held by this node (rotated out or never generated).");

        if (!_preKeyState.OneTimePreKeys.TryGetValue(payload.UsedOneTimePreKeyId, out var otpk))
            throw new CryptographicException(
                $"PreKey message references one-time pre-key id {payload.UsedOneTimePreKeyId} " +
                "which is not held (already consumed, or never generated).");

        byte[]? dh1 = null, dh2 = null, dh3 = null, dh4 = null;
        byte[]? sharedSecret = null;
        byte[]? rootKey = null;
        byte[]? sendChainKey = null;
        byte[]? recvChainKey = null;

        try
        {
            // Mirror of initiator's 4 DHs (X25519 ECDH is commutative — each
            // pair below produces the same 32-byte shared secret as the
            // corresponding initiator DH):
            //   DH1' = DH(SPK_B, IK_A)   matches DH1  = DH(IK_A, SPK_B)
            //   DH2' = DH(IK_B, EK_A)    matches DH2  = DH(EK_A, IK_B)
            //   DH3' = DH(SPK_B, EK_A)   matches DH3  = DH(EK_A, SPK_B)
            //   DH4' = DH(OPK_B, EK_A)   matches DH4  = DH(EK_A, OPK_B)
            dh1 = X25519Service.Agree(_preKeyState.SignedPreKeyPriv, initiatorIK);
            dh2 = X25519Service.Agree(_identityX25519Priv, initiatorEK);
            dh3 = X25519Service.Agree(_preKeyState.SignedPreKeyPriv, initiatorEK);
            dh4 = X25519Service.Agree(otpk.Priv, initiatorEK);

            sharedSecret = ConcatBytes(dh1, dh2, dh3, dh4);
            rootKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, AesKeySize, info: HkdfRootInfo);

            // SWAPPED: the initiator's send-chain info derives our
            // recv-chain (and vice versa). This way the per-message keys
            // line up: when initiator ratchets its send chain to encrypt
            // counter N, responder ratchets its recv chain to decrypt the
            // same counter N, both arriving at the same key.
            recvChainKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, rootKey, AesKeySize, info: HkdfChainInitiatorSendInfo);
            sendChainKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, rootKey, AesKeySize, info: HkdfChainInitiatorRecvInfo);

            var session = new SignalSession
            {
                RootKey = rootKey,
                SendChainKey = sendChainKey,
                RecvChainKey = recvChainKey,
                PendingPreKeyMessage = false // we are the responder
            };

            rootKey = null;
            sendChainKey = null;
            recvChainKey = null;

            _sessions[peerUhid] = session;

            // Consume one-time pre-key — never reuse (replay protection).
            CryptographicOperations.ZeroMemory(otpk.Priv);
            _preKeyState.OneTimePreKeys.Remove(payload.UsedOneTimePreKeyId);

            _logger.LogDebug(
                "Established responder session with {Peer} via X3DH; one-time pre-key {Id} consumed",
                LogSanitizer.SanitizeUhid(peerUhid), payload.UsedOneTimePreKeyId);
        }
        finally
        {
            if (dh1 != null) CryptographicOperations.ZeroMemory(dh1);
            if (dh2 != null) CryptographicOperations.ZeroMemory(dh2);
            if (dh3 != null) CryptographicOperations.ZeroMemory(dh3);
            if (dh4 != null) CryptographicOperations.ZeroMemory(dh4);
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

    /// <summary>Gets a copy of the Ed25519 public key for this node.</summary>
    public byte[] GetPublicKey() => (byte[])_ed25519PublicKey.Clone();

    /// <summary>Gets a copy of the X25519 ECDH public key for this node.</summary>
    public byte[] GetX25519PublicKey() => (byte[])_identityX25519Pub.Clone();

    /// <summary>
    /// Advances a chain key by one step per the Signal Double-Ratchet
    /// spec (§5.1):
    ///
    ///     message_key   = HMAC-SHA256(chain_key, 0x01)
    ///     new_chain_key = HMAC-SHA256(chain_key, 0x02)
    ///
    /// Both outputs are 32 bytes. The chain key is uniformly random by
    /// construction (output of HKDF in the root-key derivation), so HMAC
    /// alone is a sound KDF here — no HKDF wrapper needed.
    /// </summary>
    private static (byte[] NewChainKey, byte[] MessageKey) RatchetChainKey(byte[] chainKey)
    {
        var messageKey = HMACSHA256.HashData(chainKey, RatchetMessageKeyInput);
        var newChainKey = HMACSHA256.HashData(chainKey, RatchetChainKeyInput);
        return (newChainKey, messageKey);
    }

    private static byte[] ConcatBytes(params byte[][] arrays)
    {
        var totalLength = 0;
        foreach (var a in arrays) totalLength += a.Length;
        var result = new byte[totalLength];
        var offset = 0;
        foreach (var a in arrays)
        {
            Buffer.BlockCopy(a, 0, result, offset, a.Length);
            offset += a.Length;
        }
        return result;
    }
}
