// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Identity;

namespace AetherNetNodeService.Ipc;

/// <summary>
/// The operations a bound consumer can ask of the node, and the pushes the node sends back. One byte on
/// the wire (an Android <c>Message.What</c>), so it round-trips through any process boundary. Request codes
/// and event codes share one space but never collide — events start at 100.
/// </summary>
public enum NodeOp
{
    /// <summary>Request: this device's AetherTag.</summary>
    GetTag = 1,

    /// <summary>Request: the node's public key bytes.</summary>
    GetPublicKey = 2,

    /// <summary>Request: sign the argument bytes; the private key stays in the node.</summary>
    Sign = 3,

    /// <summary>Request: send a payload to a tag.</summary>
    Send = 4,

    /// <summary>Request: the most recent inbound messages.</summary>
    GetInbox = 5,

    /// <summary>Request: the current link status.</summary>
    GetLink = 6,

    /// <summary>Request: start receiving inbound / link / grant pushes on the reply channel.</summary>
    Subscribe = 7,

    /// <summary>Request: stop receiving pushes.</summary>
    Unsubscribe = 8,

    /// <summary>Request: the contacts this app wants kept reachable (replaces its set).</summary>
    Meet = 9,

    /// <summary>Request: this device's 24-word recovery phrase, for a person to write down.</summary>
    GetRecoveryPhrase = 10,

    /// <summary>Push: a message arrived.</summary>
    EventInbound = 100,

    /// <summary>Push: the link status changed.</summary>
    EventLink = 101,

    /// <summary>Push: this app's grant changed.</summary>
    EventGrant = 102,

    /// <summary>Push: the other side confirmed a message this app sent.</summary>
    EventDelivered = 103,
}

/// <summary>
/// The keys of the <c>Bundle</c> that carries one request or reply. Kept to single letters because a
/// <c>Bundle</c> key is a string on the wire and this crosses on every call.
/// </summary>
public static class NodeWireKeys
{
    /// <summary>Correlation id (long) — pairs a reply with its request on the client's one reply channel.</summary>
    public const string Correlation = "c";

    /// <summary>The request argument bytes (payload of <see cref="NodeOp.Sign"/>/<see cref="NodeOp.Send"/>, etc.).</summary>
    public const string Argument = "a";

    /// <summary>Reply: success flag (bool). When false, <see cref="ErrorCode"/> + <see cref="ErrorMessage"/> are set.</summary>
    public const string Ok = "k";

    /// <summary>Reply: the <see cref="AetherNodeErrorCode"/> as an int, when <see cref="Ok"/> is false.</summary>
    public const string ErrorCode = "e";

    /// <summary>Reply: a human message for the failure.</summary>
    public const string ErrorMessage = "m";

    /// <summary>Reply: the result bytes (encoded per the op — see <see cref="NodeWire"/>).</summary>
    public const string Result = "r";

    /// <summary>Request: an int argument (the <see cref="NodeOp.GetInbox"/> limit).</summary>
    public const string Limit = "l";
}

/// <summary>
/// Turns the bind-contract values into bytes and back — the one place the wire format of a tag, a link
/// snapshot, an inbox, a send result, and a message lives. Raw byte payloads (public key, signature,
/// sign input) cross as themselves; structured values cross as compact JSON. Every method is pure and
/// allocation-cheap, and the round trips are unit-tested off-device.
/// </summary>
public static class NodeWire
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── AetherTag ─────────────────────────────────────────────────────────────
    // The canonical "XXXXX-XXXXX" string, UTF-8. Empty bytes decode to default(AetherNetTag).

    public static byte[] EncodeTag(AetherNetTag tag) => Encoding.UTF8.GetBytes(tag.Value ?? string.Empty);

    public static AetherNetTag DecodeTag(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return default;
        }

        return AetherNetTag.TryParse(Encoding.UTF8.GetString(bytes), out var tag) ? tag : default;
    }

    // ── Recovery phrase ─────────────────────────────────────────────────────────
    // The 24 words, space-separated, UTF-8.

    public static byte[] EncodePhrase(string phrase) => Encoding.UTF8.GetBytes(phrase ?? string.Empty);

    public static string DecodePhrase(byte[]? bytes) =>
        bytes is { Length: > 0 } ? Encoding.UTF8.GetString(bytes) : string.Empty;

    // ── Send argument (tag + payload + the app's message id) ─────────────────────
    // An argument without an id (an older client) decodes to Guid.Empty; the node then assigns one.

    public static byte[] EncodeSendArgument(AetherNetTag to, ReadOnlyMemory<byte> payload)
        => EncodeSendArgument(to, payload, Guid.Empty);

    public static byte[] EncodeSendArgument(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId)
        => JsonBytes(new SendDto(to.Value ?? string.Empty, payload.ToArray(), messageId));

    public static (AetherNetTag To, byte[] Payload, Guid MessageId) DecodeSendArgument(byte[] bytes)
    {
        var dto = FromJson<SendDto>(bytes);
        var to = AetherNetTag.TryParse(dto.To, out var tag) ? tag : default;
        return (to, dto.Payload ?? [], dto.Id);
    }

    // ── Meet (the contacts to keep reachable) ────────────────────────────────────

    public static byte[] EncodeMeet(IReadOnlyList<NodeContact> contacts)
    {
        var dtos = new ContactDto[contacts.Count];
        for (var i = 0; i < dtos.Length; i++)
        {
            var c = contacts[i];
            dtos[i] = new ContactDto(c.Tag.Value ?? string.Empty, c.PublicKey, c.Mutual);
        }

        return JsonBytes(dtos);
    }

    public static IReadOnlyList<NodeContact> DecodeMeet(byte[] bytes)
    {
        var dtos = bytes is { Length: > 0 } ? FromJson<ContactDto[]>(bytes) ?? [] : [];
        var list = new List<NodeContact>(dtos.Length);
        foreach (var d in dtos)
        {
            // A contact whose tag does not parse is nobody the radios could find; drop it rather than guess.
            if (AetherNetTag.TryParse(d.Tag, out var tag))
            {
                list.Add(new NodeContact(tag, d.PublicKey, d.Mutual));
            }
        }

        return list;
    }

    // ── Delivered (the app's message id, for the push) ───────────────────────────

    public static byte[] EncodeDelivered(Guid messageId) => messageId.ToByteArray();

    public static Guid DecodeDelivered(byte[] bytes) => bytes is { Length: 16 } ? new Guid(bytes) : Guid.Empty;

    // ── OutboundResult ──────────────────────────────────────────────────────────

    public static byte[] EncodeOutbound(OutboundResult result)
        => JsonBytes(new OutboundDto(result.Accepted, result.Detail));

    public static OutboundResult DecodeOutbound(byte[] bytes)
    {
        var dto = FromJson<OutboundDto>(bytes);
        return new OutboundResult(dto.Accepted, dto.Detail);
    }

    // ── NodeLinkStatus ──────────────────────────────────────────────────────────

    public static byte[] EncodeLink(NodeLinkStatus status)
    {
        var radios = new RadioDto[status.Radios.Count];
        for (var i = 0; i < radios.Length; i++)
        {
            var r = status.Radios[i];
            radios[i] = new RadioDto(r.Name, r.Available, r.Linked, r.CarriesBps, r.Reason, r.Fixable, r.NeedsPermission);
        }

        return JsonBytes(new LinkDto(status.Linked, status.Radio, radios));
    }

    public static NodeLinkStatus DecodeLink(byte[] bytes)
    {
        var dto = FromJson<LinkDto>(bytes);
        var radios = new RadioStatus[dto.Radios?.Length ?? 0];
        for (var i = 0; i < radios.Length; i++)
        {
            var r = dto.Radios![i];
            radios[i] = new RadioStatus(r.Name, r.Available, r.Linked, r.CarriesBps)
            {
                Reason = r.Reason,
                Fixable = r.Fixable,
                NeedsPermission = r.NeedsPermission,
            };
        }

        return new NodeLinkStatus(dto.Linked, dto.Radio, radios);
    }

    // ── InboundMessage (single, for the push) ────────────────────────────────────

    public static byte[] EncodeInbound(InboundMessage message)
        => JsonBytes(ToDto(message));

    public static InboundMessage DecodeInbound(byte[] bytes)
        => FromDto(FromJson<InboundDto>(bytes));

    // ── Inbox (list) ──────────────────────────────────────────────────────────────

    public static byte[] EncodeInbox(IReadOnlyList<InboundMessage> messages)
    {
        var dtos = new InboundDto[messages.Count];
        for (var i = 0; i < dtos.Length; i++)
        {
            dtos[i] = ToDto(messages[i]);
        }

        return JsonBytes(dtos);
    }

    public static IReadOnlyList<InboundMessage> DecodeInbox(byte[] bytes)
    {
        var dtos = FromJson<InboundDto[]>(bytes) ?? [];
        var list = new InboundMessage[dtos.Length];
        for (var i = 0; i < dtos.Length; i++)
        {
            list[i] = FromDto(dtos[i]);
        }

        return list;
    }

    // ── GrantState (for the push) ────────────────────────────────────────────────

    public static byte[] EncodeGrant(GrantState state) => BitConverter.GetBytes((int)state);

    public static GrantState DecodeGrant(byte[] bytes)
        => bytes is { Length: >= 4 } ? (GrantState)BitConverter.ToInt32(bytes, 0) : GrantState.Absent;

    // ── helpers ────────────────────────────────────────────────────────────────

    private static InboundDto ToDto(InboundMessage m)
        => new(m.From.Value ?? string.Empty, m.Payload.ToArray(), m.Kind, m.ReceivedAt, m.Id);

    private static InboundMessage FromDto(InboundDto d)
    {
        var from = AetherNetTag.TryParse(d.From, out var tag) ? tag : default;
        return new InboundMessage(from, d.Payload ?? [], d.Kind ?? string.Empty, d.ReceivedAt, d.Id);
    }

    private static byte[] JsonBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);

    private static T FromJson<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Json)!;

    private sealed record SendDto(string To, byte[] Payload, Guid Id = default);

    private sealed record ContactDto(string Tag, byte[]? PublicKey, bool Mutual);

    private sealed record OutboundDto(bool Accepted, string? Detail);

    // Reason, Fixable and NeedsPermission are optional so an older service, which never sends them, still decodes.
    private sealed record RadioDto(
        string Name, bool Available, bool Linked, long CarriesBps,
        string? Reason = null, bool Fixable = false, bool NeedsPermission = false);

    private sealed record LinkDto(bool Linked, string? Radio, RadioDto[]? Radios);

    private sealed record InboundDto(string From, byte[] Payload, string Kind, DateTimeOffset ReceivedAt, Guid Id);
}
