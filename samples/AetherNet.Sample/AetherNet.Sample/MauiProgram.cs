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
            sp.GetService<ILoggerFactory>()));

        // The bytes behind a message — a voice note, a picture. Content-addressed and chunked, so a
        // transfer resumes across a dropped link and works on a radio far too slow for a call.
        builder.Services.AddSingleton<AttachmentService>();

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
        // Live video is native surfaces layered over the WebView — see AndroidVideoIo for why frames
        // must never pass through Blazor.
        builder.Services.AddSingleton<IVideoIo, AetherNet.Sample.Platforms.Android.AndroidVideoIo>();
        // Wi-Fi Direct's group is created and handed over BLE rather than negotiated, so the radio
        // itself is the thing that hosts and joins.
        builder.Services.AddSingleton<IWifiDirectGroup>(sp =>
            ((AetherNet.Sample.Platforms.Android.Transports.AndroidRadioMesh)
                sp.GetRequiredService<IRadioMesh>()).WifiDirect);
#else
        builder.Services.AddSingleton<IAudioIo, NullAudioIo>();
        builder.Services.AddSingleton<IVideoIo, NullVideoIo>();
        builder.Services.AddSingleton<IWifiDirectGroup, NullWifiDirectGroup>();
#endif
        builder.Services.AddSingleton<WifiDirectBroker>();
        builder.Services.AddSingleton<CallService>();

        // The live in-process AetherNet mesh that the demo UI drives.
        builder.Services.AddScoped<AetherDemoService>();

        // The mesh-web: signed, content-addressed pages served at aether:// addresses.
        // One node per app session hosts the sample site and browses it on-device.
        builder.Services.AddSingleton<MeshWebService>();

        // The real over-the-air radio mesh — a native radio inside THIS one app.
#if ANDROID
        builder.Services.AddSingleton<IRadioMesh, AetherNet.Sample.Platforms.Android.Transports.AndroidRadioMesh>();
#else
        builder.Services.AddSingleton<IRadioMesh, NullRadioMesh>();
#endif

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
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

            // The broker subscribes too, so a peer's group handoff arrives before anyone has called.
            Warm("wifi-direct", () =>
            {
                var wifiDirect = app.Services.GetService<WifiDirectBroker>();
#if ANDROID
                if (wifiDirect is not null)
                    wifiDirect.Trace += m => global::Android.Util.Log.Info("AetherWFD", m);
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
