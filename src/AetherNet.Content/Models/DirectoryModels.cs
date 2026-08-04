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

    /// <summary>
    /// Monotonic version of this name→descriptor binding. An authenticated publish MUST increase it; a
    /// receiver rejects a binding whose version is not strictly greater than the newest authenticated one
    /// it already holds (anti-rollback). 0 for unsigned legacy publishes.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// Base64 of the author's 32-byte Ed25519 public key. Non-null marks an AUTHENTICATED binding: the
    /// receiver verifies <see cref="Signature"/> over the canonical body
    /// (see <see cref="AetherNet.Content.NameBindingCodec"/>) before trusting it, and refuses a later
    /// binding for the same name signed by a different key. Null = unsigned legacy binding.
    /// </summary>
    public string? AuthorPublicKey { get; set; }

    /// <summary>
    /// Base64 of the 64-byte Ed25519 signature over the canonical binding body
    /// {nameHash, authorPublicKey, version, descriptor.rootHash}. Null = unsigned legacy binding.
    /// </summary>
    public string? Signature { get; set; }
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

    /// <summary>Base64 Ed25519 author key when the announce was AUTHENTICATED; null for unsigned.</summary>
    public string? AuthorPublicKey { get; init; }

    /// <summary>Binding version (0 for unsigned).</summary>
    public long Version { get; init; }
}

/// <summary>
/// A resolved name binding: the content descriptor plus — when the binding was AUTHENTICATED — the
/// author key, version, and signature the directory verified on ingest. Returned by
/// <see cref="IDirectoryService.ResolveBindingAsync"/>.
/// </summary>
public sealed record NameBinding(
    ContentDescriptor Descriptor,
    byte[]? AuthorPublicKey,
    long Version,
    byte[]? Signature,
    bool Authenticated);
