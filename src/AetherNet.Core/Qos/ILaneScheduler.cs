// SPDX-License-Identifier: MIT

namespace AetherNet.Qos;

/// <summary>
/// A fair outbound scheduler over traffic lanes. Callers enqueue items with a <see cref="TrafficClass"/>
/// and a byte cost; <see cref="TryDequeue"/> returns the next item to send under a policy that keeps a
/// bulk backlog from starving real-time traffic while never blocking emergency or control. The scheduler
/// only decides <em>ordering</em> — the caller still sends the dequeued item over its transport.
/// </summary>
public interface ILaneScheduler<T>
{
    /// <summary>Number of items queued across all lanes.</summary>
    int Count { get; }

    /// <summary>Number of items queued in a single lane.</summary>
    int CountIn(TrafficClass trafficClass);

    /// <summary>Enqueue an item on a lane. <paramref name="cost"/> is the item's size in bytes (clamped to ≥ 1).</summary>
    void Enqueue(T item, TrafficClass trafficClass, int cost);

    /// <summary>
    /// Dequeue the next item to send. Returns false only when every lane is empty. Emergency then Control
    /// are served strictly first; Realtime / Standard / Bulk share the remaining capacity by weighted
    /// deficit round-robin (higher-weight lanes get more bytes per round; none is starved).
    /// </summary>
    bool TryDequeue(out T item, out TrafficClass trafficClass);
}
