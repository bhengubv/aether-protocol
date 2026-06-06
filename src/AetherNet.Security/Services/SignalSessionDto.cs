// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherNet.Security.Services;

/// <summary>
/// Serialisable snapshot of <see cref="SignalSession"/>. Used by
/// <see cref="ISignalSessionStore"/> implementations to persist the full
/// Double-Ratchet state across a process restart without exposing the
/// internal session class.
///
/// New fields can be added to the end of this record without breaking
/// previously-stored snapshots — <see cref="JsonSerializerOptions"/> tolerates
/// missing fields by defaulting them. Existing fields must never change
/// shape: the on-disk format is part of the persistence contract.
/// </summary>
internal sealed record SignalSessionDto(
    [property: JsonPropertyName("rk")] byte[] RootKey,
    [property: JsonPropertyName("cks")] byte[]? SendChainKey,
    [property: JsonPropertyName("ckr")] byte[]? RecvChainKey,
    [property: JsonPropertyName("ns")] int SendCounter,
    [property: JsonPropertyName("nr")] int RecvCounter,
    [property: JsonPropertyName("pn")] int PreviousChainCount,
    [property: JsonPropertyName("dhs_priv")] byte[] MyEphemeralPriv,
    [property: JsonPropertyName("dhs_pub")] byte[] MyEphemeralPub,
    [property: JsonPropertyName("dhr")] byte[]? RemoteEphemeralPub,
    [property: JsonPropertyName("mkskipped")] Dictionary<string, byte[]> SkippedMessageKeys,
    [property: JsonPropertyName("pending_pkmsg")] bool PendingPreKeyMessage,
    [property: JsonPropertyName("init_ik")] byte[] InitiatorIdentityKeyX25519,
    [property: JsonPropertyName("used_spk_id")] int UsedSignedPreKeyId,
    [property: JsonPropertyName("used_opk_id")] int UsedOneTimePreKeyId);

/// <summary>
/// Conversion helpers between <see cref="SignalSession"/> and the
/// JSON-serialisable <see cref="SignalSessionDto"/>. Lives in the same
/// assembly as <see cref="SignalSession"/> so it can reach the internal
/// fields directly without exposing them.
/// </summary>
internal static class SignalSessionSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static byte[] Serialize(SignalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var dto = new SignalSessionDto(
            RootKey: session.RootKey,
            SendChainKey: session.SendChainKey,
            RecvChainKey: session.RecvChainKey,
            SendCounter: session.SendCounter,
            RecvCounter: session.RecvCounter,
            PreviousChainCount: session.PreviousChainCount,
            MyEphemeralPriv: session.MyEphemeralPriv,
            MyEphemeralPub: session.MyEphemeralPub,
            RemoteEphemeralPub: session.RemoteEphemeralPub,
            // Defensive copy: the session continues to mutate after serialise.
            SkippedMessageKeys: new Dictionary<string, byte[]>(session.SkippedMessageKeys),
            PendingPreKeyMessage: session.PendingPreKeyMessage,
            InitiatorIdentityKeyX25519: session.InitiatorIdentityKeyX25519,
            UsedSignedPreKeyId: session.UsedSignedPreKeyId,
            UsedOneTimePreKeyId: session.UsedOneTimePreKeyId);

        return JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
    }

    public static SignalSession? Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) return null;

        var dto = JsonSerializer.Deserialize<SignalSessionDto>(bytes, JsonOptions);
        if (dto is null) return null;

        var session = new SignalSession
        {
            RootKey = dto.RootKey ?? [],
            SendChainKey = dto.SendChainKey,
            RecvChainKey = dto.RecvChainKey,
            SendCounter = dto.SendCounter,
            RecvCounter = dto.RecvCounter,
            PreviousChainCount = dto.PreviousChainCount,
            MyEphemeralPriv = dto.MyEphemeralPriv ?? [],
            MyEphemeralPub = dto.MyEphemeralPub ?? [],
            RemoteEphemeralPub = dto.RemoteEphemeralPub,
            PendingPreKeyMessage = dto.PendingPreKeyMessage,
            InitiatorIdentityKeyX25519 = dto.InitiatorIdentityKeyX25519 ?? [],
            UsedSignedPreKeyId = dto.UsedSignedPreKeyId,
            UsedOneTimePreKeyId = dto.UsedOneTimePreKeyId,
        };

        if (dto.SkippedMessageKeys is not null)
        {
            foreach (var (k, v) in dto.SkippedMessageKeys)
                session.SkippedMessageKeys[k] = v;
        }

        return session;
    }
}
