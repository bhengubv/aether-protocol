// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Mesh;
using Android.Content;
using Android.Net;
using Microsoft.Extensions.Logging;

namespace AetherNet.Transport.Android;

/// <summary>
/// The real over-the-air mesh on Android: the shared <see cref="RadioMesh"/>, with the phone's own radios — Wi-Fi
/// Direct, Bluetooth, Wi-Fi Aware, the internet relay, the Wi-Fi the phone is on, NFC, LoRa — and the foreground
/// service Android insists on while they are up.
/// </summary>
public sealed class AndroidRadioMesh : RadioMesh
{
    public AndroidRadioMesh(IIdentityService me, ILogger<AndroidRadioMesh> logger,
        CircleDirectory? circle = null,
        ProxyDirectory? proxies = null,
        IRadioSwitches? switches = null)
        : base(me, logger, circle, switches)
    {
        var context = global::Android.App.Application.Context!;

        Register(new AndroidWifiDirectTransportService(context, LocalUhid, logger, RoutingKey, circle));
        // Bluetooth is gone, and so is the NearLink stand-in that was Bluetooth wearing a different
        // name. It measured 11 kbps in one direction — it cannot carry a call, a note or an APK — and
        // while it was registered it did real harm: the mesh picks whichever radio reports a link, so
        // BLE kept taking traffic that Wi-Fi Direct was sitting there able to carry properly.
        //
        // Two radios, and they are independent. Wi-Fi Direct is the one every phone has and the only
        // one measured to carry real traffic. Internet is what you fall back to when nobody is in
        // range, and it is a phone in your Circle relaying, not a service.
        // Bluetooth is back.
        //
        // It was taken out because it kept carrying traffic Wi-Fi Direct should have: the mesh sent
        // over whichever radio reported a link, so an 11 kbps radio took messages, receipts and voice
        // notes while the fast one sat idle, and a 91 KB note crawled for a minute. Deleting it fixed
        // that and cost the one thing it was good at — being the radio that works when there is no
        // Wi-Fi of any kind, which for a mesh is most of the time.
        //
        // The reason is gone. RadioChoice now sends over the widest LINKED radio measured, so BLE only
        // carries when it is genuinely the best there is — which is exactly when it should. And it now
        // advertises the meeting rather than one fixed id for the whole app, so it answers the person
        // whose tag you were handed and nobody else.
        Register(new AndroidBleTransportService("BLE",
            "61657468-6572-0001-0000-000000000001", "61657468-6572-0003-0000-000000000001",
            "61657468-6572-0002-0000-000000000001", LocalUhid, logger, routingKey: RoutingKey));

        Register(new AndroidWifiAwareTransportService(() => WireAddress.For(RoutingKey), logger));
        // The second leg. Last in the ladder on purpose: it costs the person data and puts their
        // traffic through somebody else's phone, so it is what you use when the alternative is nothing
        // at all — which, for a network meant to hold up when you walk out of range, is most of the time.
        Register(new InternetRadio(LocalUhid, logger, proxies, () => HasInternet(context, logger), "phone",
            m => global::Android.Util.Log.Info("AetherNet", m)));
        // The Wi-Fi the phone is already on. Below Wi-Fi Direct in the ladder on purpose: the router sees
        // that two devices on it are talking, how much and when. It never sees what — that is sealed above
        // every radio equally — so the difference is metadata and a dependency on somebody else's box.
        // Worth having as one way out among several rather than as the only one.
        AddWifi(s => global::Android.Util.Log.Info("AetherWifiLan", s));

        Register(new AndroidNfcTransportService(LocalUhid, logger));
        Register(new AndroidLoRaTransportService(LocalUhid, logger));
        // Wi-Fi Direct is the radio this mesh is built on, and the default says so.
        //
        // Every phone has it, and it is the only one measured to carry real traffic: 50 frames/sec
        // each way against BLE's 11 kbps in ONE direction (PROTOCOL_SPEC §5.5). BLE was the default
        // because it links reliably with no dependency on Wi-Fi P2P service discovery — which is true,
        // and is why it stays as the radio that FINDS people and brokers the group. It is not the one
        // that should carry what it finds.
        //
        // Defaulting to BLE quietly made it the answer to everything: messages, receipts and notes all
        // went over eleven kilobits while the fast radio sat idle, and a 91 KB voice note took over a
        // minute on a phone that can move it in under a second.
        //
        // This is a preference, not a restriction — the widest linked radio still carries, so nothing
        // breaks before the group forms, and everything moves across the moment it does.
        Prefer("Wi-Fi Direct", "BLE");
    }

    /// <summary>
    /// The Wi-Fi Direct radio's group-hosting side, so the broker can create and join groups on it.
    /// Exposed as the capability rather than the radio, because hosting a group is specific to this
    /// one radio and means nothing to the others.
    /// </summary>
    public IWifiDirectGroup WifiDirect => (IWifiDirectGroup)Radio("Wi-Fi Direct");

    // Take the foreground service before the radio, not after: Android only lets an app hold a
    // connection off-screen while that service is running, and the user may leave the app the
    // moment they have tapped Connect.
    protected override void BringingUp() => AetherLinkService.Start();

    protected override void NothingLinked() => AetherLinkService.Stop();

    protected override void Handed(string line) => global::Android.Util.Log.Info("AetherBLE", line);

    /// <summary>How many times the phone would not say whether it has a network. Static, as the asking is.</summary>
    private static int _unreadableNetwork;

    private static bool HasInternet(Context context, ILogger logger)
    {
        try
        {
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager cm)
                return false;

            var caps = cm.GetNetworkCapabilities(cm.ActiveNetwork);
            return caps is not null && caps.HasCapability(NetCapability.Internet);
        }
        catch (Exception ex)
        {
            // As in InternetRadio, and for the same reason: a silent false is the same as no internet, for ever.
            var failed = System.Threading.Interlocked.Increment(ref _unreadableNetwork);
            if (failed == 1 || failed % 200 == 0)
            {
                logger.LogWarning(ex, "Could not read connectivity, so this device is treated as having none ({Count} so far)", failed);
            }

            return false;
        }
    }
}
#endif
