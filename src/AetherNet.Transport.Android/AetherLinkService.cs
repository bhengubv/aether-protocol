// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using AndroidApp = Android.App.Application;

namespace AetherNet.Transport.Android;

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
/// <remarks>
/// It is also the service that lets Aether Aware know where this phone is. From Android 12, a foreground service
/// <i>started while the app is in the background</i> — which this one always is, the node having no screen — is
/// refused location, camera and microphone outright, whatever permissions are granted:
/// <c>Foreground service started from background can not have location/camera/microphone access</c> in the log.
/// So a person could allow Location and still have Quiet help send no position and "moving with you" notice
/// nothing. Declaring the location kind and holding ACCESS_BACKGROUND_LOCATION is what lifts that.
/// </remarks>
[Service(
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeConnectedDevice
        | global::Android.Content.PM.ForegroundService.TypeLocation)]
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
        StartForeground(NotificationId, BuildNotification(), Kinds());

        // If Android kills us for memory it should bring us back — a mesh link that quietly stops
        // existing is worse than one that visibly restarts.
        return StartCommandResult.Sticky;
    }

    /// <summary>
    /// What this service is for, as the phone must be told from Android 14. Holding a link, always; and knowing
    /// where the phone is, only when the person has actually allowed it — because saying "location" without the
    /// permission throws, and the mesh link would then never start at all. The kinds are chosen each time it
    /// starts, so allowing location later simply works from the next start.
    /// </summary>
    private global::Android.Content.PM.ForegroundService Kinds()
    {
        var kinds = global::Android.Content.PM.ForegroundService.TypeConnectedDevice;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q && Located)
        {
            kinds |= global::Android.Content.PM.ForegroundService.TypeLocation;
        }

        return kinds;
    }

    /// <summary>
    /// Whether the person has allowed this phone's location at all. That is exactly what saying "location" here
    /// needs — from Android 14 the phone refuses the kind outright without it, and throws. Whether a position then
    /// actually arrives is a separate matter: from Android 12 a service started from the background is handed none
    /// unless the app also holds ACCESS_BACKGROUND_LOCATION. Saying the kind costs nothing when it does not, and is
    /// required for it to work when it does, so the test is the plain permission and not the other one.
    /// </summary>
    private bool Located
    {
        get
        {
            var granted = global::Android.Content.PM.Permission.Granted;
            return CheckSelfPermission(global::Android.Manifest.Permission.AccessFineLocation) == granted
                || CheckSelfPermission(global::Android.Manifest.Permission.AccessCoarseLocation) == granted;
        }
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

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Aether is connected")
            .SetContentText("Holding a link to a phone near you.")
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDataBluetooth)
            .SetPriority((int)NotificationPriority.Low)
            .SetOngoing(true);

        // A tap opens the host's screen — when it has one. AetherNetService has no screen (a screen is attack
        // surface), so there is no launch activity: the notification is then only Android's required notice
        // that a background service is holding the radio, and opens nothing. Building a PendingIntent over the
        // missing launch intent threw on the main thread and took the whole service down.
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        if (launch is not null)
        {
            builder.SetContentIntent(PendingIntent.GetActivity(
                this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
        }

        return builder.Build()!;
    }
}
#endif
