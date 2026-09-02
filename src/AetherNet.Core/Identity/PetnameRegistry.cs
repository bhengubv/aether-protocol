// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace AetherNet.Identity;

/// <summary>Where a name-for-a-tag came from — which decides whether it can be overridden.</summary>
public enum PetnameSource
{
    /// <summary>A name I chose for this tag. Authoritative on my device; nothing overrides it.</summary>
    Pinned,

    /// <summary>A name a peer proposed (for themselves, or for someone they know). A suggestion only.</summary>
    Proposed,

    /// <summary>A name that arrived in a seed bundle (e.g. a starter contact set). Treated as pinned.</summary>
    Seed,
}

/// <summary>One binding of a memorable name to a globally-unique <see cref="AetherNetTag"/>.</summary>
/// <param name="Tag">The canonical AetherTag (XXXXX-XXXXX) this name points at.</param>
/// <param name="Name">The human-chosen name.</param>
/// <param name="Source">Where the binding came from (see <see cref="PetnameSource"/>).</param>
/// <param name="ProposedByTag">For a proposal, the tag of the peer who suggested it; otherwise null.</param>
public sealed record Petname(string Tag, string Name, PetnameSource Source, string? ProposedByTag = null);

/// <summary>Pluggable persistence for a <see cref="PetnameRegistry"/>. The in-memory default keeps
/// nothing across restarts; a host supplies a real store to persist.</summary>
public interface IPetnameStore
{
    /// <summary>Every stored binding.</summary>
    IReadOnlyCollection<Petname> Load();

    /// <summary>Replace the stored set with <paramref name="all"/>.</summary>
    void Save(IReadOnlyCollection<Petname> all);
}

/// <summary>An in-memory <see cref="IPetnameStore"/> — the default when no persistence is wired.</summary>
public sealed class InMemoryPetnameStore : IPetnameStore
{
    private volatile IReadOnlyCollection<Petname> _all = Array.Empty<Petname>();
    public IReadOnlyCollection<Petname> Load() => _all;
    public void Save(IReadOnlyCollection<Petname> all) => _all = all;
}

/// <summary>
/// A local petname registry — the memorable-name layer over AetherTags.
///
/// <para>
/// An AetherTag is globally unique and unforgeable but not memorable; a petname is memorable but only
/// meaningful to the person who chose it. This is Zooko's triangle resolved the local-first way: the
/// binding lives on your device and is <b>your</b> choice, so there is no central name registry anyone
/// could seize, censor, or squat — exactly the decentralisation this protocol is for. A name you
/// <see cref="Pin"/> is authoritative here and nothing overrides it; a name a peer <see cref="Propose"/>s
/// (for themselves, when they introduce themselves) is only a suggestion, kept until you promote it to a
/// pin or <see cref="Reject"/> it.
/// </para>
///
/// <para>
/// Names are not required to be unique — two people can both be "Sam" to you — so name→tag resolution
/// only answers when the name is unambiguous, and pins always win over proposals. Tags are validated:
/// a binding to something that is not a well-formed AetherTag is refused.
/// </para>
/// </summary>
public sealed class PetnameRegistry
{
    private readonly IPetnameStore _store;
    private readonly object _gate = new();

    // Keyed by canonical tag — at most one binding per tag (a pin supersedes a proposal for that tag).
    private readonly ConcurrentDictionary<string, Petname> _byTag = new(StringComparer.Ordinal);

    /// <summary>The longest a petname may be, so it stays something a person actually types.</summary>
    public const int LongestName = 48;

    public PetnameRegistry(IPetnameStore? store = null)
    {
        _store = store ?? new InMemoryPetnameStore();
        foreach (var p in _store.Load())
            if (Canonical(p.Tag) is { } tag)
                _byTag[tag] = p with { Tag = tag };
    }

    /// <summary>Every binding, pins and proposals alike.</summary>
    public IReadOnlyList<Petname> All => _byTag.Values.ToArray();

    /// <summary>
    /// Bind <paramref name="name"/> to <paramref name="tag"/> as my own authoritative choice, replacing
    /// any existing binding (pin or proposal) for that tag. Returns false when the tag is not a valid
    /// AetherTag or the name is blank/too long.
    /// </summary>
    public bool Pin(string? tag, string? name)
    {
        if (Canonical(tag) is not { } t) return false;
        var clean = CleanName(name);
        if (clean is null) return false;

        lock (_gate)
        {
            _byTag[t] = new Petname(t, clean, PetnameSource.Pinned);
            Flush();
        }
        return true;
    }

    /// <summary>
    /// Record a name a peer proposed. A proposal never overrides a name I have pinned or seeded for that
    /// tag (my choice stands), and never overrides an existing proposal from a different proposer without
    /// replacing it. Returns true if the proposal was stored.
    /// </summary>
    public bool Propose(string? tag, string? name, string? proposedByTag)
    {
        if (Canonical(tag) is not { } t) return false;
        var clean = CleanName(name);
        if (clean is null) return false;
        var by = Canonical(proposedByTag);

        lock (_gate)
        {
            if (_byTag.TryGetValue(t, out var existing) &&
                existing.Source is PetnameSource.Pinned or PetnameSource.Seed)
            {
                return false; // my choice stands
            }
            _byTag[t] = new Petname(t, clean, PetnameSource.Proposed, by);
            Flush();
        }
        return true;
    }

    /// <summary>Drop any binding for <paramref name="tag"/>. Returns true if one was removed.</summary>
    public bool Reject(string? tag)
    {
        if (Canonical(tag) is not { } t) return false;
        lock (_gate)
        {
            if (!_byTag.TryRemove(t, out _)) return false;
            Flush();
            return true;
        }
    }

    /// <summary>My name for a tag, or null when I have none.</summary>
    public string? NameFor(string? tag) =>
        Canonical(tag) is { } t && _byTag.TryGetValue(t, out var p) ? p.Name : null;

    /// <summary>
    /// The tag a name points at — but only when the answer is unambiguous. A pinned/seeded name wins
    /// over a proposal; if several bindings of the same source share the name, or a name matches nothing,
    /// the result is null (a person should disambiguate rather than be sent to the wrong tag).
    /// </summary>
    public string? ResolveName(string? name)
    {
        var clean = CleanName(name);
        if (clean is null) return null;

        lock (_gate)
        {
            var matches = _byTag.Values
                .Where(p => string.Equals(p.Name, clean, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) return null;

            // Prefer my own choices; fall back to proposals only when there are no pins for this name.
            var mine = matches.Where(p => p.Source is PetnameSource.Pinned or PetnameSource.Seed).ToList();
            var pool = mine.Count > 0 ? mine : matches;
            return pool.Count == 1 ? pool[0].Tag : null;
        }
    }

    /// <summary>
    /// Import an initial set of bindings (a starter contact bundle). Entries land as <see cref="PetnameSource.Seed"/>
    /// (authoritative like a pin) unless they carry a different source, and invalid tags are skipped.
    /// Returns the number stored.
    /// </summary>
    public int Seed(IEnumerable<Petname> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var added = 0;
        lock (_gate)
        {
            foreach (var e in entries)
            {
                if (Canonical(e.Tag) is not { } t) continue;
                var clean = CleanName(e.Name);
                if (clean is null) continue;
                _byTag[t] = new Petname(t, clean, e.Source == PetnameSource.Proposed ? PetnameSource.Seed : e.Source, e.ProposedByTag);
                added++;
            }
            if (added > 0) Flush();
        }
        return added;
    }

    /// <summary>
    /// The bindings I am willing to gossip to peers — my pins and seeds, offered as proposals (a peer
    /// receiving them decides whether to keep them). Never leaks another peer's un-promoted proposals.
    /// </summary>
    public IReadOnlyList<Petname> ExportProposals(string? myTag = null)
    {
        var by = Canonical(myTag);
        lock (_gate)
        {
            return _byTag.Values
                .Where(p => p.Source is PetnameSource.Pinned or PetnameSource.Seed)
                .Select(p => new Petname(p.Tag, p.Name, PetnameSource.Proposed, by))
                .ToArray();
        }
    }

    /// <summary>
    /// Accept a gossip bundle of proposals from a peer. Each is stored via <see cref="Propose"/>, so none
    /// overrides a name I have already chosen. Returns how many were stored.
    /// </summary>
    public int ImportProposals(IEnumerable<Petname> incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var stored = 0;
        foreach (var p in incoming)
            if (Propose(p.Tag, p.Name, p.ProposedByTag)) stored++;
        return stored;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void Flush() => _store.Save(_byTag.Values.ToArray());

    private static string? Canonical(string? tag) =>
        !string.IsNullOrEmpty(tag) && AetherNetTag.TryParse(tag, out var t) ? t.Value : null;

    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return trimmed.Length > LongestName ? null : trimmed;
    }
}
