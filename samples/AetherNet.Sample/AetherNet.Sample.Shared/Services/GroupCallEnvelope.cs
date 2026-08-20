// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// What one member of a group call says to another about the call itself.
///
/// <para>
/// A small versioned JSON object rather than a new packet type, for the same reason
/// <see cref="GroupEnvelope"/> is one: a new packet type has to be added to all eight language SDKs
/// and their byte-parity fixtures, and a group call is an application idea. The pipe carries bytes.
/// </para>
///
/// <para>
/// This only ever travels inside a member's own Signal session — never in the open. It carries the
/// call's master key, and anyone holding that can decrypt every stream in the call.
/// </para>
/// </summary>
public sealed class GroupCallEnvelope
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;

    /// <summary>What this says. See the constants below — anything else is ignored on arrival.</summary>
    [JsonPropertyName("k")] public string Kind { get; set; } = "";

    /// <summary>Which group is calling.</summary>
    [JsonPropertyName("g")] public string GroupId { get; set; } = "";

    /// <summary>Which call, so a second one cannot be mistaken for the first.</summary>
    [JsonPropertyName("c")] public string CallId { get; set; } = "";

    /// <summary>Who sent this.</summary>
    [JsonPropertyName("s")] public string Sender { get; set; } = "";

    /// <summary>
    /// The call's master secret, base64, present only on <see cref="Invite"/>.
    ///
    /// <para>
    /// Every participant derives their own sealing key from this and their own tag, so no two people
    /// ever seal under the same key however many join. See <see cref="CallMediaCipher.ForGroup"/>.
    /// </para>
    /// </summary>
    [JsonPropertyName("key")] public string? MasterKey { get; set; }

    /// <summary>Who is in the call as the sender understands it, so a joiner knows who to expect.</summary>
    [JsonPropertyName("p")] public string[]? Participants { get; set; }

    /// <summary>Whether the sender's camera is on. Only meaningful on <see cref="Camera"/>.</summary>
    [JsonPropertyName("cam")] public bool CameraOn { get; set; }

    /// <summary>"Come and join this call, here is the key."</summary>
    public const string Invite = "invite";

    /// <summary>"I am in." Sent to everyone, so every phone learns the membership without a server.</summary>
    public const string Accept = "accept";

    /// <summary>"Not now."</summary>
    public const string Decline = "decline";

    /// <summary>"I am going." A call carries on without whoever left.</summary>
    public const string Leave = "leave";

    /// <summary>"My camera went on, or off."</summary>
    public const string Camera = "camera";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    /// <summary>
    /// Read one off the wire.
    ///
    /// <para>
    /// Anything malformed, or of a kind this build does not know, comes back null rather than being
    /// coerced into something. This arrived from a radio and carries a key.
    /// </para>
    /// </summary>
    public static GroupCallEnvelope? Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            var e = JsonSerializer.Deserialize<GroupCallEnvelope>(body, Options);

            if (e is null) return null;
            if (string.IsNullOrEmpty(e.GroupId) || string.IsNullOrEmpty(e.CallId)) return null;
            if (string.IsNullOrEmpty(e.Sender)) return null;

            return e.Kind is Invite or Accept or Decline or Leave or Camera ? e : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
