// SPDX-License-Identifier: MIT
#if ANDROID

using Android.Content;
using Android.Hardware;
using Android.Runtime;
using AetherNetNodeService;
using AetherNetNodeService.Help;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;
using Aware = AetherNet.Aware;

namespace AetherNetService;

/// <summary>
/// The ways a person asks for help without opening anything: the power button pressed over and over, and shaking the
/// phone. Both are their own action on their own phone, and nothing the phone receives can set either off.
/// </summary>
/// <remarks>
/// <para>
/// Android gives no app the power button itself — it belongs to the system. What an app is told is the screen going
/// on and off, and a press of that button is what turns the screen on and off, so counting those transitions is
/// counting presses. It costs nothing: the broadcast arrives whether anybody listens or not.
/// </para>
/// <para>
/// The shake is the accelerometer, at the slowest rate the phone offers that still catches one. It is only listened
/// to while the person has the shake trigger on, because unlike the screen broadcast it does cost something.
/// </para>
/// <para>
/// Both lead to the same place: <see cref="QuietHelp.Start"/>, with the hold the person chose. The hold is what
/// stops a pocket sending a cry for help — a few seconds in which an accident can be taken back.
/// </para>
/// </remarks>
internal sealed class AndroidHelpTriggers : IDisposable
{
    private readonly QuietHelp _help;
    private readonly ILogger? _log;
    private readonly object _gate = new();

    private Screen? _screen;
    private Shakes? _shakes;
    private SensorManager? _sensors;
    private Aware.PowerButtonWatch? _power;
    private Aware.ShakeWatch? _shake;
    private HelpTriggers _chosen = new();
    private CancellationTokenSource? _holding;
    private bool _started;

    public AndroidHelpTriggers(QuietHelp help, ILogger? log = null)
    {
        _help = help;
        _log = log;
    }

    /// <summary>Whether a trigger has fired and the message has not gone yet — the seconds it can be taken back in.</summary>
    public bool Holding => _holding is not null;

    /// <summary>Start watching, with whatever the person has chosen. Safe to call again.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _help.Changed += Rebuild;
        Rebuild();
        _log?.LogInformation("Quiet help is watching for the ways you can ask for it without opening anything");
    }

    /// <summary>Take the person's choices as they now stand, and watch for exactly those.</summary>
    private void Rebuild()
    {
        HelpTriggers chosen;
        try
        {
            chosen = _help.Current.Mine.Triggers;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Quiet help could not read which triggers you chose, so it keeps the ones it had");
            return;
        }

        lock (_gate)
        {
            if (chosen == _chosen && (_screen is not null || _shakes is not null))
            {
                return;
            }

            _chosen = chosen;
            WatchThePowerButton(chosen);
            WatchForShaking(chosen);
        }
    }

    private void WatchThePowerButton(HelpTriggers chosen)
    {
        var want = chosen.On(HelpTrigger.PowerButton);
        if (want && _screen is null)
        {
            try
            {
                _power = new Aware.PowerButtonWatch(Math.Max(2, chosen.PowerPresses), Math.Max(1, chosen.PowerWindowMs));
                _screen = new Screen(this);
                var filter = new IntentFilter();
                filter.AddAction(Intent.ActionScreenOn);
                filter.AddAction(Intent.ActionScreenOff);

                // Dynamic only: the system will not deliver these to a receiver declared in a manifest.
                AndroidApp.Context.RegisterReceiver(_screen, filter);
            }
            catch (Exception ex)
            {
                _screen = null;
                _log?.LogWarning(ex, "Quiet help cannot watch the power button on this phone");
            }
        }
        else if (!want && _screen is not null)
        {
            try
            {
                AndroidApp.Context.UnregisterReceiver(_screen);
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "letting go of the screen broadcast");
            }

            _screen = null;
            _power = null;
        }
        else if (want)
        {
            _power = new Aware.PowerButtonWatch(Math.Max(2, chosen.PowerPresses), Math.Max(1, chosen.PowerWindowMs));
        }
    }

    private void WatchForShaking(HelpTriggers chosen)
    {
        var want = chosen.On(HelpTrigger.Shake);
        if (want && _shakes is null)
        {
            try
            {
                _sensors ??= AndroidApp.Context.GetSystemService(Context.SensorService) as SensorManager;
                var accelerometer = _sensors?.GetDefaultSensor(SensorType.Accelerometer);
                if (accelerometer is null)
                {
                    _log?.LogInformation("This phone has no accelerometer, so shaking it cannot ask for help");
                    return;
                }

                _shake = new Aware.ShakeWatch(
                    chosen.ShakeThreshold > 0 ? chosen.ShakeThreshold : 25.0,
                    Math.Max(2, chosen.ShakeCount),
                    Math.Max(1, chosen.ShakeWindowMs));
                _shakes = new Shakes(this);

                // The slowest rate that still catches a shake: this runs for as long as the service does.
                _sensors!.RegisterListener(_shakes, accelerometer, SensorDelay.Normal);
            }
            catch (Exception ex)
            {
                _shakes = null;
                _log?.LogWarning(ex, "Quiet help cannot watch for shaking on this phone");
            }
        }
        else if (!want && _shakes is not null)
        {
            try
            {
                _sensors?.UnregisterListener(_shakes);
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "letting go of the accelerometer");
            }

            _shakes = null;
            _shake = null;
        }
        else if (want)
        {
            _shake = new Aware.ShakeWatch(
                chosen.ShakeThreshold > 0 ? chosen.ShakeThreshold : 25.0,
                Math.Max(2, chosen.ShakeCount),
                Math.Max(1, chosen.ShakeWindowMs));
        }
    }

    /// <summary>
    /// A trigger fired. The hold the person chose is theirs to be saved by: nothing goes out until it runs out, and
    /// a second firing inside it calls the whole thing off, because that is what somebody does when a pocket asks
    /// for help on their behalf.
    /// </summary>
    private void Asked(HelpTrigger how)
    {
        int hold;
        lock (_gate)
        {
            hold = Math.Max(0, _chosen.HoldSeconds);

            if (_holding is { } holding)
            {
                _holding = null;
                holding.Cancel();
                holding.Dispose();
                _log?.LogInformation("Quiet help was called off before it went out");
                return;
            }

            if (hold > 0)
            {
                _holding = new CancellationTokenSource();
            }
        }

        if (hold <= 0)
        {
            Send(how);
            return;
        }

        var token = _holding!.Token;
        _log?.LogInformation(
            "Quiet help will go out in {Hold} s — {How} again to call it off", hold, how == HelpTrigger.Shake ? "shake" : "press");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(hold), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_gate)
            {
                _holding?.Dispose();
                _holding = null;
            }

            Send(how);
        });
    }

    private void Send(HelpTrigger how)
    {
        try
        {
            var went = _help.Start(HelpKind.Help);
            _log?.LogInformation(
                went ? "Quiet help asked for, by {How}" : "Quiet help could not be sent, asked for by {How}",
                how == HelpTrigger.Shake ? "shaking the phone" : "the power button");
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Quiet help could not be started");
        }
    }

    public void Dispose()
    {
        _help.Changed -= Rebuild;
        lock (_gate)
        {
            _holding?.Cancel();
            _holding?.Dispose();
            _holding = null;
        }

        try
        {
            if (_screen is not null)
            {
                AndroidApp.Context.UnregisterReceiver(_screen);
            }
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "letting go of the screen broadcast");
        }

        try
        {
            if (_shakes is not null)
            {
                _sensors?.UnregisterListener(_shakes);
            }
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "letting go of the accelerometer");
        }

        _screen = null;
        _shakes = null;
        _started = false;
    }

    /// <summary>The screen going on and off, which is what a press of the power button does.</summary>
    private sealed class Screen(AndroidHelpTriggers owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            var action = intent?.Action;
            if (action != Intent.ActionScreenOn && action != Intent.ActionScreenOff)
            {
                return;
            }

            Aware.PowerButtonWatch? watch;
            lock (owner._gate)
            {
                watch = owner._power;
            }

            if (watch?.Press(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) == true)
            {
                owner.Asked(HelpTrigger.PowerButton);
            }
        }
    }

    /// <summary>The accelerometer, for a phone being shaken hard and on purpose.</summary>
    private sealed class Shakes(AndroidHelpTriggers owner) : Java.Lang.Object, ISensorEventListener
    {
        public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy)
        {
        }

        public void OnSensorChanged(SensorEvent? e)
        {
            if (e?.Values is not { Count: >= 3 } values)
            {
                return;
            }

            Aware.ShakeWatch? watch;
            lock (owner._gate)
            {
                watch = owner._shake;
            }

            if (watch?.Reading(values[0], values[1], values[2], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) == true)
            {
                owner.Asked(HelpTrigger.Shake);
            }
        }
    }
}
#endif
