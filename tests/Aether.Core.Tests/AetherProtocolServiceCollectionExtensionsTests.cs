// SPDX-License-Identifier: MIT

using AetherMesh.Core.Tests.Fakes;
using AetherMesh.DependencyInjection;
using AetherMesh.Dtn;
using AetherMesh.Messaging;
using AetherMesh.Routing;
using AetherMesh.Security.Services;
using AetherMesh.Sos;
using AetherMesh.Transport.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AetherMesh.Core.Tests;

/// <summary>
/// Verifies the public surface of <c>services.AddAetherProtocol(...)</c>:
/// idempotent options registration, opt-in capability composition, full-stack
/// resolution end-to-end, dependency-graph guards (e.g. AddMessaging without
/// AddSignalProtocol fails fast), and configuration binding from
/// <see cref="IConfiguration"/>.
/// </summary>
public class AetherProtocolServiceCollectionExtensionsTests
{
    // ─── Empty registration ─────────────────────────────────────────

    [Fact]
    public void AddAetherProtocol_NoChainedCalls_RegistersOnlyOptions()
    {
        var services = new ServiceCollection();

        var builder = services.AddAetherProtocol();

        Assert.NotNull(builder);
        Assert.Same(services, builder.Services);

        using var sp = services.BuildServiceProvider();
        // Options bag resolves with defaults.
        var options = sp.GetRequiredService<IOptions<AetherOptions>>().Value;
        Assert.Equal("", options.LocalUhid);
        Assert.NotNull(options.Routing);
        Assert.NotNull(options.Dtn);
        Assert.NotNull(options.Signal);
        Assert.NotNull(options.Messaging);
        // No Aether services registered yet — these resolve as null.
        Assert.Null(sp.GetService<ISignalProtocolService>());
        Assert.Null(sp.GetService<IRoutingService>());
        Assert.Null(sp.GetService<IDtnService>());
        Assert.Null(sp.GetService<IMessagingService>());
    }

    // ─── Single-capability: Signal Protocol ─────────────────────────

    [Fact]
    public void AddAetherProtocol_AddSignalProtocol_ResolvesSignalAndPacketSigning()
    {
        var services = new ServiceCollection();
        services.AddAetherProtocol().AddSignalProtocol();

        using var sp = services.BuildServiceProvider();

        var signal = sp.GetService<ISignalProtocolService>();
        Assert.NotNull(signal);
        Assert.IsType<SignalProtocolService>(signal);
        Assert.False(signal!.HasSession("anyone"));

        var signing = sp.GetService<IPacketSigningService>();
        Assert.NotNull(signing);
        Assert.IsType<PacketSigningService>(signing);
    }

    // ─── Routing requires IMeshSender — host registers fake ─────────

    [Fact]
    public void AddAetherProtocol_AddRouting_ResolvesRoutingAgainstHostMeshSender()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMeshSender>(new FakeMeshSender("uhid:local"));
        services.AddAetherProtocol().AddRouting();

        using var sp = services.BuildServiceProvider();

        var routing = sp.GetService<IRoutingService>();
        Assert.NotNull(routing);
        Assert.IsType<RoutingService>(routing);
        Assert.Empty(routing!.GetAllRoutes());
    }

    // ─── DTN ────────────────────────────────────────────────────────

    [Fact]
    public void AddAetherProtocol_AddDtn_ResolvesDtnService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMeshSender>(new FakeMeshSender("uhid:local"));
        services.AddAetherProtocol().AddDtn();

        using var sp = services.BuildServiceProvider();

        var dtn = sp.GetService<IDtnService>();
        Assert.NotNull(dtn);
        Assert.IsType<DtnService>(dtn);
    }

    // ─── SOS ────────────────────────────────────────────────────────

    [Fact]
    public void AddAetherProtocol_AddSosBroadcast_ResolvesSosService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMeshSender>(new FakeMeshSender("uhid:local"));
        services.AddAetherProtocol().AddSosBroadcast();

        using var sp = services.BuildServiceProvider();

        var sos = sp.GetService<ISosBroadcastService>();
        Assert.NotNull(sos);
        Assert.IsType<SosBroadcastService>(sos);
    }

    // ─── Full chain — Messaging end-to-end resolution ───────────────

    [Fact]
    public void AddAetherProtocol_FullChain_ResolvesMessagingService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMeshSender>(new FakeMeshSender("uhid:local"));

        services.AddAetherProtocol(opts => opts.LocalUhid = "uhid:local")
                .AddSignalProtocol()
                .AddRouting()
                .AddDtn()
                .AddMessaging();

        using var sp = services.BuildServiceProvider();

        var messaging = sp.GetService<IMessagingService>();
        Assert.NotNull(messaging);
        Assert.IsType<MessagingService>(messaging);

        // Cipher resolved as Signal-backed envelope cipher.
        var cipher = sp.GetService<IMessageEnvelopeCipher>();
        Assert.NotNull(cipher);
        Assert.IsType<SignalMessageEnvelopeCipher>(cipher);

        // Default in-memory message store wired.
        var store = sp.GetService<IMessageStore>();
        Assert.NotNull(store);
        Assert.IsType<InMemoryMessageStore>(store);
    }

    // ─── Two providers in same test → independent services ──────────

    [Fact]
    public void AddAetherProtocol_TwoProviders_ResolveIndependentInstances()
    {
        ServiceProvider Build(string uhid)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IMeshSender>(new FakeMeshSender(uhid));
            services.AddAetherProtocol(opts => opts.LocalUhid = uhid)
                    .AddSignalProtocol()
                    .AddRouting();
            return services.BuildServiceProvider();
        }

        using var spAlice = Build("uhid:alice");
        using var spBob = Build("uhid:bob");

        var aliceSignal = spAlice.GetRequiredService<ISignalProtocolService>();
        var bobSignal = spBob.GetRequiredService<ISignalProtocolService>();

        Assert.NotSame(aliceSignal, bobSignal);

        var aliceSender = spAlice.GetRequiredService<IMeshSender>();
        var bobSender = spBob.GetRequiredService<IMeshSender>();
        Assert.Equal("uhid:alice", aliceSender.LocalUhid);
        Assert.Equal("uhid:bob", bobSender.LocalUhid);
    }

    // ─── AetherOptions binding from IConfiguration ──────────────────

    [Fact]
    public void AddAetherProtocol_BindsFromConfiguration()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Aether:LocalUhid"] = "uhid:configured",
            ["Aether:Routing:DegradedTableSize"] = "5000",
            ["Aether:Routing:UnhealthyTableSize"] = "20000",
            ["Aether:Dtn:MaxBundles"] = "200",
            ["Aether:Dtn:DegradedFraction"] = "0.5",
            ["Aether:Signal:DegradedSessionCount"] = "750",
            ["Aether:Signal:MinAvailableOpks"] = "20",
            ["Aether:Messaging:DegradedOutboxSize"] = "250",
            ["Aether:Messaging:MaxRetries"] = "5",
            ["Aether:Messaging:EnableDtnFallback"] = "false",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddAetherProtocol(opts => configuration.GetSection("Aether").Bind(opts));

        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<AetherOptions>>().Value;
        Assert.Equal("uhid:configured", options.LocalUhid);
        Assert.Equal(5000, options.Routing.DegradedTableSize);
        Assert.Equal(20000, options.Routing.UnhealthyTableSize);
        Assert.Equal(200, options.Dtn.MaxBundles);
        Assert.Equal(0.5, options.Dtn.DegradedFraction);
        Assert.Equal(750, options.Signal.DegradedSessionCount);
        Assert.Equal(20, options.Signal.MinAvailableOpks);
        Assert.Equal(250, options.Messaging.DegradedOutboxSize);
        Assert.Equal(5, options.Messaging.MaxRetries);
        Assert.False(options.Messaging.EnableDtnFallback);
    }

    // ─── Missing dependency: AddMessaging without AddSignalProtocol ─

    [Fact]
    public void AddAetherProtocol_AddMessagingWithoutSignal_ThrowsClearException()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMeshSender>(new FakeMeshSender("uhid:local"));
        var builder = services.AddAetherProtocol().AddRouting();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddMessaging());
        Assert.Contains("AddSignalProtocol", ex.Message);
    }

    [Fact]
    public void AddAetherProtocol_AddMessagingWithoutRouting_ThrowsClearException()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMeshSender>(new FakeMeshSender("uhid:local"));
        var builder = services.AddAetherProtocol().AddSignalProtocol();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddMessaging());
        Assert.Contains("AddRouting", ex.Message);
    }

    // ─── In-process transport ───────────────────────────────────────

    [Fact]
    public void AddAetherProtocol_AddInProcessTransport_RegistersMeshSender()
    {
        var services = new ServiceCollection();
        InProcessTransportService.ResetNetwork();

        services.AddAetherProtocol(opts => opts.LocalUhid = "uhid:transport-test")
                .AddInProcessTransport("uhid:transport-test");

        using var sp = services.BuildServiceProvider();

        var sender = sp.GetService<IMeshSender>();
        Assert.NotNull(sender);
        Assert.Equal("uhid:transport-test", sender!.LocalUhid);

        var transport = sp.GetService<InProcessTransportService>();
        Assert.NotNull(transport);

        // Cleanup — disposing the transport unregisters from the static network.
        sp.GetRequiredService<InProcessTransportService>().Dispose();
        InProcessTransportService.ResetNetwork();
    }

    // ─── Idempotent registration ────────────────────────────────────

    [Fact]
    public void AddAetherProtocol_DoubleAddSignalProtocol_RegistersOnce()
    {
        var services = new ServiceCollection();
        services.AddAetherProtocol()
                .AddSignalProtocol()
                .AddSignalProtocol();

        using var sp = services.BuildServiceProvider();

        var first = sp.GetRequiredService<ISignalProtocolService>();
        var second = sp.GetRequiredService<ISignalProtocolService>();
        Assert.Same(first, second);
    }

    // ─── Builder returns same Services collection ───────────────────

    [Fact]
    public void AddAetherProtocol_BuilderServicesIsSameCollection()
    {
        var services = new ServiceCollection();
        var builder = services.AddAetherProtocol();
        Assert.Same(services, builder.Services);
    }
}
