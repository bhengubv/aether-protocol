// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using AndroidApp = Android.App.Application;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Keeps the mesh link alive while Aether is not the app on screen.
/// <para>
/// Android hands Bluetooth to whatever is in the foreground and takes it back from everything else,
/// and Huawei's power management is stricter still. Measured on the P30: with the app in front the
/// link survives indefinitely; leave it and the connection dies a couple of minutes later — the peer
/// really disconnects, so no amount of retrying inside the app can prevent it.
/// </para>
/// <para>
/// A foreground service is the only sanctioned way to keep a radio connection off-screen. It costs a
/// permanent notification, which is honest: the phone is holding a link on your behalf, and you can
/// see it and stop it. This is the same bargain every messenger makes.
/// </para>
/// </summary>
[Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeConnectedDevice)]
public sealed class AetherLinkService : Service
{
    private const string ChannelId = "aether.link";
    private const int NotificationId = 4711;

    /// <summary>Start holding the link. Safe to call when it is already running.</summary>
    public static void Start()
    {
        var ctx = AndroidApp.Context;
        var intent = new Intent(ctx, typeof(AetherLinkService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) ctx.StartForegroundService(intent);
        else ctx.StartService(intent);
    }

    /// <summary>Stop holding the link — the notification goes with it.</summary>
    public static void Stop()
    {
        var ctx = AndroidApp.Context;
        try { ctx.StopService(new Intent(ctx, typeof(AetherLinkService))); } catch { }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        StartForeground(NotificationId, BuildNotification());

        // If Android kills us for memory it should bring us back — a mesh link that quietly stops
        // existing is worse than one that visibly restarts.
        return StartCommandResult.Sticky;
    }

    private Notification BuildNotification()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(ChannelId, "Mesh link", NotificationImportance.Low)
            {
                Description = "Shown while Aether is holding a connection to a phone near you.",
            };
            channel.SetShowBadge(false);
            (GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
        }

        var open = PendingIntent.GetActivity(
            this, 0,
            PackageManager?.GetLaunchIntentForPackage(PackageName!),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Aether is connected")
            .SetContentText("Holding a link to a phone near you.")
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDataBluetooth)
            .SetPriority((int)NotificationPriority.Low)
            .SetOngoing(true)
            .SetContentIntent(open)
            .Build()!;
    }
}
#endif
