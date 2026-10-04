// SPDX-License-Identifier: MIT
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AetherNet.Identity;
using AetherNet.Mesh;
using AetherNet.Messaging;
using AetherNetNodeService;
using AetherNetNodeService.Help;
using AetherNetNodeService.Host;
using AetherNet.Security.Services;

namespace AetherNetService;

/// <summary>
/// The node itself, the same on every system: the device's one identity, its Signal sessions, the reliable messaging
/// core, and the node contract's seams over them. Each system's head registers what it brings — where the key is kept
/// (<see cref="INodeIdentityStore"/>), its radios (<see cref="IRadioMesh"/>), its permissions — then calls
/// <see cref="Add"/>, and once the app is built, <see cref="Start"/>, and publishes the node on its own pipe.
/// </summary>
internal static class NodeCore
{
    /// <param name="services">Already holding this system's <see cref="INodeIdentityStore"/> and <see cref="IRadioMesh"/>.</param>
    /// <param name="dir">Where the node keeps what it must not forget, beside the identity.</param>
    /// <param name="permissions">What AetherNetService needs from this system, and whether each is allowed, now.</param>
    /// <param name="internetRadio">The internet leg's name among this system's radios, or null where there is none.</param>
    public static void Add(
        IServiceCollection services, string dir, Func<IReadOnlyList<ServicePermission>> permissions, string? internetRadio)
    {
        // Identity — held here, because the node IS the device's identity. (An app connects for it; here it is the
        // node's own.)
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
                    new AetherNet.Storage.FileSystemKeyValueStore(Path.Combine(dir, "dtn"))),
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
        // AetherNet's nearby radios on or off, for every app on the device — a file beside the identity. And each radio
        // on or off, the same way: every one on until the person switches it off in an app. The radios read the same
        // switches as they come up (IRadioSwitches), so a head registers its radio mesh and gets them for free.
        services.AddSingleton<INodeNearby>(sp => new NearbySetting(dir, sp.GetService<ILogger<NearbySetting>>()));
        services.AddSingleton(sp => new RadioSwitches(dir, sp.GetService<ILogger<RadioSwitches>>()));
        services.AddSingleton<INodeRadios>(sp => sp.GetRequiredService<RadioSwitches>());
        services.AddSingleton<IRadioSwitches>(sp => sp.GetRequiredService<RadioSwitches>());
        // Quiet help: the person's own help or walk, the guardians they chose, and what this device knows of
        // somebody who chose its owner as a guardian. It travels the mesh here; a head that can also put it on the
        // air registers an IHelpRadio, and without one the mesh carries it alone.
        services.AddSingleton(sp => new HelpStore(dir, sp.GetService<ILogger<HelpStore>>()));
        services.AddSingleton(sp => new QuietHelp(
            sp.GetRequiredService<HelpStore>(),
            sp.GetRequiredService<INodeMessaging>(),
            sp.GetService<IHelpRadio>(),
            log: sp.GetService<ILogger<QuietHelp>>()));
        services.AddSingleton<INodeHelpSource>(sp => sp.GetRequiredService<QuietHelp>());
        services.AddSingleton<INodeLinkSource>(sp => new MeshNodeLinkSource(
            sp.GetRequiredService<IRadioMesh>(), permissions,
            sp.GetService<ILogger<MeshNodeLinkSource>>(), sp.GetRequiredService<INodeNearby>()));
        services.AddSingleton(sp =>
            new RadioMeeting(
                sp.GetRequiredService<IIdentityService>(),
                sp.GetRequiredService<IRadioMesh>(),
                sp.GetService<ILogger<RadioMeeting>>(),
                sp.GetRequiredService<INodeNearby>(),
                internetRadio));
        services.AddSingleton<INodeMeeting>(sp => sp.GetRequiredService<RadioMeeting>());
    }

    /// <summary>
    /// Unseal the identity, build the node and wire its inbound pump — all fast, local work, no radio I/O — and hand
    /// the node back to be published on this system's pipe.
    /// </summary>
    public static AetherNodeService Start(IServiceProvider provider)
    {
        var log = provider.GetService<ILoggerFactory>()?.CreateLogger("AetherNetService");

        // Unseal once, here, rather than on an app's first call — and say whose node this is. A tag is an address the
        // person hands out; the key behind it never leaves this process.
        var minting = !provider.GetRequiredService<INodeIdentityStore>().Exists;   // asked before the first read mints one
        var tag = provider.GetRequiredService<IIdentityService>().AetherTag;
        log?.LogInformation("AetherNetService is up as {Tag}{Minted}", tag, minting ? " — a new identity, minted now" : "");

        var node = new AetherNodeService(
            provider.GetRequiredService<INodeIdentity>(),
            provider.GetRequiredService<INodeMessaging>(),
            provider.GetRequiredService<INodeLinkSource>(),
            provider.GetRequiredService<INodeMeeting>(),
            // The 24 words come from the same store the identity lives in, so they are this identity's.
            new NodeIdentityRecovery(provider.GetRequiredService<INodeIdentityStore>()),
            provider.GetRequiredService<INodeNearby>(),
            provider.GetRequiredService<INodeRadios>(),
            provider.GetRequiredService<INodeHelpSource>());

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
        _ = Task.Run(async () =>
        {
            try
            {
                await sessions.EnsureLocalBundleAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "could not publish the pre-key bundle");
            }
        });

        return node;
    }
}
