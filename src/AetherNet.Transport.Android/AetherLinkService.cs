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
        var notification = BuildNotification();
        try
        {
            StartForeground(NotificationId, notification, Kinds());
        }
        catch (Java.Lang.SecurityException ex)
        {
            // Holding the link matters more than saying why we hold it. If the phone refuses the location kind for
            // any reason of its own, keep the link and lose only the position — never die. Without this the service
            // crash-looped on a Pixel 7a: Android restarted it, it threw again, and the backoff reached three hours.
            global::Android.Util.Log.Warn(
                "AetherNetService",
                $"the phone would not let this be a location service, so it is not one: {ex.Message}");
            StartForeground(NotificationId, notification, global::Android.Content.PM.ForegroundService.TypeConnectedDevice);
        }

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
    /// Whether this service may call itself a location one. Allowing location is the first half; the second is that
    /// from Android 14 the phone also demands the app be "in the eligible state" for a permission that is only meant
    /// for an app somebody is looking at — and this service has no screen, so it never is. Allowing location only
    /// "while using the app" therefore does not make it eligible, and claiming the kind anyway throws
    /// <c>SecurityException: Starting FGS with type location</c> and takes the service down with it. "All the time"
    /// is what makes it eligible. Before Android 14 nothing is enforced, and on Android 10 and 11 the kind is itself
    /// what lets a service off-screen have a position, so there it is claimed on the plain permission alone.
    /// </summary>
    private bool Located
    {
        get
        {
            var granted = global::Android.Content.PM.Permission.Granted;
            if (CheckSelfPermission(global::Android.Manifest.Permission.AccessFineLocation) != granted
                && CheckSelfPermission(global::Android.Manifest.Permission.AccessCoarseLocation) != granted)
            {
                return false;
            }

            return Build.VERSION.SdkInt < BuildVersionCodes.UpsideDownCake
                || CheckSelfPermission(global::Android.Manifest.Permission.AccessBackgroundLocation) == granted;
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
