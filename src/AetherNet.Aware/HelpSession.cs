// SPDX-License-Identifier: MIT

namespace AetherNet.Aware;

/// <summary>
/// The sending side of Quiet help, on the phone of the person who asked for help or started a walk. Only that person
/// starts one, on their own phone; nothing a phone receives can start it. Its messages carry the latest position it
/// was given, and <see cref="MarkSafe"/> ends it.
/// </summary>
public sealed class HelpSession
{
    /// <summary>After the person says they are safe, the "safe" message keeps going this long so guardians nearby hear it.</summary>
    public const long SafeForMs = 60_000L;

    private readonly HelpKey _key;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private HelpKind _kind;
    private double? _lat;
    private double? _lon;
    private double? _accuracyM;
    private long? _fixAt;
    private int? _battery;
    private long? _safeSince;
    private long _lastStep = -1;
    private byte[]? _last;

    public HelpSession(HelpKey key, HelpKind kind, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (kind == HelpKind.Safe)
        {
            throw new ArgumentException("A session starts as help or a walk.", nameof(kind));
        }
        _key = key;
        _kind = kind;
        _time = timeProvider ?? TimeProvider.System;
        StartedAt = Now();
    }

    /// <summary>When the person started it (Unix ms).</summary>
    public long StartedAt { get; }

    public HelpKind Kind
    {
        get
        {
            lock (_gate)
            {
                return _kind;
            }
        }
    }

    /// <summary>The person said they are safe, and the "safe" message has had its minute.</summary>
    public bool IsOver
    {
        get
        {
            lock (_gate)
            {
                return _safeSince is { } since && Now() - since >= SafeForMs;
            }
        }
    }

    /// <summary>
    /// A position fix from the person's phone. A fix older than the one held, or not a real position, is ignored.
    /// </summary>
    public void UpdatePosition(double lat, double lon, long fixAt, double? accuracyM)
    {
        if (!PayloadLocation.ValidCoord(lat, lon))
        {
            return;
        }
        lock (_gate)
        {
            if (_fixAt is { } held && fixAt < held)
            {
                return;
            }
            _lat = lat;
            _lon = lon;
            _fixAt = fixAt;
            _accuracyM = accuracyM;
        }
    }

    public void UpdateBattery(int percent)
    {
        lock (_gate)
        {
            _battery = Math.Clamp(percent, 0, 100);
        }
    }

    /// <summary>The person is safe (or has arrived). From now on the message says so, for <see cref="SafeForMs"/>.</summary>
    public void MarkSafe()
    {
        lock (_gate)
        {
            _kind = HelpKind.Safe;
            _safeSince ??= Now();
            // The message held for this 20 ms step still says Help; "safe" must not wait for the next one.
            _last = null;
            _lastStep = -1;
        }
    }

    /// <summary>
    /// The message to send now, as the Bluetooth advert's payload and over the mesh to each guardian. Within one
    /// 20 ms step it is the same bytes, so two different messages are never sealed with the same step.
    /// </summary>
    public byte[] NextMessage()
    {
        lock (_gate)
        {
            var now = Now();
            var step = now / HelpCodec.StepMs;
            if (step == _lastStep && _last is not null)
            {
                return (byte[])_last.Clone();
            }
            var hasFix = _fixAt is not null;
            var message = new HelpMessage
            {
                Kind = _kind,
                Lat = _lat,
                Lon = _lon,
                AccuracyM = _accuracyM is { } acc ? (int)Math.Ceiling(acc) : null,
                BatteryPercent = _battery,
                FixAgeSeconds = hasFix ? (int)Math.Clamp((now - _fixAt!.Value) / 1000, 0, int.MaxValue) : null,
            };
            _last = HelpCodec.Encode(_key, message, now);
            _lastStep = step;
            return (byte[])_last.Clone();
        }
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();
}
