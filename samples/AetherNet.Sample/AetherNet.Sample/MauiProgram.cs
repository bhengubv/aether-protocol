using Microsoft.Extensions.Logging;
using AetherNet.Content;
using AetherNet.Content.Sqlite;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Services;

namespace AetherNet.Sample;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // Add device-specific services used by the AetherNet.Sample.Shared project
        builder.Services.AddSingleton<IFormFactor, FormFactor>();

        // Everything durable lives beside the app's own data, so it survives a restart and goes away
        // cleanly when the app is uninstalled.
        var dataDir = FileSystem.AppDataDirectory;
        builder.Services.AddSingleton(_ => new AetherStore(Path.Combine(dataDir, "aether.db")));
        builder.Services.AddSingleton<IContentStore>(_ => new SqliteContentStore(Path.Combine(dataDir, "content.db")));

        // The identity key is sealed by the phone's secure hardware where there is any.
#if ANDROID
        builder.Services.AddSingleton<ISecretVault>(_ =>
            new AetherNet.Sample.Platforms.Android.AndroidKeystoreVault(Path.Combine(dataDir, "vault")));
        builder.Services.AddSingleton<IRadioSetup, AetherNet.Sample.Platforms.Android.AndroidRadioSetup>();
#else
        builder.Services.AddSingleton<ISecretVault>(_ => new FileSecretVault(Path.Combine(dataDir, "vault")));
        builder.Services.AddSingleton<IRadioSetup, NullRadioSetup>();
#endif

        // The device's node identity. This app does not mint one — it asks, and the node mints only if
        // this device has never had an identity. One device is one node; an app that mints its own puts
        // a second peer on the same handset.
        builder.Services.AddSingleton<AetherNet.Identity.INodeIdentityStore>(sp =>
            new VaultNodeIdentityStore(sp.GetRequiredService<ISecretVault>()));
        builder.Services.AddSingleton<AetherNet.Identity.INodeIdentity>(sp =>
            new AetherNet.Identity.NodeIdentity(sp.GetRequiredService<AetherNet.Identity.INodeIdentityStore>()));
        builder.Services.AddSingleton<IIdentityService, IdentityService>();

        // The people this device knows, and the add/be-added handshake.
        builder.Services.AddSingleton<ContactService>();
        builder.Services.AddSingleton<InviteLinks>();

        // Real end-to-end encrypted messaging over the radio: Signal's X3DH + double ratchet, with
        // pre-key bundles exchanged over the mesh itself.
        // Sessions are kept in the device database, so a conversation survives the app closing. Without
        // this the ratchet starts from nothing on every launch and two phones diverge into separate
        // sessions for the same pair — which fails every message on its authentication tag and reads
        // exactly like broken crypto.
        builder.Services.AddSingleton<AetherNet.Security.Services.ISignalSessionBlobStore>(sp =>
            new StoredSignalSessions(sp.GetRequiredService<AetherStore>()));
        builder.Services.AddSingleton<AetherNet.Security.Services.ISignalProtocolService>(sp =>
            new AetherNet.Security.Services.SignalProtocolService(
                sp.GetRequiredService<ILogger<AetherNet.Security.Services.SignalProtocolService>>(),
                sp.GetRequiredService<AetherNet.Security.Services.ISignalSessionBlobStore>()));
        builder.Services.AddSingleton<AetherNet.PreKeys.IPreKeyExchangeService>(sp =>
            new AetherNet.PreKeys.PreKeyExchangeService(
                new RadioMeshSender(sp.GetRequiredService<IIdentityService>().AetherTag,
                    sp.GetRequiredService<IRadioMesh>())));
        builder.Services.AddSingleton<ChatService>(sp => new ChatService(
            sp.GetRequiredService<AetherStore>(),
            sp.GetRequiredService<IIdentityService>(),
            sp.GetRequiredService<AetherNet.Security.Services.ISignalProtocolService>(),
            sp.GetRequiredService<AetherNet.PreKeys.IPreKeyExchangeService>(),
            sp.GetService<IRadioMesh>(),
            sp.GetService<AttachmentService>(),
            sp.GetService<CircleDirectory>(),
            sp.GetService<ProxyDirectory>(),
            sp.GetService<IAppShareService>(),
            sp.GetService<IRelayHost>(),
            sp.GetService<FastRadioService>(),
            sp.GetService<ILoggerFactory>()));

        // Who, out of everyone broadcasting nearby, this phone already knows. Nothing else can answer
        // that question about a rotating address, and without an answer the only way to find out is
        // to dial a stranger and see who picks up.
        builder.Services.AddSingleton<CircleDirectory>();

        // Which phone in the Circle is carrying traffic for the others, and where to reach it. There
        // is no directory to look this up in by design — the address arrives from a contact, inside
        // their session, or not at all.
        // What this device actually has, measured against everything AetherNet can use.
        builder.Services.AddSingleton<IRadioInventory, AetherNet.Sample.Platforms.Android.AndroidRadioInventory>();
        builder.Services.AddSingleton<ProxyDirectory>();

        // The app carries itself: a mesh that needs a store to spread has a single point of
        // failure standing in front of its very first step.
        builder.Services.AddSingleton<IAppShareService, AetherNet.Sample.Platforms.Android.AndroidAppShareService>();

        // Touch My Blood: the phone becomes an NFC tag for as long as somebody is offering, and the
        // handout is the small web server that the tap points at. One singleton each — the tap is
        // armed and disarmed by the screen, and the handout expires on its own.
        builder.Services.AddSingleton<ITapShare, AetherNet.Sample.Platforms.Android.AndroidTapShare>();
        builder.Services.AddSingleton<AppHandout>();
        builder.Services.AddSingleton<AetherNet.Sample.Platforms.Android.GatewayService>(sp =>
            new AetherNet.Sample.Platforms.Android.GatewayService(
                sp.GetRequiredService<ProxyDirectory>(),
                // Resolved when it is called, not when it is built — chat holds the gateway, so
                // asking for chat here would be two singletons each waiting on the other.
                (url, ct) => sp.GetRequiredService<ChatService>().OfferProxyToCircleAsync(url, ct),
                sp.GetService<ILogger<AetherNet.Sample.Platforms.Android.GatewayService>>()));
        builder.Services.AddSingleton<IRelayHost>(sp =>
            sp.GetRequiredService<AetherNet.Sample.Platforms.Android.GatewayService>());

        // The bytes behind a message — a voice note, a picture. Content-addressed and chunked, so a
        // transfer resumes across a dropped link and works on a radio far too slow for a call.
        builder.Services.AddSingleton<AttachmentService>();

        // Brings the whole product up before the app opens — see WarmUpService. Singleton, because a
        // second warm-up would be a second set of radios coming up underneath the first.
        builder.Services.AddSingleton<WarmUpService>();

        // Recording a note. The microphone and camera are physical, so like the call path this is real
        // only on the phone; elsewhere it says no rather than recording nothing.
#if ANDROID
        builder.Services.AddSingleton<IMediaCapture>(sp =>
            new AetherNet.Sample.Platforms.Android.AndroidMediaCapture(sp.GetService<IAudioIo>()));
#else
        builder.Services.AddSingleton<IMediaCapture, NullMediaCapture>();
#endif

        // 1:1 voice. The microphone is physical, so it only exists on the phone; everywhere else the
        // call service is constructible but honestly says it cannot place one.
#if ANDROID
        builder.Services.AddSingleton<IAudioIo, AetherNet.Sample.Platforms.Android.AndroidAudioIo>();
        // Wi-Fi Direct's group is created and handed over BLE rather than negotiated, so the radio
        // itself is the thing that hosts and joins.
        builder.Services.AddSingleton<IWifiDirectGroup>(sp =>
            ((AetherNet.Sample.Platforms.Android.Transports.AndroidRadioMesh)
                sp.GetRequiredService<IRadioMesh>()).WifiDirect);
#else
        builder.Services.AddSingleton<IAudioIo, NullAudioIo>();
        builder.Services.AddSingleton<IWifiDirectGroup, NullWifiDirectGroup>();
#endif

        // Live video, for every head there is and every head there will be.
        //
        // This was a native Android implementation: camera2 into a MediaCodec surface, decoded onto
        // TextureViews layered UNDER the WebView. It served exactly one platform, it could never serve
        // the web head, and it had no path to iOS at all — which is the whole reason MAUI Blazor
        // Hybrid exists. It also spent its life fighting its host, since seeing a native view through
        // a WebView means making the entire page transparent.
        //
        // WebCodecs and getUserMedia do the same job in the layer the app already shares. Measured in
        // the live WebView on both test handsets before committing to it: secure context, camera
        // reachable, VideoEncoder and VideoDecoder present, H.264 Baseline supported at the size and
        // bitrate a call actually needs.
        // Frames do not go through the JavaScript bridge. They go over a WebSocket to a server inside
        // this app, on loopback — measured, because the bridge saturates at about four frames a second
        // each way on a Redmi Note 9 and then stops answering at all.
        builder.Services.AddSingleton<IVideoBridge, LoopbackVideoBridge>();
        builder.Services.AddSingleton<IVideoIo>(sp => new WebVideoIo(sp.GetService<IVideoBridge>()));
        builder.Services.AddSingleton<CallService>();

        // A call with more than two people in it. Built the same way group chat is — several 1:1
        // calls rather than a group key — and capped by the number of decoders this phone has, not by
        // the radio. See GroupCallService and PROTOCOL_SPEC §10.10.
        builder.Services.AddSingleton<GroupCallService>();

        // The live in-process AetherNet mesh that the demo UI drives.
        builder.Services.AddScoped<AetherDemoService>();

        // The mesh-web: signed, content-addressed pages served at aether:// addresses.
        // One node per app session hosts the sample site and browses it on-device.
        builder.Services.AddSingleton<MeshWebService>();

        // The real over-the-air radio mesh — a native radio inside THIS one app.
#if ANDROID
        builder.Services.AddSingleton<IRadioMesh, AetherNet.Sample.Platforms.Android.Transports.AndroidRadioMesh>();

        // Brings the fast radio up from the contact list, before any message exists. It asks no radio
        // anything: both phones already hold the tags and the host's key, so both work out the same
        // group without a word passing between them.
        builder.Services.AddSingleton<FastRadioService>(sp => new FastRadioService(
            sp.GetRequiredService<AetherStore>(),
            sp.GetRequiredService<IIdentityService>(),
            sp.GetRequiredService<IWifiDirectGroup>(),
            sp.GetService<ContactService>(),
            sp.GetService<ILogger<FastRadioService>>(),
            // Putting the radio away releases the foreground service with it, so the notification
            // does not outlive the link it was taken for.
            onIdle: () => (sp.GetService<IRadioMesh>()
                as AetherNet.Sample.Platforms.Android.Transports.AndroidRadioMesh)?.ReleaseIfIdle()));

        // Hosting a group is specific to one radio and means nothing to the other, so it is exposed as
        // the capability rather than the radio.
        builder.Services.AddSingleton<IWifiDirectGroup>(sp =>
            ((AetherNet.Sample.Platforms.Android.Transports.AndroidRadioMesh)sp.GetRequiredService<IRadioMesh>()).WifiDirect);
#else
        builder.Services.AddSingleton<IRadioMesh, NullRadioMesh>();
#endif

        builder.Services.AddMauiBlazorWebView();


#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#if ANDROID
        // AddDebug alone writes to System.Diagnostics.Debug, which on Android goes nowhere. Every
        // LogWarning in the app was invisible on the only platform it runs on — a message that failed
        // to decrypt said so faithfully, into a void.
        builder.Logging.AddProvider(new AetherNet.Sample.Platforms.Android.LogcatLoggerProvider());
#endif
#endif

        var app = builder.Build();

        // Warm the device-backed singletons off the UI thread. In Blazor Hybrid the .NET dispatcher,
        // the WebView thread and the Android main thread are one thread, so a service CONSTRUCTOR that
        // touches the disk — opening SQLite, unsealing the identity key from the Keystore — runs on the
        // UI thread the moment a page @injects it. Constructing them here first means the page resolves
        // an object that is already built. (See docs/DOTNET_MAUI_DOS_AND_DONTS.md — blocking ctors are
        // the recurring freeze in this stack.)
        // Each service is warmed on its own. One shared try/catch meant a single service that would
        // not build took the whole warm-up with it — including the tracing hooks below — and the app
        // then ran with no voice on the radio at all, which reads exactly like a dead radio. Chasing
        // that cost a full device session.
        _ = Task.Run(() =>
        {
            Warm("store", () => app.Services.GetService<AetherStore>());
            Warm("identity", () => app.Services.GetService<IIdentityService>());
            Warm("cards", () => app.Services.GetService<IContentStore>());
            Warm("contacts", () => app.Services.GetService<ContactService>());

            // Published where the Android activity can reach it. An activity is built by the system
            // rather than by the container, so a scanned invite has no other way in — and until this
            // existed it had no way in at all.
            Warm("invites", () =>
            {
                var invites = app.Services.GetService<InviteLinks>();
                InviteLinks.Current = invites;

                // A scan that launched the app cold delivered its link before any of this existed.
                invites?.Deliver(MainActivity.ConsumePendingLink());
            });

            // Constructing these is what subscribes them to the radio, so a message can arrive, and a
            // call can ring, before the user has opened anything.
            // Constructing this is what subscribes it to the radio, so a voice note can start arriving
            // before anyone opens the conversation it belongs to.
            Warm("attachments", () =>
            {
                var attachments = app.Services.GetService<AttachmentService>();
#if ANDROID
                if (attachments is not null)
                    attachments.Trace += m => global::Android.Util.Log.Info("AetherAtt", m);
#endif
            });

            Warm("chat", () =>
            {
                var chat = app.Services.GetService<ChatService>();
#if ANDROID
                // Put the message path in the system log next to the radio's own lines, so a receipt
                // that never comes back can be told apart from one that was never sent.
                if (chat is not null)
                    chat.Trace += m => global::Android.Util.Log.Info("AetherChat", m);
#endif
            });

            Warm("calls", () =>
            {
                var calls = app.Services.GetService<CallService>();
#if ANDROID
                if (calls is not null)
                    calls.Trace += m => global::Android.Util.Log.Info("AetherVoice", m);
#endif
            });

            // Constructing this is what subscribes it to the radio, so a group call can ring before
            // anyone has opened the group it belongs to.
            Warm("group calls", () =>
            {
                var group = app.Services.GetService<GroupCallService>();
#if ANDROID
                if (group is not null)
                    group.Trace += m => global::Android.Util.Log.Info("AetherGroupVoice", m);
#endif
            });

            // The Wi-Fi Direct radio finds its own peers and settles who hosts on its own, so there is
            // nothing here to start. Resolving the directory is the point: recognising a contact
            // behind a rotating address is what keeps the radio from dialling strangers, and it must
            // be loaded before the first beacon is seen rather than on the discovery path.
            Warm("circle", () => app.Services.GetService<CircleDirectory>());

            // The fast radio's own commentary, next to the radio's. Which group this phone decided on,
            // and whether it hosted or joined it, is the first thing to check when two phones that
            // should be talking are not.
            Warm("fast-radio", () =>
            {
                var fast = app.Services.GetService<FastRadioService>();
#if ANDROID
                if (fast is not null)
                    fast.Trace += m => global::Android.Util.Log.Info("AetherFast", m);
#endif
            });
        });

        return app;
    }

    /// <summary>
    /// Build one service, and say so out loud if it will not build.
    ///
    /// <para>
    /// This used to report through <c>Debug.WriteLine</c>, which a Release build strips — so on the
    /// only configuration that ships, a service failing to construct was completely silent. The app
    /// came up, the pages rendered, and nothing worked, with nothing anywhere to say why.
    /// </para>
    /// </summary>
    private static void Warm(string what, Action build)
    {
        try
        {
            build();
        }
        catch (Exception ex)
        {
            // Never take the app down for a warm-up; the page that needs it will surface the problem
            // in its own way. But leave a trail.
#if ANDROID
            global::Android.Util.Log.Error("AetherWarmup", $"{what} did not start: {ex}");
#else
            System.Diagnostics.Debug.WriteLine($"Aether warm-up: {what} did not start: {ex.Message}");
#endif
        }
    }
}
