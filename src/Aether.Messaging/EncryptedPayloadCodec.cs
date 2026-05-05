// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using Aether.Security.Models;

namespace Aether.Messaging;

/// <summary>
/// JSON wire codec for <see cref="EncryptedPayload"/>. Used by
/// <see cref="SignalMessageEnvelopeCipher"/> to flatten a payload into a
/// single <c>byte[]</c> the messaging-layer transport can carry, and to
/// reverse it on the far side.
///
/// Compact JSON (single-letter keys) keeps the per-message overhead small
/// while keeping the wire format human-readable for debugging. Unknown
/// keys are tolerated on deserialize — forward-compatible with future
/// envelope additions.
/// </summary>
public static class EncryptedPayloadCodec
{
    private const string KeyCiphertext = "c";
    private const string KeyNonce = "n";
    private const string KeyMessageType = "t";
    private const string KeySender = "s";
    private const string KeyCounter = "k";
    private const string KeyInitiatorIdentity = "ik";
    private const string KeyInitiatorEphemeral = "ek";
    private const string KeyRatchetPublic = "re";
    private const string KeyPreviousChainCount = "pn";
    private const string KeyUsedSpkId = "spki";
    private const string KeyUsedOpkId = "opki";

    public static byte[] Serialize(EncryptedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var obj = new
        {
            c = Convert.ToBase64String(payload.Ciphertext),
            n = Convert.ToBase64String(payload.Nonce),
            t = payload.MessageType,
            s = payload.SenderUhid,
            k = payload.Counter,
            ik = payload.InitiatorIdentityKeyX25519 == null
                ? null
                : Convert.ToBase64String(payload.InitiatorIdentityKeyX25519),
            ek = payload.InitiatorEphemeralKeyX25519 == null
                ? null
                : Convert.ToBase64String(payload.InitiatorEphemeralKeyX25519),
            re = payload.SenderEphemeralKeyX25519 == null
                ? null
                : Convert.ToBase64String(payload.SenderEphemeralKeyX25519),
            pn = payload.PreviousChainCount,
            spki = payload.UsedSignedPreKeyId,
            opki = payload.UsedOneTimePreKeyId,
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
    }

    public static EncryptedPayload Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        return new EncryptedPayload(
            Ciphertext: GetRequiredBase64(root, KeyCiphertext),
            Nonce: GetRequiredBase64(root, KeyNonce),
            MessageType: root.GetProperty(KeyMessageType).GetInt32(),
            SenderUhid: root.GetProperty(KeySender).GetString()!,
            Counter: root.GetProperty(KeyCounter).GetInt32(),
            InitiatorIdentityKeyX25519: GetOptionalBase64(root, KeyInitiatorIdentity),
            InitiatorEphemeralKeyX25519: GetOptionalBase64(root, KeyInitiatorEphemeral),
            UsedSignedPreKeyId: GetOptionalInt32(root, KeyUsedSpkId),
            UsedOneTimePreKeyId: GetOptionalInt32(root, KeyUsedOpkId),
            SenderEphemeralKeyX25519: GetOptionalBase64(root, KeyRatchetPublic),
            PreviousChainCount: GetOptionalInt32(root, KeyPreviousChainCount));
    }

    private static byte[] GetRequiredBase64(JsonElement root, string key)
    {
        return Convert.FromBase64String(root.GetProperty(key).GetString()!);
    }

    private static byte[]? GetOptionalBase64(JsonElement root, string key)
    {
        return root.TryGetProperty(key, out var elem) && elem.ValueKind == JsonValueKind.String
            ? Convert.FromBase64String(elem.GetString()!)
            : null;
    }

    private static int GetOptionalInt32(JsonElement root, string key)
    {
        return root.TryGetProperty(key, out var elem) && elem.ValueKind == JsonValueKind.Number
            ? elem.GetInt32()
            : 0;
    }
}
