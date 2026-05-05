// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Aether.DependencyInjection;

/// <summary>
/// Entry point for one-call host wiring of the Aether protocol stack.
/// </summary>
public static class AetherProtocolServiceCollectionExtensions
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
    public static IAetherProtocolBuilder AddAetherProtocol(
        this IServiceCollection services,
        Action<AetherOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<AetherOptions>();
        if (configure is not null)
            optionsBuilder.Configure(configure);

        return new AetherProtocolBuilder(services);
    }
}
