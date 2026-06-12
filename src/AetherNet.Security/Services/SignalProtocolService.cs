// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Security.Cryptography;
using AetherNet.Diagnostics;
using AetherNet.Identity;
using AetherNet.Security.Models;
using Microsoft.Extensions.Logging;

namespace AetherNet.Security.Services;

/// <summary>
/// State of a Signal-Protocol session with a single peer — both X3DH
/// session-establishment metadata and Double-Ratchet (Signal §5) state.
///
/// Double-Ratchet state per Signal §5:
///   <list type="bullet">
///     <item>RK — root key. Re-keyed on every DH-ratchet step.</item>
///     <item>DHs (priv/pub) — my current ratchet keypair.</item>
///     <item>DHr — peer's last-known ratchet public key. Null until first DH-ratchet.</item>
///     <item>CKs — my current sending chain key. Null until I've sent (or initialized) on this chain.</item>
///     <item>CKr — my current receiving chain key. Null until I've received on this chain.</item>
///     <item>Ns / Nr — send / receive counters (reset to 0 on each DH-ratchet step).</item>
///     <item>PN — number of messages I sent in my previous sending chain (so the
///       receiver can compute skipped keys across a DH-ratchet boundary).</item>
///     <item>MKSKIPPED — skipped message keys keyed by (DHr_pub, counter).</item>
///   </list>
/// </summary>
internal sealed class SignalSession
{
    public byte[] RootKey { get; set; } = [];

    /// <summary>Sending chain key. Null until the first send (or until DH-ratchet rekeys it).</summary>
    public byte[]? SendChainKey { get; set; }
    /// <summary>Receiving chain key. Null until the first receive that triggers a DH-ratchet step.</summary>
    public byte[]? RecvChainKey { get; set; }

    public int SendCounter { get; set; }
    public int RecvCounter { get; set; }
    /// <summary>Number of messages sent in the previous sending chain (Signal §5: PN).</summary>
    public int PreviousChainCount { get; set; }

    /// <summary>My current DH-ratchet private key (X25519, 32 bytes).</summary>
    public byte[] MyEphemeralPriv { get; set; } = [];
    /// <summary>My current DH-ratchet public key (X25519, 32 bytes).</summary>
    public byte[] MyEphemeralPub { get; set; } = [];
    /// <summary>Peer's last-seen DH-ratchet public key. Null until first DH-ratchet step.</summary>
    public byte[]? RemoteEphemeralPub { get; set; }

    /// <summary>
    /// Skipped message keys keyed by "Hex(remoteEphPub):counter". The
    /// remoteEphPub binding is essential — out-of-order messages from a
    /// previous chain (different DHr) can still arrive after a DH-ratchet
    /// step, and they need their own per-chain key set.
    /// </summary>
    public Dictionary<string, byte[]> SkippedMessageKeys { get; } = new();

    /// <summary>
    /// True iff this session was established in the initiator role and the
    /// first outbound message has not yet been sent. While true, the next
    /// <see cref="SignalProtocolService.EncryptAsync"/> emits a PreKey
    /// message (MessageType=1) carrying the X3DH inputs.
    /// </summary>
    public bool PendingPreKeyMessage { get; set; }
    public byte[] InitiatorIdentityKeyX25519 { get; set; } = [];
    public int UsedSignedPreKeyId { get; set; }
    public int UsedOneTimePreKeyId { get; set; }
}

/// <summary>
/// Pre-key state held by the responder side: signed pre-key (rotated
/// periodically) and a pool of one-time pre-keys (each consumed exactly
/// once). The private halves stay on the responder so that when a
/// PreKey message arrives, the matching X3DH DHs can be computed.
///
/// One-time pre-keys are managed as a pool of <c>OpkPoolSize</c>
/// (default 100) entries. Bundle generation hands out the next-unused id
/// from <see cref="AvailableOpkIds"/>; the OPK stays in
/// <see cref="OneTimePreKeys"/> until a responder consumes it via X3DH,
/// at which point it is zeroed and removed. Top-up runs each time a
/// bundle is generated so the available queue never empties under steady
/// load.
/// </summary>
internal sealed class PreKeyState
{
    /// <summary>
    /// Active signed-pre-key id. Mirrors <c>SignedPreKeyHistory[^1].Id</c>
    /// — kept as a denormalised field for the existing fast-path code that
    /// references <see cref="SignedPreKeyId"/> directly without a list lookup.
    /// </summary>
    public int SignedPreKeyId { get; set; }
    public byte[] SignedPreKeyPriv { get; set; } = [];
    public byte[] SignedPreKeyPub { get; set; } = [];
    public byte[] SignedPreKeySignature { get; set; } = [];

    /// <summary>
    /// Signed-pre-key history: oldest first, newest last. The newest entry
    /// (i.e. the last) is the active SPK that gets handed out in bundles.
    /// Older entries are retained for the rotation window so that messages
    /// signed under a recently-rotated SPK can still decrypt.
    /// </summary>
    public List<SignedPreKeyEntry> SignedPreKeyHistory { get; } = new();

    /// <summary>One-time pre-keys keyed by id. Removed and zeroed on consumption.</summary>
    public Dictionary<int, (byte[] Priv, byte[] Pub)> OneTimePreKeys { get; } = new();

    /// <summary>
    /// IDs of OPKs that exist in <see cref="OneTimePreKeys"/> and have NOT
    /// yet been issued in any bundle. Bundle generation pops from the
    /// front (FIFO). Top-up generates new OPKs and enqueues them here.
    /// </summary>
    public Queue<int> AvailableOpkIds { get; } = new();
}

/// <summary>
/// One signed-pre-key in the history (active or retained-prior). The private
/// half is held so that responder-side X3DH can still complete when a peer
/// presents a slightly-stale SPK during the rotation window.
/// </summary>
internal sealed class SignedPreKeyEntry
{
    public int Id { get; init; }
    public byte[] PrivateKey { get; init; } = [];
    public byte[] PublicKey { get; init; } = [];
    public byte[] Signature { get; init; } = [];
    public DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Signal Protocol implementation: X3DH session establishment + full
/// Double Ratchet (Signal §5).
///
/// Key agreement: X3DH (Signal §3) over X25519 (RFC 7748). Four DHs:
///   <list type="bullet">
///     <item>DH1 = DH(IK_A, SPK_B) — long-term mutual auth</item>
///     <item>DH2 = DH(EK_A, IK_B)  — initiator ephemeral binds to responder identity</item>
///     <item>DH3 = DH(EK_A, SPK_B) — initiator ephemeral binds to responder signed pre-key</item>
///     <item>DH4 = DH(EK_A, OPK_B) — initiator ephemeral binds to responder one-time pre-key</item>
///   </list>
/// Initial root key: HKDF-SHA256 over concat(DH1||DH2||DH3||DH4).
///
/// Double Ratchet (§5): each side maintains a current X25519 ratchet
/// keypair. Whenever the sender receives a peer message bearing a new
/// ratchet public key, it does a DH-ratchet step: derive a new chain key
/// via <c>KDF_RK(RK, DH(myDHs_priv, newDHr))</c>, then generate a fresh
/// <c>DHs</c> and derive its sending chain via <c>KDF_RK(RK, DH(newDHs_priv, newDHr))</c>.
/// The Signal-canonical integration with X3DH is used: the initiator's
/// X3DH ephemeral key becomes its first DH-ratchet keypair.
///
/// Symmetric ratchet (§5.1): HMAC-SHA256, single-byte domain separation
///   (0x01 → message key, 0x02 → next chain key).
/// Encryption: AES-256-GCM, 12-byte nonce, 16-byte tag.
/// Identity signing: Ed25519.
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
    /// Ratchet domain-separation bytes per Signal §5.1. 0x01 yields a
    /// per-message key, 0x02 yields the next chain key.
    /// </summary>
    private static readonly byte[] RatchetMessageKeyInput = [0x01];
    private static readonly byte[] RatchetChainKeyInput = [0x02];

    /// <summary>
    /// HKDF info string for the X3DH root-key derivation. MUST match
    /// every other language exactly — verified by
    /// fixtures/signal/expected/x3dh_basic.json.
    /// </summary>
    private static readonly byte[] HkdfRootInfo = "aether-x3dh-root-v1"u8.ToArray();

    /// <summary>
    /// HKDF info string for the DH-ratchet step (Signal §5: KDF_RK). Each
    /// DH-ratchet step derives a 64-byte block, split into the new root
    /// key (first 32 bytes) and the new chain key (second 32 bytes).
    /// </summary>
    private static readonly byte[] HkdfRatchetInfo = "aether-ratchet-rk-v1"u8.ToArray();

    private readonly ConcurrentDictionary<string, SignalSession> _sessions = new();
    private readonly ILogger<SignalProtocolService> _logger;

    private byte[] _identityX25519Priv = [];
    private byte[] _identityX25519Pub = [];
    private byte[] _ed25519PrivateKey = [];
    private byte[] _ed25519PublicKey = [];

    private string? _localUhid;
    private readonly PreKeyState _preKeyState = new();
    private readonly object _preKeyLock = new();

    private readonly ISignalSessionStore? _sessionStore;
    private readonly IPreKeyStore? _preKeyStore;
    private readonly SignedPreKeyRotationOptions _rotationOptions;
    private readonly Func<DateTimeOffset> _nowProvider;

    /// <summary>
    /// Default size of the one-time pre-key pool. Mirrors Signal's published
    /// guidance: ~100 OPKs per device so realistic concurrent-initiator
    /// loads don't collide on a single shared id.
    /// </summary>
    public const int DefaultOpkPoolSize = 100;

    /// <summary>
    /// Target size of the one-time pre-key pool. The pool is topped up to
    /// this many available (un-issued) keys on every bundle generation, and
    /// consumed keys are replaced lazily on the next bundle call.
    /// </summary>
    public int OpkPoolSize { get; }

    public SignalProtocolService(ILogger<SignalProtocolService> logger)
        : this(logger, DefaultOpkPoolSize, sessionStore: null, preKeyStore: null,
            rotationOptions: null, nowProvider: null)
    {
    }

    public SignalProtocolService(ILogger<SignalProtocolService> logger, int opkPoolSize)
        : this(logger, opkPoolSize, sessionStore: null, preKeyStore: null,
            rotationOptions: null, nowProvider: null)
    {
    }

    /// <summary>
    /// Constructor accepting persistent stores, rotation options, and a
    /// synthetic clock. Internal because <see cref="ISignalSessionStore"/>
    /// exposes the internal <see cref="SignalSession"/>; only assemblies
    /// in the InternalsVisibleTo allow-list (AetherNet.Storage,
    /// AetherNet.Core.Tests) can reference it directly.
    ///
    /// When <paramref name="preKeyStore"/> is non-null, identity keys are
    /// loaded from it on construction (or generated and saved if no prior
    /// identity is stored). The signed-pre-key history and OPK pool are
    /// likewise hydrated. When <paramref name="sessionStore"/> is non-null,
    /// every encrypt / decrypt mutation triggers a save. Saves are
    /// best-effort: a failure logs a warning and continues without
    /// blocking the message flow.
    /// </summary>
    internal SignalProtocolService(
        ILogger<SignalProtocolService> logger,
        ISignalSessionStore? sessionStore = null,
        IPreKeyStore? preKeyStore = null,
        SignedPreKeyRotationOptions? rotationOptions = null,
        Func<DateTimeOffset>? nowProvider = null)
        : this(logger, DefaultOpkPoolSize, sessionStore, preKeyStore, rotationOptions, nowProvider)
    {
    }

    internal SignalProtocolService(
        ILogger<SignalProtocolService> logger,
        int opkPoolSize,
        ISignalSessionStore? sessionStore,
        IPreKeyStore? preKeyStore,
        SignedPreKeyRotationOptions? rotationOptions,
        Func<DateTimeOffset>? nowProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (opkPoolSize < 1)
            throw new ArgumentOutOfRangeException(nameof(opkPoolSize),
                $"OpkPoolSize must be >= 1 (got {opkPoolSize}).");
        OpkPoolSize = opkPoolSize;
        _sessionStore = sessionStore;
        _preKeyStore = preKeyStore;
        _rotationOptions = rotationOptions ?? SignedPreKeyRotationOptions.Default;
        _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);

        if (_rotationOptions.RotationInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(rotationOptions),
                "RotationInterval must be > 0.");
        if (_rotationOptions.RetainedHistoryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rotationOptions),
                "RetainedHistoryCount must be >= 0.");

        InitializeIdentityKeys();
        HydrateFromPreKeyStore();
        HydrateFromSessionStore();
    }

    private void InitializeIdentityKeys()
    {
        (_identityX25519Priv, _identityX25519Pub) = X25519Service.GenerateKeyPair();
        (_ed25519PrivateKey, _ed25519PublicKey) = Ed25519SigningService.GenerateKeyPair();
    }

    private void HydrateFromPreKeyStore()
    {
        if (_preKeyStore is null) return;

        try
        {
            var stored = _preKeyStore.LoadIdentityAsync().GetAwaiter().GetResult();
            if (stored is not null)
            {
                _ed25519PrivateKey = stored.Ed25519PrivateKey;
                _ed25519PublicKey = stored.Ed25519PublicKey;
                _identityX25519Priv = stored.X25519PrivateKey;
                _identityX25519Pub = stored.X25519PublicKey;
                if (!string.IsNullOrEmpty(stored.LocalUhid))
                    _localUhid = stored.LocalUhid;
            }
            else
            {
                var fresh = new StoredIdentityKeys(
                    _ed25519PrivateKey, _ed25519PublicKey,
                    _identityX25519Priv, _identityX25519Pub, _localUhid);
                _preKeyStore.SaveIdentityAsync(fresh).GetAwaiter().GetResult();
            }

            var history = _preKeyStore.LoadSignedPreKeysAsync().GetAwaiter().GetResult();
            lock (_preKeyLock)
            {
                _preKeyState.SignedPreKeyHistory.Clear();
                foreach (var e in history.Entries.OrderBy(e => e.GeneratedAt))
                {
                    _preKeyState.SignedPreKeyHistory.Add(new SignedPreKeyEntry
                    {
                        Id = e.Id, PrivateKey = e.PrivateKey, PublicKey = e.PublicKey,
                        Signature = e.Signature, GeneratedAt = e.GeneratedAt,
                    });
                }
                if (_preKeyState.SignedPreKeyHistory.Count > 0)
                {
                    var active = _preKeyState.SignedPreKeyHistory[^1];
                    _preKeyState.SignedPreKeyId = active.Id;
                    _preKeyState.SignedPreKeyPriv = active.PrivateKey;
                    _preKeyState.SignedPreKeyPub = active.PublicKey;
                    _preKeyState.SignedPreKeySignature = active.Signature;
                }
            }

            var opks = _preKeyStore.LoadOneTimePreKeysAsync().GetAwaiter().GetResult();
            lock (_preKeyLock)
            {
                _preKeyState.OneTimePreKeys.Clear();
                _preKeyState.AvailableOpkIds.Clear();
                foreach (var (id, opk) in opks)
                {
                    _preKeyState.OneTimePreKeys[id] = (opk.PrivateKey, opk.PublicKey);
                    if (!opk.Issued)
                        _preKeyState.AvailableOpkIds.Enqueue(id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hydrate pre-key state; continuing with freshly-generated keys.");
        }
    }

    private void HydrateFromSessionStore()
    {
        if (_sessionStore is null) return;
        try
        {
            var peers = _sessionStore.ListPeersAsync().GetAwaiter().GetResult();
            foreach (var peerUhid in peers)
            {
                try
                {
                    var session = _sessionStore.LoadAsync(peerUhid).GetAwaiter().GetResult();
                    if (session is not null) _sessions[peerUhid] = session;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load session for {Peer}; skipping.",
                        LogSanitizer.SanitizeUhid(peerUhid));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate sessions from store.");
        }
    }

    private void TryPersistSession(string peerUhid, SignalSession session)
    {
        if (_sessionStore is null) return;
        try
        {
            _sessionStore.SaveAsync(peerUhid, session).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist session for {Peer}.",
                LogSanitizer.SanitizeUhid(peerUhid));
        }
    }

    private void TryPersistIdentity()
    {
        if (_preKeyStore is null) return;
        try
        {
            var snapshot = new StoredIdentityKeys(
                _ed25519PrivateKey, _ed25519PublicKey,
                _identityX25519Priv, _identityX25519Pub, _localUhid);
            _preKeyStore.SaveIdentityAsync(snapshot).GetAwaiter().GetResult();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist identity keys."); }
    }

    private void TryPersistSignedPreKeysNoLock()
    {
        if (_preKeyStore is null) return;
        try
        {
            var snapshot = new StoredSignedPreKeyHistory(
                _preKeyState.SignedPreKeyHistory.Select(e => new StoredSignedPreKey(
                    e.Id, e.PrivateKey, e.PublicKey, e.Signature, e.GeneratedAt)).ToArray());
            _preKeyStore.SaveSignedPreKeysAsync(snapshot).GetAwaiter().GetResult();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist SPK history."); }
    }

    private void TryPersistOneTimePreKeysNoLock()
    {
        if (_preKeyStore is null) return;
        try
        {
            var issued = new HashSet<int>(_preKeyState.OneTimePreKeys.Keys);
            foreach (var id in _preKeyState.AvailableOpkIds) issued.Remove(id);
            var snapshot = _preKeyState.OneTimePreKeys.ToDictionary(
                kv => kv.Key,
                kv => new StoredOneTimePreKey(kv.Key, kv.Value.Priv, kv.Value.Pub, issued.Contains(kv.Key)));
            _preKeyStore.SaveOneTimePreKeysAsync(snapshot).GetAwaiter().GetResult();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist OPK pool."); }
    }

    private void TryConsumeOneTimePreKey(int id)
    {
        if (_preKeyStore is null) return;
        try { _preKeyStore.ConsumeOneTimePreKeyAsync(id).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to consume OPK {Id}.", id); }
    }

    public void SetLocalUhid(string localUhid)
    {
        ArgumentException.ThrowIfNullOrEmpty(localUhid);
        _localUhid = localUhid;
        TryPersistIdentity();
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

        // Activity is null when no listener is subscribed — the BCL fast-path.
        // Tags are only built once we know we'll record them.
        using var activity = AetherNetTelemetry.ActivitySource.StartActivity("AetherNet.Encrypt");
        var stopwatch = ValueStopwatch.StartNew();
        try
        {
            if (!_sessions.TryGetValue(peerUhid, out var session))
                throw new InvalidOperationException(
                    $"No session established with peer {LogSanitizer.SanitizeUhid(peerUhid)}");

            var senderUhid = _localUhid ?? throw new InvalidOperationException(
                "Local UHID is not set. Call GeneratePreKeyBundleAsync(localUhid) " +
                "or SetLocalUhid(localUhid) before encrypting.");

            // Lazy CKs initialization for the initiator's first send: the X3DH
            // setup placed DHs and DHr but did not derive CKs (the Double
            // Ratchet defers it until first send to avoid an extra KDF step
            // when no message is ever sent on a session).
            if (session.SendChainKey is null)
            {
                if (session.RemoteEphemeralPub is null)
                    throw new InvalidOperationException(
                        "Cannot derive sending chain: peer's ratchet public key is unknown.");
                DhRatchetSendOnly(session, session.RemoteEphemeralPub);
            }

            byte[]? messageKey = null;
            try
            {
                (session.SendChainKey, messageKey) = RatchetChainKey(session.SendChainKey!);

                var nonce = RandomNumberGenerator.GetBytes(AesNonceSize);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[AesTagSize];

                using var aes = new AesGcm(messageKey, AesTagSize);
                aes.Encrypt(nonce, plaintext, ciphertext, tag);

                var combined = new byte[ciphertext.Length + AesTagSize];
                Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
                Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, AesTagSize);

                var counter = session.SendCounter++;
                var ratchetPub = (byte[])session.MyEphemeralPub.Clone();

                // PreKey message? First message after initiator-side X3DH.
                // Carries X3DH metadata so the responder can mirror the DHs.
                if (session.PendingPreKeyMessage)
                {
                    var preKey = new EncryptedPayload(
                        Ciphertext: combined,
                        Nonce: nonce,
                        MessageType: 1,
                        SenderUhid: senderUhid,
                        Counter: counter,
                        InitiatorIdentityKeyX25519: (byte[])session.InitiatorIdentityKeyX25519.Clone(),
                        // Backward-compat field — equals SenderEphemeralKeyX25519 on the first message
                        // because the initiator's X3DH ephemeral becomes its first DH-ratchet pubkey.
                        InitiatorEphemeralKeyX25519: ratchetPub,
                        UsedSignedPreKeyId: session.UsedSignedPreKeyId,
                        UsedOneTimePreKeyId: session.UsedOneTimePreKeyId,
                        SenderEphemeralKeyX25519: ratchetPub,
                        PreviousChainCount: session.PreviousChainCount);

                    session.PendingPreKeyMessage = false;

                    if (activity is not null)
                    {
                        activity.SetTag("aethernet.peer.uhid", LogSanitizer.SanitizeUhid(peerUhid));
                        activity.SetTag("aethernet.message.type", 1);
                        activity.SetTag("aethernet.message.counter", counter);
                    }
                    AetherNetTelemetry.MessagesEncrypted.Add(1);
                    _logger.LogDebug("Encrypted PreKey msg for {Peer}, counter={Counter}",
                        LogSanitizer.SanitizeUhid(peerUhid), counter);
                    TryPersistSession(peerUhid, session);
                    return Task.FromResult(preKey);
                }

                if (activity is not null)
                {
                    activity.SetTag("aethernet.peer.uhid", LogSanitizer.SanitizeUhid(peerUhid));
                    activity.SetTag("aethernet.message.type", 0);
                    activity.SetTag("aethernet.message.counter", counter);
                }
                AetherNetTelemetry.MessagesEncrypted.Add(1);
                _logger.LogDebug("Encrypted msg for {Peer}, counter={Counter}",
                    LogSanitizer.SanitizeUhid(peerUhid), counter);

                var payload = new EncryptedPayload(
                    Ciphertext: combined,
                    Nonce: nonce,
                    MessageType: 0,
                    SenderUhid: senderUhid,
                    Counter: counter,
                    SenderEphemeralKeyX25519: ratchetPub,
                    PreviousChainCount: session.PreviousChainCount);
                TryPersistSession(peerUhid, session);
                return Task.FromResult(payload);
            }
            finally
            {
                if (messageKey != null)
                    CryptographicOperations.ZeroMemory(messageKey);
            }
        }
        finally
        {
            AetherNetTelemetry.EncryptLatency.Record(stopwatch.GetElapsedMilliseconds());
        }
    }

    /// <inheritdoc />
    public Task<byte[]> DecryptAsync(string peerUhid, EncryptedPayload payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(payload);

        using var activity = AetherNetTelemetry.ActivitySource.StartActivity("AetherNet.Decrypt");
        var stopwatch = ValueStopwatch.StartNew();
        try
        {
            // Every Double-Ratchet message carries the sender's current ratchet
            // public key. Fall back to InitiatorEphemeralKeyX25519 for backward
            // compatibility with older PreKey messages from peers that haven't
            // upgraded to the new wire envelope.
            var senderRatchetPub = payload.SenderEphemeralKeyX25519
                ?? payload.InitiatorEphemeralKeyX25519;

            // PreKey message? Establish the responder-side session via mirrored X3DH.
            if (payload.MessageType == 1)
            {
                if (payload.InitiatorIdentityKeyX25519 == null || senderRatchetPub == null)
                    throw new CryptographicException(
                        "PreKey message missing initiator key material " +
                        "(InitiatorIdentityKeyX25519 and SenderEphemeralKeyX25519 / InitiatorEphemeralKeyX25519).");
                EstablishResponderSession(peerUhid, payload, senderRatchetPub);
            }

            if (!_sessions.TryGetValue(peerUhid, out var session))
                throw new InvalidOperationException(
                    $"No session established with peer {LogSanitizer.SanitizeUhid(peerUhid)}");

            if (senderRatchetPub == null)
                throw new CryptographicException(
                    "Message missing SenderEphemeralKeyX25519 — required for the Double Ratchet.");

            // DH-ratchet step? Triggered when the peer's ratchet public key changes.
            if (session.RemoteEphemeralPub == null
                || !ConstantTimeEquals(senderRatchetPub, session.RemoteEphemeralPub))
            {
                // First, derive any skipped keys from the previous receive chain
                // (the chain keyed by the OLD RemoteEphemeralPub). Then ratchet.
                SkipMessageKeys(session, payload.PreviousChainCount);
                DhRatchetReceive(session, senderRatchetPub);

                using var ratchetActivity = AetherNetTelemetry.ActivitySource.StartActivity("AetherNet.DhRatchet.Step");
                if (ratchetActivity is not null)
                    ratchetActivity.SetTag("aethernet.peer.uhid", LogSanitizer.SanitizeUhid(peerUhid));
                AetherNetTelemetry.DhRatchetSteps.Add(1);
            }

            byte[]? messageKey = null;
            try
            {
                // Skipped key cached for this (DHr_pub, counter) pair?
                var skippedKey = SkippedKey(senderRatchetPub, payload.Counter);
                if (session.SkippedMessageKeys.TryGetValue(skippedKey, out var cached))
                {
                    session.SkippedMessageKeys.Remove(skippedKey);
                    messageKey = cached;
                }
                else
                {
                    if (session.RecvChainKey == null)
                        throw new CryptographicException(
                            "Receive chain not initialized (DH-ratchet step missing).");

                    var gap = payload.Counter - session.RecvCounter;
                    if (gap > MaxSkippedKeys)
                        throw new CryptographicException(
                            $"Message counter gap ({gap}) exceeds maximum ({MaxSkippedKeys}). " +
                            "Session must be re-established.");

                    // Skip ahead, caching intermediate keys.
                    while (session.RecvCounter < payload.Counter)
                    {
                        byte[]? skipKey = null;
                        try
                        {
                            (session.RecvChainKey, skipKey) = RatchetChainKey(session.RecvChainKey!);
                            session.SkippedMessageKeys[SkippedKey(senderRatchetPub, session.RecvCounter)] = skipKey;
                            skipKey = null;
                            session.RecvCounter++;
                        }
                        finally
                        {
                            if (skipKey != null)
                                CryptographicOperations.ZeroMemory(skipKey);
                        }
                    }

                    (session.RecvChainKey, messageKey) = RatchetChainKey(session.RecvChainKey!);
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

                if (activity is not null)
                {
                    activity.SetTag("aethernet.peer.uhid", LogSanitizer.SanitizeUhid(peerUhid));
                    activity.SetTag("aethernet.message.type", payload.MessageType);
                    activity.SetTag("aethernet.message.counter", payload.Counter);
                }
                AetherNetTelemetry.MessagesDecrypted.Add(1);
                _logger.LogDebug("Decrypted msg from {Peer}, counter={Counter}",
                    LogSanitizer.SanitizeUhid(peerUhid), payload.Counter);

                TryPersistSession(peerUhid, session);
                return Task.FromResult(plaintext);
            }
            finally
            {
                if (messageKey != null)
                    CryptographicOperations.ZeroMemory(messageKey);
            }
        }
        finally
        {
            AetherNetTelemetry.DecryptLatency.Record(stopwatch.GetElapsedMilliseconds());
        }
    }

    /// <inheritdoc />
    public Task<PreKeyBundle> GeneratePreKeyBundleAsync(string localUhid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(localUhid);
        var uhidChanged = !string.Equals(_localUhid, localUhid, StringComparison.Ordinal);
        _localUhid = localUhid;
        if (uhidChanged) TryPersistIdentity();

        int preKeyId;
        byte[] otpkPub;
        byte[] spkPub;
        int signedPreKeyId;
        byte[] signature;
        var historyMutated = false;

        lock (_preKeyLock)
        {
            // SignedPreKey: generated lazily on the first bundle call. On
            // subsequent calls the active SPK is reused unless its age
            // exceeds RotationInterval, in which case a fresh SPK is
            // generated and the history is rolled forward. A retained
            // history (configurable) lets messages signed under a
            // recently-rotated SPK still complete X3DH during the rotation
            // window — Signal §3.3 recommends weekly rotation.
            if (_preKeyState.SignedPreKeyHistory.Count == 0)
            {
                AppendNewSignedPreKeyNoLock();
                historyMutated = true;
            }
            else
            {
                var active = _preKeyState.SignedPreKeyHistory[^1];
                if (_nowProvider() - active.GeneratedAt >= _rotationOptions.RotationInterval)
                {
                    AppendNewSignedPreKeyNoLock();
                    historyMutated = true;
                }
            }

            var activeEntry = _preKeyState.SignedPreKeyHistory[^1];
            signedPreKeyId = activeEntry.Id;
            spkPub = activeEntry.PublicKey;
            signature = activeEntry.Signature;

            // Top up the OPK pool, then dequeue the next un-issued OPK.
            TopUpOpkPoolNoLock();
            preKeyId = _preKeyState.AvailableOpkIds.Dequeue();
            otpkPub = _preKeyState.OneTimePreKeys[preKeyId].Pub;

            if (historyMutated) TryPersistSignedPreKeysNoLock();
            TryPersistOneTimePreKeysNoLock();
        }

        var bundle = new PreKeyBundle(
            Uhid: localUhid,
            IdentityKey: (byte[])_ed25519PublicKey.Clone(),
            IdentityKeyX25519: (byte[])_identityX25519Pub.Clone(),
            PreKeyId: preKeyId,
            PreKey: (byte[])otpkPub.Clone(),
            SignedPreKeyId: signedPreKeyId,
            SignedPreKey: (byte[])spkPub.Clone(),
            SignedPreKeySignature: signature);

        _logger.LogDebug("Generated pre-key bundle for {Uhid} (SPK id {Spk}, OPK id {Opk}, pool: held={Held})",
            LogSanitizer.SanitizeUhid(localUhid), signedPreKeyId, preKeyId, _preKeyState.OneTimePreKeys.Count);

        return Task.FromResult(bundle);
    }

    /// <summary>
    /// Generates a fresh SPK, appends it to the history as the new active
    /// entry, and trims the history to the retained-count budget. Caller
    /// MUST hold <see cref="_preKeyLock"/>.
    /// </summary>
    private void AppendNewSignedPreKeyNoLock()
    {
        var (spkPriv, spkPub) = X25519Service.GenerateKeyPair();
        var signedPreKeyId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var sig = Ed25519SigningService.Sign(_ed25519PrivateKey, spkPub);

        _preKeyState.SignedPreKeyHistory.Add(new SignedPreKeyEntry
        {
            Id = signedPreKeyId,
            PrivateKey = spkPriv,
            PublicKey = spkPub,
            Signature = sig,
            GeneratedAt = _nowProvider(),
        });

        var maxEntries = 1 + _rotationOptions.RetainedHistoryCount;
        while (_preKeyState.SignedPreKeyHistory.Count > maxEntries)
        {
            var pruned = _preKeyState.SignedPreKeyHistory[0];
            CryptographicOperations.ZeroMemory(pruned.PrivateKey);
            _preKeyState.SignedPreKeyHistory.RemoveAt(0);
        }

        var active = _preKeyState.SignedPreKeyHistory[^1];
        _preKeyState.SignedPreKeyId = active.Id;
        _preKeyState.SignedPreKeyPriv = active.PrivateKey;
        _preKeyState.SignedPreKeyPub = active.PublicKey;
        _preKeyState.SignedPreKeySignature = active.Signature;
    }

    /// <summary>
    /// Forces a signed-pre-key rotation if the active SPK is older than
    /// <see cref="SignedPreKeyRotationOptions.RotationInterval"/>. Returns
    /// true iff a new SPK was generated and persisted.
    /// </summary>
    public Task<bool> RotateSignedPreKeyAsync(CancellationToken ct = default)
    {
        bool rotated = false;
        lock (_preKeyLock)
        {
            var shouldRotate = _preKeyState.SignedPreKeyHistory.Count == 0
                || _nowProvider() - _preKeyState.SignedPreKeyHistory[^1].GeneratedAt
                    >= _rotationOptions.RotationInterval;

            if (shouldRotate)
            {
                AppendNewSignedPreKeyNoLock();
                TryPersistSignedPreKeysNoLock();
                rotated = true;
            }
        }
        if (rotated)
            _logger.LogInformation("Rotated signed pre-key (history size now {Size}).",
                _preKeyState.SignedPreKeyHistory.Count);
        return Task.FromResult(rotated);
    }

    /// <summary>Active signed-pre-key id for tests and observability.</summary>
    public int ActiveSignedPreKeyId
    {
        get { lock (_preKeyLock) return _preKeyState.SignedPreKeyId; }
    }

    /// <summary>Number of signed-pre-keys held — active + retained prior.</summary>
    public int SignedPreKeyHistoryCount
    {
        get { lock (_preKeyLock) return _preKeyState.SignedPreKeyHistory.Count; }
    }

    /// <summary>
    /// Looks up a signed-pre-key entry by id across the full retained
    /// history. Returns null if the id is unknown. Caller MUST hold
    /// <see cref="_preKeyLock"/>.
    /// </summary>
    private SignedPreKeyEntry? FindSignedPreKeyNoLock(int id)
    {
        for (var i = _preKeyState.SignedPreKeyHistory.Count - 1; i >= 0; i--)
        {
            var entry = _preKeyState.SignedPreKeyHistory[i];
            if (entry.Id == id) return entry;
        }
        return null;
    }

    /// <summary>
    /// Tops the OPK pool up to <see cref="OpkPoolSize"/> available
    /// (un-issued) keys. Caller MUST hold <see cref="_preKeyLock"/>.
    ///
    /// Generates a fresh X25519 keypair per missing slot, assigns it a
    /// random non-colliding id, and enqueues the id in
    /// <see cref="PreKeyState.AvailableOpkIds"/>. Idempotent — safe to call
    /// repeatedly.
    /// </summary>
    private void TopUpOpkPoolNoLock()
    {
        while (_preKeyState.AvailableOpkIds.Count < OpkPoolSize)
        {
            var (priv, pub) = X25519Service.GenerateKeyPair();

            // Choose a non-colliding id. RandomNumberGenerator.GetInt32 has
            // a 2^31 range; collisions in a 100-element pool are
            // statistically negligible but we still guard explicitly.
            int id;
            var attempts = 0;
            do
            {
                id = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                if (++attempts > 64)
                    throw new CryptographicException(
                        "Could not allocate a non-colliding OPK id after 64 attempts. " +
                        "Pool exhaustion or RNG failure.");
            }
            while (_preKeyState.OneTimePreKeys.ContainsKey(id));

            _preKeyState.OneTimePreKeys[id] = (priv, pub);
            _preKeyState.AvailableOpkIds.Enqueue(id);
        }
    }

    /// <summary>
    /// Number of OPKs currently held — both un-issued (in
    /// <see cref="PreKeyState.AvailableOpkIds"/>) and issued-but-not-yet-consumed.
    /// Exposed for tests and observability.
    /// </summary>
    public int HeldOneTimePreKeyCount
    {
        get
        {
            lock (_preKeyLock) return _preKeyState.OneTimePreKeys.Count;
        }
    }

    /// <summary>
    /// Number of OPKs in the pool that have not yet been issued in any
    /// bundle. Drops as bundles are issued; tops back up on next bundle
    /// generation. Exposed for tests and observability.
    /// </summary>
    public int AvailableOneTimePreKeyCount
    {
        get
        {
            lock (_preKeyLock) return _preKeyState.AvailableOpkIds.Count;
        }
    }

    /// <summary>
    /// Establishes an initiator-side session against a pre-key bundle:
    /// runs the four X3DH DHs (Signal §3.3) over X25519, derives the root
    /// key, and primes the Double Ratchet by adopting the X3DH ephemeral
    /// as the initiator's first <c>DHs</c>. The peer's signed pre-key
    /// becomes the initial <c>DHr</c>. The first <see cref="EncryptAsync"/>
    /// after this returns a PreKey message (MessageType=1).
    /// </summary>
    public Task ProcessPreKeyBundleAsync(PreKeyBundle bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        using var activity = AetherNetTelemetry.ActivitySource.StartActivity("AetherNet.X3DH.Initiator");
        if (activity is not null)
            activity.SetTag("aethernet.peer.uhid", LogSanitizer.SanitizeUhid(bundle.Uhid));

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

        var (ephemeralPriv, ephemeralPub) = X25519Service.GenerateKeyPair();

        byte[]? dh1 = null, dh2 = null, dh3 = null, dh4 = null;
        byte[]? sharedSecret = null;
        byte[]? rootKey = null;

        try
        {
            dh1 = X25519Service.Agree(_identityX25519Priv, bundle.SignedPreKey);
            dh2 = X25519Service.Agree(ephemeralPriv, bundle.IdentityKeyX25519);
            dh3 = X25519Service.Agree(ephemeralPriv, bundle.SignedPreKey);
            dh4 = X25519Service.Agree(ephemeralPriv, bundle.PreKey);

            sharedSecret = ConcatBytes(dh1, dh2, dh3, dh4);
            rootKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, AesKeySize, info: HkdfRootInfo);

            // Signal-canonical X3DH↔Double-Ratchet integration: the
            // initiator's X3DH ephemeral becomes its first DHs. The
            // peer's signed pre-key is the initial DHr. CKs is computed
            // lazily on first send (DhRatchetSendOnly).
            var session = new SignalSession
            {
                RootKey = rootKey,
                SendChainKey = null,                // computed on first send
                RecvChainKey = null,                // computed on first DH-ratchet receive
                MyEphemeralPriv = ephemeralPriv,
                MyEphemeralPub = ephemeralPub,
                RemoteEphemeralPub = (byte[])bundle.SignedPreKey.Clone(),
                PendingPreKeyMessage = true,
                InitiatorIdentityKeyX25519 = (byte[])_identityX25519Pub.Clone(),
                UsedSignedPreKeyId = bundle.SignedPreKeyId,
                UsedOneTimePreKeyId = bundle.PreKeyId,
            };

            // Ownership transferred to session — null out so finally doesn't zero them.
            rootKey = null;
            ephemeralPriv = null!; // session retains ownership

            _sessions[bundle.Uhid] = session;
            AetherNetTelemetry.SessionsEstablished.Add(1);
            TryPersistSession(bundle.Uhid, session);

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
            if (ephemeralPriv != null) CryptographicOperations.ZeroMemory(ephemeralPriv);
        }
    }

    /// <summary>
    /// Establishes the responder-side session when a PreKey message arrives.
    /// Runs mirror X3DH to derive the same root key, then immediately does a
    /// DH-ratchet step (the message header carries the initiator's first
    /// <c>DHs</c>). The signed pre-key (private + public) is adopted as the
    /// responder's initial <c>DHs</c>; a fresh keypair is generated when
    /// the DH-ratchet step rotates it. The one-time pre-key is consumed.
    /// </summary>
    private void EstablishResponderSession(string peerUhid, EncryptedPayload payload, byte[] initiatorRatchetPub)
    {
        using var activity = AetherNetTelemetry.ActivitySource.StartActivity("AetherNet.X3DH.Responder");
        if (activity is not null)
            activity.SetTag("aethernet.peer.uhid", LogSanitizer.SanitizeUhid(peerUhid));

        var initiatorIK = payload.InitiatorIdentityKeyX25519
            ?? throw new CryptographicException("PreKey message missing initiator identity key.");

        if (initiatorIK.Length != X25519Service.PublicKeySize)
            throw new CryptographicException(
                $"Initiator IK_X25519 has wrong size: {initiatorIK.Length} (expected {X25519Service.PublicKeySize}).");
        if (initiatorRatchetPub.Length != X25519Service.PublicKeySize)
            throw new CryptographicException(
                $"Initiator ratchet pub has wrong size: {initiatorRatchetPub.Length} (expected {X25519Service.PublicKeySize}).");

        (byte[] Priv, byte[] Pub) otpk;
        SignedPreKeyEntry spkEntry;
        lock (_preKeyLock)
        {
            // Walk the full SPK history (active + retained prior). A pruned
            // SPK fails outright because its private half has been zeroed.
            spkEntry = FindSignedPreKeyNoLock(payload.UsedSignedPreKeyId)
                ?? throw new CryptographicException(
                    $"PreKey message references signed pre-key id {payload.UsedSignedPreKeyId} " +
                    "which is not held by this node (rotated out or never generated).");

            if (!_preKeyState.OneTimePreKeys.TryGetValue(payload.UsedOneTimePreKeyId, out otpk))
                throw new CryptographicException(
                    $"PreKey message references one-time pre-key id {payload.UsedOneTimePreKeyId} " +
                    "which is not held (already consumed, or never generated).");
        }

        byte[]? dh1 = null, dh2 = null, dh3 = null, dh4 = null;
        byte[]? sharedSecret = null;
        byte[]? rootKey = null;

        try
        {
            // Mirror of initiator's 4 DHs (X25519 ECDH is commutative).
            dh1 = X25519Service.Agree(spkEntry.PrivateKey, initiatorIK);
            dh2 = X25519Service.Agree(_identityX25519Priv, initiatorRatchetPub);
            dh3 = X25519Service.Agree(spkEntry.PrivateKey, initiatorRatchetPub);
            dh4 = X25519Service.Agree(otpk.Priv, initiatorRatchetPub);

            sharedSecret = ConcatBytes(dh1, dh2, dh3, dh4);
            rootKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, AesKeySize, info: HkdfRootInfo);

            var session = new SignalSession
            {
                RootKey = rootKey,
                SendChainKey = null,
                RecvChainKey = null,
                MyEphemeralPriv = (byte[])spkEntry.PrivateKey.Clone(),
                MyEphemeralPub = (byte[])spkEntry.PublicKey.Clone(),
                RemoteEphemeralPub = null,
                PendingPreKeyMessage = false,
            };

            rootKey = null;

            _sessions[peerUhid] = session;
            AetherNetTelemetry.SessionsEstablished.Add(1);

            lock (_preKeyLock)
            {
                if (_preKeyState.OneTimePreKeys.Remove(payload.UsedOneTimePreKeyId, out var stored))
                    CryptographicOperations.ZeroMemory(stored.Priv);
            }
            TryConsumeOneTimePreKey(payload.UsedOneTimePreKeyId);
            TryPersistSession(peerUhid, session);

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
        }
    }

    /// <summary>
    /// Performs a full DH-ratchet step on receive (Signal §5.2): updates DHr,
    /// derives a new receiving chain via <c>KDF_RK(RK, DH(DHs, DHr))</c>,
    /// generates a fresh DHs, and derives a new sending chain via
    /// <c>KDF_RK(RK, DH(newDHs, DHr))</c>.
    /// </summary>
    private void DhRatchetReceive(SignalSession session, byte[] newRemoteEphemeralPub)
    {
        // Save send-counter as PN so the peer can compute skipped keys
        // across the ratchet boundary on subsequent decrypts.
        session.PreviousChainCount = session.SendCounter;
        session.SendCounter = 0;
        session.RecvCounter = 0;
        session.RemoteEphemeralPub = (byte[])newRemoteEphemeralPub.Clone();

        // Step 1: derive new receiving chain from current DHs · new DHr.
        byte[]? dh1 = null;
        byte[]? newCkr = null;
        try
        {
            dh1 = X25519Service.Agree(session.MyEphemeralPriv, session.RemoteEphemeralPub!);
            (session.RootKey, newCkr) = KdfRk(session.RootKey, dh1);
            session.RecvChainKey = newCkr;
            newCkr = null;
        }
        finally
        {
            if (dh1 != null) CryptographicOperations.ZeroMemory(dh1);
            if (newCkr != null) CryptographicOperations.ZeroMemory(newCkr);
        }

        // Step 2: rotate DHs to a fresh keypair, derive new sending chain
        // from new DHs · new DHr.
        CryptographicOperations.ZeroMemory(session.MyEphemeralPriv);
        var (newPriv, newPub) = X25519Service.GenerateKeyPair();
        session.MyEphemeralPriv = newPriv;
        session.MyEphemeralPub = newPub;

        byte[]? dh2 = null;
        byte[]? newCks = null;
        try
        {
            dh2 = X25519Service.Agree(session.MyEphemeralPriv, session.RemoteEphemeralPub!);
            (session.RootKey, newCks) = KdfRk(session.RootKey, dh2);
            session.SendChainKey = newCks;
            newCks = null;
        }
        finally
        {
            if (dh2 != null) CryptographicOperations.ZeroMemory(dh2);
            if (newCks != null) CryptographicOperations.ZeroMemory(newCks);
        }
    }

    /// <summary>
    /// Lazy half-ratchet for the very first send on a freshly-established
    /// initiator session. The initiator's DHs and DHr are already set
    /// (X3DH placed them); we just need to derive the sending chain. We do
    /// NOT rotate DHs here — only on a true DH-ratchet (i.e. on receive).
    /// </summary>
    private void DhRatchetSendOnly(SignalSession session, byte[] remotePub)
    {
        byte[]? dh = null;
        byte[]? newCks = null;
        try
        {
            dh = X25519Service.Agree(session.MyEphemeralPriv, remotePub);
            (session.RootKey, newCks) = KdfRk(session.RootKey, dh);
            session.SendChainKey = newCks;
            newCks = null;
        }
        finally
        {
            if (dh != null) CryptographicOperations.ZeroMemory(dh);
            if (newCks != null) CryptographicOperations.ZeroMemory(newCks);
        }
    }

    /// <summary>
    /// Saves any unread message keys on the current receive chain up to
    /// the given counter, so they can be consumed if those messages
    /// eventually arrive after a DH-ratchet step. Bounded by
    /// <see cref="MaxSkippedKeys"/>.
    /// </summary>
    private static void SkipMessageKeys(SignalSession session, int until)
    {
        if (session.RecvChainKey == null || session.RemoteEphemeralPub == null)
            return; // no chain to skip on
        if (until <= session.RecvCounter)
            return;
        if (until - session.RecvCounter > MaxSkippedKeys)
            throw new CryptographicException(
                $"Skipped-key request exceeds maximum ({MaxSkippedKeys}). Session must be re-established.");

        while (session.RecvCounter < until)
        {
            byte[]? skipKey = null;
            try
            {
                (session.RecvChainKey, skipKey) = RatchetChainKey(session.RecvChainKey!);
                session.SkippedMessageKeys[SkippedKey(session.RemoteEphemeralPub!, session.RecvCounter)] = skipKey;
                skipKey = null;
                session.RecvCounter++;
            }
            finally
            {
                if (skipKey != null)
                    CryptographicOperations.ZeroMemory(skipKey);
            }
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

    /// <inheritdoc />
    public byte[] DeriveEridRoutingKey()
        => EphemeralRoutingId.DeriveRoutingKey(_ed25519PrivateKey);

    /// <summary>Ed25519 public key for this node.</summary>
    public byte[] GetPublicKey() => (byte[])_ed25519PublicKey.Clone();

    /// <summary>X25519 ECDH public key for this node.</summary>
    public byte[] GetX25519PublicKey() => (byte[])_identityX25519Pub.Clone();

    /// <summary>
    /// KDF_RK per Signal §5.2: derives a new root key + new chain key from
    /// the current root key and a fresh DH output. HKDF-SHA256 over 64
    /// bytes; first 32 = new root, second 32 = new chain key.
    /// </summary>
    private static (byte[] NewRootKey, byte[] NewChainKey) KdfRk(byte[] rootKey, byte[] dhOutput)
    {
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256,
            ikm: dhOutput, outputLength: 64,
            salt: rootKey, info: HkdfRatchetInfo);
        var newRoot = new byte[32];
        var newChain = new byte[32];
        Buffer.BlockCopy(derived, 0, newRoot, 0, 32);
        Buffer.BlockCopy(derived, 32, newChain, 0, 32);
        CryptographicOperations.ZeroMemory(derived);
        return (newRoot, newChain);
    }

    /// <summary>
    /// Advances a chain key by one step per Signal §5.1.
    ///
    ///   message_key   = HMAC-SHA256(chain_key, 0x01)
    ///   new_chain_key = HMAC-SHA256(chain_key, 0x02)
    /// </summary>
    private static (byte[] NewChainKey, byte[] MessageKey) RatchetChainKey(byte[] chainKey)
    {
        var messageKey = HMACSHA256.HashData(chainKey, RatchetMessageKeyInput);
        var newChainKey = HMACSHA256.HashData(chainKey, RatchetChainKeyInput);
        return (newChainKey, messageKey);
    }

    private static string SkippedKey(byte[] dhrPub, int counter) =>
        $"{Convert.ToHexString(dhrPub)}:{counter}";

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

    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
