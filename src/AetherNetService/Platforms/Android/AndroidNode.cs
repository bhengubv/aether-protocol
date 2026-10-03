// SPDX-License-Identifier: MIT
#if ANDROID
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AetherNet.Identity;
using AetherNet.Mesh;
using AetherNet.Messaging;
using AetherNetNodeService.Android;
using AetherNetNodeService.Host;
using AetherNet.Security.Services;

namespace AetherNetService;

/// <summary>
/// AetherNetService on Android. As the process starts it mints (or loads) the device's one identity, brings up the real
/// radio mesh and the reliable messaging core over it, and wires the exported <see cref="AetherNodeAndroidService"/>
/// so a consumer app that binds gets identity <b>and</b> send/inbox/presence backed by the node's own radios.
///
/// <para>
/// The mesh lives here, in the node — not in any consumer app. This is the whole point of the node service:
/// one device, one node, one radio stack, and every app on the phone shares it by binding rather than each
/// standing up its own. The wiring below is the messaging plane the sample used to own, moved to where the
/// mesh now belongs.
/// </para>
///
/// <para>
/// It has no UI — a screen is attack surface. It is a network cable: no gate of its own. Security is
/// upstream, at the phone's lock (biometrics, pattern, PIN); no access to the phone, no access to the
/// service. So every caller is admitted (<see cref="OpenGrantStore"/>).
/// </para>
///
/// <para>
/// What is here is what Android brings to the node — its radios, its bound service, its foreground service. The
/// node itself is the same on every system; <see cref="MauiProgram"/> builds it, as Aether is built.
/// </para>
/// </summary>
internal static class AndroidNode
{
    /// <summary>The in-process host the exported service delegates to — mesh-backed.</summary>
    private static AetherNodeService? Node { get; set; }

    /// <summary>Everything the node needs, over this phone's radios. <paramref name="dir"/> is where the identity lives.</summary>
    public static void AddServices(IServiceCollection services, string dir)
    {
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

        // Secure sessions: this node's pre-key bundle, asking a peer for theirs, building and repairing the session.
        // The core cannot do this itself — it holds no pre-keys — so it asks, and the keeper answers.
        services.AddSingleton<AetherNet.PreKeys.IPreKeyExchangeService>(sp =>
            new AetherNet.PreKeys.PreKeyExchangeService(
                new RadioMeshSender(sp.GetRequiredService<IIdentityService>().AetherTag, sp.GetRequiredService<IRadioMesh>())));
        services.AddSingleton<MeshSessionKeeper>(sp =>
            new MeshSessionKeeper(
                sp.GetRequiredService<IIdentityService>(),
                sp.GetRequiredService<ISignalProtocolService>(),
                sp.GetRequiredService<AetherNet.PreKeys.IPreKeyExchangeService>(),
                sp.GetRequiredService<IMessagingService>(),
                sp.GetService<ILogger<MeshSessionKeeper>>()));

        // The node contract's own seams: messages held until a session and a path exist, presence from the radios,
        // and the radios told whom to reach from the contacts an app hands over.
        services.AddSingleton<INodeMessaging>(sp =>
            new MeshNodeMessaging(
                sp.GetRequiredService<IMessagingService>(),
                sp.GetRequiredService<MeshSessionKeeper>(),
                sp.GetRequiredService<IRadioMesh>(),
                sp.GetService<ILogger<MeshNodeMessaging>>()));
        // AetherNet's nearby radios on or off, for every app on the device — a file beside the identity.
        services.AddSingleton<INodeNearby>(sp => new NearbySetting(dir, sp.GetService<ILogger<NearbySetting>>()));
        services.AddSingleton<INodeLinkSource>(sp => new MeshNodeLinkSource(
            sp.GetRequiredService<IRadioMesh>(), sp.GetService<ILogger<MeshNodeLinkSource>>(), sp.GetRequiredService<INodeNearby>()));
        services.AddSingleton(sp =>
            new RadioMeeting(
                sp.GetRequiredService<IIdentityService>(),
                sp.GetRequiredService<IRadioMesh>(),
                sp.GetService<ILogger<RadioMeeting>>(),
                sp.GetRequiredService<INodeNearby>()));
        services.AddSingleton<INodeMeeting>(sp => sp.GetRequiredService<RadioMeeting>());

        // The fast radio: the Circle's Wi-Fi Direct group, worked out from the same contacts. It ran only in the app
        // once, so when the radios moved in here nothing formed the group at all. RadioMeeting already meets every
        // contact and points the other radios, so this is given no mesh to drive — only the question of who is here.
        services.AddSingleton(sp =>
        {
            var radio = sp.GetRequiredService<IRadioMesh>();
            return new FastRadioService(
                sp.GetRequiredService<RadioMeeting>(),
                sp.GetRequiredService<IIdentityService>(),
                ((AetherNet.Transport.Android.AndroidRadioMesh)radio).WifiDirect,
                sp.GetService<ILogger<FastRadioService>>(),
                mesh: null,
                isReachable: radio.IsReachable);
        });
    }

    /// <summary>Publish the node to the apps that bind, and bring the radios up — once MAUI has built the app.</summary>
    public static void Start(IServiceProvider provider)
    {
        // Unseal identity and construct the messaging plane, then publish the node so the first bind sees a
        // live, mesh-backed node — all fast, local work (no radio I/O). Radio bring-up is backgrounded below.
        var identity = provider.GetRequiredService<IIdentityService>();
        // Unseal once, here, rather than on a consumer's first call — and say whose node this is. A tag is an address
        // the person hands out; the key behind it never leaves this process.
        var tag = identity.AetherTag;
        provider.GetService<ILoggerFactory>()?.CreateLogger("AetherNetService")
            .LogInformation("AetherNetService is up as {Tag}{Minted}", tag, identity.IsNewIdentity ? " — a new identity, minted now" : "");

        Node = new AetherNodeService(
            provider.GetRequiredService<INodeIdentity>(),
            provider.GetRequiredService<INodeMessaging>(),
            provider.GetRequiredService<INodeLinkSource>(),
            provider.GetRequiredService<INodeMeeting>(),
            // The 24 words come from the same store the identity lives in, so they are this identity's.
            new NodeIdentityRecovery(provider.GetRequiredService<INodeIdentityStore>()),
            provider.GetRequiredService<INodeNearby>());
        AetherNodeAndroidService.Configure(() => Node, new OpenGrantStore());

        // The one inbound pump for the messaging plane: raw radio bytes → the library dispatcher → the
        // reliable core (which decrypts and raises MessageReceived) → the node's inbox seam. Pre-key requests
        // and responses go to the session keeper; everything else routes as the core decides.
        var radio = provider.GetRequiredService<IRadioMesh>();
        var dispatcher = provider.GetRequiredService<MeshInboundDispatcher>();
        var sessions = provider.GetRequiredService<MeshSessionKeeper>();
        dispatcher.Register(AetherNet.Protocol.PacketType.PreKeyRequest, (packet, _) => sessions.HandlePreKeyAsync(packet));
        dispatcher.Register(AetherNet.Protocol.PacketType.PreKeyResponse, (packet, _) => sessions.HandlePreKeyAsync(packet));
        radio.PacketReceived += bytes => _ = dispatcher.OnBytesAsync(radio.PeerTag, bytes);

        // Publish this node's bundle now, so a peer's first request can be answered.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await sessions.EnsureLocalBundleAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("AetherNetService", $"could not publish the pre-key bundle: {ex}");
            }
        });

        // Bring the radios up off the main thread — from here the node is hosting the mesh.
        var nearby = provider.GetRequiredService<INodeNearby>();
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            // AetherNet switched off: only the internet leg, as the person asked, and no Wi-Fi Direct group. The
            // foreground service still holds the process — this is still the phone's way to everyone over data.
            if (!nearby.On)
            {
                try
                {
                    AetherNet.Transport.Android.AetherLinkService.Start();
                    radio.SelectRadio(AetherNet.Transport.Android.AndroidRadioSetup.Internet);
                    global::Android.Util.Log.Info("AetherNetService", "AetherNet is switched off — internet only, no nearby radio");
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("AetherNetService", $"internet bring-up failed: {ex}");
                }
                return;
            }

            try
            {
                radio.Link();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("AetherNetService", $"radio bring-up failed: {ex}");
            }

            // And keep the Wi-Fi Direct group where it should be for as long as the service runs. Idle until the
            // radio is allowed and there is somebody to form it with; it checks again every few seconds.
            try
            {
                provider.GetRequiredService<FastRadioService>().KeepUp();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("AetherNetService", $"fast radio did not start: {ex}");
            }
        });
    }
}
#endif
