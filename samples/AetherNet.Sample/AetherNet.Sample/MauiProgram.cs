using Microsoft.Extensions.Logging;
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

        return builder.Build();
    }
}
