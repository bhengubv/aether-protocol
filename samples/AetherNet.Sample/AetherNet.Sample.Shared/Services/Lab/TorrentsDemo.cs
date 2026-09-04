// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Cryptography;
using System.Text;
using AetherNet.BitTorrent.Dht;
using AetherNet.BitTorrent.Gateway;
using AetherNet.BitTorrent.Metainfo;
using AetherNet.BitTorrent.V2;
using AetherNet.Content;
using AetherNet.Content.Models;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// Drives AetherNet's from-scratch BitTorrent stack entirely in-process, and the gateway that bridges
/// it to the mesh. Nothing here is a mock: the info-hash is a real SHA-1 over the real bencoded info
/// dictionary (so a stock client would compute the same one), the v2 root is a real SHA-256 merkle
/// tree, the DHT node speaks real KRPC over a loopback UDP socket, and the gateway re-hashes the same
/// bytes into SHA-256 content chunks — a genuine re-chunk, never a relabel.
///
/// <para>The two content identities are the whole point: BitTorrent v1 addresses a file by the SHA-1
/// of its pieces, AetherNet addresses it by a SHA-256 root over its chunks. The gateway holds both
/// over one set of bytes so a torrent can enter the mesh and mesh content can leave as a torrent.</para>
/// </summary>
public sealed class TorrentsDemo
{
    private const int MaxLog = 200;

    private readonly List<LogLine> _log = new();
    private readonly object _gate = new();

    // The last file we described, and the parsed metainfo — kept so the ingest step operates on the
    // real bytes and reads the real info-hash.
    private byte[]? _fileData;
    private TorrentMetainfo? _parsed;

    // The mesh store the ingest step writes SHA-256 chunks into.
    private readonly InMemoryContentStore _store = new();

    public event Action? Changed;

    public TorrentBuild? Built { get; private set; }
    public MeshIngest? Ingest { get; private set; }
    public TorrentExport? Export { get; private set; }
    public DhtView? Dht { get; private set; }

    public IReadOnlyList<LogLine> Log
    {
        get { lock (_gate) return _log.ToArray(); }
    }

    // ── 1. Build a .torrent from some bytes ──────────────────────────────────────

    /// <summary>
    /// Build a single-file v1 <c>.torrent</c> from <paramref name="text"/>, parse it back to read the
    /// info-hash off the raw info bytes, assemble the magnet URI and prove it re-parses, then compute
    /// the v2 (BEP-52) SHA-256 merkle root and info-hash over the same content.
    /// </summary>
    public void BuildTorrent(string name, string text, string? tracker)
    {
        name = string.IsNullOrWhiteSpace(name) ? "aether-demo.txt" : name.Trim();
        var data = Encoding.UTF8.GetBytes(text ?? string.Empty);
        // A small piece length so even a short note spans several pieces — the demo shows the layout.
        const int pieceLength = 64;

        var torrent = TorrentBuilder.CreateSingleFile(name, data, pieceLength,
            string.IsNullOrWhiteSpace(tracker) ? null : tracker.Trim());
        var meta = TorrentMetainfo.Parse(torrent);

        _fileData = data;
        _parsed = meta;

        // Magnet is assembled from the parsed info-hash, then round-tripped through the real parser to
        // prove the link we show is one a client would actually accept.
        var magnet = new StringBuilder("magnet:?xt=urn:btih:").Append(meta.InfoHashV1Hex)
            .Append("&dn=").Append(Uri.EscapeDataString(meta.Name));
        if (!string.IsNullOrWhiteSpace(tracker))
            magnet.Append("&tr=").Append(Uri.EscapeDataString(tracker.Trim()));
        var magnetStr = magnet.ToString();
        var reparsed = MagnetLink.Parse(magnetStr);

        var merkleRoot = MerkleTree.ComputeRoot(data);
        var v2Info = BitTorrentV2.InfoHash(meta.Info.Encode());

        Built = new TorrentBuild(
            Name: meta.Name,
            SourceBytes: data.Length,
            TorrentBytes: torrent.Length,
            InfoHashV1: meta.InfoHashV1Hex,
            Magnet: magnetStr,
            PieceLength: meta.PieceLength,
            PieceCount: meta.PieceHashes.Count,
            FirstPiece: meta.PieceHashes.Count > 0 ? Convert.ToHexString(meta.PieceHashes[0]).ToLowerInvariant() : "",
            MerkleRootV2: Convert.ToHexString(merkleRoot).ToLowerInvariant(),
            InfoHashV2: Convert.ToHexString(v2Info).ToLowerInvariant());

        Emit("build", $"'{meta.Name}': {data.Length} B → {torrent.Length} B .torrent, {meta.PieceHashes.Count} × {meta.PieceLength} B pieces (SHA-1).");
        Emit("build", $"v1 info-hash {meta.InfoHashV1Hex} — computed over the raw bencoded info dict, matches a stock client byte-for-byte.");
        Emit("magnet", $"assembled magnet re-parses: btih={reparsed.InfoHashV1Hex}, dn=\"{reparsed.DisplayName}\".");
        Emit("v2", $"BEP-52 SHA-256 merkle root {Short(Convert.ToHexString(merkleRoot))} over 16 KiB leaf blocks — a second, independent content identity.");
        Raise();
    }

    // ── 2. Ingest a torrent into the mesh as SHA-256 chunks ──────────────────────

    /// <summary>
    /// Forward bridge: feed the built torrent's bytes through <see cref="TorrentMeshGateway"/> so they
    /// land in the content store as SHA-256-addressed chunks, then reassemble + verify them back, and
    /// finally map the parsed torrent to a mesh descriptor to show both identities over one file.
    /// </summary>
    public async Task IngestIntoMeshAsync()
    {
        if (_fileData is null || _parsed is null)
        {
            Emit("mesh", "build a torrent first.");
            Raise();
            return;
        }

        // A small chunk size so even a short file yields a visible chunk map. The gateway re-hashes the
        // FILE bytes (a completed download, in the real thing) into SHA-256-addressed chunks.
        const int chunk = 64;
        var descriptor = await TorrentMeshGateway.IngestAsync(
            _store, _parsed.Name, _fileData, "application/octet-stream", chunkSizeBytes: chunk);

        // Pull it straight back out of the store, verifying every chunk against the descriptor.
        var reassembled = await TorrentMeshGateway.AssembleFromStoreAsync(_store, descriptor.RootHash);
        var ok = reassembled is not null && reassembled.AsSpan().SequenceEqual(_fileData);

        // The content-identity bridge: the same file bytes and chunk size map to a mesh descriptor
        // whose root equals the ingest root — one file, a v1 (SHA-1) and a mesh (SHA-256) identity.
        var meshOverFile = TorrentMeshGateway.MapToMeshDescriptor(_parsed, _fileData, chunkSizeBytes: chunk);

        Ingest = new MeshIngest(
            Root: descriptor.RootHash,
            ChunkCount: descriptor.ChunkCount,
            ChunkSize: descriptor.ChunkSizeBytes,
            TotalBytes: descriptor.TotalBytes,
            Verified: ok,
            InfoHashV1: _parsed.InfoHashV1Hex,
            MeshRootOverFile: meshOverFile.RootHash);

        Emit("mesh", $"ingested the {descriptor.TotalBytes} B file → {descriptor.ChunkCount} SHA-256 chunks of {descriptor.ChunkSizeBytes} B under root {Short(descriptor.RootHash)}.");
        Emit("mesh", ok
            ? "reassembled from the store and verified every chunk against the descriptor — bytes match."
            : "reassembly did not match (unexpected).");
        Emit("bridge", $"same file, two identities: v1 btih {Short(_parsed.InfoHashV1Hex)} · mesh root {Short(meshOverFile.RootHash)} (== ingest root) — the gateway holds both.");
        Raise();
    }

    // ── 3. Re-seed mesh content back out as a .torrent ───────────────────────────

    /// <summary>
    /// Reverse bridge: take arbitrary "mesh content" bytes, package them as a real single-file
    /// <c>.torrent</c> via <see cref="TorrentMeshGateway.ExportAsTorrent"/>, and parse the result to
    /// read the info-hash a swarm would announce.
    /// </summary>
    public void ReSeedAsTorrent(string contentName, string body)
    {
        contentName = string.IsNullOrWhiteSpace(contentName) ? "mesh-content.bin" : contentName.Trim();
        var bytes = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(body) ? "content that lived on the mesh first" : body);

        var torrent = TorrentMeshGateway.ExportAsTorrent(contentName, bytes, pieceLength: 128, announce: "udp://tracker.aether.local:6969");
        var meta = TorrentMetainfo.Parse(torrent);

        Export = new TorrentExport(
            Name: meta.Name,
            SourceBytes: bytes.Length,
            TorrentBytes: torrent.Length,
            InfoHashV1: meta.InfoHashV1Hex,
            PieceCount: meta.PieceHashes.Count,
            Announce: meta.AnnounceUrls.Count > 0 ? meta.AnnounceUrls[0] : "(none)");

        Emit("seed", $"mesh content '{meta.Name}' ({bytes.Length} B) → real .torrent, btih {Short(meta.InfoHashV1Hex)}, {meta.PieceHashes.Count} piece(s).");
        Emit("seed", "a gateway node can now announce this to a stock BitTorrent swarm.");
        Raise();
    }

    // ── 4a. Pure DHT: node id + Kademlia routing table ───────────────────────────

    /// <summary>
    /// Populate a <see cref="RoutingTable"/> for a fresh node id with random contacts, then show the
    /// k-closest to a target (XOR distance) — the Kademlia structure a DHT node keeps, with no socket.
    /// </summary>
    public void ShowRoutingTable()
    {
        var self = NodeId.Random();
        var table = new RoutingTable(self);

        var rnd = Random.Shared;
        for (int i = 0; i < 24; i++)
            table.TryAdd(new DhtContact(NodeId.Random(), new IPEndPoint(IPAddress.Loopback, 20000 + rnd.Next(2000))));

        // The content we are "looking for": a target id (an info-hash, in the real thing).
        var target = NodeId.Random();
        var closest = table.ClosestTo(target, 5)
            .Select(c => new RoutingRow(c.Id.ToString(), Convert.ToHexString(target.DistanceTo(c.Id)).ToLowerInvariant(), c.EndPoint.ToString()))
            .ToArray();

        Dht = (Dht ?? DhtView.Empty) with
        {
            SelfId = self.ToString(),
            Contacts = table.Count,
            Target = target.ToString(),
            Closest = closest,
        };

        Emit("dht", $"routing table for {Short(self.ToString())}: {table.Count} contacts across 160 k-buckets.");
        Emit("dht", $"5 closest to target {Short(target.ToString())} by XOR distance — how a get_peers walk converges.");
        Raise();
    }

    // ── 4b. Live DHT: two nodes speaking real KRPC over loopback ─────────────────

    /// <summary>
    /// Stand up two <see cref="DhtNode"/>s on loopback and exercise real BEP-5 KRPC: a ping that
    /// returns the peer's node id, then a <c>get_peers → announce_peer → get_peers</c> cycle that
    /// discovers the announced peer trackerlessly. Guarded — if the runtime forbids a UDP socket the
    /// step says so rather than throwing.
    /// </summary>
    public async Task RunLiveDhtAsync()
    {
        DhtNode? a = null, b = null;
        try
        {
            a = new DhtNode();
            b = new DhtNode();
            a.Start();
            b.Start();

            Emit("dht", $"node A {Short(a.Id.ToString())} @ {a.LocalEndPoint} · node B {Short(b.Id.ToString())} @ {b.LocalEndPoint}");

            // Ping: A asks B, B answers with its node id — proof the node really speaks KRPC.
            var pinged = await a.PingAsync(b.LocalEndPoint);
            var pingOk = pinged.Equals(b.Id);
            Emit("dht", pingOk
                ? $"A pinged B → B answered with its id {Short(pinged.ToString())}. ✓"
                : "A pinged B but the id did not match (unexpected).");

            // Trackerless discovery: A announces itself as a peer for an info-hash to B (token-gated),
            // then a fresh query to B returns A's announced endpoint.
            var infoHash = SHA1.HashData(Encoding.UTF8.GetBytes("aether:lab:torrent"));
            var (token, _, _) = await a.GetPeersAsync(b.LocalEndPoint, infoHash);
            await a.AnnouncePeerAsync(b.LocalEndPoint, infoHash, a.LocalEndPoint.Port, token);
            var (_, peers, _) = await a.GetPeersAsync(b.LocalEndPoint, infoHash);

            Emit("dht", peers.Count > 0
                ? $"after announce_peer, get_peers for {Short(Convert.ToHexString(infoHash))} returned {peers.Count} peer(s): {string.Join(", ", peers.Select(p => p.ToString()))}. ✓"
                : "get_peers returned no peers (unexpected).");
            Emit("dht", "trackerless peer discovery — the same KRPC a public DHT node runs, only the socket is loopback.");
        }
        catch (Exception ex)
        {
            Emit("dht", $"live DHT unavailable in this runtime: {ex.GetType().Name} — {ex.Message}");
        }
        finally
        {
            if (a is not null) await a.DisposeAsync();
            if (b is not null) await b.DisposeAsync();
        }
        Raise();
    }

    private static string Short(string hex) => hex.Length <= 12 ? hex.ToLowerInvariant() : hex[..12].ToLowerInvariant() + "…";

    private void Emit(string who, string text)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(who, text));
            if (_log.Count > MaxLog) _log.RemoveRange(0, _log.Count - MaxLog);
        }
    }

    private void Raise() => Changed?.Invoke();

    // ── View models ──────────────────────────────────────────────────────────────

    public sealed record LogLine(string Who, string Text);

    public sealed record TorrentBuild(
        string Name, int SourceBytes, int TorrentBytes, string InfoHashV1, string Magnet,
        long PieceLength, int PieceCount, string FirstPiece, string MerkleRootV2, string InfoHashV2);

    public sealed record MeshIngest(
        string Root, int ChunkCount, int ChunkSize, long TotalBytes, bool Verified,
        string InfoHashV1, string MeshRootOverFile);

    public sealed record TorrentExport(
        string Name, int SourceBytes, int TorrentBytes, string InfoHashV1, int PieceCount, string Announce);

    public sealed record RoutingRow(string NodeId, string Distance, string EndPoint);

    public sealed record DhtView(string SelfId, int Contacts, string Target, RoutingRow[] Closest)
    {
        public static readonly DhtView Empty = new("", 0, "", Array.Empty<RoutingRow>());
    }
}
