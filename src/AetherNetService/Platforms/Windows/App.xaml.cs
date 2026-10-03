// SPDX-License-Identifier: MIT
namespace AetherNetService.WinUI;

/// <summary>
/// The Windows head. Where a MAUI app opens its window, this opens none — AetherNetService has no UI. It builds the app
/// (<see cref="MauiProgram"/>), publishes the node on the named pipe (<see cref="WindowsNode"/>) and keeps running for
/// the apps that connect. Started a second time while it runs, it ends at once.
/// </summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    // Not base.OnLaunched: that is what opens a window.
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (!WindowsNode.BeTheOne())
        {
            Exit();
            return;
        }

        WindowsNode.Start(CreateMauiApp().Services);
    }
}
