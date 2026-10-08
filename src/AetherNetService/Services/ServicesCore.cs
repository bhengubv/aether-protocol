// SPDX-License-Identifier: MIT
using AetherNet.Browser;
using AetherNet.Content;
using AetherNet.Content.Sqlite;
using AetherNetNodeService.Host;
using AetherNetNodeService.Host.Data;
using AetherNetNodeService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AetherNetService;

/// <summary>
/// The services that moved here from the Aether app, started by their own list — beside the node's
/// (<see cref="NodeCore"/>), not merged into it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The app's list, as it was.</b> Every registration and every warm-up step below is the one the app's startup
/// made for these classes, with the same arguments, so nothing a peer sees changes. The difference is where the node
/// is: the app reached it across the binder; these classes are handed it in this process.
/// </para>
/// <para>
/// <b>Its own list, because the two lists name the same things.</b> The app's list makes a message sender, Signal
/// sessions, messaging, routing and DTN for itself — and so does the node's. Put into one list, the later one would
/// replace the node's own, and the app's messaging ("send through the node") would leave the node sending through
/// itself. Kept apart, each side keeps what it has.
/// </para>
/// <para>
/// Left out: what was the app's own way of reaching this service — finding it, installing it, its settings screen,
/// the owner check before the recovery phrase. Those stay with the app, which still calls the service.
/// </para>
/// </remarks>
internal static class ServicesCore
{
    /// <summary>The services, once <see cref="Start"/> has made them; null before.</summary>
    public static IServiceProvider? Services { get; private set; }

    /// <summary>Makes the services on top of the started <paramref name="node"/>, then warms them off this thread.</summary>
    public static IServiceProvider Start(IServiceProvider nodeServices, IAetherNodeClient node, string dir)
    {
        var services = new ServiceCollection();
        services.AddSingleton(nodeServices.GetService<ILoggerFactory>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(node);

        // The node's own identity: these classes run in the node, where a key derived from it may be used. The client's
        // stand-in an app holds refuses that by design, and with it chat, calls and the Circle did not start.
        services.AddSingleton(nodeServices.GetRequiredService<AetherNet.Identity.INodeIdentity>());

        Add(services, Path.Combine(dir, "services"));

        var built = services.BuildServiceProvider();
        Services = built;
        _ = Task.Run(() =>
        {
            // The menu first, so an app sees the warm-up as it happens rather than being told the service is still
            // starting until it is over.
            NodeAnswers.Use(built);

            Warm(built);
        });
        return built;
    }

    private static void Add(IServiceCollection services, string dataDir)
    {
        Directory.CreateDirectory(dataDir);

        services.AddSingleton(_ => new AetherStore(Path.Combine(dataDir, "aether.db")));
        services.AddSingleton<IPeerRoutingKeyStore>(sp => sp.GetRequiredService<AetherStore>());
        services.AddSingleton<IProxyDirectoryStore>(sp => sp.GetRequiredService<AetherStore>());
        services.AddSingleton<IContentStore>(_ => new SqliteContentStore(Path.Combine(dataDir, "content.db")));
#if ANDROID
        services.AddSingleton<ISecretVault>(_ =>
            new AetherNetNodeService.Android.AndroidKeystoreVault(Path.Combine(dataDir, "vault")));
#else
        services.AddSingleton<ISecretVault>(_ => new FileSecretVault(Path.Combine(dataDir, "vault")));
#endif
        services.AddSingleton<IRadioSetup>(sp =>
            sp.GetService<IAetherNodeClient>() is { } client
                ? new AetherNetNodeService.Client.NodeRadioSetup(
                    client, sp.GetService<AetherNetNodeService.Client.IAetherNetServiceSettings>())
                : new NullRadioSetup());
        services.AddSingleton<IIdentityService, IdentityService>();
        services.AddSingleton<PanicWipeService>();
        services.AddSingleton<AetherNet.Identity.IPetnameStore>(sp =>
            new AetherStorePetnameStore(sp.GetRequiredService<AetherStore>()));
        services.AddSingleton(sp =>
            new AetherNet.Identity.PetnameRegistry(sp.GetRequiredService<AetherNet.Identity.IPetnameStore>()));
        services.AddSingleton<ContactService>();
        services.AddSingleton<NodeContactSync>();
        services.AddSingleton<AetherNet.Security.Services.ISignalSessionBlobStore>(sp =>
            new StoredSignalSessions(sp.GetRequiredService<AetherStore>()));
        services.AddSingleton<AetherNet.Security.Services.ISignalProtocolService>(sp =>
            new AetherNet.Security.Services.SignalProtocolService(
                sp.GetRequiredService<ILogger<AetherNet.Security.Services.SignalProtocolService>>(),
                sp.GetRequiredService<AetherNet.Security.Services.ISignalSessionBlobStore>()));
        services.AddSingleton<AetherNet.PreKeys.IPreKeyExchangeService>(sp =>
            new AetherNet.PreKeys.PreKeyExchangeService(
                new RadioMeshSender(sp.GetRequiredService<IIdentityService>().AetherTag,
                    sp.GetRequiredService<IRadioMesh>())));
        services.AddSingleton<AetherNet.Messaging.IMessageEnvelopeCipher>(sp =>
            new AetherNet.Messaging.SignalMessageEnvelopeCipher(
                sp.GetRequiredService<AetherNet.Security.Services.ISignalProtocolService>(),
                sp.GetService<ILogger<AetherNet.Messaging.SignalMessageEnvelopeCipher>>()));
        services.AddSingleton<AetherNet.Routing.IMeshSender>(sp =>
            new RadioMeshSender(sp.GetRequiredService<IIdentityService>().AetherTag,
                sp.GetRequiredService<IRadioMesh>()));
        services.AddSingleton<AetherNet.Routing.IRoutingService, OneHopRoutingService>();
        services.AddSingleton<AetherNet.Routing.IWireAddressResolver>(sp =>
            new NoMeshWireAddressResolver(sp.GetRequiredService<IIdentityService>()));
        services.AddSingleton<AetherNet.Dtn.IDtnService>(sp =>
            new AetherNet.Dtn.DtnService(
                sp.GetRequiredService<AetherNet.Routing.IMeshSender>(),
                new AetherNet.Storage.KeyValueDtnBundleStore(
                    new AetherNet.Storage.FileSystemKeyValueStore(Path.Combine(dataDir, "dtn"))),
                logger: sp.GetService<ILogger<AetherNet.Dtn.DtnService>>()));
        services.AddSingleton<AetherNet.Messaging.IMessagingService>(sp =>
            new AetherNetNodeService.Client.NodeBackedMessaging(sp.GetRequiredService<IAetherNodeClient>()));
        services.AddSingleton<AetherNet.Messaging.MeshInboundDispatcher>(sp =>
            new AetherNet.Messaging.MeshInboundDispatcher(
                sender: sp.GetRequiredService<AetherNet.Routing.IMeshSender>(),
                messaging: sp.GetRequiredService<AetherNet.Messaging.IMessagingService>(),
                routing: sp.GetRequiredService<AetherNet.Routing.IRoutingService>(),
                resolver: sp.GetRequiredService<AetherNet.Routing.IWireAddressResolver>(),
                dtn: sp.GetRequiredService<AetherNet.Dtn.IDtnService>(),
                logger: sp.GetService<ILogger<AetherNet.Messaging.MeshInboundDispatcher>>()));
        services.AddSingleton<AetherNet.Handshake.IHandshakeService>(sp =>
        {
            var caps = new HashSet<string>(
                AetherNet.Handshake.HandshakeService.DefaultCapabilities, StringComparer.Ordinal);
            foreach (var radio in sp.GetRequiredService<IRadioInventory>().Survey())
                if (radio.Carries &&
                    AetherNet.Transport.Services.TransportCapability.TagFor(radio.Name) is { } tag)
                    caps.Add(tag);
            return new AetherNet.Handshake.HandshakeService(
                sp.GetRequiredService<AetherNet.Routing.IMeshSender>(),
                sp.GetService<ILogger<AetherNet.Handshake.HandshakeService>>(),
                ourCapabilities: caps);
        });
        services.AddSingleton<ChatService>(sp => new ChatService(
            sp.GetRequiredService<AetherStore>(),
            sp.GetRequiredService<IIdentityService>(),
            sp.GetRequiredService<AetherNet.Security.Services.ISignalProtocolService>(),
            sp.GetRequiredService<AetherNet.PreKeys.IPreKeyExchangeService>(),
            sp.GetRequiredService<AetherNet.Messaging.IMessagingService>(),
            sp.GetRequiredService<AetherNet.Messaging.MeshInboundDispatcher>(),
            sp.GetService<IRadioMesh>(),
            sp.GetService<AttachmentService>(),
            sp.GetService<CircleDirectory>(),
            sp.GetService<ProxyDirectory>(),
            sp.GetService<IAppShareService>(),
            sp.GetService<IRelayHost>(),
            sp.GetService<FastRadioService>(),
            sp.GetService<ILoggerFactory>()));
        services.AddSingleton(sp => new QuietHelpService(
            sp.GetService<IAetherNodeClient>(),
            sp.GetService<ILoggerFactory>()));
        services.AddSingleton(sp => new AwareService(
            sp.GetService<IAetherNodeClient>(),
            sp.GetService<ILoggerFactory>()));
        services.AddSingleton<SosService>(sp => new SosService(
            sp.GetRequiredService<IIdentityService>(),
            sp.GetService<IRadioMesh>(),
            sp.GetService<ILoggerFactory>()));
        services.AddSingleton<WatchService>(sp => new WatchService(
            sp.GetRequiredService<IIdentityService>(),
            sp.GetService<IRadioMesh>(),
            sp.GetService<ILoggerFactory>()));
#if ANDROID
        services.AddSingleton<AetherNetNodeService.Host.Cast.IMulticastHold,
            AetherNetNodeService.Android.AndroidMulticastHold>();
#else
        services.AddSingleton<AetherNetNodeService.Host.Cast.IMulticastHold,
            AetherNetNodeService.Host.Cast.NoMulticastHold>();
#endif
        services.AddSingleton<AetherNetNodeService.Host.Cast.DlnaCastService>(sp =>
            new AetherNetNodeService.Host.Cast.DlnaCastService(
                sp.GetService<AttachmentService>(),
                sp.GetService<AetherNetNodeService.Host.Cast.IMulticastHold>(),
                sp.GetService<ILogger<AetherNetNodeService.Host.Cast.DlnaCastService>>()));
        services.AddSingleton<AetherNetNodeService.Host.Cast.CastService>(sp =>
            new AetherNetNodeService.Host.Cast.CastService(
                sp.GetRequiredService<ContactService>(),
                sp.GetRequiredService<WatchService>(),
                sp.GetRequiredService<AetherNetNodeService.Host.Cast.DlnaCastService>(),
                sp.GetService<IRadioMesh>(),
                sp.GetService<ILogger<AetherNetNodeService.Host.Cast.CastService>>()));
        services.AddSingleton<AetherNetNodeService.Host.Cast.UpnpRendererService>(sp =>
            new AetherNetNodeService.Host.Cast.UpnpRendererService(
                sp.GetService<AetherNetNodeService.Host.Cast.IMulticastHold>(),
                sp.GetService<ILogger<AetherNetNodeService.Host.Cast.UpnpRendererService>>()));
        services.AddSingleton<DtnCarrierService>(sp => new DtnCarrierService(
            sp.GetRequiredService<AetherNet.Dtn.IDtnService>(),
            sp.GetRequiredService<AetherNet.Messaging.IMessagingService>(),
            sp.GetRequiredService<IIdentityService>(),
            sp.GetService<IRadioMesh>(),
            sp.GetService<ILogger<DtnCarrierService>>()));
        services.AddSingleton<IRadioInventory, NullRadioInventory>();
        services.AddSingleton<ProxyDirectory>();
#if ANDROID
        services.AddSingleton<IAppShareService, AetherNetNodeService.Android.AndroidAppShareService>();
#else
        services.AddSingleton<IAppShareService, NoAppShare>();
#endif
        services.AddSingleton<AppHandout>();
#if ANDROID
        services.AddSingleton<AetherNetNodeService.Android.GatewayService>(sp =>
            new AetherNetNodeService.Android.GatewayService(
                sp.GetRequiredService<ProxyDirectory>(),
                (url, ct) => sp.GetRequiredService<ChatService>().OfferProxyToCircleAsync(url, ct),
                sp.GetService<ILogger<AetherNetNodeService.Android.GatewayService>>()));
        services.AddSingleton<IRelayHost>(sp =>
            sp.GetRequiredService<AetherNetNodeService.Android.GatewayService>());
#endif
        services.AddSingleton<AttachmentService>();
        services.AddSingleton<WarmUpService>();
#if ANDROID
        services.AddSingleton<IAudioIo, AetherNetNodeService.Android.AndroidAudioIo>();
#else
        services.AddSingleton<IAudioIo, NullAudioIo>();
#endif
        services.AddSingleton<IWifiDirectGroup, NullWifiDirectGroup>();

        // The camera and the screen are the app's, and so is the recorder. A call here reaches them through the menu.
        services.AddSingleton<AppVideoIo>();
        services.AddSingleton<IVideoIo>(sp => sp.GetRequiredService<AppVideoIo>());
        services.AddSingleton<CallService>();
        services.AddSingleton<GroupCallService>();
        services.AddScoped<AetherDemoService>();
        services.AddSingleton<ICardStore, AetherStoreCardStore>();
        services.AddSingleton<IMeshLink, RadioMeshLink>();
        services.AddAetherBrowser();
        services.AddSingleton<HandedCard.OpenPackaged>(
            _ => async named => await FileSystem.OpenAppPackageFileAsync(named));
        services.AddSingleton<IRadioMesh, NullRadioMesh>();
        services.AddSingleton<ICircleContacts, StoreCircleContacts>();
        services.AddSingleton<FastRadioService>();
        services.AddSingleton<CircleDirectory>();
    }

    // The app's warm-up, step for step. The steps left out are the app's own: handing over the deep link its window had
    // just been opened with, and setting where invite links and taps arrive, which the app's activity hands over.
    private static void Warm(IServiceProvider app)
    {
        Warm("store", () => app.GetService<AetherStore>());
        Warm("identity", () => app.GetService<IIdentityService>());
        Warm("cards", () => app.GetService<IContentStore>());
        Warm("contacts", () => app.GetService<ContactService>());
        Warm("node contacts", () => app.GetService<NodeContactSync>()?.SyncInBackground());
        Warm("attachments", () =>
        {
            var attachments = app.GetService<AttachmentService>();
            if (attachments is not null) attachments.Trace += m => Say("AetherAtt", m);
        });
        Warm("handout", () =>
        {
            var handout = app.GetService<AppHandout>();
            if (handout is not null) handout.Say += m => Say("AetherGive", m);
        });
        Warm("chat", () =>
        {
            var chat = app.GetService<ChatService>();
            if (chat is not null) chat.Trace += m => Say("AetherChat", m);
        });
        Warm("inbound", () =>
        {
            var dispatcher = app.GetService<AetherNet.Messaging.MeshInboundDispatcher>();
            var radio = app.GetService<IRadioMesh>();
            if (dispatcher is not null && radio is not null)
                radio.PacketReceived += bytes => _ = dispatcher.OnBytesAsync(radio.PeerTag, bytes);
        });
        Warm("negotiation", () =>
        {
            var handshake = app.GetService<AetherNet.Handshake.IHandshakeService>();
            var dispatcher = app.GetService<AetherNet.Messaging.MeshInboundDispatcher>();
            var radio = app.GetService<IRadioMesh>();
            if (handshake is null || dispatcher is null || radio is null) return;
            dispatcher.Register(AetherNet.Protocol.PacketType.Hello,
                (packet, ct) => handshake.HandleHelloAsync(packet, ct));
            dispatcher.Register(AetherNet.Protocol.PacketType.HelloAck,
                (packet, ct) => handshake.HandleHelloAckAsync(packet, ct));
            radio.PeerLinked += peer => _ = handshake.InitiateAsync(peer);
            handshake.PeerNegotiated += (_, caps) =>
            {
                var transports = caps.Capabilities
                    .Where(AetherNet.Transport.Services.TransportCapability.IsTransport)
                    .ToHashSet(StringComparer.Ordinal);
                radio.NotePeerTransports(caps.PeerUhid, transports);
            };
            handshake.PeerNegotiated += (_, caps) =>
                Say("AetherNeg", $"negotiated with {caps.PeerUhid}: [{string.Join(", ", caps.Capabilities)}]");
        });
        Warm("calls", () =>
        {
            var calls = app.GetService<CallService>();
            if (calls is not null) calls.Trace += m => Say("AetherVoice", m);
        });
        Warm("group calls", () =>
        {
            var group = app.GetService<GroupCallService>();
            if (group is not null) group.Trace += m => Say("AetherGroupVoice", m);
        });
        Warm("sos", () => app.GetService<SosService>()?.Prime());
        Warm("watch", () => app.GetService<WatchService>());
        Warm("carry", () => app.GetService<DtnCarrierService>()?.Prime());
        Warm("cast", () =>
        {
            var dlna = app.GetService<AetherNetNodeService.Host.Cast.DlnaCastService>();
            if (dlna is not null) dlna.Trace += m => Say("AetherCast", m);
        });
        Warm("renderer", () =>
        {
            var rend = app.GetService<AetherNetNodeService.Host.Cast.UpnpRendererService>();
            var me = app.GetService<IIdentityService>();
            if (rend is null || me is null) return;
            rend.Trace += m => Say("AetherCast", m);
            var model = Microsoft.Maui.Devices.DeviceInfo.Current.Model;
            var name = string.IsNullOrWhiteSpace(model) ? "Aether" : $"Aether — {model}";
            rend.Start(me.AetherTag, name);
        });
        Warm("circle", () => app.GetService<CircleDirectory>());
        Warm("fast-radio", () =>
        {
            var fast = app.GetService<FastRadioService>();
            if (fast is null) return;
            var lastSaid = "";
            fast.Trace += m =>
            {
                if (string.Equals(m, lastSaid, StringComparison.Ordinal)) return;
                lastSaid = m;
                Say("AetherFast", m);
            };
        });
    }

    private static void Warm(string what, Action build)
    {
        try
        {
            build();
        }
        catch (Exception ex)
        {
            Say("AetherWarmup", $"{what} did not start: {ex}");
        }
    }

    private static void Say(string tag, string line)
    {
#if ANDROID
        global::Android.Util.Log.Info(tag, line);
#else
        System.Diagnostics.Debug.WriteLine($"{tag}: {line}");
#endif
    }
}
