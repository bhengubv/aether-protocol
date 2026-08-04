// SPDX-License-Identifier: MIT

using AetherNet.Content.Models;

namespace AetherNet.Cards;

/// <summary>
/// A card — a signed, versioned, content-addressed pointer binding an author-owned <see cref="Name"/>
/// to a content blob (the <see cref="Descriptor"/>). The blob's bytes are carried and verified by
/// <c>IContentService</c> (content addressing = tamper-evidence); the name→content binding is
/// authenticated by <see cref="Signature"/> over {nameHash, authorPublicKey, version, rootHash}.
/// A card can live at many hosts at once and be reached through any of them — no server owns it.
/// </summary>
public sealed record Card(
    string Name,
    long Version,
    byte[] AuthorPublicKey,
    ContentDescriptor Descriptor,
    byte[] Signature);
