// SPDX-License-Identifier: MIT

using Aether.Protocol;

namespace Aether.Routing;

/// <summary>
/// Verifies that a received RREP was actually signed by the node it claims to come from.
/// Without this check an intermediate forwarder can forge an RREP and hijack traffic
/// for the destination. Hosts that ship a real implementation (typically backed by
/// <c>Aether.Security</c>) supply one; the default is permissive — all RREPs accepted.
/// </summary>
public interface IRouteReplyVerifier
{
    /// <summary>
    /// Returns true if <paramref name="routeReply"/> is acceptable. The default
    /// implementation accepts every RREP — fine for tests and trust-the-fabric demos,
    /// not fine for production.
    /// </summary>
    Task<bool> VerifyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}

/// <summary>
/// Permissive default: all RREPs are accepted.
/// </summary>
public sealed class AcceptAllRouteReplyVerifier : IRouteReplyVerifier
{
}
