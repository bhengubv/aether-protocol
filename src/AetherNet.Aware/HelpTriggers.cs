// SPDX-License-Identifier: MIT

namespace AetherNet.Aware;

/// <summary>
/// The ways a person can start Quiet help on their own phone. They choose which ones are on; all of them are their
/// own action on their own phone, and none of them can be set off by anything the phone receives.
/// </summary>
[Flags]
public enum HelpTrigger
{
    None = 0,

    /// <summary>The button on Aether's own screen.</summary>
    AppButton = 1,

    /// <summary>A held press on AetherNetService's notification, which is always there.</summary>
    Notification = 2,

    /// <summary>The power button, pressed several times in a row.</summary>
    PowerButton = 4,

    /// <summary>Shaking the phone.</summary>
    Shake = 8,

    /// <summary>
    /// A second PIN that opens the app as usual and asks for help at the same time. The app's own PIN check decides
    /// which PIN was entered (the phone PIN standard already owns that); this flag only says the person turned it on.
    /// </summary>
    DuressPin = 16,
}

/// <summary>What the person chose: which triggers are on, and how sensitive each is.</summary>
public sealed record HelpTriggerSettings
{
    /// <summary>On by default: the screen button, the notification, the power button and a shake.</summary>
    public HelpTrigger Enabled { get; init; } =
        HelpTrigger.AppButton | HelpTrigger.Notification | HelpTrigger.PowerButton | HelpTrigger.Shake;

    public int PowerPresses { get; init; } = 5;

    public int PowerWindowMs { get; init; } = 3_000;

    /// <summary>Total acceleration that counts as a shake, in m/s² (gravity alone is about 9.8).</summary>
    public double ShakeThreshold { get; init; } = 25.0;

    public int ShakeCount { get; init; } = 3;

    public int ShakeWindowMs { get; init; } = 1_500;

    /// <summary>
    /// Seconds the person has to call it off before it goes out. Zero sends at once, which is what a person who is
    /// being hurried wants; a few seconds guards against a pocket. Theirs to choose.
    /// </summary>
    public int HoldSeconds { get; init; }

    public bool On(HelpTrigger trigger) => trigger != HelpTrigger.None && (Enabled & trigger) == trigger;

    public PowerButtonWatch NewPowerButtonWatch() => new(PowerPresses, PowerWindowMs);

    public ShakeWatch NewShakeWatch() => new(ShakeThreshold, ShakeCount, ShakeWindowMs);
}

/// <summary>
/// Counts power-button presses: so many within the window and the person is asking for help. The host feeds it the
/// presses the system reports (on Android, the screen going on and off).
/// </summary>
public sealed class PowerButtonWatch
{
    private readonly Queue<long> _presses = new();
    private readonly int _needed;
    private readonly long _windowMs;

    public PowerButtonWatch(int presses = 5, int windowMs = 3_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(presses, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowMs, 1);
        _needed = presses;
        _windowMs = windowMs;
    }

    /// <summary>One press. True when this one completes the pattern; the count then starts again.</summary>
    public bool Press(long atMs)
    {
        while (_presses.Count > 0 && atMs - _presses.Peek() > _windowMs)
        {
            _presses.Dequeue();
        }
        _presses.Enqueue(atMs);
        if (_presses.Count < _needed)
        {
            return false;
        }
        _presses.Clear();
        return true;
    }

    public void Reset() => _presses.Clear();
}

/// <summary>
/// Counts shakes from the phone's accelerometer: so many hard moves within the window. One shake spans many readings,
/// so readings closer together than <see cref="GapMs"/> count once.
/// </summary>
public sealed class ShakeWatch
{
    /// <summary>How far apart two readings must be to count as two shakes.</summary>
    public const long GapMs = 150L;

    private readonly Queue<long> _shakes = new();
    private readonly double _threshold;
    private readonly int _needed;
    private readonly long _windowMs;
    private long? _last;

    public ShakeWatch(double threshold = 25.0, int count = 3, int windowMs = 1_500)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowMs, 1);
        _threshold = threshold;
        _needed = count;
        _windowMs = windowMs;
    }

    /// <summary>One accelerometer reading (m/s²). True when the shaking completes the pattern.</summary>
    public bool Reading(double x, double y, double z, long atMs)
    {
        if (Math.Sqrt((x * x) + (y * y) + (z * z)) < _threshold || (_last is { } last && atMs - last < GapMs))
        {
            return false;
        }
        _last = atMs;
        while (_shakes.Count > 0 && atMs - _shakes.Peek() > _windowMs)
        {
            _shakes.Dequeue();
        }
        _shakes.Enqueue(atMs);
        if (_shakes.Count < _needed)
        {
            return false;
        }
        _shakes.Clear();
        _last = null;
        return true;
    }

    public void Reset()
    {
        _shakes.Clear();
        _last = null;
    }
}
