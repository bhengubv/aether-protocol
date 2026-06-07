// SPDX-License-Identifier: MIT

namespace AetherNet.Content.Models;

/// <summary>
/// Wire payload for <see cref="AetherNet.Protocol.PacketType.NamePublish"/>.
/// Serialized as JSON with snake_case property names for cross-language interop.
///
/// <para>
/// Two modes:
/// <list type="bullet">
///   <item>Unsolicited broadcast: the publisher emits this on <see cref="IDirectoryService.PublishAsync"/>.
///         <see cref="InResponseToQueryId"/> is null.</item>
///   <item>Query response: a peer that holds the name emits this in unicast back to a querier.
///         <see cref="InResponseToQueryId"/> carries the query's correlation id.</item>
/// </list>
/// </para>
/// </summary>
public sealed class NamePublishPayload
{
    /// <summary>The application-layer name being announced.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The full descriptor that the name resolves to.</summary>
    public ContentDescriptor Descriptor { get; set; } = new();

    /// <summary>If non-null, this is a unicast response to a prior <see cref="AetherNet.Protocol.PacketType.NameQuery"/>
    /// whose <see cref="NameQueryPayload.QueryId"/> matched this value. If null, the publish is unsolicited.</summary>
    public Guid? InResponseToQueryId { get; set; }
}

/// <summary>
/// Wire payload for <see cref="AetherNet.Protocol.PacketType.NameQuery"/>. A broadcast
/// request asking peers to send a <see cref="NamePublishPayload"/> for the named entry
/// back to the sender, correlated by <see cref="QueryId"/>.
/// Serialized as JSON with snake_case property names for cross-language interop.
/// </summary>
public sealed class NameQueryPayload
{
    /// <summary>The application-layer name being queried.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Correlation id. Echoed by responders in <see cref="NamePublishPayload.InResponseToQueryId"/>
    /// so the querier can match responses to outstanding queries.</summary>
    public Guid QueryId { get; set; } = Guid.NewGuid();
}

/// <summary>Event payload for <see cref="IDirectoryService.EntryAnnounced"/> — raised when a
/// <see cref="AetherNet.Protocol.PacketType.NamePublish"/> packet arrives and the local catalogue
/// learns a new (or replaced) name → descriptor binding.</summary>
public sealed class DirectoryEntryAnnouncedEventArgs : EventArgs
{
    /// <summary>The newly-learned application-layer name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The descriptor the name resolves to.</summary>
    public ContentDescriptor Descriptor { get; init; } = new();

    /// <summary>UHID of the peer that emitted the announcement.</summary>
    public string SourceUhid { get; init; } = string.Empty;

    /// <summary>UTC time the announcement arrived locally.</summary>
    public DateTime AnnouncedAtUtc { get; init; } = DateTime.UtcNow;
}
