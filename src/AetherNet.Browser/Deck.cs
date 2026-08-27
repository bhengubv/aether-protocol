// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Cards;
using AetherNet.Content.Models;
using AetherNet.Identity;

namespace AetherNet.Browser;

/// <summary>
/// The cards this phone holds — written by other people, kept here, and served on from here.
///
/// <para>
/// <b>A card is an object, not an address.</b> Somebody hands you theirs and it is yours: it opens
/// with no signal, with the author asleep, unreachable or gone, and it opens the same way in a year.
/// That is the whole difference between a card and a link, and it only holds if the thing survives
/// this app being closed — so the deck is a table in the device's own database rather than a list in
/// memory.
/// </para>
///
/// <para>
/// <b>Holding it means being able to pass it on.</b> Everything kept here is what a third phone needs
/// to check the card without ever meeting its author: their public key, their version, their
/// signature over the name binding, and the descriptor the bytes verify against. Passing a card on
/// therefore proves nothing about whoever passed it and everything about whoever wrote it — which is
/// exactly why it is safe to accept one from a stranger, and why spread can come unstuck from origin.
/// </para>
///
/// <para>
/// Nothing here is trusted because it is stored. A card is re-verified from its signature and its
/// hashes every time it is opened; the deck is a place to keep bytes, not a place where things become
/// true.
/// </para>
/// </summary>
public sealed class Deck
{
    private static readonly JsonSerializerOptions Options = new();

    private readonly ICardStore _store;

    public Deck(ICardStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Raised when a card is taken in or let go.</summary>
    public event Action? Changed;

    /// <summary>Every card this phone holds, most recently acquired first.</summary>
    public IReadOnlyList<HeldCard> All => _store.GetHeldCards();

    /// <summary>How many cards are in the deck.</summary>
    public int Count => All.Count;

    /// <summary>Whether this phone already holds the card at this address.</summary>
    public bool Holds(string? address) =>
        !string.IsNullOrWhiteSpace(address) && _store.HoldsCard(address);

    /// <summary>
    /// Take a card into the deck.
    /// </summary>
    /// <param name="address">Where it answers, under its author's tag.</param>
    /// <param name="card">The signed binding, exactly as the author made it.</param>
    /// <param name="title">What it calls itself — so a deck reads without opening every card.</param>
    /// <param name="from">
    ///   Whose phone handed it over. Often not the author, and worth keeping: it is the difference
    ///   between "I met them" and "somebody who met them met me".
    /// </param>
    public void Hold(string address, Card card, string? title, string? from = null)
    {
        if (string.IsNullOrWhiteSpace(address) || card is null) return;

        var authorTag = AetherNetTag.FromPublicKey(card.AuthorPublicKey).Value;

        _store.HoldCard(new HeldCard(
            Address: address,
            AuthorTag: authorTag,
            AuthorKey: card.AuthorPublicKey,
            Name: card.Name,
            Title: title ?? "",
            Version: card.Version,
            RootHash: card.Descriptor.RootHash,
            Signature: card.Signature,
            Descriptor: JsonSerializer.Serialize(card.Descriptor, Options),
            GotMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            GotFrom: from ?? ""));

        Changed?.Invoke();
    }

    /// <summary>
    /// Let a card go.
    /// </summary>
    /// <remarks>
    /// Only from this phone. Every other phone that holds it still does, and the author still has it —
    /// there is no way to un-give a card, which is a property of the thing rather than a gap.
    /// </remarks>
    public bool Drop(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        if (!_store.DropCard(address)) return false;

        Changed?.Invoke();
        return true;
    }

    /// <summary>The card this phone holds at that address, if it holds one.</summary>
    public HeldCard? Get(string? address) =>
        address is { Length: > 0 } ? All.FirstOrDefault(c => c.Address == address) : null;

    /// <summary>
    /// The descriptor a held card's bytes verify against.
    /// </summary>
    /// <remarks>
    /// Null when it cannot be read — which is a card that can no longer be served rather than a card
    /// that can be served wrongly. Storage that has gone bad must not become content that looks fine.
    /// </remarks>
    public static ContentDescriptor? DescriptorOf(HeldCard card)
    {
        try
        {
            var read = JsonSerializer.Deserialize<ContentDescriptor>(card.Descriptor, Options);
            return read is { RootHash.Length: > 0 } && read.RootHash == card.RootHash ? read : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
