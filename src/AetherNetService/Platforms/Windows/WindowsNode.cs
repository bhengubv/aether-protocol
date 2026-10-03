// SPDX-License-Identifier: MIT
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AetherNet.Identity;
using AetherNet.Mesh;
using AetherNet.Transport.Windows;
using AetherNetNodeService.Host;
using AetherNetNodeService.Pipe;
using AetherNetNodeService.Windows;

namespace AetherNetService;

/// <summary>
/// AetherNetService on Windows: the same node as on a phone (<see cref="NodeCore"/>), reached by apps over a named
/// pipe instead of a bind. It has no window — a screen is attack surface — and no gate of its own: only processes of
/// the person signed in can open the pipe, and past that every caller is admitted (<see cref="OpenGrantStore"/>).
///
/// <para>
/// The computer's radios are the phone's radio mesh with Windows' radios in it (<see cref="WindowsRadioMesh"/>):
/// Wi-Fi Direct, Bluetooth, the internet relay, and the network the computer is on — every one on unless the person
/// switched it off in an app.
/// </para>
/// </summary>
internal static class WindowsNode
{
    /// <summary>Held for as long as this process runs: one AetherNetService for the person signed in.</summary>
    private static Mutex? _one;

    private static PipeNodeServer? _pipe;

    /// <summary>Where AetherNetService keeps the identity and everything beside it.</summary>
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetherNetService");

    /// <summary>Be the one AetherNetService for the person signed in. False when another is already running.</summary>
    public static bool BeTheOne()
    {
        var one = new Mutex(initiallyOwned: true, @"Local\AetherNetService", out var first);
        if (!first)
        {
            one.Dispose();
            return false;
        }

        _one = one;
        return true;
    }

    /// <summary>Everything the node needs on this computer. <paramref name="dir"/> is where the identity lives.</summary>
    public static void AddServices(IServiceCollection services, string dir)
    {
        // The key, sealed by Windows for the person signed in.
        services.AddSingleton<INodeIdentityStore>(new ProtectedNodeIdentityStore(dir));

        // The computer's radios.
        services.AddSingleton<IRadioMesh, WindowsRadioMesh>();

        // Windows asks nothing of an app run by the person signed in, so there are no permissions to show.
        NodeCore.Add(services, dir, static () => [], internetRadio: "Internet");
    }

    /// <summary>Publish the node on the pipe — once MAUI has built the app.</summary>
    public static void Start(IServiceProvider provider)
    {
        var logs = provider.GetService<ILoggerFactory>();
        var log = logs?.CreateLogger("AetherNetService");

        var node = NodeCore.Start(provider);
        _pipe = new PipeNodeServer(() => node, new OpenGrantStore(), logger: logs?.CreateLogger<PipeNodeServer>());
        _pipe.Start();

        // Bring the radios up off the launch thread — from here the computer is hosting the mesh. AetherNet switched off:
        // only the internet leg, as the person asked.
        var radio = provider.GetRequiredService<IRadioMesh>();
        var nearby = provider.GetRequiredService<INodeNearby>();
        _ = Task.Run(() =>
        {
            try
            {
                if (nearby.On)
                {
                    radio.Link();
                }
                else
                {
                    radio.SelectRadio("Internet");
                    log?.LogInformation("AetherNet is switched off — internet only, no nearby radio");
                }
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "radio bring-up failed");
            }
        });

        // Where Windows finds AetherNetService by name, so an app can start it. Written each start, as an installer would.
        try
        {
            if (Environment.ProcessPath is { } path)
            {
                WindowsNodeLauncher.Register(path);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            log?.LogWarning(ex, "could not tell Windows where AetherNetService is; apps can reach it only while it runs");
        }
    }
}
