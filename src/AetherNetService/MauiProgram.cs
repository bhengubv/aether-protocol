// SPDX-License-Identifier: MIT
using Microsoft.Extensions.Logging;

namespace AetherNetService;

/// <summary>
/// AetherNetService is built the way Aether is: one MAUI app, one project, a head for each system. So the service
/// and the app that asks it are the same split on every system, and only the pipe between them differs. What each
/// system brings to the node (its radios, the way apps connect) is under Platforms.
/// </summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

#if ANDROID
        builder.Logging.AddProvider(new LogcatLoggerProvider());
        // The identity stays where it has always been (files/aether-node.key), so a phone keeps its AetherTag.
        AndroidNode.AddServices(builder.Services, global::Android.App.Application.Context.FilesDir!.AbsolutePath);
#endif

        return builder.Build();
    }
}
