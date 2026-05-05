// SPDX-License-Identifier: MIT

using Aether.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Aether.DependencyInjection.HealthChecks;

/// <summary>
/// Reports the size of the in-memory routing table as a coarse-grained
/// liveness signal. A healthy mesh has a bounded route count; runaway
/// growth points to a routing-state leak (RREP forwarding loop, expired
/// routes not pruned, etc.).
///
/// Thresholds:
/// <list type="bullet">
///   <item>healthy — table size below <see cref="RoutingOptionsBag.DegradedTableSize"/></item>
///   <item>degraded — table size at or above <see cref="RoutingOptionsBag.DegradedTableSize"/></item>
///   <item>unhealthy — table size at or above <see cref="RoutingOptionsBag.UnhealthyTableSize"/></item>
/// </list>
/// </summary>
public sealed class RoutingHealthCheck : IHealthCheck
{
    private readonly IRoutingService _routing;
    private readonly RoutingOptionsBag _options;

    public RoutingHealthCheck(IRoutingService routing, IOptions<AetherOptions> options)
    {
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _options = options?.Value?.Routing ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Factory used by the builder when registering with the DI container —
    /// keeps the constructor signature decoupled from the registration site.
    /// </summary>
    internal static RoutingHealthCheck Create(IServiceProvider sp)
    {
        var routing = sp.GetRequiredService<IRoutingService>();
        var options = sp.GetRequiredService<IOptions<AetherOptions>>();
        return new RoutingHealthCheck(routing, options);
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var routes = _routing.GetAllRoutes();
        var count = routes.Count;
        var data = new Dictionary<string, object>
        {
            ["routeCount"] = count,
            ["degradedThreshold"] = _options.DegradedTableSize,
            ["unhealthyThreshold"] = _options.UnhealthyTableSize,
        };

        if (count >= _options.UnhealthyTableSize)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Routing table size {count} >= unhealthy threshold {_options.UnhealthyTableSize} — likely a routing-state leak.",
                data: data));
        }

        if (count >= _options.DegradedTableSize)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Routing table size {count} >= degraded threshold {_options.DegradedTableSize} — investigate growth.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Routing table size {count}.",
            data: data));
    }
}
