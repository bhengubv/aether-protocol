// SPDX-License-Identifier: MIT

using AetherMesh.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AetherMesh.DependencyInjection;

/// <summary>
/// Entry point for one-call host wiring of the Aether protocol stack.
/// </summary>
public static class AetherMeshProtocolServiceCollectionExtensions
{
    /// <summary>
    /// Register the root Aether configuration bag and return a fluent builder
    /// for opting into specific capabilities. Calling this with no further
    /// chained methods registers options only — no services. Hosts then chain
    /// <c>.AddSignalProtocol()</c>, <c>.AddRouting()</c>, <c>.AddMessaging()</c>,
    /// etc. as needed.
    ///
    /// Calling this method is idempotent: calling it twice on the same
    /// <see cref="IServiceCollection"/> does not double-register the options
    /// or the builder.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configure">
    /// Optional configuration callback. Bind from <c>IConfiguration</c> here
    /// or set values directly. If null, defaults are used.
    /// </param>
    /// <returns>A builder for chaining capability registrations.</returns>
    public static IAetherMeshProtocolBuilder AddAetherMeshProtocol(
        this IServiceCollection services,
        Action<AetherMeshOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<AetherMeshOptions>();
        if (configure is not null)
            optionsBuilder.Configure(configure);

        // ── Extensibility defaults ────────────────────────────────────────────
        // Each is registered with TryAdd so that hosts which install a real
        // implementation (CircleAI, SDPKT biometrics, etc.) before calling
        // AddAetherMeshProtocol() keep theirs unchanged. All Null* singletons are
        // allocation-free no-ops that satisfy the contract without any cost.

        // AI provider — route suggestion, transport biasing, threat assessment.
        services.TryAddSingleton<IAetherMeshAiProvider, NullAetherMeshAiProvider>();

        // Telemetry bus — fan-out publish/subscribe to all registered observers.
        // Built as a factory so it resolves any IAetherMeshTelemetryObserver instances
        // already registered in the container (CircleAI, BhenguAI, custom analytics).
        services.TryAddSingleton<IAetherMeshTelemetry>(sp =>
        {
            var bus = new AetherMeshTelemetryBus();
            foreach (var observer in sp.GetServices<IAetherMeshTelemetryObserver>())
                bus.Subscribe(observer);
            return bus;
        });

        // Context memory — semantic memory for AI-layer route/behaviour context.
        services.TryAddSingleton<IAetherMeshContextMemory, NullAetherMeshContextMemory>();

        // Biometric provider — device-native authentication gate.
        services.TryAddSingleton<IBiometricProvider, NullBiometricProvider>();

        // Security audit — static + runtime vulnerability scanning.
        services.TryAddSingleton<IAetherMeshSecurityAudit, NullAetherMeshSecurityAudit>();

        // Security directive consumer — receives hardened-mesh commands from the AI
        // security layer (block node, revoke key, isolate segment, etc.).
        services.TryAddSingleton<ISecurityDirectiveConsumer, NullSecurityDirectiveConsumer>();

        return new AetherMeshProtocolBuilder(services);
    }
}
