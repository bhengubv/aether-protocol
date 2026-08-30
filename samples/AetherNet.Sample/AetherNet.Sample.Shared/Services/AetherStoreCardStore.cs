// SPDX-License-Identifier: MIT

using AetherNet.Browser;
using AetherNet.Sample.Shared.Data;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Where this app keeps cards: the device's own database.
///
/// <para>
/// One of the two seams <c>AetherNet.Browser</c> leaves for its host, and the whole of this one is
/// below. The browser has no idea what SQLite is; this app has no idea how a card is signed. That
/// division is what lets the same browser be one tab here and the system browser on Circle OS, where
/// the answer to "where do things live" is something else entirely.
/// </para>
/// </summary>
public sealed class AetherStoreCardStore : ICardStore
{
    /// <summary>Where the owner's pages live in settings.</summary>
    public const string PagesKey = "my_pages";

    /// <summary>Where the owner's decks live in settings.</summary>
    public const string DecksKey = "my_decks";

    /// <summary>Where the owner's name lives. Theirs, and this app's to keep — not the browser's.</summary>
    public const string NameKey = "my_name";

    /// <summary>The single card this app kept before a device hosted pages.</summary>
    /// <remarks>
    /// Read once, never written. Somebody upgrading should find what they wrote at their front door
    /// rather than find it gone — and the migration belongs here, with the storage, rather than in a
    /// library that never knew the old shape.
    /// </remarks>
    public const string OldCardKey = "my_card";

    private readonly AetherStore _store;

    public AetherStoreCardStore(AetherStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public string? GetPages() => _store.GetSetting(PagesKey) ?? Inherited();

    public void SetPages(string json) => _store.SetSetting(PagesKey, json);

    public string? GetDecks() => _store.GetSetting(DecksKey);

    public void SetDecks(string json) => _store.SetSetting(DecksKey, json);

    public string? GetOwnerName() => _store.GetSetting(NameKey);

    public void HoldCard(HeldCard card) => _store.HoldCard(card);

    public IReadOnlyList<HeldCard> GetHeldCards() => _store.GetHeldCards();

    public bool HoldsCard(string address) => _store.HoldsCard(address);

    public bool DropCard(string address) => _store.DropCard(address);

    /// <summary>The old single card, as a set of pages with one page in it.</summary>
    /// <remarks>
    /// Read, not moved: the old value stays where it is, so a downgrade still finds it.
    /// </remarks>
    private string? Inherited()
    {
        var old = _store.GetSetting(OldCardKey);
        if (string.IsNullOrWhiteSpace(old)) return null;
        if (CardDocument.Parse(old) is not { } card) return null;

        return $"[{{\"name\":\"{MyPages.Home}\",\"v\":0,\"live\":false,\"doc\":{card.ToJson()}}}]";
    }
}
