// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using AetherNet.Protocol;
using AetherNet.Sample.Shared.Services; // InProcessMeshSender (reused from AetherDemoService)
using AetherNet.Transport.Services;      // InProcessTransportService
using AetherNet.Vault;                    // InMemoryVaultService, VaultShardRequestService
using AetherNet.Vault.Models;             // VaultManifest, VaultHealth
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// Drives the real <see cref="InMemoryVaultService"/> — the erasure-coded distributed backup the
/// eight SDKs port — entirely in this page. A secret is sharded by the real systematic
/// Cauchy-Reed-Solomon codec (K=10 data + M=4 parity → 14 shards), each shard laid on its own
/// in-process peer. Peers go dark; the file still reconstructs from ANY ten survivors because the
/// parity shards are MDS; below nine it cannot, and the demo shows exactly that cliff. Health and
/// recovery are computed by the actual service against an <i>effective</i> manifest that hides the
/// shards on offline peers — nothing is faked, the codec really inverts a Cauchy submatrix when a
/// data shard is among the dead.
///
/// A second, smaller panel drives <see cref="VaultShardRequestService"/> across a genuine three-node
/// in-process mesh: a node asks the wire "who holds shard X?" (<c>PacketType.VaultShardRequest</c>,
/// 42) and the holder answers — the transport a real re-replication uses to find a surviving shard.
/// </summary>
public sealed class VaultLabDemo : IDisposable
{
    public const int K = 10;
    public const int M = 4;
    public const int N = K + M; // 14

    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();
    private readonly string _run = Guid.NewGuid().ToString("N")[..6];

    private readonly InMemoryVaultService _vault = new();
    private readonly bool[] _online = new bool[N];

    private VaultManifest? _manifest;
    private VaultHealth? _health;
    private string _secret = "Meeting point: old fig tree, sunrise. Bring the maps.";
    private string? _recovered;
    private bool _integrityOk;
    private bool _started;
    private bool _disposed;

    // The three-node mesh for the VaultShardRequest (PacketType 42) panel.
    private readonly List<MeshNode> _mesh = new();

    public event Action? Changed;

    public string Secret { get => _secret; set => _secret = value; }
    public bool HasBackup => _manifest is not null;
    public VaultManifest? Manifest => _manifest;
    public VaultHealth? Health => _health;
    public string? Recovered => _recovered;
    public bool IntegrityOk => _integrityOk;

    public IReadOnlyList<LogLine> Log()
    {
        lock (_gate) return _log.ToArray();
    }

    /// <summary>A per-shard view: which peer holds it, whether it is data or parity, and if it is live.</summary>
    public IReadOnlyList<ShardView> Shards()
    {
        if (_manifest is null) return Array.Empty<ShardView>();
        var views = new ShardView[N];
        for (int i = 0; i < N; i++)
            views[i] = new ShardView(
                Index: i,
                Peer: $"Peer-{i + 1:00}",
                IsParity: i >= K,
                Online: _online[i],
                Hash8: Short(_manifest.ShardHashes[i]));
        return views;
    }

    public int OnlineCount => _online.Count(x => x);

    // ─── Setup ──────────────────────────────────────────────────────────────────

    /// <summary>Stand up the three-node shard-request mesh. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        var owner = CreateMeshNode("Owner");
        var peerA = CreateMeshNode("Peer-A");
        var peerB = CreateMeshNode("Peer-B");

        foreach (var self in new[] { owner, peerA, peerB })
        foreach (var other in new[] { owner, peerA, peerB })
            if (!ReferenceEquals(self, other))
                self.Sender.AddPotentialPeer(other.Uhid);

        foreach (var node in new[] { owner, peerA, peerB })
        {
            var n = node;
            n.Transport.DataReceived += (_src, bytes) =>
            {
                MeshPacket packet;
                try { packet = PacketSerializer.Deserialize(bytes); }
                catch { return; }
                if (packet.Type == PacketType.VaultShardRequest)
                    _ = n.Shard.HandleAsync(packet);
            };
            // When a peer is asked for a shard, it answers only if it actually holds it.
            n.Shard.ShardRequested += (_, req) =>
            {
                if (n.Held.Contains(req.ShardHash))
                    Emit($"{n.Name} holds shard {Short(req.ShardHash)} — serving it to {PetnameOf(req.RequesterUhid)}.", strong: true);
            };
        }

        lock (_gate) _mesh.AddRange(new[] { owner, peerA, peerB });
    }

    private MeshNode CreateMeshNode(string name)
    {
        var uhid = $"lab:vault:{_run}:{name}";
        var transport = new InProcessTransportService(uhid, NullLogger<InProcessTransportService>.Instance);
        var sender = new InProcessMeshSender(uhid, transport);
        return new MeshNode(name, uhid, transport, sender, new VaultShardRequestService(sender));
    }

    // ─── Backup / drop / recover / health / re-replicate ─────────────────────────

    /// <summary>Shard the secret with the real RS codec and lay each shard on its own peer.</summary>
    public async Task BackupAsync()
    {
        if (string.IsNullOrEmpty(_secret)) return;

        var bytes = Encoding.UTF8.GetBytes(_secret);
        using var stream = new MemoryStream(bytes);
        _manifest = await _vault.StoreAsync(stream, "lab-secret").ConfigureAwait(false);

        for (int i = 0; i < N; i++) _online[i] = true;
        _recovered = null;
        _integrityOk = false;

        // Seed the shard-request mesh: Peer-A archives even shards, Peer-B the odd — so any shard is on
        // exactly one of them, and "who has S07?" has one honest answer. (The owner holds the manifest,
        // not the shards.)
        var peerA = MeshByName("Peer-A");
        var peerB = MeshByName("Peer-B");
        peerA?.Held.Clear();
        peerB?.Held.Clear();
        for (int i = 0; i < N; i++)
            (i % 2 == 0 ? peerA : peerB)?.Held.Add(_manifest.ShardHashes[i]);

        Emit($"Sharded the secret ({bytes.Length} B) into {N}: {K} systematic data + {M} Cauchy-Reed-Solomon parity. Any {K} of the {N} reconstruct it.", strong: true);
        Emit($"Content hash (SHA-256): {Short(_manifest.ContentHash)} — kept in the manifest to prove integrity on recovery.");
        await RefreshHealthAsync().ConfigureAwait(false);
    }

    /// <summary>Toggle a peer (and therefore its one shard) between reachable and dark.</summary>
    public async Task ToggleShardAsync(int index)
    {
        if (_manifest is null || index < 0 || index >= N) return;
        _online[index] = !_online[index];
        Emit(_online[index]
            ? $"Peer-{index + 1:00} back on the mesh — shard {(index >= K ? "P" : "D")}{index:00} reachable again."
            : $"Peer-{index + 1:00} dropped — shard {(index >= K ? "P" : "D")}{index:00} ({Short(_manifest.ShardHashes[index])}) is now unreachable.");
        await RefreshHealthAsync().ConfigureAwait(false);
    }

    /// <summary>Ask the real service to recover the file from whatever shards remain reachable.</summary>
    public async Task RecoverAsync()
    {
        if (_manifest is null) return;
        var effective = Effective();
        try
        {
            using var stream = await _vault.RecoverAsync(effective).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms).ConfigureAwait(false);
            var recoveredBytes = ms.ToArray();
            _recovered = Encoding.UTF8.GetString(recoveredBytes);

            var hash = Convert.ToHexString(SHA256.HashData(recoveredBytes)).ToLowerInvariant();
            _integrityOk = string.Equals(hash, _manifest.ContentHash, StringComparison.Ordinal);

            Emit($"Recovered from {OnlineCount}/{N} reachable shards (needed {K}). Reed-Solomon inverted the surviving generator rows and rebuilt the file.", strong: true);
            Emit(_integrityOk
                ? "Integrity: recovered SHA-256 matches the manifest — byte-for-byte the original."
                : "Integrity: hash MISMATCH — recovered bytes differ from the manifest.");
        }
        catch (InvalidOperationException ex)
        {
            _recovered = null;
            _integrityOk = false;
            Emit($"Unrecoverable: {ex.Message} Below {K} shards there is no way back — that is the erasure-code cliff.", strong: true);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Restore redundancy without the dropped peers: reconstruct from survivors and re-encode a fresh
    /// full shard set, then place every shard on a live peer again. (The in-memory service's
    /// <see cref="InMemoryVaultService.ReplicateAsync"/> is a documented no-op with no remote peers to
    /// copy to, so the demo does the real reconstruct-and-re-encode a networked replicator would do.)
    /// </summary>
    public async Task ReReplicateAsync()
    {
        if (_manifest is null) return;
        if (OnlineCount < K)
        {
            Emit($"Cannot re-replicate: only {OnlineCount}/{N} shards reachable, fewer than the {K} needed to reconstruct.");
            return;
        }

        // Reconstruct from the survivors, then re-store to regenerate the full N-shard set.
        using var recovered = await _vault.RecoverAsync(Effective()).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await recovered.CopyToAsync(ms).ConfigureAwait(false);
        ms.Position = 0;
        _manifest = await _vault.StoreAsync(ms, "lab-secret").ConfigureAwait(false);
        await _vault.ReplicateAsync(_manifest).ConfigureAwait(false); // real API (no-op in-memory), invoked for fidelity

        for (int i = 0; i < N; i++) _online[i] = true;

        var peerA = MeshByName("Peer-A");
        var peerB = MeshByName("Peer-B");
        peerA?.Held.Clear();
        peerB?.Held.Clear();
        for (int i = 0; i < N; i++)
            (i % 2 == 0 ? peerA : peerB)?.Held.Add(_manifest.ShardHashes[i]);

        Emit($"Re-replicated: reconstructed from the survivors and re-encoded {N} fresh shards, each on a live peer. Redundancy back to {N}/{N}.", strong: true);
        await RefreshHealthAsync().ConfigureAwait(false);
    }

    /// <summary>Ask the mesh over the wire (PacketType 42) which peer holds a given shard.</summary>
    public async Task LocateShardAsync(int index)
    {
        if (_manifest is null || index < 0 || index >= N) return;
        var owner = MeshByName("Owner");
        if (owner is null) return;

        var hash = _manifest.ShardHashes[index];
        Emit($"Owner broadcasts a VaultShardRequest for shard {(index >= K ? "P" : "D")}{index:00} ({Short(hash)})…");
        var reached = await owner.Shard.RequestShardAsync(hash).ConfigureAwait(false);
        Emit($"Request reached {reached} peer(s) on the wire. The one holding it answers below.");
        RaiseChanged();
    }

    public void ClearLog()
    {
        lock (_gate) _log.Clear();
        RaiseChanged();
    }

    // ─── Internals ────────────────────────────────────────────────────────────────

    private async Task RefreshHealthAsync()
    {
        if (_manifest is null) return;
        _health = await _vault.CheckHealthAsync(Effective()).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>
    /// A copy of the manifest whose offline shards are pointed at a hash the store does not hold — so
    /// the real service reports them as unreachable. This is how a "dropped peer" is expressed to the
    /// unmodified <see cref="InMemoryVaultService"/>.
    /// </summary>
    private VaultManifest Effective()
    {
        var m = _manifest!;
        var hashes = new string[N];
        for (int i = 0; i < N; i++)
            hashes[i] = _online[i] ? m.ShardHashes[i] : $"offline-peer-{i}";
        return new VaultManifest
        {
            FileId = m.FileId,
            ContentHash = m.ContentHash,
            EncryptionSalt = m.EncryptionSalt,
            ShardHashes = hashes,
            K = m.K,
            M = m.M,
            CreatedAtUtc = m.CreatedAtUtc,
            SizeBytes = m.SizeBytes,
            Label = m.Label,
        };
    }

    private MeshNode? MeshByName(string name)
    {
        lock (_gate) return _mesh.FirstOrDefault(n => n.Name == name);
    }

    private void Emit(string text, bool strong = false)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(text, strong));
            if (_log.Count > 200) _log.RemoveRange(0, _log.Count - 200);
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    private static string Short(string hash) => hash.Length <= 10 ? hash : hash[..10];

    private static string PetnameOf(string uhid)
    {
        var parts = uhid.Split(':');
        return parts.Length >= 1 ? parts[^1] : uhid;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var n in _mesh) n.Transport.Dispose();
            _mesh.Clear();
        }
    }

    // ─── View + node types ───────────────────────────────────────────────────────

    public sealed record LogLine(string Text, bool Strong);

    public sealed record ShardView(int Index, string Peer, bool IsParity, bool Online, string Hash8);

    private sealed class MeshNode
    {
        public MeshNode(string name, string uhid, InProcessTransportService transport,
            InProcessMeshSender sender, VaultShardRequestService shard)
        {
            Name = name;
            Uhid = uhid;
            Transport = transport;
            Sender = sender;
            Shard = shard;
        }

        public string Name { get; }
        public string Uhid { get; }
        public InProcessTransportService Transport { get; }
        public InProcessMeshSender Sender { get; }
        public VaultShardRequestService Shard { get; }
        public HashSet<string> Held { get; } = new(StringComparer.Ordinal);
    }
}
