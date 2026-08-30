// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherNet.Browser;

/// <summary>
/// A named set of cards, in an order somebody chose, that can be handed over as one thing.
///
/// <para>
/// <b>Why a set rather than a pile.</b> Cards arrive one at a time and land in a heap sorted by when
/// they turned up, which is the order they were least likely to be wanted in. A deck is the other
/// thing people do with cards: gather the ones that go together, put them in an order, give it a
/// name, and hand the whole lot to somebody — the plumber, the electrician and the tiler; the six
/// stalls on that street; the four pages that make up a shop.
/// </para>
///
/// <para>
/// <b>It holds addresses, not copies.</b> A card lives once on this phone whether it is in no decks
/// or five, so putting one in a deck costs nothing and taking it out loses nothing. A deck naming a
/// card this phone no longer holds simply has one fewer card in it, which is what dropping a card
/// should mean and needs no bookkeeping to stay true.
/// </para>
/// </summary>
public sealed class CardDeck
{
    /// <summary>What its owner calls it.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>The cards in it, in the order they were put.</summary>
    [JsonPropertyName("cards")] public List<string> Cards { get; set; } = [];
}

/// <summary>
/// The decks on this phone.
///
/// <para>
/// Kept the way the pages are kept — one blob, saved whole, because a person has a handful of decks
/// and a schema would buy nothing. See <see cref="MyPages"/>, which this deliberately mirrors down to
/// the load-once-and-flush shape, so somebody reading one has read both.
/// </para>
/// </summary>
public sealed class Decks
{
    /// <summary>The most decks one phone will keep.</summary>
    /// <remarks>
    /// Enough to sort a life into; few enough that the list is still something to look at rather than
    /// to search. A person with more than this wants folders, and folders are somebody else's app.
    /// </remarks>
    public const int Most = 20;

    /// <summary>The most cards one deck will hold.</summary>
    /// <remarks>
    /// A deck is handed over card by card across a radio, so this is also how long somebody stands
    /// there. Sixty cards is already a minute of holding two phones together.
    /// </remarks>
    public const int MostCards = 60;

    /// <summary>The longest a deck's name may be.</summary>
    public const int LongestName = 32;

    private static readonly JsonSerializerOptions Options =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly ICardStore _store;

    private readonly List<CardDeck> _decks = [];

    private bool _loaded;

    /// <summary>Reads and writes the decks on this device.</summary>
    public Decks(ICardStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Raised when a deck is made, named, ordered or dropped.</summary>
    public event Action? Changed;

    /// <summary>Every deck, in the order its owner put them.</summary>
    public IReadOnlyList<CardDeck> All
    {
        get { Load(); return _decks; }
    }

    /// <summary>Whether there is room for another.</summary>
    public bool Full => All.Count >= Most;

    /// <summary>The deck by this name, or nothing.</summary>
    public CardDeck? Get(string? name) =>
        Clean(name) is { Length: > 0 } wanted
            ? All.FirstOrDefault(d => string.Equals(d.Name, wanted, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// Start a deck.
    /// </summary>
    /// <returns>The deck, whether it was made now or already there.</returns>
    public CardDeck? Make(string? name)
    {
        if (Clean(name) is not { Length: > 0 } called) return null;
        if (Get(called) is { } already) return already;

        Load();
        if (_decks.Count >= Most) return null;

        var made = new CardDeck { Name = called };
        _decks.Add(made);
        Flush();

        return made;
    }

    /// <summary>
    /// Call it something else.
    /// </summary>
    /// <remarks>
    /// Refused when the new name is already taken, rather than merging: two decks quietly becoming one
    /// loses whichever order somebody had put the second one in, and they would not find out until
    /// they went looking for it.
    /// </remarks>
    public bool Rename(string? was, string? now)
    {
        if (Get(was) is not { } deck) return false;
        if (Clean(now) is not { Length: > 0 } called) return false;

        if (!string.Equals(deck.Name, called, StringComparison.OrdinalIgnoreCase)
            && Get(called) is not null) return false;

        deck.Name = called;
        Flush();
        return true;
    }

    /// <summary>Put it away. The cards themselves are untouched — a deck is a list, not a box.</summary>
    public bool Drop(string? name)
    {
        if (Get(name) is not { } deck) return false;

        Load();
        _decks.Remove(deck);
        Flush();
        return true;
    }

    /// <summary>Put a card in, at the end, if it is not in already.</summary>
    public bool Add(string? name, string? address)
    {
        if (Get(name) is not { } deck) return false;
        if (address is not { Length: > 0 } card) return false;
        if (deck.Cards.Count >= MostCards) return false;
        if (deck.Cards.Contains(card, StringComparer.OrdinalIgnoreCase)) return false;

        deck.Cards.Add(card);
        Flush();
        return true;
    }

    /// <summary>Take a card out. It stays on the phone.</summary>
    public bool Remove(string? name, string? address)
    {
        if (Get(name) is not { } deck) return false;

        var at = deck.Cards.FindIndex(c => string.Equals(c, address, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return false;

        deck.Cards.RemoveAt(at);
        Flush();
        return true;
    }

    /// <summary>Move a card up or down inside its deck.</summary>
    public bool MoveCard(string? name, string? address, int by)
    {
        if (Get(name) is not { } deck) return false;

        var at = deck.Cards.FindIndex(c => string.Equals(c, address, StringComparison.OrdinalIgnoreCase));
        return Shift(deck.Cards, at, by) && Flushed();
    }

    /// <summary>Move a whole deck up or down the list.</summary>
    public bool Move(string? name, int by)
    {
        if (Get(name) is not { } deck) return false;

        Load();
        return Shift(_decks, _decks.IndexOf(deck), by) && Flushed();
    }

    /// <summary>A name with nothing in it that would make a mess of a list.</summary>
    public static string Clean(string? name)
    {
        if (name is null) return "";

        var kept = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        return kept.Length > LongestName ? kept[..LongestName].TrimEnd() : kept;
    }

    /// <summary>Move one item of a list by a step, staying inside it.</summary>
    private static bool Shift<T>(List<T> items, int at, int by)
    {
        if (at < 0 || by == 0) return false;

        var to = Math.Clamp(at + by, 0, items.Count - 1);
        if (to == at) return false;

        var moved = items[at];
        items.RemoveAt(at);
        items.Insert(to, moved);
        return true;
    }

    private bool Flushed()
    {
        Flush();
        return true;
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;

        if (_store.GetDecks() is not { Length: > 0 } stored) return;

        try
        {
            foreach (var deck in JsonSerializer.Deserialize<List<CardDeck>>(stored, Options) ?? [])
            {
                if (_decks.Count >= Most) break;

                deck.Name = Clean(deck.Name);
                if (deck.Name.Length == 0) continue;
                if (_decks.Any(d => string.Equals(d.Name, deck.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                deck.Cards = [.. (deck.Cards ?? []).Where(c => c is { Length: > 0 }).Distinct(StringComparer.OrdinalIgnoreCase).Take(MostCards)];
                _decks.Add(deck);
            }
        }
        catch (JsonException)
        {
            // Somebody else's bytes, or ours from a version that wrote something different. A phone
            // with no decks is a phone that can make some; a phone that will not open is not.
            _decks.Clear();
        }
    }

    private void Flush()
    {
        _store.SetDecks(JsonSerializer.Serialize(_decks, Options));
        Changed?.Invoke();
    }
}
