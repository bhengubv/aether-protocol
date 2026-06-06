// SPDX-License-Identifier: MIT

using AetherNet.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AetherNet.DependencyInjection;

/// <summary>
/// Entry point for one-call host wiring of the Aether protocol stack.
/// </summary>
public static class AetherNetProtocolServiceCollectionExtensions
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
    public static IAetherNetProtocolBuilder AddAetherNetProtocol(
        this IServiceCollection services,
        Action<AetherNetOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<AetherNetOptions>();
        if (configure is not null)
            optionsBuilder.Configure(configure);

        // ── Extensibility defaults ────────────────────────────────────────────
        // Each is registered with TryAdd so that hosts which install a real
        // implementation (CircleAI, SDPKT biometrics, etc.) before calling
        // AddAetherNetProtocol() keep theirs unchanged. All Null* singletons are
        // allocation-free no-ops that satisfy the contract without any cost.

        // AI provider — route suggestion, transport biasing, threat assessment.
        services.TryAddSingleton<IAetherNetAiProvider, NullAetherNetAiProvider>();

        // Telemetry bus — fan-out publish/subscribe to all registered observers.
        // Built as a factory so it resolves any IAetherNetTelemetryObserver instances
        // already registered in the container (CircleAI, BhenguAI, custom analytics).
        services.TryAddSingleton<IAetherNetTelemetry>(sp =>
        {
            var bus = new AetherNetTelemetryBus();
            foreach (var observer in sp.GetServices<IAetherNetTelemetryObserver>())
                bus.Subscribe(observer);
            return bus;
        });

        // Context memory — semantic memory for AI-layer route/behaviour context.
        services.TryAddSingleton<IAetherNetContextMemory, NullAetherNetContextMemory>();

        // Biometric provider — device-native authentication gate.
        services.TryAddSingleton<IBiometricProvider, NullBiometricProvider>();

        // Security audit — static + runtime vulnerability scanning.
        services.TryAddSingleton<IAetherNetSecurityAudit, NullAetherNetSecurityAudit>();

        // Security directive consumer — receives hardened-mesh commands from the AI
        // security layer (block node, revoke key, isolate segment, etc.).
        services.TryAddSingleton<ISecurityDirectiveConsumer, NullSecurityDirectiveConsumer>();

        return new AetherNetProtocolBuilder(services);
    }
}
