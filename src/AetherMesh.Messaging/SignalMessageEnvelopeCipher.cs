// SPDX-License-Identifier: MIT

using AetherMesh.Security.Models;
using AetherMesh.Security.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherMesh.Messaging;

/// <summary>
/// <see cref="IMessageEnvelopeCipher"/> implementation backed by
/// <see cref="ISignalProtocolService"/>. Provides real Signal-Protocol
/// end-to-end encryption (X3DH + Double Ratchet) for the messaging layer.
///
/// The wire envelope between encrypt and decrypt is a JSON-serialized
/// <see cref="EncryptedPayload"/> — chosen so the messaging layer can hand
/// a single opaque <c>byte[]</c> to its transports. The format is internal
/// to this cipher; consumers should treat it as opaque ciphertext.
///
/// **Security rule** (Signal-canonical): if there is no session with the
/// recipient yet, <see cref="EncryptAsync"/> returns null. The messaging
/// layer interprets that as "queue the message" and never falls back to
/// any weaker scheme. Hosts that want to *establish* a session must do so
/// out of band by calling <see cref="ISignalProtocolService.ProcessPreKeyBundleAsync"/>
/// against a published <see cref="PreKeyBundle"/> from the recipient.
/// </summary>
public sealed class SignalMessageEnvelopeCipher : IMessageEnvelopeCipher
{
    private readonly ISignalProtocolService _signal;
    private readonly ILogger<SignalMessageEnvelopeCipher> _logger;

    public SignalMessageEnvelopeCipher(
        ISignalProtocolService signal,
        ILogger<SignalMessageEnvelopeCipher>? logger = null)
    {
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _logger = logger ?? NullLogger<SignalMessageEnvelopeCipher>.Instance;
    }

    /// <inheritdoc />
    public async Task<byte[]?> EncryptAsync(string recipientUhid, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(recipientUhid);
        ArgumentNullException.ThrowIfNull(plaintext);

        if (!_signal.HasSession(recipientUhid))
        {
            _logger.LogDebug("No Signal session with {Recipient} — queuing instead of sending insecurely", recipientUhid);
            return null;
        }

        try
        {
            var payload = await _signal.EncryptAsync(recipientUhid, plaintext, cancellationToken).ConfigureAwait(false);
            return EncryptedPayloadCodec.Serialize(payload);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // Session disappeared between HasSession and EncryptAsync, or the
            // session's chain state was corrupted. Returning null causes the
            // messaging layer to queue the message for re-attempt. Never
            // surface the failure as a successful (insecure) send.
            _logger.LogWarning(ex, "Signal encryption failed for {Recipient}, queuing", recipientUhid);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> DecryptAsync(string senderUhid, byte[] ciphertext, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(senderUhid);
        ArgumentNullException.ThrowIfNull(ciphertext);

        EncryptedPayload payload;
        try
        {
            payload = EncryptedPayloadCodec.Deserialize(ciphertext);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Malformed envelope from {Sender}", senderUhid);
            return null;
        }

        try
        {
            return await _signal.DecryptAsync(senderUhid, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // Session missing, MAC failure, or counter gap. We DROP — never
            // surface a failed decrypt to the application as plaintext.
            _logger.LogWarning(ex, "Signal decryption failed for ciphertext from {Sender}, dropping", senderUhid);
            return null;
        }
    }

    /// <inheritdoc />
    public bool HasSession(string peerUhid) => _signal.HasSession(peerUhid);
}
