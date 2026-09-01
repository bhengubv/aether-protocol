// SPDX-License-Identifier: MIT

namespace AetherNet.Browser;

/// <summary>
/// Where this device keeps cards — the ones its owner wrote, and the ones they hold.
///
/// <para>
/// An interface rather than a database, because the browser should not decide how a device stores
/// things. A phone has SQLite, a desktop may have a file, Circle OS will have its own vault, and a
/// test wants none of them. What all of them owe the browser is the same short list, and it is short
/// on purpose: a seam this narrow can be implemented in an afternoon by somebody who has never read
/// the rest of this library.
/// </para>
///
/// <para>
/// <b>Durability is the contract, not an implementation detail.</b> A card is an object somebody
/// owns: it opens with no signal, with its author gone, and it opens next year. A store that forgets
/// on restart satisfies every signature here and breaks the only promise that matters — so an
/// implementation that cannot persist should say so rather than pretend.
/// </para>
/// </summary>
public interface ICardStore
{
    /// <summary>The pages this device's owner wrote, as JSON, or null if they have written none.</summary>
    /// <remarks>
    /// One blob rather than a row per page. A page is a few hundred bytes and a person has a handful;
    /// a schema would buy nothing and would need migrating every time the card model grew a block.
    /// </remarks>
    string? GetPages();

    /// <summary>Replace the whole set of pages this device's owner wrote.</summary>
    void SetPages(string json);

    /// <summary>The decks this device's owner has gathered, as JSON, or null if they have none.</summary>
    /// <remarks>
    /// One blob, for the same reason as the pages: a person has a handful of decks and a schema would
    /// need migrating every time a deck grew a field. See <see cref="Decks"/>.
    /// </remarks>
    string? GetDecks();

    /// <summary>Replace the whole set of decks.</summary>
    void SetDecks(string json);

    /// <summary>Addresses this device wants but has not been able to reach yet, as one JSON blob.</summary>
    /// <remarks>
    /// The bridge across distance. Somebody far away hands you their address — over a message, a
    /// poster, a spoken tag — but their phone is not in range and there is no relay yet, so the card
    /// cannot be fetched in that moment. Keeping the address means the reach is not lost: it is tried
    /// again the next time the two of you are near, and, once the relay layer exists, on its own. An
    /// address that is reached becomes a held card and leaves this list. See <see cref="Wanted"/>.
    /// </remarks>
    string? GetWanted();

    /// <summary>Replace the whole set of wanted addresses.</summary>
    void SetWanted(string json);

    /// <summary>The name its owner goes by, if they have given one.</summary>
    string? GetOwnerName();

    /// <summary>Keep a card written by somebody else, replacing an older copy of the same card.</summary>
    /// <remarks>
    /// <b>Newer only.</b> A later version from the same author replaces what is held; an earlier one
    /// must not, or any holder who has been out of range could roll a card back to a stale copy just
    /// by meeting its owner.
    /// </remarks>
    void HoldCard(HeldCard card);

    /// <summary>Every card this device holds, most recently acquired first.</summary>
    IReadOnlyList<HeldCard> GetHeldCards();

    /// <summary>Whether this device holds the card at that address.</summary>
    bool HoldsCard(string address);

    /// <summary>Let a card go. It stays on every other device that holds it.</summary>
    bool DropCard(string address);
}

/// <summary>
/// A card written by somebody else that this device holds — and can serve on their behalf.
/// </summary>
/// <param name="Address">The <c>aether://</c> address it answers at, under its author's tag.</param>
/// <param name="AuthorTag">Who wrote it. Derived from <paramref name="AuthorKey"/>, never claimed.</param>
/// <param name="AuthorKey">Their public key — what a third device checks the signature against.</param>
/// <param name="Name">The page name under the author's tag.</param>
/// <param name="Title">What it calls itself, so a deck reads without opening every card.</param>
/// <param name="Version">The author's version. A later one replaces this; an earlier one cannot.</param>
/// <param name="RootHash">The content hash the bytes verify against.</param>
/// <param name="Signature">The author's signature over the binding. Passing it on carries this along.</param>
/// <param name="Descriptor">The content manifest, as JSON, so chunks verify without the author present.</param>
/// <param name="GotMs">When this device came to hold it, in Unix milliseconds.</param>
/// <param name="GotFrom">Whose device handed it over — which may not be the author. That is the point.</param>
public sealed record HeldCard(
    string Address,
    string AuthorTag,
    byte[] AuthorKey,
    string Name,
    string Title,
    long Version,
    string RootHash,
    byte[] Signature,
    string Descriptor,
    long GotMs,
    string GotFrom)
{
    /// <summary>When this device came to hold it.</summary>
    public DateTimeOffset GotAt => DateTimeOffset.FromUnixTimeMilliseconds(GotMs).ToLocalTime();
}
