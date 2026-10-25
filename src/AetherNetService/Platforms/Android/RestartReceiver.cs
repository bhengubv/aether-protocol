// SPDX-License-Identifier: MIT
using Android.App;
using Android.Content;
using AetherNet.Transport.Android;
using Log = Android.Util.Log;

namespace AetherNetService;

/// <summary>
/// Brings the node back after the phone restarts and after AetherNetService is updated, without waiting for anybody to
/// open Aether. Being sent either broadcast is what starts the process, and the node starts with the process
/// (<see cref="MainApplication"/>). All this adds is the mesh link's foreground service, started while the phone still
/// lets a service with no screen start one: it does for these two broadcasts, and not later.
/// </summary>
/// <remarks>
/// <para>
/// Before it, nothing restarted the node. After an update or a restart the phone was off the mesh, Aware heard nothing
/// and Quiet help could not be raised, until somebody opened Aether (the P30, 2026-10-24).
/// </para>
/// <para>
/// Not exported: only the phone sends these, and no other app may. Not for the boot before the first unlock either:
/// the identity is in the app's own files, which the phone opens only once the person has unlocked it.
/// </para>
/// </remarks>
[BroadcastReceiver(Name = "com.bhengubv.aethernetservice.RestartReceiver", Exported = false)]
[IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced })]
public sealed class RestartReceiver : BroadcastReceiver
{
    private const string Tag = "AetherNetService";

    public override void OnReceive(Context? context, Intent? intent)
    {
        Log.Info(Tag, $"started by the phone ({intent?.Action}): the node is up without Aether opened");
        try
        {
            AetherLinkService.Start();
        }
        catch (Exception ex)
        {
            // Refused, the node still runs for as long as the phone keeps the process, and opening Aether brings the link
            // up as it always has.
            Log.Warn(Tag, $"the mesh link did not start after {intent?.Action}: {ex.Message}");
        }
    }
}
