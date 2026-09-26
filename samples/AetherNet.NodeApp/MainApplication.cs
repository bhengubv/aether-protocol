// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App;
using Android.Runtime;
using AetherNet.Identity;
using AetherNet.Node;
using AetherNet.Node.Android;
using AetherNet.Node.Host;

namespace AetherNet.NodeApp;

/// <summary>
/// The node app process. On start it mints (or loads) the device's one identity, builds the reference host
/// over it, and wires the exported <see cref="AetherNodeAndroidService"/> so a consumer app can bind. The
/// mesh is not wired into the node yet — auth and minting are the job here — so messaging and presence use
/// the null seams.
/// </summary>
[Application(Label = "Aether Node", AllowBackup = false)]
public sealed class MainApplication : Application
{
    /// <summary>The one grant store, shared by the bound service and the approval screen (durable; set at start).</summary>
    public static IGrantStore Grants { get; private set; } = new InMemoryGrantStore();

    /// <summary>The in-process host the exported service delegates to.</summary>
    public static AetherNodeService? Node { get; private set; }

    public MainApplication(nint handle, JniHandleOwnership ownership) : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();

        var dir = FilesDir!.AbsolutePath;
        Grants = new FileGrantStore(dir);

        var store = new FileNodeIdentityStore(dir);
        var identity = new NodeIdentity(store);
        Node = new AetherNodeService(identity, new NullNodeMessaging(), new OfflineNodeLinkSource());

        AetherNodeAndroidService.Configure(() => Node, Grants);
    }
}
#endif
