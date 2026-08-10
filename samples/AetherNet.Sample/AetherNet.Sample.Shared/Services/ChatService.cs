// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Messaging;
using AetherNet.PreKeys;
using AetherNet.Protocol;
using AetherNet.Sample.Shared.Data;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Real messaging over the mesh. Not a demo surface: a message you type is end-to-end encrypted with
/// the Signal double ratchet, carried to the other phone over the radio, decrypted there, and stored
/// on both devices. No server sees it, because there isn't one.
///
/// The session is established the Signal way, over the mesh: each device publishes a pre-key bundle,
/// asks its peer for theirs (<see cref="IPreKeyExchangeService"/>, packet types 25/26), and performs
/// X3DH locally. Until that completes a message is kept <b>pending</b> — never sent in the clear —
/// and flushed the moment the session exists.
/// </summary>
public sealed class ChatService
{
    /// <summary>Marks a chat payload inside a generic Data packet.</summary>
    private const string Marker = "AETHERMSG";

    private readonly AetherStore _store;
    private readonly IIdentityService _me;
    private readonly IRadioMesh? _radio;
    private readonly ISignalProtocolService _signal;
    private readonly IPreKeyExchangeService _preKeys;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private bool _bundlePublished;

    public ChatService(
        AetherStore store,
        IIdentityService me,
        ISignalProtocolService signal,
        IPreKeyExchangeService preKeys,
        IRadioMesh? radio = null,
        ILoggerFactory? loggerFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _preKeys = preKeys ?? throw new ArgumentNullException(nameof(preKeys));
        _radio = radio;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ChatService>();

        _preKeys.BundleReceived += OnBundleReceived;
        if (_radio is not null)
            _radio.PacketReceived += OnPacket;
    }

    /// <summary>Raised when a conversation changes, so the UI can re-render.</summary>
    public event Action? Changed;

    public IReadOnlyList<ChatMessage> Conversation(string peerTag) => _store.GetMessages(peerTag);

    public IReadOnlyList<ChatMessage> Latest() => _store.GetLatestPerPeer();

    /// <summary>True once there is a secure session with this peer — messages flow immediately.</summary>
    public bool IsSecure(string peerTag) => !string.IsNullOrEmpty(peerTag) && _signal.HasSession(peerTag);

    /// <summary>
    /// Publish our pre-key bundle so peers can start a session with us, and ask a peer for theirs.
    /// Safe to call repeatedly — the bundle is published once per run.
    /// </summary>
    public async Task EnsureSessionAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(peerTag) || _radio is null) return;

        await EnsureLocalBundleAsync(cancellationToken).ConfigureAwait(false);

        if (_signal.HasSession(peerTag)) return;

        // Maybe their bundle already arrived unsolicited; otherwise ask over the radio.
        var known = _preKeys.GetReceivedBundle(peerTag);
        if (known is not null)
        {
            await AdoptBundleAsync(peerTag, known, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _preKeys.RequestBundleAsync(peerTag, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publish this device's pre-key bundle. A node must have one ready <b>before</b> anyone asks —
    /// otherwise it cannot answer a pre-key request and no session can ever start.
    /// </summary>
    private async Task EnsureLocalBundleAsync(CancellationToken cancellationToken)
    {
        if (_bundlePublished) return;

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bundlePublished) return;
            var bundle = await _signal.GeneratePreKeyBundleAsync(_me.AetherTag, cancellationToken).ConfigureAwait(false);
            _preKeys.SetLocalBundle(bundle);
            _bundlePublished = true;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// Send a message. It is stored locally either way; if there is no secure session yet it stays
    /// <b>pending</b> rather than going out unprotected, and leaves as soon as the session is up.
    /// </summary>
    public async Task SendAsync(string peerTag, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(peerTag) || string.IsNullOrWhiteSpace(text)) return;

        var message = new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            PeerTag: peerTag,
            Body: text.Trim(),
            Mine: true,
            State: ChatMessage.Pending,
            SentMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _store.SaveMessage(message);
        Changed?.Invoke();

        await EnsureSessionAsync(peerTag, cancellationToken).ConfigureAwait(false);
        await TryDeliverAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Push everything still pending for a peer — called once a session comes up.</summary>
    public async Task FlushAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        foreach (var pending in _store.GetPendingMessages(peerTag))
            await TryDeliverAsync(pending, cancellationToken).ConfigureAwait(false);
    }

    // ── Send path ───────────────────────────────────────────────────────────────

    private async Task TryDeliverAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        if (_radio is null || !_signal.HasSession(message.PeerTag)) return;

        try
        {
            var sealedPayload = await _signal
                .EncryptAsync(message.PeerTag, Encoding.UTF8.GetBytes(message.Body), cancellationToken)
                .ConfigureAwait(false);

            var body = EncryptedPayloadCodec.Serialize(sealedPayload);
            var payload = new byte[Marker.Length + body.Length];
            Encoding.UTF8.GetBytes(Marker).CopyTo(payload, 0);
            body.CopyTo(payload, Marker.Length);

            var packet = new MeshPacket
            {
                Type = PacketType.Data,
                SourceUhid = _me.AetherTag,
                DestinationUhid = message.PeerTag,
                Ttl = 1,
                Payload = payload,
            };

            if (await _radio.SendPacketAsync(PacketSerializer.Serialize(packet)).ConfigureAwait(false))
            {
                _store.SetMessageState(message.Id, ChatMessage.Sent);
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // Stays pending and will be retried on the next flush rather than being lost.
            _log.LogWarning(ex, "Could not deliver message {Id} to {Peer}", message.Id, message.PeerTag);
        }
    }

    // ── Receive path ────────────────────────────────────────────────────────────

    private void OnPacket(byte[] bytes)
    {
        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return; }

        // The pre-key exchange owns its own packet types; hand those straight over.
        if (packet.Type is PacketType.PreKeyRequest or PacketType.PreKeyResponse)
        {
            _ = HandlePreKeyAsync(packet);
            return;
        }

        if (packet.Type != PacketType.Data) return;
        var payload = packet.Payload;
        if (payload is null || payload.Length <= Marker.Length) return;
        if (Encoding.UTF8.GetString(payload, 0, Marker.Length) != Marker) return;

        _ = ReceiveAsync(packet.SourceUhid, payload);
    }

    /// <summary>
    /// Answer a pre-key request. Our own bundle has to exist first — a node that has never been asked
    /// before still has to be able to reply, or the very first conversation could never start.
    /// </summary>
    private async Task HandlePreKeyAsync(MeshPacket packet)
    {
        await EnsureLocalBundleAsync(CancellationToken.None).ConfigureAwait(false);
        await _preKeys.HandleAsync(packet).ConfigureAwait(false);
    }

    private async Task ReceiveAsync(string? senderTag, byte[] payload)
    {
        if (string.IsNullOrEmpty(senderTag)) return;

        try
        {
            var body = payload.AsSpan(Marker.Length).ToArray();
            var sealedPayload = EncryptedPayloadCodec.Deserialize(body);
            var plaintext = await _signal.DecryptAsync(senderTag, sealedPayload).ConfigureAwait(false);

            _store.SaveMessage(new ChatMessage(
                Id: Guid.NewGuid().ToString("N"),
                PeerTag: senderTag,
                Body: Encoding.UTF8.GetString(plaintext),
                Mine: false,
                State: ChatMessage.Received,
                SentMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

            // Seeing their message means we can reach them — send anything we were holding.
            await FlushAsync(senderTag).ConfigureAwait(false);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // A message we cannot open is dropped, never shown as if it were readable.
            _log.LogWarning(ex, "Could not open a message from {Peer}", senderTag);
        }
    }

    private void OnBundleReceived(object? sender, PreKeyBundleReceivedEventArgs e)
    {
        if (e.Bundle is null) return;
        var peer = !string.IsNullOrEmpty(e.FromUhid) ? e.FromUhid : e.Bundle.Uhid;
        _ = AdoptBundleAsync(peer, e.Bundle, CancellationToken.None);
    }

    private async Task AdoptBundleAsync(string peerTag, AetherNet.Security.Models.PreKeyBundle bundle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(peerTag) || _signal.HasSession(peerTag)) return;

        try
        {
            await _signal.ProcessPreKeyBundleAsync(bundle, cancellationToken).ConfigureAwait(false);
            await FlushAsync(peerTag, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not start a secure session with {Peer}", peerTag);
        }
    }
}
