// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using AndroidApp = Android.App.Application;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Makes an incoming call reach someone who is not looking at Aether.
///
/// <para>
/// A call used to arrive as a banner inside the app, which means it only ever reached a person
/// already staring at the screen — the one person who did not need telling. Everyone else simply
/// missed it. That is the second of the two things that would actually lose against WhatsApp.
/// </para>
///
/// <para>
/// A full-screen-intent notification is how Android lets a call take over the screen, including over
/// the lock screen, with Answer and Decline. The system decides whether to show it full-screen or as
/// a heads-up depending on what the person is doing — which is the right behaviour: it interrupts a
/// locked phone, and merely appears over a game.
/// </para>
///
/// <para>
/// The ringtone itself is <see cref="AndroidAudioIo.StartRinging"/> — the phone's own, so it obeys
/// the volume and silent switch already chosen. This adds the vibration that should go with it.
/// </para>
/// </summary>
public static class AetherIncomingCall
{
    private const string ChannelId = "aether.incoming";
    private const int NotificationId = 4713;

    private static Vibrator? _vibrator;

    /// <summary>Ring the phone for a call from <paramref name="callerTag"/>.</summary>
    public static void Show(string callerTag)
    {
        var ctx = AndroidApp.Context;

        try
        {
            EnsureChannel(ctx);

            // Tapping anywhere takes them to the app, where the in-app banner has Answer and Decline.
            // Deliberately not action buttons that answer directly: answering needs the microphone
            // permission prompt, which cannot be raised from a notification.
            var open = PendingIntent.GetActivity(
                ctx, 0,
                ctx.PackageManager?.GetLaunchIntentForPackage(ctx.PackageName!)
                    ?.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            var notification = new NotificationCompat.Builder(ctx, ChannelId)
                .SetContentTitle($"{callerTag} is calling")
                .SetContentText("Tap to answer on Aether.")
                .SetSmallIcon(global::Android.Resource.Drawable.StatSysPhoneCall)
                .SetPriority(NotificationCompat.PriorityMax)
                .SetCategory(NotificationCompat.CategoryCall)
                .SetOngoing(true)
                .SetAutoCancel(false)
                .SetContentIntent(open)
                // This is the part that takes over the screen, including over the lock screen. Android
                // shows it full-screen on a locked or idle phone and as a heads-up otherwise, which is
                // exactly the right call to leave to the system.
                .SetFullScreenIntent(open, highPriority: true)
                .Build();

            NotificationManagerCompat.From(ctx).Notify(NotificationId, notification);
        }
        catch (Exception)
        {
            // A phone that will not show the notification still rings and still shows the in-app
            // banner. Never let this take the call down.
        }

        Vibrate();
    }

    /// <summary>Answered, declined, or they gave up — stop ringing the screen.</summary>
    public static void Dismiss()
    {
        try { NotificationManagerCompat.From(AndroidApp.Context).Cancel(NotificationId); } catch { }
        try { _vibrator?.Cancel(); } catch { }
        _vibrator = null;
    }

    private static void EnsureChannel(Context ctx)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

        // Importance HIGH is the minimum Android will honour a full-screen intent for.
        var channel = new NotificationChannel(ChannelId, "Incoming calls", NotificationImportance.High)
        {
            Description = "Rings when someone calls you on Aether.",
        };
        channel.SetShowBadge(true);
        channel.LockscreenVisibility = NotificationVisibility.Public;

        // The ringtone is played by the audio path so it can be stopped precisely on answer; silencing
        // the channel's own sound keeps the phone from ringing twice over itself.
        channel.SetSound(null, null);
        channel.EnableVibration(false);

        (ctx.GetSystemService(Context.NotificationService) as NotificationManager)
            ?.CreateNotificationChannel(channel);
    }

    /// <summary>
    /// Buzz in the pattern of a ringing phone until the call is dealt with.
    /// <para>Respects the phone's own ringer mode — a silent phone stays silent.</para>
    /// </summary>
    private static void Vibrate()
    {
        try
        {
            var ctx = AndroidApp.Context;
            if (ctx.GetSystemService(Context.AudioService) is global::Android.Media.AudioManager audio
                && audio.RingerMode == global::Android.Media.RingerMode.Silent)
                return;

            _vibrator = Build.VERSION.SdkInt >= BuildVersionCodes.S
                ? (ctx.GetSystemService(Context.VibratorManagerService) as VibratorManager)?.DefaultVibrator
                : ctx.GetSystemService(Context.VibratorService) as Vibrator;

            if (_vibrator is null || !_vibrator.HasVibrator) return;

            long[] pattern = [0, 800, 1000];         // buzz, pause, repeat — a phone ringing
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                _vibrator.Vibrate(VibrationEffect.CreateWaveform(pattern, repeat: 0));
            else
#pragma warning disable CA1422 // the pre-O call is the only one that exists there
                _vibrator.Vibrate(pattern, 0);
#pragma warning restore CA1422
        }
        catch (Exception)
        {
            // A phone that will not buzz still rings and still shows the call.
        }
    }
}
#endif
