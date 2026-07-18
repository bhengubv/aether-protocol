// SPDX-License-Identifier: MIT

using System.Numerics;

namespace AetherNet.BitTorrent.Dht;

/// <summary>
/// A Kademlia routing table (BEP-5): 160 k-buckets indexed by the leading-zero count of the XOR
/// distance from our own node id, each holding up to <see cref="K"/> contacts.
/// </summary>
public sealed class RoutingTable
{
    public const int K = 8;

    private readonly NodeId _self;
    private readonly List<DhtContact>[] _buckets;

    private static readonly IComparer<byte[]> DistanceComparer =
        Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b));

    public RoutingTable(NodeId self)
    {
        _self = self ?? throw new ArgumentNullException(nameof(self));
        _buckets = new List<DhtContact>[160];
        for (int i = 0; i < _buckets.Length; i++) _buckets[i] = new List<DhtContact>();
    }

    public int Count => _buckets.Sum(b => b.Count);

    /// <summary>Add or refresh a contact. Returns false if it's us or its bucket is full.</summary>
    public bool TryAdd(DhtContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (contact.Id.Equals(_self)) return false;

        var bucket = _buckets[BucketIndex(_self.DistanceTo(contact.Id))];
        int existing = bucket.FindIndex(c => c.Id.Equals(contact.Id));
        if (existing >= 0)
        {
            bucket[existing] = contact; // refresh
            return true;
        }
        if (bucket.Count < K)
        {
            bucket.Add(contact);
            return true;
        }
        return false; // bucket full (a full impl would ping the oldest and evict it if dead)
    }

    /// <summary>The <paramref name="count"/> known contacts closest (by XOR distance) to <paramref name="target"/>.</summary>
    public IReadOnlyList<DhtContact> ClosestTo(NodeId target, int count = K)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _buckets
            .SelectMany(b => b)
            .OrderBy(c => target.DistanceTo(c.Id), DistanceComparer)
            .Take(count)
            .ToList();
    }

    private static int BucketIndex(byte[] distance)
    {
        for (int i = 0; i < distance.Length; i++)
        {
            if (distance[i] != 0)
            {
                int leadingZerosInByte = BitOperations.LeadingZeroCount((uint)distance[i]) - 24;
                return Math.Min(159, i * 8 + leadingZerosInByte);
            }
        }
        return 159; // identical id — never actually stored
    }
}
