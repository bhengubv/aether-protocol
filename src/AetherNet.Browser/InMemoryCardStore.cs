// SPDX-License-Identifier: MIT

namespace AetherNet.Browser;

/// <summary>
/// A card store that forgets — for tests, and for a host that has not wired a real one yet.
///
/// <para>
/// Everything here works and nothing here lasts, which makes it exactly wrong for a phone and exactly
/// right for a test. It exists so the browser has a default and never a null, and so that a developer
/// trying the library out gets a working browser in one line before deciding where their device keeps
/// things.
/// </para>
///
/// <para>
/// A host that ships this to real people has a browser whose owner loses every page they wrote and
/// every card they were given, every time the app closes. See <see cref="ICardStore"/>: durability is
/// the contract, not a detail.
/// </para>
/// </summary>
public sealed class InMemoryCardStore : ICardStore
{
    private readonly Dictionary<string, HeldCard> _held = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private string? _pages;

    private string? _decks;

    /// <summary>The name this store reports as its owner's. Settable, because a test may want one.</summary>
    public string? OwnerName { get; set; }

    public string? GetPages()
    {
        lock (_gate) return _pages;
    }

    public void SetPages(string json)
    {
        lock (_gate) _pages = json;
    }

    public string? GetDecks()
    {
        lock (_gate) return _decks;
    }

    public void SetDecks(string json)
    {
        lock (_gate) _decks = json;
    }

    public string? GetOwnerName() => OwnerName;

    public void HoldCard(HeldCard card)
    {
        lock (_gate)
        {
            // Newer only — the same rule a real store enforces in SQL. Getting this wrong lets a
            // stale copy travelling the long way round overwrite a fresh one.
            if (_held.TryGetValue(card.Address, out var already) && already.Version >= card.Version)
                return;

            _held[card.Address] = card;
        }
    }

    public IReadOnlyList<HeldCard> GetHeldCards()
    {
        lock (_gate) return [.. _held.Values.OrderByDescending(c => c.GotMs)];
    }

    public bool HoldsCard(string address)
    {
        lock (_gate) return _held.ContainsKey(address);
    }

    public bool DropCard(string address)
    {
        lock (_gate) return _held.Remove(address);
    }
}
