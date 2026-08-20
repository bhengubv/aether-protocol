// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Several people talking, one earpiece.
///
/// <para>
/// A 1:1 call can hand each frame straight to the speaker as it arrives, because there is only ever
/// one stream and its pace is the pace of playback. A group call cannot: three people speaking are
/// three independent streams arriving on their own schedules, and playing them as they land means
/// each one interrupts the last. They have to be summed.
/// </para>
///
/// <para>
/// So each participant gets a short queue, and a pump takes one frame from every queue on the beat
/// and adds them together. Someone who is silent contributes silence rather than holding the beat up,
/// which is what stops one person on a bad link stalling the conversation for everybody.
/// </para>
/// </summary>
public sealed class AudioMixer : IDisposable
{
    /// <summary>
    /// How many frames may wait for one participant before the oldest is dropped.
    ///
    /// <para>
    /// Four frames is 80 ms — enough to ride out a jittery link, short enough that nobody hears a
    /// delay. Beyond that, holding audio is worse than losing it: a queue that grows is a person who
    /// gets steadily further behind the conversation and never catches up.
    /// </para>
    /// </summary>
    public const int QueuedFramesPerSpeaker = 4;

    private readonly ConcurrentDictionary<string, ConcurrentQueue<short[]>> _queues = new(StringComparer.Ordinal);
    private readonly int _frameSamples;
    private bool _disposed;

    /// <param name="frameSamples">Samples in one frame — every stream in a call agrees on this.</param>
    public AudioMixer(int frameSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameSamples);
        _frameSamples = frameSamples;
    }

    /// <summary>Who currently has audio waiting to be played.</summary>
    public IReadOnlyCollection<string> Speakers => _queues.Keys.ToArray();

    /// <summary>
    /// Take one decoded frame from one participant.
    ///
    /// <para>
    /// Frames of the wrong length are refused rather than padded. A participant whose codec disagrees
    /// about the frame size would otherwise be mixed in at the wrong rate and turn the whole call into
    /// noise — far harder to diagnose than one person simply not being heard.
    /// </para>
    /// </summary>
    public void Offer(string from, short[] pcm)
    {
        if (_disposed || string.IsNullOrEmpty(from)) return;
        if (pcm is null || pcm.Length != _frameSamples) return;

        var queue = _queues.GetOrAdd(from, _ => new ConcurrentQueue<short[]>());
        queue.Enqueue(pcm);

        // Drop the oldest rather than the newest. In a conversation the newest frame is the one
        // somebody is saying now, and the old one is already too late to be worth hearing.
        while (queue.Count > QueuedFramesPerSpeaker) queue.TryDequeue(out _);
    }

    /// <summary>
    /// One frame of everybody, summed — or null when nobody has anything to say.
    ///
    /// <para>
    /// Null rather than a frame of silence on purpose: handing the speaker silence keeps the audio
    /// path busy for no reason, and on a phone that costs battery for the whole length of a quiet
    /// call.
    /// </para>
    /// </summary>
    public short[]? Mix()
    {
        if (_disposed) return null;

        short[]? mixed = null;

        foreach (var (_, queue) in _queues)
        {
            if (!queue.TryDequeue(out var frame)) continue;

            if (mixed is null)
            {
                // The first speaker's frame is used as-is. With one person talking — which is most of
                // any real conversation — this makes the mixer free.
                mixed = frame;
                continue;
            }

            for (var i = 0; i < mixed.Length; i++)
            {
                // Summed in 32 bits and then clamped. Adding two shorts and storing the result back
                // into a short wraps on overflow, and a wrap is not a loud sample — it is the opposite
                // sign, which sounds like a violent crack rather than like two people talking at once.
                var sum = mixed[i] + frame[i];
                mixed[i] = sum > short.MaxValue ? short.MaxValue
                    : sum < short.MinValue ? short.MinValue
                    : (short)sum;
            }
        }

        return mixed;
    }

    /// <summary>
    /// Someone left, or was never really here. Their queue goes with them so a stale frame cannot be
    /// mixed into a conversation they are no longer part of.
    /// </summary>
    public void Forget(string who)
    {
        if (string.IsNullOrEmpty(who)) return;
        _queues.TryRemove(who, out _);
    }

    /// <summary>Everyone hung up. Nothing queued outlives the call it belonged to.</summary>
    public void Clear() => _queues.Clear();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queues.Clear();
    }
}
