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

        // One AetherTag for the whole app — generated once, then loaded every run after that.
        builder.Services.AddSingleton<IIdentityService, IdentityService>();

        // The people this device knows, and the add/be-added handshake.
        builder.Services.AddSingleton<ContactService>();

        // Conversations + people (the messenger surface).
        builder.Services.AddSingleton<MessengerService>();

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
        _ = Task.Run(() =>
        {
            try
            {
                app.Services.GetService<AetherStore>();
                app.Services.GetService<IIdentityService>();
                app.Services.GetService<IContentStore>();
                app.Services.GetService<ContactService>();
            }
            catch (Exception ex)
            {
                // Never take the app down for a warm-up; the real resolve will surface any problem.
                System.Diagnostics.Debug.WriteLine($"Aether warm-up skipped: {ex.Message}");
            }
        });

        return app;
    }
}
