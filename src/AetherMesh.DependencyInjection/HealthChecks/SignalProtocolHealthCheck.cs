// SPDX-License-Identifier: MIT

using System.Collections;
using System.Reflection;
using AetherMesh.Security.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AetherMesh.DependencyInjection.HealthChecks;

/// <summary>
/// Reports Signal Protocol identity health. Two distinct signals:
/// <list type="bullet">
///   <item>
///     <strong>OPK pool floor</strong> — if available one-time pre-keys
///     drop below <see cref="SignalOptionsBag.MinAvailableOpks"/>, responder
///     X3DH starts failing for new initiators. Reported as
///     <see cref="HealthStatus.Unhealthy"/>.
///   </item>
///   <item>
///     <strong>Session count ceiling</strong> — if active sessions exceed
///     <see cref="SignalOptionsBag.DegradedSessionCount"/> the host probably
///     has a session leak (sessions not torn down on peer eviction).
///     Reported as <see cref="HealthStatus.Degraded"/>. Session count is
///     read reflectively from the concrete <c>SignalProtocolService</c>'s
///     internal session map; if reflection fails (subclassed implementation,
///     trimmed assembly, etc.) the session-count signal is omitted and only
///     the OPK floor drives the result.
///   </item>
/// </list>
/// </summary>
public sealed class SignalProtocolHealthCheck : IHealthCheck
{
    private static readonly FieldInfo? SessionsField = typeof(SignalProtocolService)
        .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly ISignalProtocolService _signal;
    private readonly SignalOptionsBag _options;

    public SignalProtocolHealthCheck(ISignalProtocolService signal, IOptions<AetherMeshOptions> options)
    {
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _options = options?.Value?.Signal ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Factory used by the builder when registering with the DI container.
    /// </summary>
    internal static SignalProtocolHealthCheck Create(IServiceProvider sp)
    {
        var signal = sp.GetRequiredService<ISignalProtocolService>();
        var options = sp.GetRequiredService<IOptions<AetherMeshOptions>>();
        return new SignalProtocolHealthCheck(signal, options);
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();

        // OPK pool floor — only available on the concrete SignalProtocolService.
        var availableOpks = -1;
        var heldOpks = -1;
        if (_signal is SignalProtocolService concrete)
        {
            availableOpks = concrete.AvailableOneTimePreKeyCount;
            heldOpks = concrete.HeldOneTimePreKeyCount;
            data["availableOpkCount"] = availableOpks;
            data["heldOpkCount"] = heldOpks;
            data["opkPoolSize"] = concrete.OpkPoolSize;
        }
        data["minAvailableOpks"] = _options.MinAvailableOpks;

        // Session count via reflection — best-effort, gracefully omitted on failure.
        var sessionCount = TryReadSessionCount(_signal);
        if (sessionCount.HasValue)
        {
            data["activeSessionCount"] = sessionCount.Value;
            data["degradedSessionCount"] = _options.DegradedSessionCount;
        }

        // Unhealthy: OPK floor breached.
        if (availableOpks >= 0 && availableOpks < _options.MinAvailableOpks)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"OPK pool low: {availableOpks} available < min {_options.MinAvailableOpks} — responder X3DH will fail.",
                data: data));
        }

        // Degraded: session count ceiling.
        if (sessionCount.HasValue && sessionCount.Value > _options.DegradedSessionCount)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Active sessions {sessionCount.Value} > degraded threshold {_options.DegradedSessionCount} — possible session leak.",
                data: data));
        }

        var description = availableOpks >= 0
            ? $"Signal healthy: {availableOpks} OPKs available, {sessionCount?.ToString() ?? "?"} active sessions."
            : "Signal healthy.";
        return Task.FromResult(HealthCheckResult.Healthy(description, data: data));
    }

    /// <summary>
    /// Attempts to read the active session count from the concrete
    /// <see cref="SignalProtocolService"/> instance via reflection. Returns
    /// null if the field can't be located or the value isn't enumerable.
    ///
    /// Reflection is read-only and never modifies the service — this stays
    /// within the "do not modify existing service" constraint.
    /// </summary>
    private static int? TryReadSessionCount(ISignalProtocolService signal)
    {
        if (SessionsField is null) return null;
        if (signal is not SignalProtocolService concrete) return null;

        try
        {
            var value = SessionsField.GetValue(concrete);
            if (value is ICollection collection) return collection.Count;
            return null;
        }
        catch
        {
            return null;
        }
    }
}
