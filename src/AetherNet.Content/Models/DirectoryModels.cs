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
    /// <summary>
    /// PRIVACY: the SALTED HASH of the application-layer name being announced — never the plaintext.
    /// The directory is a private, DNS-style resolver: only a node that already knows the exact name
    /// (and hashes it identically) can match this, so an eavesdropper cannot harvest what names exist.
    /// NOTE: a fixed-salt hash stops passive harvesting; a determined attacker can still dictionary-guess
    /// low-entropy names. Airtight privacy for guessable names would need PIR — out of scope here.
    /// </summary>
    public string NameHash { get; set; } = string.Empty;

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
    /// <summary>PRIVACY: the SALTED HASH of the name being queried — never the plaintext.
    /// See <see cref="NamePublishPayload.NameHash"/>.</summary>
    public string NameHash { get; set; } = string.Empty;

    /// <summary>Correlation id. Echoed by responders in <see cref="NamePublishPayload.InResponseToQueryId"/>
    /// so the querier can match responses to outstanding queries.</summary>
    public Guid QueryId { get; set; } = Guid.NewGuid();
}

/// <summary>Event payload for <see cref="IDirectoryService.EntryAnnounced"/> — raised when a
/// <see cref="AetherNet.Protocol.PacketType.NamePublish"/> packet arrives and the local catalogue
/// learns a new (or replaced) name → descriptor binding.</summary>
public sealed class DirectoryEntryAnnouncedEventArgs : EventArgs
{
    /// <summary>The salted hash of the newly-learned name. The wire never carries the plaintext;
    /// this node knows the plaintext only for names it published or queried itself.</summary>
    public string NameHash { get; init; } = string.Empty;

    /// <summary>The descriptor the name resolves to.</summary>
    public ContentDescriptor Descriptor { get; init; } = new();

    /// <summary>UHID of the peer that emitted the announcement.</summary>
    public string SourceUhid { get; init; } = string.Empty;

    /// <summary>UTC time the announcement arrived locally.</summary>
    public DateTime AnnouncedAtUtc { get; init; } = DateTime.UtcNow;
}
