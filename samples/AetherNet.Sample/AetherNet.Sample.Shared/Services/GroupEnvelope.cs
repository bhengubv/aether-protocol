// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Sample.Shared.Data;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// What one member of a group says to another, inside the encryption.
/// <para>
/// It is a small versioned JSON object rather than a new packet type on the wire: a new packet type
/// would have to be added to all eight language SDKs and their byte-parity fixtures, and a group is
/// an application idea, not something the pipe needs to understand. The pipe carries bytes; who they
/// belong to is our business.
/// </para>
/// </summary>
public sealed class GroupEnvelope
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;

    /// <summary>"new" — this group exists and here is who is in it. "msg" — someone said something.</summary>
    [JsonPropertyName("k")] public string Kind { get; set; } = "msg";

    [JsonPropertyName("g")] public string GroupId { get; set; } = "";
    [JsonPropertyName("n")] public string? Name { get; set; }
    [JsonPropertyName("m")] public string[]? Members { get; set; }
    [JsonPropertyName("i")] public string? MessageId { get; set; }
    [JsonPropertyName("s")] public string? Sender { get; set; }
    [JsonPropertyName("b")] public string? Body { get; set; }

    /// <summary>
    /// The author's signature over this contribution. Nothing populates it yet — provenance is a
    /// rule the sample does not meet, and the tests that assert it are red on purpose.
    /// <para>
    /// It matters because attribution that cannot be checked is not attribution. A member's phone is
    /// told who wrote a message; without a signature it has no way to confirm it, and no third phone
    /// receiving the same message later can either.
    /// </para>
    /// </summary>
    [JsonPropertyName("sig")] public string? Signature { get; set; }

    /// <summary>
    /// Roughly where the contribution was made — coarse by design, because the privacy work
    /// deliberately strips precise location from everything else and provenance is not a reason to
    /// put it back. Unset today.
    /// </summary>
    [JsonPropertyName("geo")] public string? GeoHash { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string News(GroupRecord group, IReadOnlyList<string> members,
        Func<byte[], byte[]>? sign = null, string? geoHash = null) =>
        Seal(new GroupEnvelope
        {
            Kind = "new",
            GroupId = group.Id,
            Name = group.Name,
            Members = members.ToArray(),
            Sender = group.AdminTag,
            GeoHash = geoHash,
        }, sign);

    public static string Message(string groupId, string messageId, string sender, string body,
        Func<byte[], byte[]>? sign = null, string? geoHash = null) =>
        Seal(new GroupEnvelope
        {
            Kind = "msg",
            GroupId = groupId,
            MessageId = messageId,
            Sender = sender,
            Body = body,
            GeoHash = geoHash,
        }, sign);

    /// <summary>
    /// Sign the envelope over its own contents, then serialize it.
    /// <para>
    /// The signature covers the document with the signature field absent, so a verifier can strip it,
    /// re-serialize, and check — no separate canonical form to drift out of step. Attribution the
    /// receiver can check, rather than attribution it is simply told.
    /// </para>
    /// </summary>
    private static string Seal(GroupEnvelope envelope, Func<byte[], byte[]>? sign)
    {
        if (sign is not null)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
            envelope.Signature = Convert.ToBase64String(sign(body));
        }
        return JsonSerializer.Serialize(envelope, Options);
    }

    /// <summary>
    /// The bytes this envelope's signature covers — everything it says, minus the signature.
    /// A verifier rebuilds these and checks them against <see cref="Signature"/>.
    /// </summary>
    public byte[] SignedBody()
    {
        var signature = Signature;
        Signature = null;
        try { return JsonSerializer.SerializeToUtf8Bytes(this, Options); }
        finally { Signature = signature; }
    }

    /// <summary>Read one back. Anything malformed is dropped rather than half-applied.</summary>
    public static GroupEnvelope? Parse(string json)
    {
        try
        {
            var e = JsonSerializer.Deserialize<GroupEnvelope>(json, Options);
            return e is null || string.IsNullOrEmpty(e.GroupId) ? null : e;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
