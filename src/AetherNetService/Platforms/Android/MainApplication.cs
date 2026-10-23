// SPDX-License-Identifier: MIT
using Android.App;
using Android.Runtime;

namespace AetherNetService;

/// <summary>
/// The Android head. MAUI builds the app (<see cref="MauiProgram"/>); once it has, the node is published to the apps
/// that bind and the radios come up (<see cref="AndroidNode"/>). There is no page and nothing in the launcher: the one
/// activity, <see cref="AskActivity"/>, is see-through and only puts up the phone's own prompts for the radios, over
/// the app that asked.
/// </summary>
// Aether's logo, generated from Aether's own drawings (MauiIcon in the project file). Set here, not in the manifest:
// this attribute wins over the manifest's <application> element.
[Application(Label = "AetherNetService", Icon = "@mipmap/appicon", RoundIcon = "@mipmap/appicon_round", AllowBackup = false)]
public sealed class MainApplication : MauiApplication
{
    public MainApplication(nint handle, JniHandleOwnership ownership) : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override void OnCreate()
    {
        base.OnCreate();
        AndroidNode.Start(IPlatformApplication.Current!.Services);
    }
}
