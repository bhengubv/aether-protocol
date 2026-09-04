// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using AetherNet.Protocol;
using AetherNet.Space;
using AetherNet.Space.Models;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging.Abstractions;

// The in-process mesh sender that bridges IMeshSender to the byte transport lives one namespace up,
// beside AetherDemoService — reused here rather than re-declared.
using AetherNet.Sample.Shared.Services;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// A whole neighbourhood of phones, in-process, driving the real <c>aether-space</c> breadcrumb code.
///
/// <para>Every cell of a geohash grid holds one device — a real <see cref="InProcessTransportService"/>
/// node with a real <see cref="SpaceBreadcrumbService"/> on the wire and a real
/// <see cref="InMemorySpaceService"/> as its local store. A device peers only with the eight cells
/// touching it, so a notice dropped in the middle can only reach the edge of the grid by being carried:
/// each device that pulls it re-hosts it for its own neighbours. That relay is the feature, not a
/// backdrop to it.</para>
///
/// <para>The one rule the shipping services document but leave to the host — <i>forward only to peers
/// within three cells of the breadcrumb's geohash</i> — lives here, measured on the real
/// <see cref="AetherNet.Map.Geohash"/> lattice the grid is built from. A device inside the radius pulls
/// and re-hosts; a device outside it still <b>receives</b> the packet (the mesh delivered it) but drops
/// it on the floor, so the flood dies three cells out instead of crossing the city. An
/// <see cref="BreadcrumbType.Emergency"/> breadcrumb ignores the guard and rides every reachable hop.</para>
///
/// Only the radio is simulated. The drop, the flood, the pull, the cache, the scan, the TTL prune are
/// the same calls the eight SDKs port — <see cref="ISpaceService.DropAsync"/>,
/// <see cref="ISpaceBreadcrumbService.BroadcastAsync"/>/<see cref="ISpaceBreadcrumbService.HandleAsync"/>,
/// <see cref="ISpaceService.PinAsync"/>, <see cref="ISpaceService.ScanAsync"/>,
/// <see cref="ISpaceService.PruneExpired"/>.
/// </summary>
public sealed class BreadcrumbsDemo : IDisposable
{
    public const int Rows = 9;
    public const int Cols = 9;

    /// <summary>The flood-guard radius the aether-space docs pin (<c>ISpaceService</c> propagation rules).</summary>
    public const int RadiusCells = 3;

    private const int MaxLog = 160;

    private readonly Device[] _devices = new Device[Rows * Cols];
    private readonly List<LogLine> _log = new();
    private readonly HashSet<int> _decided = new();   // devices that have made their pull/drop call this run
    private readonly HashSet<int> _received = new();   // devices the current wave delivered to (fills during broadcast)
    private readonly CancellationTokenSource _cts = new();

    private SpaceBreadcrumb? _current;
    private string _currentNote = string.Empty;
    private bool _busy;
    private bool _disposed;

    public BreadcrumbsDemo(double centerLat = -26.2041, double centerLon = 28.0473)
    {
        // Johannesburg, precision-6 (~1.2 km) cells — a believable neighbourhood block.
        Grid = new GeohashGrid(centerLat, centerLon, Rows, Cols, precision: 6);

        // A per-instance id keeps this demo's UHIDs off the process-wide in-process registry that the
        // three-node mesh and other Blazor circuits also use — nobody collides, nobody gets reset.
        var run = Guid.NewGuid().ToString("N")[..8];

        for (var r = 0; r < Rows; r++)
        for (var c = 0; c < Cols; c++)
        {
            var index = Index(r, c);
            var uhid = $"aether:lab:crumb:{run}:{r}-{c}";
            var transport = new InProcessTransportService(uhid, NullLogger<InProcessTransportService>.Instance);
            var sender = new InProcessMeshSender(uhid, transport);
            _devices[index] = new Device(index, r, c, uhid, Grid.Cell(r, c),
                transport, sender, new SpaceBreadcrumbService(sender), new InMemorySpaceService());
        }

        // Radio range is one cell: each device can only reach the (up to) eight cells around it.
        foreach (var d in _devices)
        {
            for (var dr = -1; dr <= 1; dr++)
            for (var dc = -1; dc <= 1; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                int nr = d.Row + dr, nc = d.Col + dc;
                if (nr < 0 || nr >= Rows || nc < 0 || nc >= Cols) continue;
                d.Sender.AddPotentialPeer(_devices[Index(nr, nc)].Uhid);
            }

            var self = d; // capture
            self.Transport.DataReceived += (_src, bytes) =>
            {
                MeshPacket packet;
                try { packet = PacketSerializer.Deserialize(bytes); }
                catch { return; }
                if (packet.Type == PacketType.SpaceBreadcrumb)
                    _ = self.Breadcrumb.HandleAsync(packet); // synchronous — fires BreadcrumbReceived inline
            };
            self.Breadcrumb.BreadcrumbReceived += (_, crumb) => OnReceived(self, crumb);
            self.Space.BreadcrumbExpired += (_, crumb) =>
                Log($"a cached notice at {crumb.GeoHash} passed its TTL and was dropped.", false);
        }
    }

    public event Action? Changed;

    public GeohashGrid Grid { get; }
    public bool Busy => _busy;

    // ── Views the page binds to ──────────────────────────────────────────────
    public enum CrumbKind { Idle, Origin, Carrying, Refused }

    public sealed record DeviceView(int Row, int Col, string Cell, CrumbKind Kind, int Distance, bool InRadius);
    public sealed record CrumbInfo(string Note, BreadcrumbType Type, int TtlHours, DateTime ExpiresAtUtc,
        string ContentHash, int CarriedBy, int Total);
    public sealed record LogLine(string Text, bool Emphasis);

    public IReadOnlyList<DeviceView> Devices()
        => _devices.Select(d => new DeviceView(d.Row, d.Col, d.Cell, d.Kind,
            GeohashGrid.Distance((d.Row, d.Col), Grid.Center),
            GeohashGrid.Distance((d.Row, d.Col), Grid.Center) <= RadiusCells)).ToArray();

    public CrumbInfo? Current => _current is null
        ? null
        : new CrumbInfo(_currentNote, _current.Type, _current.TtlHours, _current.ExpiresAtUtc,
            _current.ContentHash, _devices.Count(d => d.Kind is CrumbKind.Carrying or CrumbKind.Origin), _devices.Length);

    public IReadOnlyList<LogLine> Log() => _log.ToArray();

    // ── The drop, and the flood it starts ────────────────────────────────────

    /// <summary>
    /// Drop a note at the centre cell and let it propagate hop by hop. A device inside the three-cell
    /// radius pulls it and re-hosts it; one outside receives it and drops it; an Emergency ignores the
    /// radius entirely. Every hop is a real broadcast over the in-process wire.
    /// </summary>
    public async Task DropAsync(string note, BreadcrumbType type, int ttlHours)
    {
        if (_busy || _disposed) return;
        _busy = true;
        try
        {
            ResetRun();
            note = string.IsNullOrWhiteSpace(note) ? "Community notice" : note.Trim();

            var origin = _devices[Index(Grid.Center)];
            var hash = ContentHash($"{note}|{DateTime.UtcNow.Ticks}");
            _currentNote = note;

            var crumb = await origin.Space.DropAsync(origin.Cell, hash, origin.Uhid, type, ttlHours);
            _current = crumb;
            origin.Kind = CrumbKind.Origin;
            _decided.Add(origin.Index);
            Log($"You dropped “{note}” at cell {origin.Cell} — {Label(type)}, TTL {crumb.TtlHours} h " +
                $"(expires {crumb.ExpiresAtUtc:MMM d HH:mm} UTC).", true);
            Raise();
            await Delay(220);

            var frontier = new List<Device> { origin };
            var hop = 0;
            while (frontier.Count > 0 && !_disposed)
            {
                hop++;
                _received.Clear();
                foreach (var f in frontier)
                {
                    if (_disposed) return;
                    await f.Breadcrumb.BroadcastAsync(crumb); // real flood; OnReceived fills _received inline
                }

                var newly = _received.Where(i => !_decided.Contains(i))
                    .Select(i => _devices[i])
                    .OrderBy(Dist)
                    .ToList();
                if (newly.Count == 0) break;

                var next = new List<Device>();
                foreach (var d in newly)
                {
                    _decided.Add(d.Index);
                    if (type == BreadcrumbType.Emergency || Dist(d) <= RadiusCells)
                    {
                        await d.Space.PinAsync(crumb); // real auto-pull + cache
                        d.Kind = CrumbKind.Carrying;
                        next.Add(d);                       // and now it re-hosts for the next ring
                    }
                    else
                    {
                        d.Kind = CrumbKind.Refused;        // heard it, but it is out of range of the guard
                    }
                }

                var pulled = newly.Count(x => x.Kind == CrumbKind.Carrying);
                var dropped = newly.Count - pulled;
                Log(dropped == 0
                    ? $"hop {hop}: {pulled} device(s) pulled and re-hosted."
                    : $"hop {hop}: {pulled} pulled + re-hosted, {dropped} received it but were past the {RadiusCells}-cell guard — dropped.",
                    false);
                Raise();
                await Delay(170);
                frontier = next;
            }

            var carried = _devices.Count(d => d.Kind is CrumbKind.Carrying or CrumbKind.Origin);
            Log(type == BreadcrumbType.Emergency
                ? $"Emergency ignored the guard and rode every hop — carried by {carried} of {_devices.Length} devices."
                : $"Settled: carried + re-hosted by {carried} of {_devices.Length}; the flood died {RadiusCells} cells out.",
                true);
            Raise();
        }
        finally
        {
            _busy = false;
        }
    }

    // ── TTL: seed something already stale, then prune it (real IsExpired + PruneExpired) ──

    /// <summary>Cache a notice that was dropped ten days ago under a 72 h TTL — already expired.</summary>
    public async Task SeedStaleNoticeAsync()
    {
        if (_disposed) return;
        var origin = _devices[Index(Grid.Center)];
        var crumb = new SpaceBreadcrumb
        {
            ContentHash = ContentHash($"stale|{DateTime.UtcNow.Ticks}"),
            GeoHash = origin.Cell,
            AnchorUhid = origin.Uhid,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-10),
            TtlHours = 72,
            Type = BreadcrumbType.Notice,
        };
        await origin.Space.PinAsync(crumb);
        Log($"Seeded a notice at {origin.Cell} dropped 10 days ago with a 72 h TTL — so it is already " +
            $"{(crumb.IsExpired ? "expired" : "live")}. Press Prune.", false);
        Raise();
    }

    /// <summary>Every device sweeps its own store; expired notices are removed and their event fires.</summary>
    public void PruneExpired()
    {
        var removed = 0;
        foreach (var d in _devices) removed += d.Space.PruneExpired();
        Log($"Prune swept the neighbourhood — {removed} expired notice(s) removed. TTL is enforced by every device, not a server.", true);
        Raise();
    }

    /// <summary>What the origin device can see near it right now — the real radius-aware scan.</summary>
    public Task<IReadOnlyList<SpaceBreadcrumb>> ScanFromOriginAsync(int radiusCells)
        => _devices[Index(Grid.Center)].Space.ScanAsync(_devices[Index(Grid.Center)].Cell, radiusCells);

    public void ClearLog()
    {
        _log.Clear();
        Raise();
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void OnReceived(Device d, SpaceBreadcrumb crumb)
    {
        if (_current is null || !string.Equals(crumb.ContentHash, _current.ContentHash, StringComparison.Ordinal))
            return;
        _received.Add(d.Index); // recorded, not acted on — the pump decides pull vs. drop, so the flood stays paced
    }

    private void ResetRun()
    {
        foreach (var d in _devices) d.Kind = CrumbKind.Idle;
        _decided.Clear();
        _received.Clear();
        _current = null;
        _currentNote = string.Empty;
    }

    private int Dist(Device d) => GeohashGrid.Distance((d.Row, d.Col), Grid.Center);

    private static int Index(int r, int c) => r * Cols + c;
    private static int Index((int Row, int Col) i) => i.Row * Cols + i.Col;

    private static string Label(BreadcrumbType t) => t switch
    {
        BreadcrumbType.Emergency => "Emergency",
        BreadcrumbType.Commerce => "Commerce",
        BreadcrumbType.Event => "Event",
        BreadcrumbType.JobPosting => "Job posting",
        _ => "Notice",
    };

    private static string ContentHash(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private async Task Delay(int ms)
    {
        try { await Task.Delay(ms, _cts.Token); }
        catch (OperationCanceledException) { }
    }

    private void Log(string text, bool emphasis)
    {
        _log.Add(new LogLine(text, emphasis));
        if (_log.Count > MaxLog) _log.RemoveRange(0, _log.Count - MaxLog);
    }

    private void Raise()
    {
        if (!_disposed) Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        foreach (var d in _devices)
            d.Transport.Dispose(); // leaves the in-process registry
    }

    private sealed class Device
    {
        public Device(int index, int row, int col, string uhid, string cell,
            InProcessTransportService transport, InProcessMeshSender sender,
            SpaceBreadcrumbService breadcrumb, InMemorySpaceService space)
        {
            Index = index;
            Row = row;
            Col = col;
            Uhid = uhid;
            Cell = cell;
            Transport = transport;
            Sender = sender;
            Breadcrumb = breadcrumb;
            Space = space;
        }

        public int Index { get; }
        public int Row { get; }
        public int Col { get; }
        public string Uhid { get; }
        public string Cell { get; }
        public InProcessTransportService Transport { get; }
        public InProcessMeshSender Sender { get; }
        public SpaceBreadcrumbService Breadcrumb { get; }
        public InMemorySpaceService Space { get; }
        public CrumbKind Kind { get; set; } = CrumbKind.Idle;
    }
}
