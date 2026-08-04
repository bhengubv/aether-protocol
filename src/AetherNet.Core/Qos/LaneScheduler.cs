// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace AetherNet.Qos;

/// <summary>
/// Weighted-deficit-round-robin lane scheduler (see <see cref="ILaneScheduler{T}"/>).
///
/// <para><see cref="TrafficClass.Emergency"/> and <see cref="TrafficClass.Control"/> form a strict tier —
/// low-volume, must-deliver, always served first. <see cref="TrafficClass.Realtime"/>,
/// <see cref="TrafficClass.Standard"/> and <see cref="TrafficClass.Bulk"/> form a deficit-round-robin
/// tier: each visit a lane earns <c>weight × baseQuantum</c> bytes of credit and sends while it can
/// afford its head packet, carrying any remainder to the next round. So a lane with a huge backlog of big
/// packets cannot monopolise the link (it only spends its quantum per round), a real-time packet is never
/// stuck behind the whole bulk queue, and no lane is starved. Thread-safe.</para>
/// </summary>
public sealed class LaneScheduler<T> : ILaneScheduler<T>
{
    /// <summary>Default per-round credit unit (bytes) multiplied by each lane's weight.</summary>
    public const int DefaultBaseQuantumBytes = 4096;

    private static readonly TrafficClass[] WdrrOrder =
        { TrafficClass.Realtime, TrafficClass.Standard, TrafficClass.Bulk };

    private readonly object _lock = new();

    private readonly Dictionary<TrafficClass, Queue<Entry>> _lanes = new()
    {
        [TrafficClass.Emergency] = new(),
        [TrafficClass.Control] = new(),
        [TrafficClass.Realtime] = new(),
        [TrafficClass.Standard] = new(),
        [TrafficClass.Bulk] = new(),
    };

    private readonly Dictionary<TrafficClass, int> _quantum;
    private readonly Dictionary<TrafficClass, int> _deficit = new()
    {
        [TrafficClass.Realtime] = 0,
        [TrafficClass.Standard] = 0,
        [TrafficClass.Bulk] = 0,
    };

    private int _ptr;       // index into WdrrOrder — the current round-robin position
    private bool _granted;  // has the current visit to WdrrOrder[_ptr] already earned its quantum?
    private int _count;

    private readonly record struct Entry(T Item, int Cost);

    /// <summary>
    /// Create a scheduler. Lane weights set the bytes-per-round ratio between Realtime / Standard / Bulk
    /// (defaults 4 : 2 : 1, so real-time gets the largest share and bulk the smallest).
    /// </summary>
    public LaneScheduler(int realtimeWeight = 4, int standardWeight = 2, int bulkWeight = 1, int baseQuantumBytes = DefaultBaseQuantumBytes)
    {
        if (realtimeWeight < 1) throw new ArgumentOutOfRangeException(nameof(realtimeWeight), "Lane weight must be ≥ 1.");
        if (standardWeight < 1) throw new ArgumentOutOfRangeException(nameof(standardWeight), "Lane weight must be ≥ 1.");
        if (bulkWeight < 1) throw new ArgumentOutOfRangeException(nameof(bulkWeight), "Lane weight must be ≥ 1.");
        if (baseQuantumBytes < 1) throw new ArgumentOutOfRangeException(nameof(baseQuantumBytes), "Base quantum must be ≥ 1.");

        _quantum = new()
        {
            [TrafficClass.Realtime] = realtimeWeight * baseQuantumBytes,
            [TrafficClass.Standard] = standardWeight * baseQuantumBytes,
            [TrafficClass.Bulk] = bulkWeight * baseQuantumBytes,
        };
    }

    public int Count
    {
        get { lock (_lock) { return _count; } }
    }

    public int CountIn(TrafficClass trafficClass)
    {
        lock (_lock) { return _lanes[trafficClass].Count; }
    }

    public void Enqueue(T item, TrafficClass trafficClass, int cost)
    {
        if (cost < 1) cost = 1;
        lock (_lock)
        {
            _lanes[trafficClass].Enqueue(new Entry(item, cost));
            _count++;
        }
    }

    public bool TryDequeue(out T item, out TrafficClass trafficClass)
    {
        lock (_lock)
        {
            // Strict tier: emergency then control, always first.
            if (_lanes[TrafficClass.Emergency].Count > 0) return Take(TrafficClass.Emergency, out item, out trafficClass);
            if (_lanes[TrafficClass.Control].Count > 0) return Take(TrafficClass.Control, out item, out trafficClass);

            if (WdrrCount() == 0)
            {
                item = default!;
                trafficClass = default;
                return false;
            }

            // Rotate the WDRR tier until a lane can afford its head packet. Terminates: each visited
            // non-empty lane earns its quantum, so a lane's deficit grows until it covers its head; the
            // guard force-serves a pathologically oversized head so the loop can never spin.
            var guard = WdrrOrder.Length * 2 + 2;
            var forceServe = false;
            while (true)
            {
                var c = WdrrOrder[_ptr];
                var lane = _lanes[c];

                if (lane.Count == 0)
                {
                    _deficit[c] = 0;   // an empty lane forfeits its accrued credit
                    _granted = false;
                    Advance();
                    continue;
                }

                if (!_granted)
                {
                    _deficit[c] += _quantum[c];
                    _granted = true;
                }

                var headCost = lane.Peek().Cost;
                if (forceServe || _deficit[c] >= headCost)
                {
                    _deficit[c] -= headCost;
                    var served = Take(c, out item, out trafficClass);

                    // Stay on this lane while it can still afford its next head; otherwise move on and
                    // let the next visit re-earn a quantum.
                    if (lane.Count == 0 || _deficit[c] < lane.Peek().Cost)
                    {
                        _granted = false;
                        Advance();
                    }
                    return served;
                }

                // Cannot afford the head yet — move on; the next visit adds more credit.
                _granted = false;
                Advance();
                if (--guard <= 0)
                {
                    forceServe = true;
                }
            }
        }
    }

    private bool Take(TrafficClass c, out T item, out TrafficClass trafficClass)
    {
        var entry = _lanes[c].Dequeue();
        _count--;
        item = entry.Item;
        trafficClass = c;
        return true;
    }

    private int WdrrCount()
        => _lanes[TrafficClass.Realtime].Count + _lanes[TrafficClass.Standard].Count + _lanes[TrafficClass.Bulk].Count;

    private void Advance() => _ptr = (_ptr + 1) % WdrrOrder.Length;
}
