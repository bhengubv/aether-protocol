// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App;
using Android.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AetherNet.Identity;
using AetherNet.Mesh;
using AetherNet.Messaging;
using AetherNet.Node.Android;
using AetherNet.Node.Host;
using AetherNet.Security.Services;

namespace AetherNet.NodeApp;

/// <summary>
/// The node app process. On start it mints (or loads) the device's one identity, brings up the real radio
/// mesh and the reliable messaging core over it, and wires the exported <see cref="AetherNodeAndroidService"/>
/// so a consumer app that binds gets identity <b>and</b> send/inbox/presence backed by the node's own radios.
///
/// <para>
/// The mesh lives here, in the node — not in any consumer app. This is the whole point of the node service:
/// one device, one node, one radio stack, and every app on the phone shares it by binding rather than each
/// standing up its own. The wiring below is the messaging plane the sample used to own, moved to where the
/// mesh now belongs.
/// </para>
/// </summary>
[Application(Label = "Aether Node", AllowBackup = false)]
public sealed class MainApplication : Application
{
    /// <summary>The one grant store, shared by the bound service and the approval screen (durable).</summary>
    public static IGrantStore Grants { get; private set; } = new InMemoryGrantStore();

    /// <summary>The in-process host the exported service delegates to — now mesh-backed.</summary>
    public static AetherNodeService? Node { get; private set; }

    private ServiceProvider? _services;

    public MainApplication(nint handle, JniHandleOwnership ownership) : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();

        var dir = FilesDir!.AbsolutePath;
        Grants = new FileGrantStore(dir);

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(new LogcatLoggerProvider());
        });

        // Identity — held locally, because the node IS the device's identity. (A consumer app binds this;
        // here it is the node's own.)
        services.AddSingleton<INodeIdentityStore>(new FileNodeIdentityStore(dir));
        services.AddSingleton<INodeIdentity>(sp => new NodeIdentity(sp.GetRequiredService<INodeIdentityStore>()));
        services.AddSingleton<IIdentityService>(sp => new NodeIdentityService(sp.GetRequiredService<INodeIdentity>()));

        // The node's own persistence for the mesh directory seams. CircleDirectory and ProxyDirectory take
        // these as a seam; the sample keeps them in SQLite, the node keeps them in small JSON files.
        services.AddSingleton<NodeMeshStore>(new NodeMeshStore(dir));
        services.AddSingleton<IPeerRoutingKeyStore>(sp => sp.GetRequiredService<NodeMeshStore>());
        services.AddSingleton<IProxyDirectoryStore>(sp => sp.GetRequiredService<NodeMeshStore>());

        // The recognition + relay directories the mesh consults, parameterised by the seams above.
        services.AddSingleton<CircleDirectory>();
        services.AddSingleton<ProxyDirectory>();

        // The real radios.
        services.AddSingleton<IRadioSetup, AetherNet.Transport.Android.AndroidRadioSetup>();
        services.AddSingleton<IRadioInventory, AetherNet.Transport.Android.AndroidRadioInventory>();
        services.AddSingleton<IRadioMesh, AetherNet.Transport.Android.AndroidRadioMesh>();

        // Signal ratchet over durable, node-private session storage — so two nodes do not diverge into
        // separate sessions for one pair across a restart.
        services.AddSingleton<ISignalSessionBlobStore>(new FileSignalSessionStore(dir));
        services.AddSingleton<ISignalProtocolService>(sp =>
            new SignalProtocolService(
                sp.GetRequiredService<ILogger<SignalProtocolService>>(),
                sp.GetRequiredService<ISignalSessionBlobStore>()));

        // The reliable messaging core — the same wiring the sample uses, moved to where the mesh now lives.
        // Sealing (Signal), the outbox, retries, delivery receipts, DTN fallback ON, central relay OFF.
        services.AddSingleton<IMessageEnvelopeCipher>(sp =>
            new SignalMessageEnvelopeCipher(
                sp.GetRequiredService<ISignalProtocolService>(),
                sp.GetService<ILogger<SignalMessageEnvelopeCipher>>()));
        services.AddSingleton<AetherNet.Routing.IMeshSender>(sp =>
            new RadioMeshSender(
                sp.GetRequiredService<IIdentityService>().AetherTag,
                sp.GetRequiredService<IRadioMesh>()));
        services.AddSingleton<AetherNet.Routing.IRoutingService, OneHopRoutingService>();
        services.AddSingleton<AetherNet.Routing.IWireAddressResolver>(sp =>
            new CircleDirectoryWireResolver(
                sp.GetRequiredService<CircleDirectory>(),
                sp.GetRequiredService<IIdentityService>()));
        services.AddSingleton<AetherNet.Dtn.IDtnService>(sp =>
            new AetherNet.Dtn.DtnService(
                sp.GetRequiredService<AetherNet.Routing.IMeshSender>(),
                new AetherNet.Storage.KeyValueDtnBundleStore(
                    new AetherNet.Storage.FileSystemKeyValueStore(System.IO.Path.Combine(dir, "dtn"))),
                logger: sp.GetService<ILogger<AetherNet.Dtn.DtnService>>()));
        services.AddSingleton<IMessagingService>(sp =>
            new MessagingService(
                sp.GetRequiredService<AetherNet.Routing.IMeshSender>(),
                sp.GetRequiredService<AetherNet.Routing.IRoutingService>(),
                cipher: sp.GetRequiredService<IMessageEnvelopeCipher>(),
                dtn: sp.GetRequiredService<AetherNet.Dtn.IDtnService>(),
                options: new MessagingOptions { EnableDtnFallback = true, EnableBackendRelay = false },
                logger: sp.GetService<ILogger<MessagingService>>()));
        services.AddSingleton<MeshInboundDispatcher>(sp =>
            new MeshInboundDispatcher(
                sender: sp.GetRequiredService<AetherNet.Routing.IMeshSender>(),
                messaging: sp.GetRequiredService<IMessagingService>(),
                routing: sp.GetRequiredService<AetherNet.Routing.IRoutingService>(),
                resolver: sp.GetRequiredService<AetherNet.Routing.IWireAddressResolver>(),
                dtn: sp.GetRequiredService<AetherNet.Dtn.IDtnService>(),
                logger: sp.GetService<ILogger<MeshInboundDispatcher>>()));

        // The node contract's own seams, now mesh-backed rather than null.
        services.AddSingleton<INodeMessaging>(sp => new MeshNodeMessaging(sp.GetRequiredService<IMessagingService>()));
        services.AddSingleton<INodeLinkSource>(sp => new MeshNodeLinkSource(sp.GetRequiredService<IRadioMesh>()));

        var provider = services.BuildServiceProvider();
        _services = provider;

        // Unseal identity and construct the messaging plane, then publish the node so the first bind sees a
        // live, mesh-backed node — all fast, local work (no radio I/O). Radio bring-up is backgrounded below.
        var identity = provider.GetRequiredService<IIdentityService>();
        _ = identity.AetherTag; // unseal once, here, rather than on a consumer's first call

        Node = new AetherNodeService(
            provider.GetRequiredService<INodeIdentity>(),
            provider.GetRequiredService<INodeMessaging>(),
            provider.GetRequiredService<INodeLinkSource>());
        AetherNodeAndroidService.Configure(() => Node, Grants);

        // The one inbound pump for the messaging plane: raw radio bytes → the library dispatcher → the
        // reliable core (which decrypts and raises MessageReceived) → the node's inbox seam.
        var radio = provider.GetRequiredService<IRadioMesh>();
        var dispatcher = provider.GetRequiredService<MeshInboundDispatcher>();
        radio.PacketReceived += bytes => _ = dispatcher.OnBytesAsync(radio.PeerTag, bytes);

        // Bring the radios up off the main thread — from here the node is hosting the mesh.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                radio.Link();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("AetherNode", $"radio bring-up failed: {ex}");
            }
        });
    }
}
#endif
