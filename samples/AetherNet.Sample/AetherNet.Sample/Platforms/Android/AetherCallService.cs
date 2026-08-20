// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using AndroidApp = Android.App.Application;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Keeps a call alive while Aether is not the app on screen.
///
/// <para>
/// <see cref="AetherLinkService"/> already holds the radio link off-screen, but a call needs more
/// than a link: it needs the microphone. From Android 14 a service may only record while it declares
/// <c>ForegroundServiceType.Microphone</c>, and without one the moment the person opens anything else
/// the recording stops and the call goes silent — or the process is killed outright. A call that only
/// survives while you stare at it is not a call.
/// </para>
///
/// <para>
/// Separate from the link service rather than folded into it, because the two have different
/// lifetimes and different service types, and Android 14 will not let a running service change its
/// type. The link is held for as long as a peer is near; the microphone only for as long as someone
/// is actually talking.
/// </para>
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMicrophone
                          | global::Android.Content.PM.ForegroundService.TypeConnectedDevice)]
public sealed class AetherCallService : Service
{
    private const string ChannelId = "aether.call";
    private const int NotificationId = 4712;

    /// <summary>Who the call is with, so the notification can say. Set before <see cref="Start"/>.</summary>
    public static string? PeerTag { get; private set; }

    /// <summary>Hold the call. Safe to call when it is already running.</summary>
    public static void Start(string? peerTag)
    {
        PeerTag = peerTag;
        var ctx = AndroidApp.Context;
        var intent = new Intent(ctx, typeof(AetherCallService));
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) ctx.StartForegroundService(intent);
            else ctx.StartService(intent);
        }
        catch (Exception)
        {
            // A call that cannot claim a foreground service still rings and still connects — it just
            // will not survive leaving the app. Never let this take the call down.
        }
    }

    /// <summary>The call is over — release the microphone claim and the notification with it.</summary>
    public static void Stop()
    {
        PeerTag = null;
        var ctx = AndroidApp.Context;
        try { ctx.StopService(new Intent(ctx, typeof(AetherCallService))); } catch { }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        StartForeground(NotificationId, BuildNotification());

        // Deliberately NOT sticky. If Android kills this, the call is already gone — the far end has
        // no idea, the audio is dead, and restarting an empty service would leave a notification
        // claiming a call that is not happening.
        return StartCommandResult.NotSticky;
    }

    private Notification BuildNotification()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(ChannelId, "Calls", NotificationImportance.Low)
            {
                Description = "Shown while a call is in progress.",
            };
            channel.SetShowBadge(false);
            (GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
        }

        var open = PendingIntent.GetActivity(
            this, 0,
            PackageManager?.GetLaunchIntentForPackage(PackageName!),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle(PeerTag is { Length: > 0 } p ? $"On a call with {p}" : "On a call")
            .SetContentText("Tap to return to the call.")
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysPhoneCall)
            .SetPriority((int)NotificationPriority.Low)
            .SetOngoing(true)
            .SetCategory(NotificationCompat.CategoryCall)
            .SetContentIntent(open)
            .Build();
    }
}
#endif
