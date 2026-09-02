// SPDX-License-Identifier: MIT

using AetherNet.Diagnostics;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AetherNet.DependencyInjection.HealthChecks;

/// <summary>
/// Reports messaging outbox depth as a liveness signal. Outbox growth
/// indicates the protocol can't deliver and the host should scale, scan
/// peer connectivity, or ack pending sessions.
///
/// Thresholds:
/// <list type="bullet">
///   <item>healthy — outbox below <see cref="MessagingOptionsBag.DegradedOutboxSize"/></item>
///   <item>degraded — outbox at or above the degraded size</item>
///   <item>unhealthy — outbox above the degraded size AND the previous
///     sample was below the current depth, i.e. the queue is growing</item>
/// </list>
///
/// The "growing" check is naive but cheap: each invocation of
/// <see cref="CheckHealthAsync"/> compares against the immediately-previous
/// snapshot. Hosts that need a richer trend signal should layer a custom
/// check on top of <see cref="IMessagingService.GetOutboxAsync"/>.
/// </summary>
public sealed class MessagingOutboxHealthCheck : IHealthCheck
{
    private readonly IMessagingService _messaging;
    private readonly MessagingOptionsBag _options;

    // Last-observed sample, retained across invocations to detect growth.
    // Volatile is sufficient — the health-check infrastructure serialises
    // checks per registration so we don't need a heavier lock.
    private long _previousDepth = -1;

    public MessagingOutboxHealthCheck(IMessagingService messaging, IOptions<AetherNetOptions> options)
    {
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
        _options = options?.Value?.Messaging ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Factory used by the builder when registering with the DI container.
    /// </summary>
    internal static MessagingOutboxHealthCheck Create(IServiceProvider sp)
    {
        var messaging = sp.GetRequiredService<IMessagingService>();
        var options = sp.GetRequiredService<IOptions<AetherNetOptions>>();
        return new MessagingOutboxHealthCheck(messaging, options);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // GetOutboxAsync surfaces only Pending/Sending entries when invoked
        // through the standard InMemoryMessageStore; tighter window suffices
        // for a coarse health signal.
        var outbox = await _messaging.GetOutboxAsync(limit: _options.DegradedOutboxSize * 2, cancellationToken).ConfigureAwait(false);
        var pending = outbox.Count(m => m.Status is MessageStatus.Pending or MessageStatus.Sending);

        var previousDepth = Interlocked.Exchange(ref _previousDepth, pending);

        var data = new Dictionary<string, object>
        {
            ["pendingOutboxDepth"] = pending,
            ["previousOutboxDepth"] = previousDepth,
            ["degradedThreshold"] = _options.DegradedOutboxSize,
        };

        // The degraded/unhealthy decision is the formal outbox-backpressure invariant: healthy iff the
        // queue is within its cap. Wired to MeshInvariants so the runtime monitor and the Petri-net
        // model (formal/outbox-backpressure) can't drift apart.
        var withinCap = MeshInvariants.OutboxBounded(pending, _options.DegradedOutboxSize);

        if (!withinCap && previousDepth >= 0 && pending > previousDepth)
        {
            return HealthCheckResult.Unhealthy(
                $"Messaging outbox growing: {previousDepth} -> {pending} (threshold {_options.DegradedOutboxSize}) — delivery is stuck.",
                data: data);
        }

        if (!withinCap)
        {
            return HealthCheckResult.Degraded(
                $"Messaging outbox at {pending} (threshold {_options.DegradedOutboxSize}) — investigate delivery.",
                data: data);
        }

        return HealthCheckResult.Healthy(
            $"Messaging outbox at {pending}.",
            data: data);
    }
}
