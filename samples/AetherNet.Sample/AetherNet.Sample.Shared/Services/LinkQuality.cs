// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// What a link is actually doing, measured from traffic that really crossed it.
///
/// <para>
/// Every bandwidth number in this app that was believed rather than measured has been wrong, and each
/// one cost a day. BLE advertised 2 Mbps and delivered 11 kbps in one direction. A voice note was
/// described as 10 KB from the bitrate the encoder was <em>asked</em> for and measured 91 KB. Wi-Fi
/// Direct still reports a flat 250 Mbps that nothing has ever checked, and the video gate happily
/// believes it — which is how 800 kbps of video ended up being pushed into a link that was
/// time-slicing against the phone's own access point.
/// </para>
///
/// <para>
/// So this measures two different things, and keeps them apart:
/// </para>
/// <list type="bullet">
///   <item><b>Throughput</b> — bytes that actually crossed, per second. This is a <em>floor</em>, never
///   a capacity: a link carrying 20 kbps because that is all anyone offered it is not a 20 kbps link.
///   Treating it as capacity is the same error in a new coat.</item>
///   <item><b>Strain</b> — how hard the link is having to work to carry what it is being given, from
///   how long each send actually took and how many were refused. This is the honest signal for "back
///   off", because it rises before anything is lost, and it does not need to know the capacity.</item>
/// </list>
/// </summary>
public sealed class LinkQuality
{
    /// <summary>
    /// How far back the window reaches. Long enough that one slow frame does not look like collapse,
    /// short enough that a link going bad is noticed inside a sentence rather than after it.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Below this many samples the window is not describing a link, it is describing a coincidence.
    /// </summary>
    private const int EnoughSamples = 8;

    private readonly record struct Sample(DateTimeOffset At, int Bytes, double Milliseconds, bool Sent);

    private readonly Queue<Sample> _samples = new();
    private readonly object _gate = new();

    /// <summary>
    /// Record one send: how big it was, how long the radio took to accept it, and whether it went.
    /// </summary>
    public void Record(int bytes, TimeSpan took, bool sent, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;

        lock (_gate)
        {
            _samples.Enqueue(new Sample(at, Math.Max(bytes, 0), Math.Max(took.TotalMilliseconds, 0), sent));
            Trim(at);
        }
    }

    private void Trim(DateTimeOffset now)
    {
        var oldest = now - Window;
        while (_samples.Count > 0 && _samples.Peek().At < oldest) _samples.Dequeue();
    }

    /// <summary>Whether enough has crossed recently to say anything at all.</summary>
    public bool HasEnough(DateTimeOffset? now = null)
    {
        lock (_gate) { Trim(now ?? DateTimeOffset.UtcNow); return _samples.Count >= EnoughSamples; }
    }

    /// <summary>
    /// Bits per second that genuinely crossed in the window — a floor on what the link can do, and
    /// never a claim about its capacity. Zero when there is not enough to say.
    /// </summary>
    public long ThroughputBps(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;

        lock (_gate)
        {
            Trim(at);
            if (_samples.Count < EnoughSamples) return 0;

            long bytes = 0;
            foreach (var s in _samples) if (s.Sent) bytes += s.Bytes;

            var span = (at - _samples.Peek().At).TotalSeconds;
            if (span <= 0) return 0;

            return (long)(bytes * 8 / span);
        }
    }

    /// <summary>
    /// How hard the link is working, from 0 (comfortable) to 1 (failing).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from refusals and from how long sends are taking, because those rise <em>before</em>
    /// anything is lost — a link at its limit gets slow first and drops second. Waiting for loss
    /// means adapting after the call has already broken up.
    /// </para>
    /// <para>
    /// Deliberately not derived from a capacity figure. There is no trustworthy capacity figure, and
    /// inventing one is the mistake this whole class exists to stop making.
    /// </para>
    /// </remarks>
    public double Strain(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;

        lock (_gate)
        {
            Trim(at);
            if (_samples.Count < EnoughSamples) return 0;

            var refused = 0;
            double slowest = 0, total = 0;
            foreach (var s in _samples)
            {
                if (!s.Sent) refused++;
                total += s.Milliseconds;
                if (s.Milliseconds > slowest) slowest = s.Milliseconds;
            }

            // A refusal is the loudest thing a link can say, so it counts for much more than a slow
            // send. All of them refused is a link that is gone, whatever the timings look like.
            var refusalPart = (double)refused / _samples.Count;

            // A send that takes longer than a frame interval is a send the next frame is queuing
            // behind. Thirty-three milliseconds is one frame at 30fps; past that the queue only grows.
            var average = total / _samples.Count;
            var latencyPart = Math.Clamp((average - 33) / 100, 0, 1);

            return Math.Clamp(Math.Max(refusalPart, latencyPart * 0.9), 0, 1);
        }
    }

    /// <summary>
    /// A meter that is never fed, for radios that carry nothing. Reports no throughput and no strain,
    /// which is exactly true of a radio nothing has crossed.
    /// </summary>
    public static LinkQuality Silent { get; } = new();

    /// <summary>Forget everything — a new link is not the old one having a bad day.</summary>
    public void Reset()
    {
        lock (_gate) _samples.Clear();
    }
}
