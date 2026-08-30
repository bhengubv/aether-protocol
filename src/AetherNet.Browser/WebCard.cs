// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherNet.Browser;

/// <summary>
/// One page this device hosts on AetherNet.
///
/// <para>
/// A person's AetherTag is their domain and this is a path under it: <c>aether://Y6TK9-EW9KK/me</c>,
/// <c>/urbanplumbers</c>, <c>/mylinks</c>. Nobody issued the domain, so nobody can withdraw it, and the
/// page is served by the phone in their pocket rather than by anybody's server.
/// </para>
///
/// <para>
/// The version is not decoration. A name binding is only accepted by <c>DirectoryService</c> if it is
/// newer than the one already held, so an edit that does not raise this number is an edit that never
/// reaches anybody — the author sees their change and every reader keeps the old page.
/// </para>
/// </summary>
public sealed class WebCard
{
    /// <summary>The path under the author's tag. Lowercase, no spaces — it is an address.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Raised on every publish. See the class remarks — this is load-bearing.</summary>
    [JsonPropertyName("v")] public long Version { get; set; }

    /// <summary>Whether the current text is standing on the mesh.</summary>
    /// <remarks>
    /// A page can be written and not published — somebody who opens the wizard, types half a page and
    /// walks away has a draft, not a broadcast. Publishing is a deliberate act, so it is a state.
    /// </remarks>
    [JsonPropertyName("live")] public bool Live { get; set; }

    /// <summary>The document itself — the same block model the mesh already carries.</summary>
    [JsonPropertyName("doc")] public CardDocument Doc { get; set; } = new();
}

/// <summary>
/// Every page this device hosts, kept in its own database.
///
/// <para>
/// Held as one JSON array in settings rather than a table per block. A page is a few hundred bytes and
/// a person has a handful of them; a schema would buy nothing and would have to be migrated every time
/// the card model grew a block kind. The document is already a portable format — storing it as itself
/// means what is saved is byte-for-byte what gets published.
/// </para>
/// </summary>
public sealed class MyPages
{
    /// <summary>Where the whole set lives in this device's settings.</summary>
    public const string Key = "my_pages";

    /// <summary>The front door. Every node answers this from first launch.</summary>
    public const string Home = "me";

    /// <summary>How many pages one device offers.</summary>
    /// <remarks>
    /// Twelve. A person is publishing, not hosting — and a stranger who opens somebody's tag should
    /// find a place to read, not a sitemap.
    /// </remarks>
    public const int MostPages = 12;

    /// <summary>The longest a page name may be, so an address stays sayable out loud.</summary>
    public const int LongestName = 24;

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ICardStore _store;
    private readonly List<WebCard> _pages = [];
    private bool _loaded;

    /// <summary>
    /// What each page said when it last went up, so a real edit can be told from a save.
    /// </summary>
    /// <remarks>
    /// The editor saves on every keystroke, so "has this changed" cannot be answered by whether Save
    /// was called. It also cannot be answered by comparing the page to the one in this list, because
    /// they are the same object — <see cref="Get"/> hands out the stored page, and the editor edits it
    /// in place. So the words are kept separately, as of the last time this page was published.
    /// </remarks>
    private readonly Dictionary<string, string> _standing = new(StringComparer.Ordinal);

    public MyPages(ICardStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Raised when a page is written, published or taken down.</summary>
    public event Action? Changed;

    /// <summary>The author's own name, as they typed it — the title a first page starts with.</summary>
    public string? OwnerName => _store.GetOwnerName();

    /// <summary>Every page, in the order the author arranged them.</summary>
    public IReadOnlyList<WebCard> All
    {
        get { Load(); return _pages; }
    }

    /// <summary>Whether there is room for another.</summary>
    public bool Full => All.Count >= MostPages;

    /// <summary>The page at this name, or null.</summary>
    public WebCard? Get(string? name)
    {
        var wanted = Clean(name);
        return wanted.Length == 0 ? null : All.FirstOrDefault(p => p.Name == wanted);
    }

    /// <summary>Whether this name is already taken.</summary>
    public bool Has(string? name) => Get(name) is not null;

    /// <summary>
    /// Write a page, replacing any page of the same name.
    /// </summary>
    /// <remarks>
    /// Saving is not publishing. The version is untouched here and the page drops back to a draft,
    /// because the copy on the mesh is still the old one until somebody says otherwise — showing an
    /// edit as live when no reader can see it is the one lie a publishing tool must not tell.
    /// </remarks>
    public void Save(WebCard page)
    {
        if (page is null) return;

        page.Name = Clean(page.Name);
        if (page.Name.Length == 0) return;

        page.Doc = OwnCard.Tidy(page.Doc ?? new CardDocument());
        Load();

        var at = _pages.FindIndex(p => p.Name == page.Name);

        // Only a change takes a page off the air.
        //
        // This cleared Live on every save, and the editor saves on every keystroke — so opening a
        // published page and touching nothing at all un-published it. The address stopped answering,
        // the page vanished from the bookmark row, and the only sign was that something somebody had
        // already published quietly became a draft again while they were looking at it.
        //
        // Live means "the copy standing on the mesh is this copy". It survives a save that changed
        // nothing, and a real edit makes it false — which is the honest reading, because at that
        // moment what is standing out there is genuinely not what is on the phone.
        var same = _standing.TryGetValue(page.Name, out var stood) && stood == Written(page);

        if (at >= 0) _pages[at] = page;
        else if (_pages.Count < MostPages) _pages.Add(page);
        else return;

        page.Live = same && page.Live;
        Flush();
    }

    /// <summary>What a page says, for telling a real edit from a save that changed nothing.</summary>
    private static string Written(WebCard page) => (page.Doc ?? new CardDocument()).ToJson();

    /// <summary>Mark a page as now standing on the mesh at this version.</summary>
    public void WentLive(string? name, long version)
    {
        if (Get(name) is not { } page) return;

        page.Version = version;
        page.Live = true;

        // What is standing out there is now these words. Anything else is an edit.
        _standing[page.Name] = Written(page);

        Flush();
    }

    /// <summary>
    /// The version a fresh publish of this page should carry.
    /// </summary>
    /// <remarks>
    /// Always one above what stands. A directory entry is refused if it is not newer, so re-publishing
    /// at the same number is indistinguishable — to every reader — from not publishing at all.
    /// </remarks>
    public long NextVersion(string? name) => (Get(name)?.Version ?? 0) + 1;

    /// <summary>Take a page down from this device. It stays on any phone that already fetched it.</summary>
    public bool Remove(string? name)
    {
        Load();
        var wanted = Clean(name);
        if (wanted.Length == 0) return false;
        if (_pages.RemoveAll(p => p.Name == wanted) == 0) return false;

        Flush();
        return true;
    }

    /// <summary>Move a page up or down, so the author decides what a visitor meets first.</summary>
    public void Move(string? name, int by)
    {
        Load();
        var at = _pages.FindIndex(p => p.Name == Clean(name));
        if (at < 0) return;

        var to = at + by;
        if (to < 0 || to >= _pages.Count) return;

        (_pages[at], _pages[to]) = (_pages[to], _pages[at]);
        Flush();
    }

    /// <summary>
    /// Turn whatever somebody typed into something that can be an address.
    /// </summary>
    /// <remarks>
    /// Lowercase letters, digits and hyphens. Not a restriction for its own sake: this ends up spoken
    /// across a table and typed into another person's phone from memory, so whatever survives that is
    /// in, and whatever does not is out.
    /// </remarks>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var clean = new StringBuilder(LongestName);

        foreach (var c in raw.Trim().ToLowerInvariant())
        {
            if (clean.Length >= LongestName) break;

            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)) clean.Append(c);
            else if (c is '-' or ' ' or '_' or '.' && clean.Length > 0 && clean[^1] != '-') clean.Append('-');
        }

        return clean.ToString().Trim('-');
    }

    /// <summary>A name nobody here has taken, built from the one they wanted.</summary>
    public string Free(string? wanted)
    {
        var stem = Clean(wanted);
        if (stem.Length == 0) stem = "page";
        if (!Has(stem)) return stem;

        var room = stem[..Math.Min(stem.Length, LongestName - 3)].TrimEnd('-');

        for (var n = 2; n < 100; n++)
        {
            var tried = room + "-" + n;
            if (!Has(tried)) return tried;
        }

        return stem;
    }

    // ─── Storage ────────────────────────────────────────────────────────────────

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;

        var stored = _store.GetPages();
        if (string.IsNullOrWhiteSpace(stored)) return;

        try
        {
            foreach (var page in JsonSerializer.Deserialize<List<WebCard>>(stored, Options) ?? [])
            {
                if (_pages.Count >= MostPages) break;

                page.Name = Clean(page.Name);
                if (page.Name.Length == 0 || _pages.Any(p => p.Name == page.Name)) continue;

                page.Doc = OwnCard.Tidy(page.Doc ?? new CardDocument());
                _pages.Add(page);

                // Taken as standing: this is what was flushed, alongside the Live flag that was
                // flushed with it. Without this, every page would look edited on the first save after
                // a restart and would take itself off the air — the same bug, one launch later.
                _standing[page.Name] = Written(page);
            }
        }
        catch (JsonException)
        {
            // Text we cannot read is not text we should overwrite. Start empty for this launch and
            // leave what is stored where it is, rather than replacing somebody's pages with nothing.
            _pages.Clear();
        }
    }

    private void Flush()
    {
        _store.SetPages(JsonSerializer.Serialize(_pages, Options));
        Changed?.Invoke();
    }
}
