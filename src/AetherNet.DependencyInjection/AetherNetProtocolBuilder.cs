// SPDX-License-Identifier: MIT

using AetherNet.Content;
using AetherNet.Dtn;
using AetherNet.Forge;
using AetherNet.Market;
using AetherNet.Space;
using AetherNet.Vault;
using AetherNet.Extensibility;
using AetherNet.Handshake;
using AetherNet.Messaging;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Reputation;
using AetherNet.Routing;
using AetherNet.Security.Services;
using AetherNet.Sos;
using AetherNet.Streaming;
using AetherNet.Transport.Services;
using AetherNet.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherNet.DependencyInjection;

/// <summary>
/// Sealed implementation of <see cref="IAetherNetProtocolBuilder"/>. Tracks which
/// capabilities have been added so that dependency-checking calls (e.g.
/// <see cref="AddMessaging"/> requiring <see cref="AddSignalProtocol"/> and
/// <see cref="AddRouting"/>) can fail fast at registration time rather than
/// at first resolution.
/// </summary>
internal sealed class AetherNetProtocolBuilder : IAetherNetProtocolBuilder
{
    private bool _signalAdded;
    private bool _routingAdded;
    private bool _dtnAdded;
    private bool _sosAdded;
    private bool _messagingAdded;
    private bool _transportAdded;
    private bool _handshakeAdded;
    private bool _reputationAdded;
    private bool _anomalyDetectorAdded;
    private bool _gossipAdded;
    private bool _streamingAdded;
    private bool _watchTogetherAdded;
    private bool _videoCallAdded;
    private bool _groupVideoAdded;
    private bool _voiceAdded;
    private bool _groupVoiceAdded;
    private bool _contentAdded;
    private bool _directoryAdded;
    private bool _spaceAdded;
    private bool _forgeAdded;
    private bool _vaultAdded;
    private bool _marketAdded;

    public AetherNetProtocolBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public IServiceCollection Services { get; }

    public IAetherNetProtocolBuilder AddSignalProtocol()
    {
        if (_signalAdded) return this;
        _signalAdded = true;

        Services.TryAddSingleton<ISignalProtocolService>(sp =>
        {
            var logger = sp.GetService<ILogger<SignalProtocolService>>()
                ?? NullLogger<SignalProtocolService>.Instance;
            return new SignalProtocolService(logger);
        });

        // PacketSigningService takes ISignalProtocolService directly.
        Services.TryAddSingleton<IPacketSigningService>(sp =>
        {
            var signal = sp.GetRequiredService<ISignalProtocolService>();
            var logger = sp.GetService<ILogger<PacketSigningService>>()
                ?? NullLogger<PacketSigningService>.Instance;
            var reputation = sp.GetService<INodeReputationService>();
            return new PacketSigningService(signal, logger, reputation);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddRouting()
    {
        if (_routingAdded) return this;
        _routingAdded = true;

        // Default in-memory store unless host has registered something else first.
        Services.TryAddSingleton<IRouteStore, InMemoryRouteStore>();

        Services.TryAddSingleton<IRoutingService>(sp =>
        {
            var sender     = sp.GetRequiredService<IMeshSender>();
            var store      = sp.GetService<IRouteStore>();
            var verifier   = sp.GetService<IRouteReplyVerifier>();
            var reputation = sp.GetService<INodeReputationService>();
            var logger     = sp.GetService<ILogger<RoutingService>>()
                             ?? NullLogger<RoutingService>.Instance;
            var telemetry  = sp.GetService<IAetherNetTelemetry>();
            return new RoutingService(sender, store, verifier, incentives: null,
                reputation: reputation, logger: logger, telemetry: telemetry);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddDtn()
    {
        if (_dtnAdded) return this;
        _dtnAdded = true;

        Services.TryAddSingleton<IDtnBundleStore, InMemoryDtnBundleStore>();

        Services.TryAddSingleton<IDtnService>(sp =>
        {
            var sender = sp.GetRequiredService<IMeshSender>();
            var store = sp.GetService<IDtnBundleStore>();
            var reputation = sp.GetService<INodeReputationService>();
            var logger = sp.GetService<ILogger<DtnService>>()
                ?? NullLogger<DtnService>.Instance;
            return new DtnService(sender, store, strategy: null, incentives: null, backend: null, reputation: reputation, logger: logger);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddSosBroadcast()
    {
        if (_sosAdded) return this;
        _sosAdded = true;

        Services.TryAddSingleton<ISosBroadcastService>(sp =>
        {
            var sender = sp.GetRequiredService<IMeshSender>();
            var logger = sp.GetService<ILogger<SosBroadcastService>>()
                ?? NullLogger<SosBroadcastService>.Instance;
            return new SosBroadcastService(sender, backend: null, incentives: null, logger: logger);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddMessaging()
    {
        if (!_signalAdded)
        {
            throw new InvalidOperationException(
                "AddMessaging() requires AddSignalProtocol() to have been called first. " +
                "MessagingService composes a Signal-backed envelope cipher (SignalMessageEnvelopeCipher) " +
                "and that cipher needs ISignalProtocolService in the container.");
        }
        if (!_routingAdded)
        {
            throw new InvalidOperationException(
                "AddMessaging() requires AddRouting() to have been called first. " +
                "MessagingService consumes IRoutingService for next-hop discovery.");
        }
        if (_messagingAdded) return this;
        _messagingAdded = true;

        // Default in-memory store unless host has registered something else first.
        Services.TryAddSingleton<IMessageStore, InMemoryMessageStore>();

        // Default cipher: SignalMessageEnvelopeCipher backed by the Signal service.
        Services.TryAddSingleton<IMessageEnvelopeCipher>(sp =>
        {
            var signal = sp.GetRequiredService<ISignalProtocolService>();
            var logger = sp.GetService<ILogger<SignalMessageEnvelopeCipher>>()
                ?? NullLogger<SignalMessageEnvelopeCipher>.Instance;
            return new SignalMessageEnvelopeCipher(signal, logger);
        });

        Services.TryAddSingleton<IMessagingService>(sp =>
        {
            var sender = sp.GetRequiredService<IMeshSender>();
            var routing = sp.GetRequiredService<IRoutingService>();
            var store = sp.GetService<IMessageStore>();
            var cipher = sp.GetService<IMessageEnvelopeCipher>();
            var dtn = sp.GetService<IDtnService>();
            var optionsBag = sp.GetRequiredService<IOptions<AetherNetOptions>>().Value.Messaging;
            var logger = sp.GetService<ILogger<MessagingService>>()
                ?? NullLogger<MessagingService>.Instance;

            var messagingOptions = new MessagingOptions
            {
                MaxRetries = optionsBag.MaxRetries,
                EnableDtnFallback = optionsBag.EnableDtnFallback,
                EnableBackendRelay = optionsBag.EnableBackendRelay,
                SendDeliveryAcks = optionsBag.SendDeliveryAcks,
            };

            return new MessagingService(
                sender: sender,
                routing: routing,
                store: store,
                cipher: cipher,
                dtn: dtn,
                backend: null,
                incentives: null,
                options: messagingOptions,
                logger: logger);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddInProcessTransport(string localUhid)
    {
        ArgumentException.ThrowIfNullOrEmpty(localUhid);

        if (_transportAdded) return this;
        _transportAdded = true;

        // Singleton InProcessTransportService scoped to this localUhid. The
        // simulated network is process-static, so this works for tests and
        // demos without further plumbing.
        Services.TryAddSingleton(sp =>
        {
            var logger = sp.GetService<ILogger<InProcessTransportService>>()
                ?? NullLogger<InProcessTransportService>.Instance;
            return new InProcessTransportService(localUhid, logger);
        });

        // IMeshSender adapter — bridges packet-level routing/DTN/messaging
        // to byte-level InProcessTransportService.
        Services.TryAddSingleton<IMeshSender>(sp =>
        {
            var transport = sp.GetRequiredService<InProcessTransportService>();
            return new InProcessMeshSenderAdapter(localUhid, transport);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddHealthChecks()
    {
        // Register the four protocol-level checks. The host is expected to
        // have called services.AddHealthChecks() beforehand (which adds
        // HealthCheckService); we register HealthCheckRegistration entries
        // that the standard pipeline picks up.
        Services.AddSingleton(sp => HealthChecks.RoutingHealthCheck.Create(sp));
        Services.AddSingleton(sp => HealthChecks.DtnHealthCheck.Create(sp));
        Services.AddSingleton(sp => HealthChecks.SignalProtocolHealthCheck.Create(sp));
        Services.AddSingleton(sp => HealthChecks.MessagingOutboxHealthCheck.Create(sp));

        Services.AddSingleton<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration>(
            sp => new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
                "aether-routing",
                sp.GetRequiredService<HealthChecks.RoutingHealthCheck>(),
                failureStatus: null,
                tags: new[] { "aether", "routing" }));

        Services.AddSingleton<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration>(
            sp => new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
                "aether-dtn",
                sp.GetRequiredService<HealthChecks.DtnHealthCheck>(),
                failureStatus: null,
                tags: new[] { "aether", "dtn" }));

        Services.AddSingleton<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration>(
            sp => new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
                "aether-signal",
                sp.GetRequiredService<HealthChecks.SignalProtocolHealthCheck>(),
                failureStatus: null,
                tags: new[] { "aether", "signal" }));

        Services.AddSingleton<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration>(
            sp => new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
                "aether-messaging-outbox",
                sp.GetRequiredService<HealthChecks.MessagingOutboxHealthCheck>(),
                failureStatus: null,
                tags: new[] { "aether", "messaging" }));

        return this;
    }

    public IAetherNetProtocolBuilder AddHandshake()
    {
        if (_handshakeAdded) return this;
        _handshakeAdded = true;

        Services.TryAddSingleton<IHandshakeService>(sp =>
        {
            var sender    = sp.GetRequiredService<IMeshSender>();
            var logger    = sp.GetService<ILogger<HandshakeService>>()
                            ?? NullLogger<HandshakeService>.Instance;
            var telemetry = sp.GetService<IAetherNetTelemetry>();
            return new HandshakeService(sender, logger, telemetry: telemetry);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddReputation()
    {
        if (_reputationAdded) return this;
        _reputationAdded = true;

        Services.TryAddSingleton<INodeReputationService, InMemoryNodeReputationService>();

        return this;
    }

    public IAetherNetProtocolBuilder AddAnomalyDetector(Action<AnomalyDetectorOptions>? configure = null)
    {
        if (!_reputationAdded)
        {
            throw new InvalidOperationException(
                "AddAnomalyDetector() requires AddReputation() to have been called first. " +
                "BehavioralAnomalyDetector feeds directly into INodeReputationService.");
        }
        if (_anomalyDetectorAdded) return this;
        _anomalyDetectorAdded = true;

        Services.TryAddSingleton<IAnomalyDetector>(sp =>
        {
            var reputation = sp.GetRequiredService<INodeReputationService>();
            var opts = new AnomalyDetectorOptions();
            configure?.Invoke(opts);
            return new BehavioralAnomalyDetector(reputation, opts);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddGossip()
    {
        if (!_reputationAdded)
        {
            throw new InvalidOperationException(
                "AddGossip() requires AddReputation() to have been called first. " +
                "ReputationGossipService reads and writes INodeReputationService.");
        }
        if (!_signalAdded)
        {
            throw new InvalidOperationException(
                "AddGossip() requires AddSignalProtocol() to have been called first. " +
                "ReputationGossipService signs outbound gossip packets via IPacketSigningService.");
        }
        if (_gossipAdded) return this;
        _gossipAdded = true;

        Services.TryAddSingleton<IReputationGossipService>(sp =>
        {
            var sender = sp.GetRequiredService<IMeshSender>();
            var signing = sp.GetRequiredService<IPacketSigningService>();
            var reputation = sp.GetRequiredService<INodeReputationService>();
            var logger = sp.GetService<ILogger<ReputationGossipService>>()
                ?? NullLogger<ReputationGossipService>.Instance;
            return new ReputationGossipService(sender, signing, reputation, logger);
        });

        return this;
    }

    // ── Media layer ───────────────────────────────────────────────────────────

    public IAetherNetProtocolBuilder AddStreaming()
    {
        if (!_routingAdded)
            throw new InvalidOperationException(
                "AddStreaming() requires AddRouting() to have been called first. " +
                "StreamingService consumes both IMeshSender and IRoutingService.");
        if (_streamingAdded) return this;
        _streamingAdded = true;

        Services.TryAddSingleton<IStreamingService>(sp =>
        {
            var sender    = sp.GetRequiredService<IMeshSender>();
            var routing   = sp.GetRequiredService<IRoutingService>();
            var incentives = sp.GetService<IAetherNetIncentiveProvider>();
            var logger    = sp.GetService<ILogger<StreamingService>>()
                            ?? NullLogger<StreamingService>.Instance;
            return new StreamingService(sender, routing, incentives, logger);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddWatchTogether()
    {
        if (!_routingAdded)
            throw new InvalidOperationException(
                "AddWatchTogether() requires AddRouting() to have been called first.");
        if (_watchTogetherAdded) return this;
        _watchTogetherAdded = true;

        Services.TryAddSingleton<IWatchTogetherService>(sp =>
        {
            var sender    = sp.GetRequiredService<IMeshSender>();
            var routing   = sp.GetRequiredService<IRoutingService>();
            var incentives = sp.GetService<IAetherNetIncentiveProvider>();
            var logger    = sp.GetService<ILogger<WatchTogetherService>>()
                            ?? NullLogger<WatchTogetherService>.Instance;
            return new WatchTogetherService(sender, routing, incentives, logger);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddVideoCall()
    {
        if (!_routingAdded)
            throw new InvalidOperationException(
                "AddVideoCall() requires AddRouting() to have been called first.");
        if (_videoCallAdded) return this;
        _videoCallAdded = true;

        Services.TryAddSingleton<IVideoCallService>(sp =>
        {
            var sender    = sp.GetRequiredService<IMeshSender>();
            var routing   = sp.GetRequiredService<IRoutingService>();
            var incentives = sp.GetService<IAetherNetIncentiveProvider>();
            var logger    = sp.GetService<ILogger<VideoCallService>>()
                            ?? NullLogger<VideoCallService>.Instance;
            return new VideoCallService(sender, routing, incentives, logger);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddGroupVideo()
    {
        if (!_routingAdded)
            throw new InvalidOperationException(
                "AddGroupVideo() requires AddRouting() to have been called first.");
        if (_groupVideoAdded) return this;
        _groupVideoAdded = true;

        Services.TryAddSingleton<IGroupVideoService>(sp =>
        {
            var sender    = sp.GetRequiredService<IMeshSender>();
            var routing   = sp.GetRequiredService<IRoutingService>();
            var incentives = sp.GetService<IAetherNetIncentiveProvider>();
            var logger    = sp.GetService<ILogger<GroupVideoService>>()
                            ?? NullLogger<GroupVideoService>.Instance;
            return new GroupVideoService(sender, routing, incentives, logger);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddVoice()
    {
        if (!_routingAdded)
            throw new InvalidOperationException(
                "AddVoice() requires AddRouting() to have been called first.");
        if (_voiceAdded) return this;
        _voiceAdded = true;

        Services.TryAddSingleton<IVoiceCallService>(sp =>
        {
            var sender    = sp.GetRequiredService<IMeshSender>();
            var routing   = sp.GetRequiredService<IRoutingService>();
            var incentives = sp.GetService<IAetherNetIncentiveProvider>();
            var logger    = sp.GetService<ILogger<VoiceCallService>>()
                            ?? NullLogger<VoiceCallService>.Instance;
            return new VoiceCallService(sender, routing, incentives, logger);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddGroupVoice()
    {
        if (!_routingAdded)
            throw new InvalidOperationException(
                "AddGroupVoice() requires AddRouting() to have been called first.");
        if (_groupVoiceAdded) return this;
        _groupVoiceAdded = true;

        Services.TryAddSingleton<IGroupVoiceCallService>(sp =>
        {
            var sender    = sp.GetRequiredService<IMeshSender>();
            var routing   = sp.GetRequiredService<IRoutingService>();
            var keys      = sp.GetService<IGroupKeyProvider>();
            var incentives = sp.GetService<IAetherNetIncentiveProvider>();
            var logger    = sp.GetService<ILogger<GroupVoiceCallService>>()
                            ?? NullLogger<GroupVoiceCallService>.Instance;
            return new GroupVoiceCallService(sender, routing, keys, incentives, logger);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddContent()
    {
        if (!_routingAdded)
            throw new InvalidOperationException(
                "AddContent() requires AddRouting() to have been called first.");
        if (_contentAdded) return this;
        _contentAdded = true;

        Services.TryAddSingleton<IContentStore, InMemoryContentStore>();

        Services.TryAddSingleton<IContentService>(sp =>
        {
            var sender     = sp.GetRequiredService<IMeshSender>();
            var routing    = sp.GetRequiredService<IRoutingService>();
            var store      = sp.GetService<IContentStore>();
            var incentives = sp.GetService<IAetherNetIncentiveProvider>();
            var logger     = sp.GetService<ILogger<ContentService>>()
                             ?? NullLogger<ContentService>.Instance;
            var telemetry  = sp.GetService<IAetherNetTelemetry>();
            return new ContentService(sender, routing, store, incentives, logger, telemetry);
        });

        return this;
    }

    public IAetherNetProtocolBuilder AddDirectory()
    {
        if (!_routingAdded)
            throw new InvalidOperationException(
                "AddDirectory() requires AddRouting() to have been called first.");
        if (_directoryAdded) return this;
        _directoryAdded = true;

        Services.TryAddSingleton<IDirectoryService>(sp =>
        {
            var sender = sp.GetRequiredService<IMeshSender>();
            var logger = sp.GetService<ILogger<DirectoryService>>()
                         ?? NullLogger<DirectoryService>.Instance;
            return new DirectoryService(sender, logger);
        });

        return this;
    }

    // ── Phase-2 Extensions ────────────────────────────────────────────────────

    public IAetherNetProtocolBuilder AddSpace()
    {
        if (!_contentAdded)
            throw new InvalidOperationException(
                "AddSpace() requires AddContent() to have been called first. " +
                "ISpaceService addresses payloads by IContentService hash.");
        if (!_dtnAdded)
            throw new InvalidOperationException(
                "AddSpace() requires AddDtn() to have been called first. " +
                "Breadcrumb propagation relies on DTN store-and-forward for offline delivery.");
        if (_spaceAdded) return this;
        _spaceAdded = true;

        Services.TryAddSingleton<ISpaceService, InMemorySpaceService>();

        return this;
    }

    public IAetherNetProtocolBuilder AddForge()
    {
        if (!_contentAdded)
            throw new InvalidOperationException(
                "AddForge() requires AddContent() to have been called first. " +
                "IForgeService addresses payloads by IContentService content hash.");
        if (_forgeAdded) return this;
        _forgeAdded = true;

        Services.TryAddSingleton<IForgeService, InMemoryForgeService>();

        return this;
    }

    public IAetherNetProtocolBuilder AddVault()
    {
        if (!_contentAdded)
            throw new InvalidOperationException(
                "AddVault() requires AddContent() to have been called first. " +
                "IVaultService addresses shard payloads by IContentService content hash.");
        if (_vaultAdded) return this;
        _vaultAdded = true;

        Services.TryAddSingleton<IVaultService, InMemoryVaultService>();

        return this;
    }

    public IAetherNetProtocolBuilder AddMarket()
    {
        if (!_spaceAdded)
            throw new InvalidOperationException(
                "AddMarket() requires AddSpace() to have been called first. " +
                "IMarketService distributes geo-pinned listings via ISpaceService.");
        if (!_vaultAdded)
            throw new InvalidOperationException(
                "AddMarket() requires AddVault() to have been called first. " +
                "IMarketService uses IVaultService for document escrow on trades.");
        if (_marketAdded) return this;
        _marketAdded = true;

        Services.TryAddSingleton<IPoVService, InMemoryPoVService>();
        Services.TryAddSingleton<IMarketService, InMemoryMarketService>();

        return this;
    }

    // ── Extensibility ─────────────────────────────────────────────────────────

    public IAetherNetProtocolBuilder AddTelemetry<T>() where T : class, IAetherNetTelemetryObserver
    {
        // Not TryAdd — multiple observers are valid and additive.
        Services.AddSingleton<IAetherNetTelemetryObserver, T>();
        return this;
    }

    public IAetherNetProtocolBuilder AddTelemetry(IAetherNetTelemetryObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        Services.AddSingleton(observer);
        return this;
    }

    public IAetherNetProtocolBuilder AddCircleAI<T>() where T : class, IAetherNetAiProvider
    {
        Services.Replace(ServiceDescriptor.Singleton<IAetherNetAiProvider, T>());
        return this;
    }

    public IAetherNetProtocolBuilder AddCircleAI(IAetherNetAiProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Services.Replace(ServiceDescriptor.Singleton(typeof(IAetherNetAiProvider), provider));
        return this;
    }

    public IAetherNetProtocolBuilder AddBiometrics<T>() where T : class, IBiometricProvider
    {
        Services.Replace(ServiceDescriptor.Singleton<IBiometricProvider, T>());
        return this;
    }

    public IAetherNetProtocolBuilder AddBiometrics(IBiometricProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Services.Replace(ServiceDescriptor.Singleton(typeof(IBiometricProvider), provider));
        return this;
    }

    public IAetherNetProtocolBuilder AddContextMemory<T>() where T : class, IAetherNetContextMemory
    {
        Services.Replace(ServiceDescriptor.Singleton<IAetherNetContextMemory, T>());
        return this;
    }

    public IAetherNetProtocolBuilder AddContextMemory(IAetherNetContextMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        Services.Replace(ServiceDescriptor.Singleton(typeof(IAetherNetContextMemory), memory));
        return this;
    }

    public IAetherNetProtocolBuilder AddSecurityAudit<T>() where T : class, IAetherNetSecurityAudit
    {
        Services.Replace(ServiceDescriptor.Singleton<IAetherNetSecurityAudit, T>());
        return this;
    }

    public IAetherNetProtocolBuilder AddSecurityAudit(IAetherNetSecurityAudit auditor)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        Services.Replace(ServiceDescriptor.Singleton(typeof(IAetherNetSecurityAudit), auditor));
        return this;
    }
}

/// <summary>
/// Bridges <see cref="IMeshSender"/> (used by routing/DTN/SOS/messaging) to
/// <see cref="InProcessTransportService"/> (byte-level). Mirrors the helper
/// in the bundled console demo. Intended for tests and demos only — not for
/// production hosting.
/// </summary>
internal sealed class InProcessMeshSenderAdapter : IMeshSender
{
    private readonly InProcessTransportService _transport;
    private readonly HashSet<string> _potentialPeers = new(StringComparer.Ordinal);

    public InProcessMeshSenderAdapter(string localUhid, InProcessTransportService transport)
    {
        LocalUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public string LocalUhid { get; }
    public string? LocalGeohash => null;

    /// <summary>Register a peer we may want to reach over the in-process network.</summary>
    public void AddPotentialPeer(string uhid) => _potentialPeers.Add(uhid);

    public IReadOnlyList<PeerInfo> GetConnectedPeers()
    {
        var alive = new List<PeerInfo>();
        foreach (var uhid in _potentialPeers)
        {
            if (_transport.IsConnected(uhid))
                alive.Add(new PeerInfo { Uhid = uhid, TransportType = "InProcess" });
        }
        return alive;
    }

    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
    {
        var bytes = PacketSerializer.Serialize(packet);
        return _transport.SendAsync(nextHopUhid, bytes, cancellationToken);
    }

    public async Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        var bytes = PacketSerializer.Serialize(packet);
        var delivered = 0;
        foreach (var uhid in _potentialPeers)
        {
            if (await _transport.SendAsync(uhid, bytes, cancellationToken).ConfigureAwait(false))
                delivered++;
        }
        return delivered;
    }
}
