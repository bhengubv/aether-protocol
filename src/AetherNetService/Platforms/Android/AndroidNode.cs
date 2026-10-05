// SPDX-License-Identifier: MIT
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AetherNet.Identity;
using AetherNet.Mesh;
using AetherNet.Transport.Android;
using AetherNetNodeService.Android;
using AetherNetNodeService.Help;
using AetherNetNodeService.Host;

namespace AetherNetService;

/// <summary>
/// AetherNetService on Android. As the process starts it mints (or loads) the device's one identity, brings up the real
/// radio mesh and the reliable messaging core over it, and wires the exported <see cref="AetherNodeAndroidService"/>
/// so a consumer app that binds gets identity <b>and</b> send/inbox/presence backed by the node's own radios.
///
/// <para>
/// The mesh lives here, in the node — not in any consumer app. This is the whole point of the node service:
/// one device, one node, one radio stack, and every app on the phone shares it by binding rather than each
/// standing up its own.
/// </para>
///
/// <para>
/// It has no UI — a screen is attack surface. It is a network cable: no gate of its own. Security is
/// upstream, at the phone's lock (biometrics, pattern, PIN); no access to the phone, no access to the
/// service. So every caller is admitted (<see cref="OpenGrantStore"/>).
/// </para>
///
/// <para>
/// What is here is what Android brings to the node — where the key is kept, its radios, its bound service, its
/// foreground service. The node itself is the same on every system (<see cref="NodeCore"/>).
/// </para>
/// </summary>
internal static class AndroidNode
{
    /// <summary>What Aether Aware is called where the radios are switched, so a person can switch it off like one.</summary>
    public const string AwareRadioName = "Aether Aware";

    /// <summary>Everything the node needs, over this phone's radios. <paramref name="dir"/> is where the identity lives.</summary>
    public static void AddServices(IServiceCollection services, string dir)
    {
        // The key, in the app's own files directory, which Android sandboxes to this app.
        services.AddSingleton<INodeIdentityStore>(new FileNodeIdentityStore(dir));

        // The real radios.
        services.AddSingleton<IRadioSetup, AndroidRadioSetup>();
        services.AddSingleton<IRadioInventory, AndroidRadioInventory>();
        services.AddSingleton<IRadioMesh, AndroidRadioMesh>();

        // Aether Aware: one Bluetooth scan that takes every advert in the air, the Wi-Fi results, and this phone's
        // own position — and the same radio puts Quiet help's message back on the air. Registered before the node so
        // Quiet help finds it; switched like a radio, and on until the person switches it off.
        services.AddSingleton(sp => new AndroidAwareRadio(
            sp.GetService<ILoggerFactory>()?.CreateLogger("AetherAware")));
        services.AddSingleton<IHelpRadio>(sp => sp.GetRequiredService<AndroidAwareRadio>());
        services.AddSingleton<AetherNetNodeService.Host.INodeAwareSource>(sp => sp.GetRequiredService<AndroidAwareRadio>());

        // The ways a person asks for help without opening anything: the power button, and shaking the phone.
        services.AddSingleton(sp => new AndroidHelpTriggers(
            sp.GetRequiredService<QuietHelp>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("QuietHelpTriggers")));

        NodeCore.Add(services, dir, ServicePermissions.Now, AndroidRadioSetup.Internet);

        // The fast radio: the Circle's Wi-Fi Direct group, worked out from the same contacts. It ran only in the app
        // once, so when the radios moved in here nothing formed the group at all. RadioMeeting already meets every
        // contact and points the other radios, so this is given no mesh to drive — only the question of who is here.
        services.AddSingleton(sp =>
        {
            var radio = sp.GetRequiredService<IRadioMesh>();
            return new FastRadioService(
                sp.GetRequiredService<RadioMeeting>(),
                sp.GetRequiredService<IIdentityService>(),
                ((AndroidRadioMesh)radio).WifiDirect,
                sp.GetService<ILogger<FastRadioService>>(),
                mesh: null,
                isReachable: radio.IsReachable);
        });
    }

    /// <summary>Publish the node to the apps that bind, and bring the radios up — once MAUI has built the app.</summary>
    public static void Start(IServiceProvider provider)
    {
        var node = NodeCore.Start(provider);
        AetherNodeAndroidService.Configure(() => node, new OpenGrantStore());

        // Bring the radios up off the main thread — from here the node is hosting the mesh.
        var radio = provider.GetRequiredService<IRadioMesh>();
        var nearby = provider.GetRequiredService<INodeNearby>();
        _ = Task.Run(() =>
        {
            // AetherNet switched off: only the internet leg, as the person asked, and no Wi-Fi Direct group. The
            // foreground service still holds the process — this is still the phone's way to everyone over data.
            if (!nearby.On)
            {
                try
                {
                    AetherLinkService.Start();
                    radio.SelectRadio(AndroidRadioSetup.Internet);
                    global::Android.Util.Log.Info("AetherNetService", "AetherNet is switched off — internet only, no nearby radio");
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("AetherNetService", $"internet bring-up failed: {ex}");
                }
                return;
            }

            try
            {
                radio.Link();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("AetherNetService", $"radio bring-up failed: {ex}");
            }

            // Aether Aware listens beside the mesh: the same Bluetooth, a scan that takes everything rather than
            // only AetherNet, so a finder tag moving with the person can be named. It also carries Quiet help, and
            // feeds it this phone's position and battery.
            try
            {
                if (provider.GetRequiredService<IRadioSwitches>().IsOn(AwareRadioName))
                {
                    var aware = provider.GetRequiredService<AndroidAwareRadio>();
                    var help = provider.GetRequiredService<QuietHelp>();
                    aware.Changed += () =>
                    {
                        if (aware.Here is { } here) help.UpdatePosition(here.Lat, here.Lon, here.At);
                        if (aware.Battery is { } battery) help.UpdateBattery(battery);
                    };
                    aware.Start();
                }
                else
                {
                    global::Android.Util.Log.Info("AetherNetService", "Aether Aware is switched off — nothing is listened to");
                }

                // The ways a person asks for help without opening anything. These need no radio of their own —
                // only the phone's own button and sensor — so they run whether Aware is switched on or not.
                provider.GetRequiredService<AndroidHelpTriggers>().Start();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("AetherNetService", $"Aether Aware did not start: {ex}");
            }

            // And keep the Wi-Fi Direct group where it should be for as long as the service runs. Idle until the
            // radio is allowed and there is somebody to form it with; it checks again every few seconds. Not at all
            // when the person has switched Wi-Fi Direct off.
            try
            {
                if (provider.GetRequiredService<IRadioSwitches>().IsOn("Wi-Fi Direct"))
                    provider.GetRequiredService<FastRadioService>().KeepUp();
                else
                    global::Android.Util.Log.Info("AetherNetService", "Wi-Fi Direct is switched off — no group");
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("AetherNetService", $"fast radio did not start: {ex}");
            }
        });
    }
}
