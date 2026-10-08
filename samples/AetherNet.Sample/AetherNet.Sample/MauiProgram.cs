using Microsoft.Extensions.Logging;
using AetherNet.Sample.Shared.Cache;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Services;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace AetherNet.Sample;

/// <summary>
/// The app's start-up: it starts the app, and calls AetherNetService.
/// </summary>
/// <remarks>
/// Aether is a thin client. Everything it shows comes from AetherNetService, a separate app (a service on a computer),
/// through one menu of requests (<c>NodeOp</c>): the classes its pages are handed keep their names, and each of their
/// members is one line on that menu. What is here is what only the app can do — its screen, its camera and
/// microphone, the phone's share sheet and file picker, and getting AetherNetService onto the device when it is not.
/// </remarks>
public static class MauiProgram
{
    /// <summary>The separate AetherNetService app this app connects to for its identity (Android's package).</summary>
    private const string AetherNetServicePackage = "com.bhengubv.aethernetservice";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()   // ZXing.Net.Maui — managed decode + CameraX, no ML Kit (see the csproj note)
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // Add device-specific services used by the AetherNet.Sample.Shared project
        builder.Services.AddSingleton<IFormFactor, FormFactor>();

        // The app's line to AetherNetService: a bind on a phone, its named pipe on a computer. The same line says whether
        // AetherNetService is on the device at all, for the install flow below.
#if ANDROID
        builder.Services.AddSingleton<AetherNet.Sample.Platforms.Android.AndroidServiceCall>();
        builder.Services.AddSingleton<IServiceCall>(sp => sp.GetRequiredService<AetherNet.Sample.Platforms.Android.AndroidServiceCall>());
#elif WINDOWS
        builder.Services.AddSingleton<AetherNet.Sample.Platforms.Windows.WindowsServiceCall>();
        builder.Services.AddSingleton<IServiceCall>(sp => sp.GetRequiredService<AetherNet.Sample.Platforms.Windows.WindowsServiceCall>());
#else
        // AetherNetService does not run on this device (an iPhone, a Mac), and the line says so to whatever asks.
        builder.Services.AddSingleton<IServiceCall, NoServiceCall>();
#endif

        // The menu, and every class the pages are handed that answers from it.
        builder.Services.AddServiceMenu();
        // The node itself, as the pages that ask it directly see it: its link, its radios, its switch.
        builder.Services.AddSingleton<AetherNetNodeService.IAetherNodeClient>(sp =>
            new AetherNetNodeService.NodeFromService(sp.GetRequiredService<ServiceMenu>()));

        // A call on this app's screen: the service's call drives the camera and the pictures here.
        builder.Services.AddSingleton<CallScreen>();

#if ANDROID
        // Backup: the phone confirms its owner (its own fingerprint, PIN or pattern screen), then this app asks the
        // service for the 24 words and shows them. Restore is not available from here yet.
        builder.Services.AddSingleton<AetherNetNodeService.Client.IOwnerCheck>(_ =>
            new AetherNetNodeService.Android.AndroidOwnerCheck(() => Microsoft.Maui.ApplicationModel.Platform.CurrentActivity));
        // AetherNetService has no screen to ask for the radios' permission from, so this app offers the way to its
        // page in the phone's settings; the person allows it there, once.
        builder.Services.AddSingleton<AetherNetNodeService.Client.IAetherNetServiceSettings>(_ =>
            new AetherNetNodeService.Android.AndroidAetherNetServiceSettings(
                global::Android.App.Application.Context, AetherNetServicePackage));
        // And when the phone does not have AetherNetService at all — Touch My Blood hands over Aether alone — Aether asks
        // for it before anything else: from SleptOn, checked to be AetherNetService signed like Aether, and given to the
        // phone's own installer for the person to confirm.
        builder.Services.AddSingleton(sp =>
        {
            var context = global::Android.App.Application.Context;
            void Log(string line) => global::Android.Util.Log.Info("AetherInstall", line);
            return new AetherNetNodeService.Client.NodeInstallFlow(
                ServiceConnector(sp.GetRequiredService<AetherNet.Sample.Platforms.Android.AndroidServiceCall>()),
                new AetherNetNodeService.Client.SleptOnPackageStore(new HttpClient(), AetherNetServicePackage, StoreApi()),
                new AetherNetNodeService.Android.AndroidNodePackageVerifier(context, AetherNetServicePackage),
                new AetherNetNodeService.Android.AndroidNodePackageInstaller(context, Log),
                Log);
        });
#elif WINDOWS
        // Backup: Windows Hello confirms the person at the computer is the one signed in, then this app asks the
        // service for the 24 words and shows them.
        builder.Services.AddSingleton<AetherNetNodeService.Client.IOwnerCheck>(_ =>
            new AetherNetNodeService.Windows.WindowsOwnerCheck(WindowHandle));
        // AetherNetService's own settings, managed from Aether's: its switch and its radios come through the pipe, and
        // the way to the service itself is its folder, where its identity and its log are kept.
        builder.Services.AddSingleton<AetherNetNodeService.Client.IAetherNetServiceSettings>(_ =>
            new AetherNetNodeService.Windows.WindowsAetherNetServiceSettings());
        // And when the computer does not have AetherNetService, Aether asks for it before anything else, as on a phone:
        // the Windows build from SleptOn, checked to be signed by Aether's makers, put in the person's own Programs folder,
        // told to Windows (App Paths) and started.
        builder.Services.AddSingleton(sp =>
        {
            var log = sp.GetService<ILoggerFactory>()?.CreateLogger("AetherInstall");
            void Log(string line) => log?.LogInformation("{Line}", line);
            return new AetherNetNodeService.Client.NodeInstallFlow(
                ServiceConnector(sp.GetRequiredService<AetherNet.Sample.Platforms.Windows.WindowsServiceCall>()),
                new AetherNetNodeService.Client.SleptOnPackageStore(new HttpClient(), AetherNetServicePackage, StoreApi(), platform: "windows"),
                new AetherNetNodeService.Windows.WindowsNodePackageVerifier(allowUnsigned: IsDebugBuild),
                new AetherNetNodeService.Windows.WindowsNodePackageInstaller(Log),
                Log);
        });
#endif
#if ANDROID || WINDOWS
        // The 24 words are the node's, asked for only after the owner check above says yes.
        builder.Services.AddSingleton<AetherNet.Identity.INodeIdentityRecovery>(sp =>
            new AetherNet.Identity.NodeIdentityRecoveryFromService(
                sp.GetRequiredService<ServiceMenu>(),
                sp.GetRequiredService<AetherNetNodeService.Client.IOwnerCheck>()));
#endif

        // A scanned invite and a tap arrive at the activity, which the system builds rather than the container; these
        // are where it hands them over (see the start-up below).
        builder.Services.AddSingleton<InviteLinks>();
        builder.Services.AddSingleton<Taps>();

        // What the person chose in Settings, applied to the shell as well as the page.
#if ANDROID
        builder.Services.AddSingleton<IAppTheme, AetherNet.Sample.Platforms.Android.AndroidAppTheme>();
#else
        builder.Services.AddSingleton<IAppTheme, NullAppTheme>();
#endif

        // Touch My Blood: the phone becomes an NFC tag for as long as somebody is offering. The tap is armed and
        // disarmed by the screen; what it points at is AetherNetService's handout.
#if ANDROID
        builder.Services.AddSingleton<ITapShare, AetherNet.Sample.Platforms.Android.AndroidTapShare>();
#else
        builder.Services.AddSingleton<ITapShare, NoTapShare>();
#endif

        // Recording a note. The microphone and camera are physical, so this is real only on the phone; elsewhere it
        // says no rather than recording nothing. A call holds the microphone, so the recorder asks the call screen.
#if ANDROID
        builder.Services.AddSingleton<IMediaCapture>(sp =>
            new AetherNet.Sample.Platforms.Android.AndroidMediaCapture(
                () => sp.GetRequiredService<CallScreen>().OnCall));
#else
        builder.Services.AddSingleton<IMediaCapture, NullMediaCapture>();
#endif

        // Sharing a photo or file already on the phone — the everyday counterpart to recording a note.
        // MAUI's own file chooser works on every head this app runs on, so it needs no platform branch.
        // Fully qualified: MAUI's global usings also declare an IFilePicker, so the bare name is ambiguous.
        builder.Services.AddSingleton<AetherNet.Sample.Shared.Services.IFilePicker, MauiFilePicker>();

        // Handing your AetherTag OUT to other apps — a link to send privately, a QR image to post
        // publicly. MAUI's share sheet works on every head this app runs on.
        builder.Services.AddSingleton<AetherNet.Sample.Shared.Services.ITagShare, MauiTagShare>();

        // Scanning someone else's QR to add them — the consume side. Opens a native camera page over
        // the Blazor UI, decodes with ZXing (no ML Kit), and hands the aether:// invite back.
        builder.Services.AddSingleton<AetherNet.Sample.Shared.Services.IQrScanner, MauiQrScanner>();

        // Live video, on this app's screen: the camera and the pictures are WebCodecs and getUserMedia in the page,
        // and frames go over a WebSocket to a server inside this app, on loopback — measured, because the JavaScript
        // bridge saturates at about four frames a second each way on a Redmi Note 9 and then stops answering at all.
        // The call screen above carries them to and from AetherNetService.
        builder.Services.AddSingleton<IVideoBridge, LoopbackVideoBridge>();
        builder.Services.AddSingleton<IVideoIo>(sp => new WebVideoIo(sp.GetService<IVideoBridge>()));

        // Reading a file that shipped inside the APK. Used to put the example card on the phone the
        // first time the AetherNet tab is opened — see HandedCard.
        builder.Services.AddSingleton<HandedCard.OpenPackaged>(
            _ => async named => await FileSystem.OpenAppPackageFileAsync(named));

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

        // The app starts, then calls the service: from here the service's call can reach this app's camera and screen,
        // and AetherNetService is asked for while the page is still loading.
        _ = app.Services.GetRequiredService<CallScreen>().StartAsync();
        _ = ReachAsync(app.Services);

#if WINDOWS && DEBUG
        // The end-to-end chat test's hooks on a computer — a folder a test drops commands into (see E2eHooks).
        AetherNet.Sample.Platforms.Windows.E2eHooks.Start(app.Services);
#endif

        // Published where the Android activity can reach it. An activity is built by the system
        // rather than by the container, so a scanned invite has no other way in — and until this
        // existed it had no way in at all.
        Warm("invites", () =>
        {
            var invites = app.Services.GetService<InviteLinks>();
            InviteLinks.Current = invites;
            Taps.Current = app.Services.GetService<Taps>();

            // A scan that launched the app cold delivered its link before any of this existed.
#if ANDROID
            invites?.Deliver(MainActivity.ConsumePendingLink());
#endif
        });

        return app;
    }

    /// <summary>
    /// Reach AetherNetService as the app starts. Not reaching it is not this method's to show: the first screen asks
    /// again, and says why when it cannot.
    /// </summary>
    private static async Task ReachAsync(IServiceProvider services)
    {
        try
        {
            await services.GetRequiredService<IServiceCall>().ConnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
#if ANDROID
            global::Android.Util.Log.Warn("AetherLine", $"AetherNetService not reached at start: {ex.Message}");
#else
            System.Diagnostics.Debug.WriteLine($"Aether: AetherNetService not reached at start: {ex.Message}");
#endif
        }
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

#if WINDOWS
    /// <summary>The handle of Aether's window, for Windows Hello to ask over; 0 while there is none.</summary>
    private static nint WindowHandle()
    {
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        return window is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(window);
    }
#endif

#if ANDROID || WINDOWS
#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    /// <summary>
    /// A file a Debug test may leave to steer Aether — never read by a Release build. On a phone in Aether's own storage
    /// (adb run-as); on a computer in the test's own folder, beside the hooks' (E2eHooks).
    /// </summary>
    private static string DebugFile(string name) =>
#if ANDROID
        Path.Combine(global::Android.App.Application.Context.FilesDir!.AbsolutePath, name);
#else
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetherE2E", name);
#endif

    /// <summary>Where AetherNetService is looked for — SleptOn's own API, unless a Debug test points elsewhere.</summary>
    private static Uri? StoreApi()
    {
#if DEBUG
        // A test leaves the address of a stand-in for SleptOn in Aether's own storage (adb run-as on a phone, the app's
        // data folder on a computer), so the whole download and install can be run before AetherNetService is published.
        var file = DebugFile("debug-store-api");
        if (File.Exists(file) && Uri.TryCreate(File.ReadAllText(file).Trim(), UriKind.Absolute, out var api)) return api;
#endif
        return null;
    }

    /// <summary>
    /// How Aether tells whether AetherNetService is on the device. In a Debug build a test can have it offered once on a
    /// device that has it — the install then lands as an update, keeping the identity — by leaving a file in Aether's
    /// storage. A Release build only ever offers it when it is not there.
    /// </summary>
    private static AetherNetNodeService.Client.INodeConnector ServiceConnector(AetherNetNodeService.Client.INodeConnector real)
    {
#if DEBUG
        if (File.Exists(DebugFile("debug-offer-aethernetservice"))) return new OfferedOnce(real);
#endif
        return real;
    }

#if DEBUG
    /// <summary>Says "not installed" the first time it is asked, and the truth after that.</summary>
    private sealed class OfferedOnce(AetherNetNodeService.Client.INodeConnector real) : AetherNetNodeService.Client.INodeConnector
    {
        private int _asked;

        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default) =>
            Interlocked.Exchange(ref _asked, 1) == 0 ? Task.FromResult(false) : real.IsInstalledAsync(cancellationToken);

        public Task<AetherNetNodeService.IAetherNodeClient?> TryBindAsync(CancellationToken cancellationToken = default) =>
            real.TryBindAsync(cancellationToken);
    }
#endif
#endif
}
