// SPDX-License-Identifier: MIT

using AetherNet.Transport.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AetherNet.Transport.Relay;

/// <summary>
/// DI registration for the portable Aether Purple relay transports (HTTP and QUIC / HTTP-3).
/// Both use only <c>System.Net.*</c>, so they are cross-platform (Windows / macOS / Linux) and
/// live in the portable <c>AetherNet.Transport</c> package rather than any platform-specific one.
///
/// <para>
/// Registration is granular and explicit: the consuming app wires a relay — with its own server
/// URL — according to its own settings. Whether the relay is active at runtime is a decision the
/// app owns; the SDK only guarantees the transport builds and runs on every desktop OS.
/// </para>
/// </summary>
public static class RelayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the HTTP relay transport (Aether Purple) as an <see cref="ITransportService"/> so it
    /// joins the transport ladder. <paramref name="baseUrl"/> is the relay server (e.g.
    /// <c>"https://relay.example.com"</c>); <paramref name="localNodeId"/> is this node's UHID.
    /// </summary>
    public static IServiceCollection AddHttpRelay(
        this IServiceCollection services, string baseUrl, string localNodeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(localNodeId);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITransportService>(sp =>
            new HttpRelayTransportService(
                baseUrl, localNodeId, sp.GetService<ILogger<HttpRelayTransportService>>())));
        return services;
    }

    /// <summary>
    /// Registers the QUIC (HTTP/3) relay transport for <paramref name="baseUrl"/>. The transport
    /// self-gates on <see cref="QuicRelayTransportService.IsSupported"/>, so a host can register it
    /// unconditionally and it stays inert where MsQuic is unavailable.
    /// </summary>
    public static IServiceCollection AddQuicRelay(
        this IServiceCollection services, string baseUrl, string localNodeId,
        bool skipCertificateValidation = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(localNodeId);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITransportService>(sp =>
            new QuicRelayTransportService(
                baseUrl, localNodeId, skipCertificateValidation,
                sp.GetService<ILogger<QuicRelayTransportService>>())));
        return services;
    }
}
