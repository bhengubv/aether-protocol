// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Aether.DependencyInjection;

/// <summary>
/// Fluent builder returned by
/// <see cref="AetherProtocolServiceCollectionExtensions.AddAetherProtocol"/>.
/// Each capability is opt-in: hosts that need only routing get only routing,
/// hosts that need the full stack chain everything together.
///
/// Canonical full-stack wiring:
/// <code>
/// services.AddAetherProtocol(opts =&gt; opts.LocalUhid = "aether:alice:01")
///         .AddSignalProtocol()
///         .AddRouting()
///         .AddDtn()
///         .AddSosBroadcast()
///         .AddMessaging()
///         .AddInProcessTransport("aether:alice:01")
///         .AddHealthChecks();
/// </code>
/// </summary>
public interface IAetherProtocolBuilder
{
    /// <summary>The underlying service collection — exposed so adopters can register their own seam implementations.</summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Register <c>SignalProtocolService</c> (and the <c>PacketSigningService</c>
    /// that depends on it) as singletons. Once added, hosts can resolve
    /// <c>ISignalProtocolService</c> and <c>IPacketSigningService</c>.
    /// </summary>
    IAetherProtocolBuilder AddSignalProtocol();

    /// <summary>
    /// Register <c>RoutingService</c> as a singleton. Requires the host to
    /// have registered an <c>IMeshSender</c> implementation already (the
    /// transport adapter). The default <c>InMemoryRouteStore</c> is used
    /// unless the host has registered an <c>IRouteStore</c>.
    /// </summary>
    IAetherProtocolBuilder AddRouting();

    /// <summary>
    /// Register <c>DtnService</c> as a singleton. Requires <c>IMeshSender</c>;
    /// uses <c>InMemoryDtnBundleStore</c> by default.
    /// </summary>
    IAetherProtocolBuilder AddDtn();

    /// <summary>
    /// Register <c>SosBroadcastService</c> as a singleton. Requires <c>IMeshSender</c>.
    /// </summary>
    IAetherProtocolBuilder AddSosBroadcast();

    /// <summary>
    /// Register <c>MessagingService</c> + <c>SignalMessageEnvelopeCipher</c>
    /// as singletons. Requires both <see cref="AddSignalProtocol"/> and
    /// <see cref="AddRouting"/> to have been called first; throws
    /// <see cref="InvalidOperationException"/> otherwise (with a clear message).
    /// </summary>
    IAetherProtocolBuilder AddMessaging();

    /// <summary>
    /// Register the in-process transport adapter for the given local UHID.
    /// Wires <c>IMeshSender</c> via a thin transport-bridge so routing/DTN/messaging
    /// can run end-to-end without an external transport. Suitable for tests
    /// and demos; not for production. The transport adapter is registered as
    /// a singleton.
    /// </summary>
    IAetherProtocolBuilder AddInProcessTransport(string localUhid);

    /// <summary>
    /// Register the four protocol-level <c>IHealthCheck</c> implementations
    /// (routing, DTN, Signal, messaging-outbox) with the host's
    /// <c>IHealthChecksBuilder</c>. The host must have already called
    /// <c>services.AddHealthChecks()</c> before reaching this method;
    /// otherwise the registrations are a no-op.
    /// </summary>
    IAetherProtocolBuilder AddHealthChecks();

    /// <summary>
    /// Register <c>HandshakeService</c> as a singleton. Requires <c>IMeshSender</c>.
    /// Wires the protocol-version + capability negotiation entry point that
    /// runs on first contact with each peer (<c>PacketType.Hello</c> /
    /// <c>PacketType.HelloAck</c>). Once added, hosts can resolve
    /// <c>IHandshakeService</c>.
    /// </summary>
    IAetherProtocolBuilder AddHandshake();
}
