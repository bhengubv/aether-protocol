// SPDX-License-Identifier: MIT

namespace AetherNet.Channels;

/// <summary>
/// JSON payload for <see cref="Protocol.PacketType.ChannelMessage"/> packets. Wire format: UTF-8 JSON
/// with snake_case property names, field order channel_id, message_id, sender_uhid, content, sent_at_ms,
/// no whitespace, lowercase-dashed UUID, sent_at_ms a bare integer. Byte-identity is locked by
/// fixtures/channels/vectors.json (with ASCII content; escaping of non-ASCII content follows standard JSON).
///
/// <para>A named channel is an application-layer pub/sub topic ("res-floor-3", a society, a project team).
/// Publishing floods a <c>ChannelMessage</c>; nodes subscribed to <see cref="ChannelId"/> surface it. The
/// original author is carried in <see cref="SenderUhid"/> so it survives relay hops (the enclosing packet's
/// SourceUhid changes at each hop).</para>
/// </summary>
public sealed class ChannelMessagePayload
{
    /// <summary>Application-defined channel identifier (opaque to the protocol).</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Unique id for this message — used for flood de-duplication.</summary>
    public Guid MessageId { get; set; }

    /// <summary>UHID of the original author (preserved across relay hops).</summary>
    public string SenderUhid { get; set; } = string.Empty;

    /// <summary>Message body.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Unix timestamp in milliseconds when the author published the message.</summary>
    public long SentAtMs { get; set; }
}

/// <summary>Event raised when a channel message arrives on a channel this node is subscribed to.</summary>
public sealed class ChannelMessageReceived
{
    /// <summary>Channel the message was published to.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Unique id of the message.</summary>
    public Guid MessageId { get; set; }

    /// <summary>UHID of the original author.</summary>
    public string SenderUhid { get; set; } = string.Empty;

    /// <summary>Message body.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Unix-ms timestamp the author published the message.</summary>
    public long SentAtMs { get; set; }
}
