// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherNet.Browser;

/// <summary>
/// The addresses this device wants but has not been able to reach yet.
/// </summary>
/// <remarks>
/// <para>
/// A phone that hosts a card is a server; the missing half was always <b>reach</b> — a way for
/// somebody who is not standing next to you to end up with your card. Half of that is easy: an
/// address is just text, so it travels over any channel there already is — a message, a poster, a tag
/// read out loud. The hard half is the moment after: they have your address, but your phone is not in
/// range and there is no relay yet, so the fetch fails and — until now — the address was simply lost.
/// </para>
/// <para>
/// This is what keeps it. An address you could not reach is held here and tried again: the next time
/// the two of you are near, by hand; and, once the relay layer (§3a) exists, on its own. When an
/// address is finally reached it becomes a held card and leaves this list — so what is here is always
/// exactly "the cards I was promised and do not have yet."
/// </para>
/// <para>
/// Persisted as one JSON array through <see cref="ICardStore"/>, the same way pages and decks are: a
/// person wants a handful of things, and a table per address would buy nothing.
/// </para>
/// </remarks>
public sealed class Wanted
{
    /// <summary>The most addresses this list will hold. Older ones fall off the end.</summary>
    /// <remarks>
    /// A bound, because this is fed by opening things and things do not always resolve; without one a
    /// stream of dead addresses would grow without limit. A person is not waiting on a thousand cards.
    /// </remarks>
    public const int Most = 200;

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ICardStore _store;
    private List<string>? _addresses;

    public Wanted(ICardStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Raised when the set changes, so a view can redraw.</summary>
    public event Action? Changed;

    /// <summary>Every address still wanted, newest first.</summary>
    public IReadOnlyList<string> All
    {
        get
        {
            Load();
            return _addresses!;
        }
    }

    /// <summary>Whether this address is on the wanted list.</summary>
    public bool Holds(string? address)
    {
        if (Clean(address) is not { } clean) return false;
        Load();
        return _addresses!.Contains(clean, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Keep an address to try later. Newest first, no duplicates, capped.
    /// </summary>
    /// <returns>Whether anything changed — false if the address was unusable or already here.</returns>
    public bool Add(string? address)
    {
        if (Clean(address) is not { } clean) return false;

        Load();

        // Move an address already here back to the top rather than refusing it: wanting it again is a
        // fresh signal that it still matters.
        _addresses!.RemoveAll(a => string.Equals(a, clean, StringComparison.OrdinalIgnoreCase));
        _addresses.Insert(0, clean);

        if (_addresses.Count > Most) _addresses.RemoveRange(Most, _addresses.Count - Most);

        Flush();
        return true;
    }

    /// <summary>Stop wanting an address — because it was reached, or given up on.</summary>
    public bool Remove(string? address)
    {
        if (Clean(address) is not { } clean) return false;

        Load();
        if (_addresses!.RemoveAll(a => string.Equals(a, clean, StringComparison.OrdinalIgnoreCase)) == 0)
            return false;

        Flush();
        return true;
    }

    /// <summary>An address is only worth keeping if it is one — a mesh address, trimmed.</summary>
    private static string? Clean(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;

        var trimmed = address.Trim();
        return trimmed.StartsWith("aether://", StringComparison.OrdinalIgnoreCase) ? trimmed : null;
    }

    private void Load()
    {
        if (_addresses is not null) return;

        try
        {
            _addresses = _store.GetWanted() is { Length: > 0 } json
                ? JsonSerializer.Deserialize<List<string>>(json, Options) ?? []
                : [];
        }
        catch (JsonException)
        {
            // A corrupt blob is not worth a crash; start clean and the next Flush repairs it.
            _addresses = [];
        }
    }

    private void Flush()
    {
        _store.SetWanted(JsonSerializer.Serialize(_addresses, Options));
        Changed?.Invoke();
    }
}
