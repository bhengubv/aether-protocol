// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Fmhy;
using AetherNet.Fmhy.Models;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// The FMHY (Free Media Heck Yeah) content catalogue, running over the mesh. A bundled seed snapshot
/// makes discovery work offline on first launch; a fresh catalogue is parsed by the real FMHY
/// markdown parser; and — the point — the parsed markdown propagates to a peer that has no internet,
/// carried one hop over the in-process transport, so the second device gains the catalogue without
/// ever touching the network.
///
/// <para>Every entry, star, category and tracker source below comes out of the real
/// <see cref="IFmhyCatalogueService"/> — the seed via <see cref="FmhySeedLoader"/>, the sync via
/// <see cref="FmhyMarkdownParser"/>. The catalogue content here is neutral placeholder data; the
/// machinery is the shipping code.</para>
/// </summary>
public sealed class FmhyDemo : IDisposable
{
    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();

    private InMemoryFmhyCatalogueService? _online;
    private InMemoryFmhyCatalogueService? _offline;
    private string? _newEntryReachedOffline;

    public event Action? Changed;

    public bool Loaded => _online is not null;
    public int OnlineCount => _online?.EntryCount ?? 0;
    public int OfflineCount => _offline?.EntryCount ?? 0;
    public DateTime? OnlineSyncedAt => _online?.LastSyncedAt;
    public DateTime? OfflineSyncedAt => _offline?.LastSyncedAt;
    public string? NewEntryReachedOffline => _newEntryReachedOffline;

    public string CategoryFilter { get; private set; } = "";

    public IReadOnlyList<LogLine> Log
    {
        get { lock (_gate) return _log.ToArray(); }
    }

    public IReadOnlyList<string> Categories =>
        _online?.Browse().Select(e => e.Category).Distinct().OrderBy(c => c).ToArray() ?? Array.Empty<string>();

    public IReadOnlyList<FmhyEntry> Entries =>
        _online?.Browse(string.IsNullOrWhiteSpace(CategoryFilter) ? null : CategoryFilter) ?? Array.Empty<FmhyEntry>();

    public IReadOnlyList<FmhyEntry> Starred =>
        _online?.GetStarred() ?? Array.Empty<FmhyEntry>();

    public IReadOnlyList<TrackerSource> Trackers =>
        _online?.GetTrackerSources() ?? Array.Empty<TrackerSource>();

    // ── Load the bundled seed ────────────────────────────────────────────────────

    /// <summary>Load the bundled seed snapshot into both peers — discovery works immediately, offline.</summary>
    public void LoadSeed()
    {
        var seed = FmhySeedLoader.LoadFromJson(SeedJson);
        _online = new InMemoryFmhyCatalogueService(seed);
        _offline = new InMemoryFmhyCatalogueService(seed);
        _newEntryReachedOffline = null;
        CategoryFilter = "";

        Emit("seed", $"loaded {seed.Count} bundled entries into both peers — {_online.GetStarred().Count} starred. No network touched.");
        Raise();
    }

    public void Filter(string? category)
    {
        CategoryFilter = category ?? "";
        Raise();
    }

    // ── Online peer syncs a fresh catalogue ──────────────────────────────────────

    /// <summary>The online peer parses a fresh FMHY markdown dump (the real parser) and adopts it.</summary>
    public async Task SyncOnlineAsync()
    {
        if (_online is null) LoadSeed();
        var before = _online!.EntryCount;
        await _online.SyncAsync(FreshMarkdown);
        Emit("sync", $"online peer parsed the fresh FMHY markdown → {_online.EntryCount} entries (was {before}); {_online.GetStarred().Count} starred now.");
        Emit("sync", "the offline peer still sits on the seed — until a peer hands it the catalogue.");
        Raise();
    }

    // ── Mesh propagation to the offline peer ─────────────────────────────────────

    /// <summary>
    /// Carry the freshly-synced markdown one hop over the in-process transport to the offline peer,
    /// which parses it itself — gaining the whole catalogue, and the new starred entry, with no
    /// internet. This is the "no internet ever required by the second user" path.
    /// </summary>
    public async Task PropagateToOfflineAsync()
    {
        if (_online is null || _offline is null) { LoadSeed(); }
        if (_online!.LastSyncedAt is null)
        {
            Emit("mesh", "sync the online peer first — there's nothing fresher than the seed to carry yet.");
            Raise();
            return;
        }

        var beforeOffline = _offline!.EntryCount;
        var starredBefore = _offline.GetStarred().Select(e => e.Name).ToHashSet();

        // One real hop: the online node sends the catalogue bytes; the offline node receives and syncs
        // from exactly what arrived on the wire.
        var run = Guid.NewGuid().ToString("N")[..8];
        var onlineUhid = $"lab:fmhy:online:{run}";
        var offlineUhid = $"lab:fmhy:offline:{run}";
        using var onlineNode = new InProcessTransportService(onlineUhid, NullLogger<InProcessTransportService>.Instance);
        using var offlineNode = new InProcessTransportService(offlineUhid, NullLogger<InProcessTransportService>.Instance);

        string? received = null;
        offlineNode.DataReceived += (_src, bytes) => received = Encoding.UTF8.GetString(bytes);
        await onlineNode.SendAsync(offlineUhid, Encoding.UTF8.GetBytes(FreshMarkdown));

        if (received is null)
        {
            Emit("mesh", "the catalogue packet did not arrive (unexpected).");
            Raise();
            return;
        }

        await _offline.SyncAsync(received);

        var newStarred = _offline.GetStarred().FirstOrDefault(e => !starredBefore.Contains(e.Name));
        _newEntryReachedOffline = newStarred?.Name;

        Emit("mesh", $"offline peer received {Encoding.UTF8.GetByteCount(received):N0} B of catalogue over the mesh and parsed it → {_offline.EntryCount} entries (was {beforeOffline}).");
        if (newStarred is not null)
            Emit("mesh", $"the new starred entry \"{newStarred.Name}\" is now on a device that never went online. ✓");
        Raise();
    }

    private void Emit(string who, string text)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(who, text));
            if (_log.Count > 200) _log.RemoveRange(0, _log.Count - 200);
        }
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose() { }

    public sealed record LogLine(string Who, string Text);

    // ── Bundled demo data (neutral placeholder catalogue; real FMHY format) ──────

    private const string SeedJson = """
    [
      { "name": "Public Domain Library", "url": "https://example.org/pd", "description": "Books whose copyright has lapsed", "category": "Reading", "isStarred": true, "mirrors": [] },
      { "name": "Open Courseware", "url": "https://example.org/ocw", "description": "Freely-licensed university lectures", "category": "Learning", "isStarred": false, "mirrors": ["https://mirror.example.org/ocw"] },
      { "name": "Community Radio", "url": "https://example.org/radio", "description": "Listener-run audio streams", "category": "Audio", "isStarred": false, "mirrors": [] }
    ]
    """;

    // A fresh single-page dump in the exact FMHY markdown shape the real parser expects.
    private const string FreshMarkdown = """
    # Reading
    * ⭐ **[Public Domain Library](https://example.org/pd)** - Books whose copyright has lapsed
    * **[Zine Archive](https://example.org/zines)** - Independent self-published zines

    # Video
    ## Streaming
    * ⭐ **[Open Film Vault](https://example.org/film)** - Freely-licensed and public-domain films
    * **[Documentary Commons](https://example.org/docs)**, [Mirror](https://mirror.example.org/docs) - Public-interest documentaries

    # Torrenting
    ## Trackers
    * **[Open Tracker List](https://example.org/trackers)** - Community-maintained tracker aggregator
    """;
}
