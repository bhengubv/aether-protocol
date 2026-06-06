// SPDX-License-Identifier: MIT

using AetherNet.Dtn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AetherNet.DependencyInjection.HealthChecks;

/// <summary>
/// Reports DTN bundle-store occupancy as a liveness signal. A healthy DTN
/// node has plenty of free custody slots; once the store fills, custody
/// requests start being refused (see
/// <c>DtnService.HandleAsync</c> capacity check).
///
/// Thresholds:
/// <list type="bullet">
///   <item>healthy — bundle count below <see cref="DtnOptionsBag.DegradedFraction"/> of <see cref="DtnOptionsBag.MaxBundles"/></item>
///   <item>degraded — bundle count at or above the degraded fraction</item>
///   <item>unhealthy — bundle count at or above <see cref="DtnOptionsBag.MaxBundles"/></item>
/// </list>
/// </summary>
public sealed class DtnHealthCheck : IHealthCheck
{
    private readonly IDtnService _dtn;
    private readonly DtnOptionsBag _options;

    public DtnHealthCheck(IDtnService dtn, IOptions<AetherNetOptions> options)
    {
        _dtn = dtn ?? throw new ArgumentNullException(nameof(dtn));
        _options = options?.Value?.Dtn ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Factory used by the builder when registering with the DI container.
    /// </summary>
    internal static DtnHealthCheck Create(IServiceProvider sp)
    {
        var dtn = sp.GetRequiredService<IDtnService>();
        var options = sp.GetRequiredService<IOptions<AetherNetOptions>>();
        return new DtnHealthCheck(dtn, options);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var active = await _dtn.GetActiveBundlesAsync(cancellationToken).ConfigureAwait(false);
        var count = active.Count;
        var degradedThreshold = (int)Math.Ceiling(_options.MaxBundles * _options.DegradedFraction);

        var data = new Dictionary<string, object>
        {
            ["activeBundles"] = count,
            ["maxBundles"] = _options.MaxBundles,
            ["degradedThreshold"] = degradedThreshold,
        };

        if (count >= _options.MaxBundles)
        {
            return HealthCheckResult.Unhealthy(
                $"DTN store full: {count}/{_options.MaxBundles} bundles — new custody requests will be refused.",
                data: data);
        }

        if (count >= degradedThreshold)
        {
            return HealthCheckResult.Degraded(
                $"DTN store at {count}/{_options.MaxBundles} ({_options.DegradedFraction:P0}) — approaching capacity.",
                data: data);
        }

        return HealthCheckResult.Healthy(
            $"DTN store at {count}/{_options.MaxBundles}.",
            data: data);
    }
}
