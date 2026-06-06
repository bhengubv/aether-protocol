// SPDX-License-Identifier: MIT

using AetherNet.Core.Tests.Fakes;
using AetherNet.DependencyInjection;
using AetherNet.DependencyInjection.HealthChecks;
using AetherNet.Dtn;
using AetherNet.Models;
using AetherNet.Routing;
using AetherNet.Security.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Verifies the four protocol-level <see cref="IHealthCheck"/> implementations
/// behave as documented across the Healthy/Degraded/Unhealthy transitions, and
/// that <c>builder.AddHealthChecks()</c> registers them in the standard
/// health-check pipeline.
/// </summary>
public class HealthCheckTests
{
    private static IOptions<AetherNetOptions> Options(Action<AetherNetOptions>? configure = null)
    {
        var opts = new AetherNetOptions();
        configure?.Invoke(opts);
        return Microsoft.Extensions.Options.Options.Create(opts);
    }

    // ─── RoutingHealthCheck ─────────────────────────────────────────

    [Fact]
    public async Task RoutingHealthCheck_EmptyTable_ReportsHealthy()
    {
        var sender = new FakeMeshSender("uhid:local");
        var routing = new RoutingService(sender);
        var check = new RoutingHealthCheck(routing, Options());

        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("aether-routing", check, null, null) });

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("routeCount", result.Data.Keys);
        Assert.Equal(0, result.Data["routeCount"]);
    }

    [Fact]
    public async Task RoutingHealthCheck_DegradedThresholdExceeded_ReportsDegraded()
    {
        // Use a low threshold so we don't have to actually create thousands of routes.
        var options = Options(o =>
        {
            o.Routing.DegradedTableSize = 2;
            o.Routing.UnhealthyTableSize = 100;
        });
        var routing = new RouteListBackedFakeRouting(routeCount: 5);
        var check = new RoutingHealthCheck(routing, options);

        var result = await check.CheckHealthAsync(NewContext("aether-routing", check));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(5, result.Data["routeCount"]);
    }

    [Fact]
    public async Task RoutingHealthCheck_UnhealthyThresholdExceeded_ReportsUnhealthy()
    {
        var options = Options(o =>
        {
            o.Routing.DegradedTableSize = 1;
            o.Routing.UnhealthyTableSize = 3;
        });
        var routing = new RouteListBackedFakeRouting(routeCount: 5);
        var check = new RoutingHealthCheck(routing, options);

        var result = await check.CheckHealthAsync(NewContext("aether-routing", check));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(5, result.Data["routeCount"]);
    }

    // ─── DtnHealthCheck ─────────────────────────────────────────────

    [Fact]
    public async Task DtnHealthCheck_BelowDegradedThreshold_ReportsHealthy()
    {
        var options = Options(o =>
        {
            o.Dtn.MaxBundles = 10;
            o.Dtn.DegradedFraction = 0.8;
        });
        var dtn = new BundleCountFakeDtn(activeCount: 0);
        var check = new DtnHealthCheck(dtn, options);

        var result = await check.CheckHealthAsync(NewContext("aether-dtn", check));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(0, result.Data["activeBundles"]);
    }

    [Fact]
    public async Task DtnHealthCheck_AboveDegradedThreshold_ReportsDegraded()
    {
        // 10 max * 0.8 = 8 — eight bundles trips the degraded threshold.
        var options = Options(o =>
        {
            o.Dtn.MaxBundles = 10;
            o.Dtn.DegradedFraction = 0.8;
        });
        var dtn = new BundleCountFakeDtn(activeCount: 8);
        var check = new DtnHealthCheck(dtn, options);

        var result = await check.CheckHealthAsync(NewContext("aether-dtn", check));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(8, result.Data["activeBundles"]);
        Assert.Equal(8, result.Data["degradedThreshold"]);
    }

    [Fact]
    public async Task DtnHealthCheck_AtMaxBundles_ReportsUnhealthy()
    {
        var options = Options(o =>
        {
            o.Dtn.MaxBundles = 10;
            o.Dtn.DegradedFraction = 0.8;
        });
        var dtn = new BundleCountFakeDtn(activeCount: 10);
        var check = new DtnHealthCheck(dtn, options);

        var result = await check.CheckHealthAsync(NewContext("aether-dtn", check));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(10, result.Data["activeBundles"]);
    }

    // ─── SignalProtocolHealthCheck ──────────────────────────────────

    [Fact]
    public async Task SignalProtocolHealthCheck_FreshService_ReportsHealthy()
    {
        var signal = new SignalProtocolService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SignalProtocolService>.Instance);
        // Generate a bundle so the OPK pool is populated above the floor.
        await signal.GeneratePreKeyBundleAsync("uhid:local");

        var check = new SignalProtocolHealthCheck(signal, Options(o => o.Signal.MinAvailableOpks = 10));
        var result = await check.CheckHealthAsync(NewContext("aether-signal", check));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("availableOpkCount", result.Data.Keys);
        var available = (int)result.Data["availableOpkCount"];
        Assert.True(available >= 10, $"expected >= 10 available OPKs, got {available}");
    }

    [Fact]
    public async Task SignalProtocolHealthCheck_OpkPoolBelowFloor_ReportsUnhealthy()
    {
        // Create the service with a tiny pool size so the available count
        // starts well below the configured floor — easier than draining a
        // 100-key pool.
        var signal = new SignalProtocolService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SignalProtocolService>.Instance,
            opkPoolSize: 5);
        await signal.GeneratePreKeyBundleAsync("uhid:local");

        var check = new SignalProtocolHealthCheck(signal, Options(o => o.Signal.MinAvailableOpks = 100));
        var result = await check.CheckHealthAsync(NewContext("aether-signal", check));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("OPK pool low", result.Description);
    }

    // ─── MessagingOutboxHealthCheck ─────────────────────────────────

    [Fact]
    public async Task MessagingOutboxHealthCheck_EmptyOutbox_ReportsHealthy()
    {
        var messaging = new OutboxBackedFakeMessaging(pending: 0);
        var check = new MessagingOutboxHealthCheck(messaging,
            Options(o => o.Messaging.DegradedOutboxSize = 10));

        var result = await check.CheckHealthAsync(NewContext("aether-messaging-outbox", check));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(0, result.Data["pendingOutboxDepth"]);
    }

    [Fact]
    public async Task MessagingOutboxHealthCheck_AboveThresholdSteady_ReportsDegraded()
    {
        var messaging = new OutboxBackedFakeMessaging(pending: 20);
        var check = new MessagingOutboxHealthCheck(messaging,
            Options(o => o.Messaging.DegradedOutboxSize = 10));

        var result = await check.CheckHealthAsync(NewContext("aether-messaging-outbox", check));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(20, result.Data["pendingOutboxDepth"]);
    }

    [Fact]
    public async Task MessagingOutboxHealthCheck_GrowingAboveThreshold_ReportsUnhealthy()
    {
        var messaging = new OutboxBackedFakeMessaging(pending: 15);
        var check = new MessagingOutboxHealthCheck(messaging,
            Options(o => o.Messaging.DegradedOutboxSize = 10));

        // First sample establishes baseline (seed 15 -> 15, so degraded but not growing).
        var first = await check.CheckHealthAsync(NewContext("aether-messaging-outbox", check));
        Assert.Equal(HealthStatus.Degraded, first.Status);

        // Second sample: queue grew -> unhealthy.
        messaging.SetPending(25);
        var second = await check.CheckHealthAsync(NewContext("aether-messaging-outbox", check));

        Assert.Equal(HealthStatus.Unhealthy, second.Status);
        Assert.Equal(25, second.Data["pendingOutboxDepth"]);
        Assert.Equal((long)15, second.Data["previousOutboxDepth"]);
    }

    // ─── End-to-end: builder.AddHealthChecks() wires all four ───────

    [Fact]
    public async Task AddHealthChecks_RegistersAllFourChecks()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMeshSender>(new FakeMeshSender("uhid:local"));
        services.AddHealthChecks();
        services.AddAetherNetProtocol(opts => opts.LocalUhid = "uhid:local")
                .AddSignalProtocol()
                .AddRouting()
                .AddDtn()
                .AddMessaging()
                .AddHealthChecks();

        using var sp = services.BuildServiceProvider();

        // The four singleton instances are registered.
        Assert.NotNull(sp.GetService<RoutingHealthCheck>());
        Assert.NotNull(sp.GetService<DtnHealthCheck>());
        Assert.NotNull(sp.GetService<SignalProtocolHealthCheck>());
        Assert.NotNull(sp.GetService<MessagingOutboxHealthCheck>());

        // And the four HealthCheckRegistrations are present (one per check).
        var registrations = sp.GetServices<HealthCheckRegistration>().ToArray();
        var names = registrations.Select(r => r.Name).ToHashSet();
        Assert.Contains("aether-routing", names);
        Assert.Contains("aether-dtn", names);
        Assert.Contains("aether-signal", names);
        Assert.Contains("aether-messaging-outbox", names);

        // Each check is invokable end-to-end.
        foreach (var registration in registrations.Where(r => r.Name.StartsWith("aether-")))
        {
            var ctx = new HealthCheckContext { Registration = registration };
            var result = await registration.Factory(sp).CheckHealthAsync(ctx);
            Assert.True(result.Status is HealthStatus.Healthy or HealthStatus.Degraded or HealthStatus.Unhealthy);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static HealthCheckContext NewContext(string name, IHealthCheck check)
    {
        return new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(name, check, failureStatus: null, tags: null),
        };
    }

    /// <summary>
    /// Minimal <see cref="IRoutingService"/> that returns N synthetic routes —
    /// purpose-built for the routing-health-check threshold tests.
    /// </summary>
    private sealed class RouteListBackedFakeRouting : IRoutingService
    {
        private readonly List<RouteEntry> _routes;

        public RouteListBackedFakeRouting(int routeCount)
        {
            _routes = Enumerable.Range(0, routeCount).Select(i => new RouteEntry
            {
                DestinationUhid = $"uhid:peer-{i}",
                NextHopUhid = $"uhid:peer-{i}",
                HopCount = 1,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            }).ToList();
        }

        public Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default)
            => Task.FromResult<RouteEntry?>(null);
        public RouteEntry? GetCachedRoute(string destinationUhid) => null;
        public IReadOnlyList<RouteEntry> GetAllRoutes() => _routes;
        public Task HandleRouteRequestAsync(AetherNet.Protocol.MeshPacket routeRequest, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HandleRouteReplyAsync(AetherNet.Protocol.MeshPacket routeReply, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PruneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Stub <see cref="IDtnService"/> exposing a configurable bundle count.</summary>
    private sealed class BundleCountFakeDtn : IDtnService
    {
        private readonly DtnBundle[] _bundles;

        public BundleCountFakeDtn(int activeCount)
        {
            _bundles = Enumerable.Range(0, activeCount).Select(i => new DtnBundle
            {
                SenderUhid = "uhid:sender",
                RecipientUhid = $"uhid:rcpt-{i}",
                EncryptedPayload = Array.Empty<byte>(),
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            }).ToArray();
        }

#pragma warning disable CS0067 // event-stub for interface contract
        public event EventHandler<DtnDeliveryReceipt>? BundleDelivered;
#pragma warning restore CS0067
        public Task<DtnBundle> CreateBundleAsync(string recipientUhid, byte[] encryptedPayload, BundlePriority priority = BundlePriority.Normal, string? recipientLastGeohash = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task HandleAsync(AetherNet.Protocol.MeshPacket packet, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RunDeliveryScanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<IReadOnlyList<DtnBundle>> GetActiveBundlesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DtnBundle>>(_bundles);
    }

    /// <summary>Stub <see cref="AetherNet.Messaging.IMessagingService"/> exposing a configurable outbox depth.</summary>
    private sealed class OutboxBackedFakeMessaging : AetherNet.Messaging.IMessagingService
    {
        private int _pending;

        public OutboxBackedFakeMessaging(int pending) => _pending = pending;

        public void SetPending(int pending) => _pending = pending;

#pragma warning disable CS0067 // event-stubs for interface contract
        public event EventHandler<AetherNet.Messaging.Models.MeshMessage>? MessageReceived;
        public event EventHandler<AetherNet.Messaging.Models.DeliveryReceipt>? DeliveryConfirmed;
        public event EventHandler<string>? SessionRequired;
#pragma warning restore CS0067

        public Task<bool> SendAsync(AetherNet.Messaging.Models.MeshMessage message, byte[] plaintext, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task HandleAsync(AetherNet.Protocol.MeshPacket packet, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> ProcessOutboxAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<IReadOnlyList<AetherNet.Messaging.Models.MeshMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AetherNet.Messaging.Models.MeshMessage>>(Array.Empty<AetherNet.Messaging.Models.MeshMessage>());

        public Task<IReadOnlyList<AetherNet.Messaging.Models.MeshMessage>> GetOutboxAsync(int limit = 50, CancellationToken cancellationToken = default)
        {
            var pending = Enumerable.Range(0, _pending).Select(i => new AetherNet.Messaging.Models.MeshMessage
            {
                Status = AetherNet.Messaging.Models.MessageStatus.Pending,
            }).ToArray();
            return Task.FromResult<IReadOnlyList<AetherNet.Messaging.Models.MeshMessage>>(pending);
        }
    }
}
