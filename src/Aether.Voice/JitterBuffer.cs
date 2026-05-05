// SPDX-License-Identifier: MIT

using Aether.Voice.Models;

namespace Aether.Voice;

/// <summary>
/// Time-based jitter buffer. Frames are inserted as they arrive (potentially out of order
/// or with duplicates). <see cref="Pop"/> returns the next frame in sequence order if its
/// playout deadline has passed, or null if we should still wait.
///
/// Backed by a simple sorted dictionary; for the typical 20ms-frame, ≤200ms-buffer regime
/// this is comfortably fast. Hosts that need a fancier dropout-tolerant implementation can
/// build their own; this default matches what the private CircleAether voice service ships.
/// </summary>
public sealed class JitterBuffer
{
    private readonly SortedDictionary<uint, VoiceFrame> _frames = new();
    private readonly object _gate = new();
    private readonly int _targetMs;
    private readonly int _maxMs;
    private long _firstArrivalTicks;
    private uint? _nextExpected;

    /// <summary>Last sequence number popped, useful for diagnostics.</summary>
    public uint? LastPopped { get; private set; }

    /// <summary>Number of frames currently buffered.</summary>
    public int Count
    {
        get { lock (_gate) return _frames.Count; }
    }

    public JitterBuffer(
        int targetDepthMs = Aether.Constants.ProtocolConstants.JitterBufferTargetMs,
        int maxDepthMs = Aether.Constants.ProtocolConstants.JitterBufferMaxMs)
    {
        if (targetDepthMs <= 0) throw new ArgumentOutOfRangeException(nameof(targetDepthMs));
        if (maxDepthMs < targetDepthMs) throw new ArgumentOutOfRangeException(nameof(maxDepthMs));
        _targetMs = targetDepthMs;
        _maxMs = maxDepthMs;
    }

    /// <summary>Insert a frame. Duplicates and frames older than the next-expected sequence are dropped.</summary>
    public void Push(VoiceFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            if (_firstArrivalTicks == 0) _firstArrivalTicks = Environment.TickCount64;
            if (_nextExpected.HasValue && SeqLessThan(frame.Sequence, _nextExpected.Value)) return;
            _frames[frame.Sequence] = frame;

            // Hard cap: drop oldest if we exceed max depth so memory doesn't grow without bound.
            while (_frames.Count > 0 && DepthMs() > _maxMs)
            {
                using var enumerator = _frames.Keys.GetEnumerator();
                if (enumerator.MoveNext())
                    _frames.Remove(enumerator.Current);
            }
        }
    }

    /// <summary>
    /// Returns the next frame to play, or null if the target depth has not yet been reached
    /// since the first arrival. Frames are returned strictly in sequence order; gaps appear
    /// as null returns *after* a previous frame, which the caller should interpret as
    /// concealment / silence frames.
    /// </summary>
    public VoiceFrame? Pop()
    {
        lock (_gate)
        {
            if (_frames.Count == 0) return null;
            if (_firstArrivalTicks == 0) return null;

            var elapsed = Environment.TickCount64 - _firstArrivalTicks;
            if (elapsed < _targetMs) return null;

            using var enumerator = _frames.GetEnumerator();
            enumerator.MoveNext();
            var head = enumerator.Current;
            _frames.Remove(head.Key);
            _nextExpected = head.Key + 1;
            LastPopped = head.Key;
            return head.Value;
        }
    }

    /// <summary>Reset state; call when a call ends.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _firstArrivalTicks = 0;
            _nextExpected = null;
            LastPopped = null;
        }
    }

    private int DepthMs()
    {
        if (_frames.Count <= 1) return 0;
        var first = _frames.Values.GetEnumerator();
        first.MoveNext();
        var firstTs = first.Current.TimestampMs;

        long lastTs = firstTs;
        foreach (var frame in _frames.Values) lastTs = frame.TimestampMs;
        return (int)(lastTs - firstTs);
    }

    /// <summary>Sequence comparison treating uint32 wraparound as a circular distance.</summary>
    private static bool SeqLessThan(uint a, uint b)
        => (int)(a - b) < 0;
}
