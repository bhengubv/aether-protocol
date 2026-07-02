// SPDX-License-Identifier: MIT

using AetherNet.DependencyInjection;
using AetherNet.Transport.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIPSorcery.Net;

namespace AetherNet.Transport.WebRtc;

/// <summary>
/// DI wiring for the WebRTC direct peer-to-peer transport. A host registers an
/// <see cref="IWebRtcSignaling"/> source (relay-backed for cross-device, or in-process) and the
/// transport itself, which then joins <c>TransportManager</c>'s additional-transport ladder ordered
/// by <see cref="ITransportService.PowerCostRelative"/>.
/// </summary>
public static class WebRtcServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RelayWebRtcSignaling"/> as the <see cref="IWebRtcSignaling"/>, carrying the
    /// SDP/ICE handshake over the already-registered transport
    /// <typeparamref name="TSignallingTransport"/> (typically the QUIC/HTTP relay).
    /// </summary>
    public static IServiceCollection AddRelayWebRtcSignaling<TSignallingTransport>(this IServiceCollection services)
        where TSignallingTransport : class, ITransportService
    {
        services.TryAddSingleton<IWebRtcSignaling>(sp =>
            new RelayWebRtcSignaling(
                sp.GetRequiredService<TSignallingTransport>(),
                sp.GetService<ILogger<RelayWebRtcSignaling>>()));
        return services;
    }

    /// <summary>
    /// Registers an in-process <see cref="IWebRtcSignaling"/> endpoint for <paramref name="localUhid"/>
    /// over a shared <see cref="InMemoryWebRtcSignalingBus"/> — for single-process / simulation hosts.
    /// </summary>
    public static IServiceCollection AddInMemoryWebRtcSignaling(this IServiceCollection services, string localUhid)
    {
        ArgumentException.ThrowIfNullOrEmpty(localUhid);
        services.TryAddSingleton<InMemoryWebRtcSignalingBus>();
        services.TryAddSingleton<IWebRtcSignaling>(sp =>
            sp.GetRequiredService<InMemoryWebRtcSignalingBus>().CreateEndpoint(localUhid));
        return services;
    }

    /// <summary>
    /// Registers the <see cref="WebRtcTransportService"/> — a direct P2P transport — as an
    /// <see cref="ITransportService"/> (so it joins the TransportManager additional-transport ladder)
    /// and as a concrete singleton. Requires an <see cref="IWebRtcSignaling"/> in the container; see
    /// <see cref="AddRelayWebRtcSignaling{T}"/> or <see cref="AddInMemoryWebRtcSignaling"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="localUhid">This node's UHID. When null, taken from <see cref="AetherNetOptions.LocalUhid"/>.</param>
    /// <param name="iceServers">
    /// ICE servers. Null uses the serverless default (NO ICE servers; host-candidate-only ICE, no
    /// STUN/TURN). Pass an explicit list to opt into STUN/TURN; an explicit empty list keeps
    /// host-candidate-only ICE.
    /// </param>
    public static IServiceCollection AddWebRtcTransport(
        this IServiceCollection services,
        string? localUhid = null,
        IReadOnlyList<RTCIceServer>? iceServers = null)
    {
        services.TryAddSingleton(sp =>
        {
            var uhid = localUhid
                ?? sp.GetService<IOptions<AetherNetOptions>>()?.Value.LocalUhid
                ?? throw new InvalidOperationException(
                    "AddWebRtcTransport: no localUhid supplied and AetherNetOptions.LocalUhid is not configured.");
            var signalling = sp.GetRequiredService<IWebRtcSignaling>();
            var logger = sp.GetService<ILogger<WebRtcTransportService>>();
            return new WebRtcTransportService(uhid, signalling, iceServers, logger);
        });

        // Additive (idempotent) ITransportService registration: GetServices<ITransportService>() — the
        // source TransportManager draws its additionalTransports from — now includes the WebRTC path.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITransportService, WebRtcTransportService>(
                sp => sp.GetRequiredService<WebRtcTransportService>()));

        return services;
    }

    /// <summary>
    /// Builder-chaining convenience for
    /// <see cref="AddWebRtcTransport(IServiceCollection,string?,IReadOnlyList{RTCIceServer}?)"/>.
    /// </summary>
    public static IAetherNetProtocolBuilder AddWebRtcTransport(
        this IAetherNetProtocolBuilder builder,
        string? localUhid = null,
        IReadOnlyList<RTCIceServer>? iceServers = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddWebRtcTransport(localUhid, iceServers);
        return builder;
    }
}
