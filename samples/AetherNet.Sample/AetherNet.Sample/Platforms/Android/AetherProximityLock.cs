// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Content;
using Android.OS;
using AndroidApp = Android.App.Application;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Blanks the screen while the phone is held to an ear, and lights it again when it comes away.
///
/// <para>
/// Without this, a call is answered and then pressed against a cheek — which is a touchscreen. Mute
/// gets toggled, the speaker gets switched, the call gets hung up, and none of it was meant. Every
/// phone solves this the same way and it is not optional once calls are usable.
/// </para>
///
/// <para>
/// Uses <c>PROXIMITY_SCREEN_OFF_WAKE_LOCK</c> — the platform's own answer, so the screen is off in the
/// way the system means it (touch ignored, display dark, call unaffected) rather than anything this
/// app has to reimplement. Present since API 21 but only honoured on devices with the sensor; where
/// it is absent, acquiring simply does nothing and the call carries on.
/// </para>
/// </summary>
public static class AetherProximityLock
{
    private static PowerManager.WakeLock? _lock;
    private static readonly object Gate = new();

    /// <summary>Start watching the proximity sensor. Safe to call when already watching.</summary>
    public static void Acquire()
    {
        lock (Gate)
        {
            if (_lock is { IsHeld: true }) return;

            try
            {
                if (AndroidApp.Context.GetSystemService(Context.PowerService) is not PowerManager power)
                    return;

                // Ask the platform first rather than assuming. A phone with no proximity sensor — or a
                // ROM that does not honour the lock — should carry on with the screen on rather than
                // throw in the middle of a call.
                if (!power.IsWakeLockLevelSupported((int)WakeLockFlags.ProximityScreenOff)) return;

                _lock = power.NewWakeLock(WakeLockFlags.ProximityScreenOff, "aether:call");
                _lock?.Acquire();
            }
            catch (Exception)
            {
                // A call that cannot blank the screen is still a call.
                _lock = null;
            }
        }
    }

    /// <summary>Stop watching — the call is over and the screen belongs to the person again.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            try { if (_lock is { IsHeld: true }) _lock.Release(); } catch (Exception) { }
            _lock = null;
        }
    }
}
#endif
