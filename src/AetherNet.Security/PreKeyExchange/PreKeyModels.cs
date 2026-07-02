// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;
using AetherNet.Security.Models;

namespace AetherNet.PreKeys;

/// <summary>
/// JSON payload for <see cref="AetherNet.Protocol.PacketType.PreKeyRequest"/> (25) — a directed ask
/// for a peer's published <see cref="PreKeyBundle"/> so the requester can start an X3DH session while
/// the peer is offline. Wire: UTF-8 JSON, field order request_id, requester_uhid, no whitespace,
/// lowercase-dashed UUID. Byte-identity gate: fixtures/prekey/vectors.json.
/// </summary>
public sealed class PreKeyRequestPayload
{
    /// <summary>Correlation id minted by the requester; echoed in the response.</summary>
    [JsonPropertyName("request_id")] public Guid RequestId { get; set; }

    /// <summary>UHID of the node asking for the bundle — where the response is sent.</summary>
    [JsonPropertyName("requester_uhid")] public string RequesterUhid { get; set; } = string.Empty;
}

/// <summary>
/// JSON payload for <see cref="AetherNet.Protocol.PacketType.PreKeyResponse"/> (26) — the responder's
/// published <see cref="PreKeyBundle"/> carried back to the requester. All public-key material is
/// STANDARD base64 (System.Text.Json byte[] default). Field order (pinned via JsonPropertyName):
/// request_id, uhid, identity_key, identity_key_x25519, pre_key_id, pre_key, signed_pre_key_id,
/// signed_pre_key, signed_pre_key_signature. Byte-identity gate: fixtures/prekey/vectors.json.
/// </summary>
public sealed class PreKeyResponsePayload
{
    [JsonPropertyName("request_id")] public Guid RequestId { get; set; }
    [JsonPropertyName("uhid")] public string Uhid { get; set; } = string.Empty;
    [JsonPropertyName("identity_key")] public byte[] IdentityKey { get; set; } = Array.Empty<byte>();
    [JsonPropertyName("identity_key_x25519")] public byte[] IdentityKeyX25519 { get; set; } = Array.Empty<byte>();
    [JsonPropertyName("pre_key_id")] public int PreKeyId { get; set; }
    [JsonPropertyName("pre_key")] public byte[] PreKey { get; set; } = Array.Empty<byte>();
    [JsonPropertyName("signed_pre_key_id")] public int SignedPreKeyId { get; set; }
    [JsonPropertyName("signed_pre_key")] public byte[] SignedPreKey { get; set; } = Array.Empty<byte>();
    [JsonPropertyName("signed_pre_key_signature")] public byte[] SignedPreKeySignature { get; set; } = Array.Empty<byte>();

    /// <summary>Project this wire payload into the security-layer <see cref="PreKeyBundle"/>.</summary>
    public PreKeyBundle ToBundle() => new(
        Uhid, IdentityKey, IdentityKeyX25519, PreKeyId, PreKey, SignedPreKeyId, SignedPreKey, SignedPreKeySignature);

    /// <summary>Build a response payload from a bundle, echoing the originating request id.</summary>
    public static PreKeyResponsePayload FromBundle(Guid requestId, PreKeyBundle b) => new()
    {
        RequestId = requestId,
        Uhid = b.Uhid,
        IdentityKey = b.IdentityKey,
        IdentityKeyX25519 = b.IdentityKeyX25519,
        PreKeyId = b.PreKeyId,
        PreKey = b.PreKey,
        SignedPreKeyId = b.SignedPreKeyId,
        SignedPreKey = b.SignedPreKey,
        SignedPreKeySignature = b.SignedPreKeySignature,
    };
}

/// <summary>Raised when a peer's pre-key bundle arrives in a <c>PreKeyResponse</c>.</summary>
public sealed class PreKeyBundleReceivedEventArgs : EventArgs
{
    /// <summary>The request id echoed from the original <c>PreKeyRequest</c> (Guid.Empty if unsolicited).</summary>
    public Guid RequestId { get; init; }

    /// <summary>UHID of the peer that sent the bundle.</summary>
    public string FromUhid { get; init; } = string.Empty;

    /// <summary>The received pre-key bundle — feed to ISignalProtocolService.ProcessPreKeyBundleAsync.</summary>
    public PreKeyBundle Bundle { get; init; } = null!;
}
