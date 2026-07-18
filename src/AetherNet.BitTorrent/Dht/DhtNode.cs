// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.Trackers;

namespace AetherNet.BitTorrent.Dht;

/// <summary>
/// A Mainline DHT node (BEP-5) over UDP: answers <c>ping</c> / <c>find_node</c> / <c>get_peers</c> /
/// <c>announce_peer</c>, issues those queries with transaction-id correlation and a timeout, maintains
/// a Kademlia <see cref="RoutingTable"/>, stores token-gated peer announcements, and performs an
/// iterative <see cref="FindPeersAsync"/> lookup — trackerless peer discovery.
/// </summary>
public sealed class DhtNode : IAsyncDisposable
{
    public NodeId Id { get; }

    private readonly UdpClient _udp;
    private readonly RoutingTable _table;
    private readonly TimeSpan _timeout;
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(16);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<KrpcMessage>> _pending = new();
    private readonly Dictionary<string, HashSet<IPEndPoint>> _peers = new();
    private readonly object _peersLock = new();

    private static readonly IComparer<byte[]> DistanceComparer =
        Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b));

    private int _txnCounter;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public DhtNode(NodeId? id = null, int port = 0, TimeSpan? timeout = null)
    {
        Id = id ?? NodeId.Random();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        _table = new RoutingTable(Id);
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    // ── Server: receive + answer queries, correlate responses ────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await _udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }

            KrpcMessage message;
            try { message = KrpcMessage.Decode(result.Buffer); }
            catch { continue; }

            _ = HandleAsync(message, result.RemoteEndPoint);
        }
    }

    private async Task HandleAsync(KrpcMessage message, IPEndPoint from)
    {
        if (message.Type is KrpcType.Response or KrpcType.Error)
        {
            if (_pending.TryRemove(Key(message.TransactionId), out var tcs)) tcs.TrySetResult(message);
            return;
        }

        // Learn the querying node.
        if (message.Body["id"] is { } senderId && senderId.AsBytes().Length == NodeId.Length)
            _table.TryAdd(new DhtContact(new NodeId(senderId.AsBytes()), from));

        try
        {
            var body = BuildResponse(message, from);
            var reply = new KrpcMessage { TransactionId = message.TransactionId, Type = KrpcType.Response, Body = body }.Encode();
            await _udp.SendAsync(reply, from).ConfigureAwait(false);
        }
        catch { /* malformed query / send failure — ignore */ }
    }

    private BencodeDictionary BuildResponse(KrpcMessage query, IPEndPoint from)
    {
        var r = new BencodeDictionary();
        r.Add("id", new BencodeString(Id.ToBytes()));
        switch (query.Method)
        {
            case "find_node":
            {
                var target = new NodeId(query.Body["target"]!.AsBytes());
                r.Add("nodes", new BencodeString(CompactInfo.EncodeNodes(_table.ClosestTo(target))));
                break;
            }
            case "get_peers":
            {
                var infoHash = query.Body["info_hash"]!.AsBytes();
                r.Add("token", new BencodeString(MakeToken(from.Address)));
                var have = GetPeers(infoHash);
                if (have.Count > 0)
                    r.Add("values", new BencodeList(have.Select(ep => (BencodeValue)new BencodeString(CompactInfo.EncodePeer(ep))).ToList()));
                else
                    r.Add("nodes", new BencodeString(CompactInfo.EncodeNodes(_table.ClosestTo(new NodeId(infoHash)))));
                break;
            }
            case "announce_peer":
            {
                var infoHash = query.Body["info_hash"]!.AsBytes();
                var token = query.Body["token"]!.AsBytes();
                if (!ValidToken(token, from.Address)) throw new KrpcException("bad announce token");
                bool impliedPort = (query.Body["implied_port"]?.AsInteger() ?? 0) == 1;
                int port = impliedPort ? from.Port : (int)query.Body["port"]!.AsInteger();
                AddPeer(infoHash, new IPEndPoint(from.Address, port));
                break;
            }
            // "ping" (and anything else): just { id }.
        }
        return r;
    }

    // ── Client: queries ──────────────────────────────────────────────────────────

    public async Task<KrpcMessage> QueryAsync(IPEndPoint endpoint, string method, BencodeDictionary args, CancellationToken ct = default)
    {
        args.Add("id", new BencodeString(Id.ToBytes()));
        var txn = NextTxn();
        var tcs = new TaskCompletionSource<KrpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[Key(txn)] = tcs;
        try
        {
            var request = new KrpcMessage { TransactionId = txn, Type = KrpcType.Query, Method = method, Body = args }.Encode();
            await _udp.SendAsync(request, endpoint, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);
            var response = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (response.Type == KrpcType.Error)
                throw new KrpcException($"{method} error: {response.Error?.Message}");
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new KrpcException($"{method} timed out");
        }
        finally
        {
            _pending.TryRemove(Key(txn), out _);
        }
    }

    public async Task<NodeId> PingAsync(IPEndPoint endpoint, CancellationToken ct = default)
    {
        var resp = await QueryAsync(endpoint, "ping", new BencodeDictionary(), ct).ConfigureAwait(false);
        return new NodeId(resp.Body["id"]!.AsBytes());
    }

    /// <summary>Ping a node and add it to the routing table (a bootstrap contact).</summary>
    public async Task BootstrapAsync(IPEndPoint endpoint, CancellationToken ct = default)
    {
        var id = await PingAsync(endpoint, ct).ConfigureAwait(false);
        _table.TryAdd(new DhtContact(id, endpoint));
    }

    public async Task<(byte[] Token, IReadOnlyList<PeerAddress> Peers, IReadOnlyList<DhtContact> Nodes)> GetPeersAsync(
        IPEndPoint endpoint, byte[] infoHash, CancellationToken ct = default)
    {
        var args = new BencodeDictionary();
        args.Add("info_hash", new BencodeString(infoHash));
        var resp = await QueryAsync(endpoint, "get_peers", args, ct).ConfigureAwait(false);

        var token = resp.Body["token"]?.AsBytes() ?? Array.Empty<byte>();
        var peers = resp.Body["values"] is BencodeList values ? CompactInfo.DecodePeerValues(values) : Array.Empty<PeerAddress>();
        var nodes = resp.Body["nodes"] is BencodeString ns ? CompactInfo.DecodeNodes(ns.Value) : Array.Empty<DhtContact>();
        foreach (var n in nodes) _table.TryAdd(n);
        return (token, peers, nodes);
    }

    public async Task AnnouncePeerAsync(IPEndPoint endpoint, byte[] infoHash, int port, byte[] token, CancellationToken ct = default)
    {
        var args = new BencodeDictionary();
        args.Add("info_hash", new BencodeString(infoHash));
        args.Add("port", new BencodeInteger(port));
        args.Add("token", new BencodeString(token));
        await QueryAsync(endpoint, "announce_peer", args, ct).ConfigureAwait(false);
    }

    /// <summary>Iteratively query progressively-closer nodes for peers holding <paramref name="infoHash"/>.</summary>
    public async Task<IReadOnlyList<PeerAddress>> FindPeersAsync(byte[] infoHash, int rounds = 4, CancellationToken ct = default)
    {
        var target = new NodeId(infoHash);
        var queried = new HashSet<string>();
        var candidates = new List<DhtContact>(_table.ClosestTo(target));
        var seenPeers = new HashSet<string>();
        var results = new List<PeerAddress>();

        for (int round = 0; round < rounds; round++)
        {
            var batch = candidates.Where(c => queried.Add(c.EndPoint.ToString())).Take(RoutingTable.K).ToList();
            if (batch.Count == 0) break;

            foreach (var contact in batch)
            {
                try
                {
                    var (_, peers, nodes) = await GetPeersAsync(contact.EndPoint, infoHash, ct).ConfigureAwait(false);
                    foreach (var p in peers)
                        if (seenPeers.Add(p.ToString())) results.Add(p);
                    candidates.AddRange(nodes);
                }
                catch (KrpcException) { /* unresponsive node */ }
            }

            candidates = candidates.OrderBy(c => target.DistanceTo(c.Id), DistanceComparer).ToList();
        }

        return results;
    }

    // ── Peer store + tokens ──────────────────────────────────────────────────────

    private void AddPeer(byte[] infoHash, IPEndPoint endpoint)
    {
        lock (_peersLock)
        {
            var key = Convert.ToHexString(infoHash);
            if (!_peers.TryGetValue(key, out var set)) _peers[key] = set = new HashSet<IPEndPoint>();
            set.Add(endpoint);
        }
    }

    private IReadOnlyList<IPEndPoint> GetPeers(byte[] infoHash)
    {
        lock (_peersLock)
        {
            return _peers.TryGetValue(Convert.ToHexString(infoHash), out var set) ? set.ToList() : Array.Empty<IPEndPoint>();
        }
    }

    private byte[] MakeToken(IPAddress address)
    {
        var addr = address.GetAddressBytes();
        var input = new byte[addr.Length + _secret.Length];
        addr.CopyTo(input, 0);
        _secret.CopyTo(input, addr.Length);
        return SHA1.HashData(input)[..4];
    }

    private bool ValidToken(byte[] token, IPAddress address) => token.AsSpan().SequenceEqual(MakeToken(address));

    private static string Key(byte[] transactionId) => Convert.ToHexString(transactionId);

    private byte[] NextTxn()
    {
        var n = (ushort)Interlocked.Increment(ref _txnCounter);
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, n);
        return b;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _udp.Dispose();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
    }
}
