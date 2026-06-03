// SPDX-License-Identifier: MIT

using Aether.Content;
using Aether.Dtn;
using Aether.Space;
using Aether.Extensibility;
using Aether.Handshake;
using Aether.Messaging;
using Aether.Models;
using Aether.Protocol;
using Aether.Reputation;
using Aether.Routing;
using Aether.Security.Services;
using Aether.Sos;
using Aether.Streaming;
using Aether.Transport.Services;
using Aether.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aether.DependencyInjection;

/// <summary>
/// Sealed implementation of <see cref="IAetherProtocolBuilder"/>. Tracks which
/// capabilities have been added so that dependency-checking calls (e.g.
/// <see cref="AddMessaging"/> requiring <see cref="AddSignalProtocol"/> and
/// <see cref="AddRouting"/>) can fail fast at registration time rather than
/// at first resolution.
/// </summary>
internal sealed class AetherProtocolBuilder : IAetherProtocolBuilder
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
    private bool _spaceAdded;

    public AetherProtocolBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public IServiceCollection Services { get; }

    public IAetherProtocolBuilder AddSignalProtocol()
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

    public IAetherProtocolBuilder AddRouting()
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
            var telemetry  = sp.GetService<IAetherTelemetry>();
            return new RoutingService(sender, store, verifier, incentives: null,
                reputation: reputation, logger: logger, telemetry: telemetry);
        });

        return this;
    }

    public IAetherProtocolBuilder AddDtn()
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

    public IAetherProtocolBuilder AddSosBroadcast()
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

    public IAetherProtocolBuilder AddMessaging()
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
            var optionsBag = sp.GetRequiredService<IOptions<AetherOptions>>().Value.Messaging;
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

    public IAetherProtocolBuilder AddInProcessTransport(string localUhid)
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

    public IAetherProtocolBuilder AddHealthChecks()
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

    public IAetherProtocolBuilder AddHandshake()
    {
        if (_handshakeAdded) return this;
        _handshakeAdded = true;

        Services.TryAddSingleton<IHandshakeService>(sp =>
        {
            var sender    = sp.GetRequiredService<IMeshSender>();
            var logger    = sp.GetService<ILogger<HandshakeService>>()
                            ?? NullLogger<HandshakeService>.Instance;
            var telemetry = sp.GetService<IAetherTelemetry>();
            return new HandshakeService(sender, logger, telemetry: telemetry);
        });

        return this;
    }

    public IAetherProtocolBuilder AddReputation()
    {
        if (_reputationAdded) return this;
        _reputationAdded = true;

        Services.TryAddSingleton<INodeReputationService, InMemoryNodeReputationService>();

        return this;
    }

    public IAetherProtocolBuilder AddAnomalyDetector(Action<AnomalyDetectorOptions>? configure = null)
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

    public IAetherProtocolBuilder AddGossip()
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

    public IAetherProtocolBuilder AddStreaming()
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
            var incentives = sp.GetService<IAetherIncentiveProvider>();
            var logger    = sp.GetService<ILogger<StreamingService>>()
                            ?? NullLogger<StreamingService>.Instance;
            return new StreamingService(sender, routing, incentives, logger);
        });

        return this;
    }

    public IAetherProtocolBuilder AddWatchTogether()
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
            var incentives = sp.GetService<IAetherIncentiveProvider>();
            var logger    = sp.GetService<ILogger<WatchTogetherService>>()
                            ?? NullLogger<WatchTogetherService>.Instance;
            return new WatchTogetherService(sender, routing, incentives, logger);
        });

        return this;
    }

    public IAetherProtocolBuilder AddVideoCall()
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
            var incentives = sp.GetService<IAetherIncentiveProvider>();
            var logger    = sp.GetService<ILogger<VideoCallService>>()
                            ?? NullLogger<VideoCallService>.Instance;
            return new VideoCallService(sender, routing, incentives, logger);
        });

        return this;
    }

    public IAetherProtocolBuilder AddGroupVideo()
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
            var incentives = sp.GetService<IAetherIncentiveProvider>();
            var logger    = sp.GetService<ILogger<GroupVideoService>>()
                            ?? NullLogger<GroupVideoService>.Instance;
            return new GroupVideoService(sender, routing, incentives, logger);
        });

        return this;
    }

    public IAetherProtocolBuilder AddVoice()
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
            var incentives = sp.GetService<IAetherIncentiveProvider>();
            var logger    = sp.GetService<ILogger<VoiceCallService>>()
                            ?? NullLogger<VoiceCallService>.Instance;
            return new VoiceCallService(sender, routing, incentives, logger);
        });

        return this;
    }

    public IAetherProtocolBuilder AddGroupVoice()
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
            var incentives = sp.GetService<IAetherIncentiveProvider>();
            var logger    = sp.GetService<ILogger<GroupVoiceCallService>>()
                            ?? NullLogger<GroupVoiceCallService>.Instance;
            return new GroupVoiceCallService(sender, routing, keys, incentives, logger);
        });

        return this;
    }

    public IAetherProtocolBuilder AddContent()
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
            var incentives = sp.GetService<IAetherIncentiveProvider>();
            var logger     = sp.GetService<ILogger<ContentService>>()
                             ?? NullLogger<ContentService>.Instance;
            var telemetry  = sp.GetService<IAetherTelemetry>();
            return new ContentService(sender, routing, store, incentives, logger, telemetry);
        });

        return this;
    }

    // ── Phase-2 Extensions ────────────────────────────────────────────────────

    public IAetherProtocolBuilder AddSpace()
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

    // ── Extensibility ─────────────────────────────────────────────────────────

    public IAetherProtocolBuilder AddTelemetry<T>() where T : class, IAetherTelemetryObserver
    {
        // Not TryAdd — multiple observers are valid and additive.
        Services.AddSingleton<IAetherTelemetryObserver, T>();
        return this;
    }

    public IAetherProtocolBuilder AddTelemetry(IAetherTelemetryObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        Services.AddSingleton(observer);
        return this;
    }

    public IAetherProtocolBuilder AddCircleAI<T>() where T : class, IAetherAiProvider
    {
        Services.Replace(ServiceDescriptor.Singleton<IAetherAiProvider, T>());
        return this;
    }

    public IAetherProtocolBuilder AddCircleAI(IAetherAiProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Services.Replace(ServiceDescriptor.Singleton(typeof(IAetherAiProvider), provider));
        return this;
    }

    public IAetherProtocolBuilder AddBiometrics<T>() where T : class, IBiometricProvider
    {
        Services.Replace(ServiceDescriptor.Singleton<IBiometricProvider, T>());
        return this;
    }

    public IAetherProtocolBuilder AddBiometrics(IBiometricProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Services.Replace(ServiceDescriptor.Singleton(typeof(IBiometricProvider), provider));
        return this;
    }

    public IAetherProtocolBuilder AddContextMemory<T>() where T : class, IAetherContextMemory
    {
        Services.Replace(ServiceDescriptor.Singleton<IAetherContextMemory, T>());
        return this;
    }

    public IAetherProtocolBuilder AddContextMemory(IAetherContextMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        Services.Replace(ServiceDescriptor.Singleton(typeof(IAetherContextMemory), memory));
        return this;
    }

    public IAetherProtocolBuilder AddSecurityAudit<T>() where T : class, IAetherSecurityAudit
    {
        Services.Replace(ServiceDescriptor.Singleton<IAetherSecurityAudit, T>());
        return this;
    }

    public IAetherProtocolBuilder AddSecurityAudit(IAetherSecurityAudit auditor)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        Services.Replace(ServiceDescriptor.Singleton(typeof(IAetherSecurityAudit), auditor));
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
