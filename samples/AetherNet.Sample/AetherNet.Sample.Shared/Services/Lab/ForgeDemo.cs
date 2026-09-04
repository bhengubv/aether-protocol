// SPDX-License-Identifier: MIT

using AetherNet.Content;
using AetherNet.Forge;
using AetherNet.Forge.Models;
using AetherNet.Protocol;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// The Forge mesh package cache: a mesh-local mirror of a package registry (npm / pip / cargo / go /
/// nuget / git). A publishes an artifact once; the cache entry gossips to B over the mesh via a real
/// <see cref="ForgeAnnounceService"/> (packet type 41), so B can serve later pulls locally without
/// ever hitting the internet. The download counter and the "bytes saved" statistic are the real
/// <see cref="IForgeService"/> aggregates.
///
/// <para>Two nodes, each a full stack — <see cref="ContentService"/> for the bytes,
/// <see cref="MeshPackageDistributor"/> to publish-and-announce, <see cref="IForgeService"/> for the
/// index, <see cref="ForgeAnnounceService"/> for the gossip — over one in-process transport.</para>
/// </summary>
public sealed class ForgeDemo : IDisposable
{
    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();
    private readonly List<IDisposable> _disposables = new();

    private Node? _a;
    private Node? _b;
    private readonly HashSet<string> _bLearnedByGossip = new(StringComparer.Ordinal);

    public event Action? Changed;

    public bool Ready => _a is not null && _b is not null;
    public ForgeStats? StatsA { get; private set; }
    public ForgeStats? StatsB { get; private set; }

    /// <summary>Snapshot of the package ids B learned purely from a peer's gossip (never published locally).</summary>
    public IReadOnlyCollection<string> LearnedByGossip
    {
        get { lock (_gate) return _bLearnedByGossip.ToArray(); }
    }

    public IReadOnlyList<LogLine> Log
    {
        get { lock (_gate) return _log.ToArray(); }
    }

    // ── Bring the two nodes up and wire the gossip loop ──────────────────────────

    public void Init()
    {
        Teardown();
        var run = Guid.NewGuid().ToString("N")[..8];
        var a = NewNode($"lab:forge:A:{run}", "A");
        var b = NewNode($"lab:forge:B:{run}", "B");
        _a = a;
        _b = b;

        foreach (var (self, other) in new[] { (a, b), (b, a) })
        {
            self.Sender.AddPotentialPeer(other.Uhid);

            // Inbound wire: forge announcements to the announce service, content to the content service.
            self.Transport.DataReceived += (_src, bytes) =>
            {
                MeshPacket packet;
                try { packet = PacketSerializer.Deserialize(bytes); }
                catch { return; }
                if (packet.Type == PacketType.ForgeAnnounce) _ = self.Announce.HandleAsync(packet);
                else _ = self.Content.HandleAsync(packet);
            };

            // Caching a NEW entry locally gossips it to the mesh…
            self.Forge.NewEntryAnnounced += (_, entry) =>
                _ = self.Announce.BroadcastAsync(entry.PackageId, entry.ContentHash, entry.SizeBytes,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            // …and an announcement arriving from a peer records the entry in this node's own cache.
            var node = self;
            self.Announce.AnnounceReceived += (_, payload) =>
            {
                _ = node.Forge.CacheAsync(payload.PackageId, payload.ContentHash, payload.SizeBytes);
                if (ReferenceEquals(node, _b))
                {
                    lock (_gate) _bLearnedByGossip.Add(payload.PackageId);
                    Emit("gossip", $"B learned {payload.PackageId} from a peer's announce — {payload.SizeBytes:N0} B, hash {Short(payload.ContentHash)}.");
                    Raise();
                }
            };
        }

        Emit("mesh", "two Forge nodes up: A publishes, B mirrors. Announcements ride packet type 41.");
        Raise();
    }

    // ── Publish an artifact on A → it caches and gossips to B ────────────────────

    public async Task PublishOnAAsync(string packageId, int sizeKb)
    {
        if (!Ready) Init();
        packageId = string.IsNullOrWhiteSpace(packageId) ? "npm:demo@1.0.0" : packageId.Trim();
        sizeKb = Math.Clamp(sizeKb, 1, 64);
        var payload = new byte[sizeKb * 1024];
        Random.Shared.NextBytes(payload);

        var entry = await _a!.Distributor.PublishAsync(packageId, payload, "application/octet-stream");
        Emit("publish", $"A published {entry.PackageId}: {entry.SizeBytes:N0} B → content hash {Short(entry.ContentHash)}, cached and announced.");
        await RefreshStatsAsync();
        Raise();
    }

    /// <summary>Publish a small starter set on A so the caches and stats have something to show.</summary>
    public async Task SeedSampleAsync()
    {
        if (!Ready) Init();
        foreach (var (pkg, kb) in new[] { ("npm:react@18.2.0", 6), ("pip:requests@2.31.0", 4), ("cargo:serde@1.0.195", 9) })
            await PublishOnAAsync(pkg, kb);
    }

    // ── Look a package up on B (proves gossip delivered it) ──────────────────────

    public async Task<ForgeEntry?> LookupOnBAsync(string packageId)
    {
        if (!Ready) return null;
        packageId = packageId?.Trim() ?? "";
        var entry = await _b!.Forge.QueryAsync(packageId);
        Emit("lookup", entry is not null
            ? $"B has {packageId} in its cache (hash {Short(entry.ContentHash)}) — it never published it; the mesh brought it."
            : $"B has no entry for '{packageId}' yet.");
        await RefreshStatsAsync();
        Raise();
        return entry;
    }

    // ── Serve a package from B's cache (grows the download counter) ──────────────

    public async Task FetchOnBAsync(string packageId, int times)
    {
        if (!Ready) return;
        packageId = packageId?.Trim() ?? "";
        ForgeEntry? entry = null;
        for (int i = 0; i < Math.Clamp(times, 1, 20); i++)
            entry = await _b!.Forge.FetchAsync(packageId);

        if (entry is null)
        {
            Emit("serve", $"B cannot serve '{packageId}' — not in its cache.");
        }
        else
        {
            Emit("serve", $"B served {packageId} {times}× from the mesh — download count now {entry.DownloadCount}, {(long)entry.DownloadCount * entry.SizeBytes:N0} B kept off mobile data.");
        }
        await RefreshStatsAsync();
        Raise();
    }

    public async Task RefreshStatsAsync()
    {
        if (_a is not null) StatsA = await _a.Forge.GetStatsAsync();
        if (_b is not null) StatsB = await _b.Forge.GetStatsAsync();
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────────

    private Node NewNode(string uhid, string label)
    {
        var transport = new InProcessTransportService(uhid, NullLogger<InProcessTransportService>.Instance);
        var sender = new InProcessMeshSender(uhid, transport);
        var store = new InMemoryContentStore();
        var content = new ContentService(sender, new NullRoutingService(), store);
        var forge = new InMemoryForgeService();
        var announce = new ForgeAnnounceService(sender);
        var incentives = new NoOpIncentives();
        var distributor = new MeshPackageDistributor(forge, content, incentives, uhid);
        _disposables.Add(transport);
        return new Node(uhid, label, transport, sender, content, forge, announce, distributor);
    }

    private void Teardown()
    {
        foreach (var d in _disposables)
            try { d.Dispose(); } catch { /* best-effort */ }
        _disposables.Clear();
        _a = null;
        _b = null;
        lock (_gate) _bLearnedByGossip.Clear();
        StatsA = StatsB = null;
    }

    private static string Short(string hex) => string.IsNullOrEmpty(hex) ? "(none)" : hex.Length <= 12 ? hex : hex[..12] + "…";

    private void Emit(string who, string text)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(who, text));
            if (_log.Count > 200) _log.RemoveRange(0, _log.Count - 200);
        }
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose() => Teardown();

    private sealed record Node(
        string Uhid, string Label, InProcessTransportService Transport, InProcessMeshSender Sender,
        ContentService Content, InMemoryForgeService Forge, ForgeAnnounceService Announce,
        MeshPackageDistributor Distributor);

    public sealed record LogLine(string Who, string Text);
}
