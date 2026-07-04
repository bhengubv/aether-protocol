// SPDX-License-Identifier: MIT

using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Security.Services;

/// <summary>
/// Production <see cref="IRouteReplyVerifier"/>: accepts an RREP only if it carries a valid
/// Ed25519 signature produced by the node it claims to originate from.
///
/// <para>This closes the RREP-hijack hole. An AODV forward route is installed straight from an
/// RREP's <c>SourceUhid</c>; without a signature check, any intermediate forwarder can forge an
/// RREP for the destination and blackhole / man-in-the-middle the victim's traffic. Here we
/// resolve the claimed source's public key and verify the signature over the exact same canonical
/// bytes the source signed (<see cref="PacketSigningService.BuildSignableData(MeshPacket)"/>), so a
/// forged or unsigned RREP fails and no route is installed.</para>
///
/// <para><b>Fail-closed at every branch:</b> a missing signature, an unresolvable / unknown
/// source key, or a signature that does not verify all return <see langword="false"/>. Only a
/// signature that validates against a known key is accepted.</para>
///
/// <para>Replay / freshness (nonce dedup, timestamp window) is NOT duplicated here — that is
/// already enforced by <see cref="PacketSigningService"/> in the packet-ingest pipeline. This
/// verifier is purely the source-identity gate the routing layer needs before trusting a route
/// reply.</para>
/// </summary>
public sealed class Ed25519RouteReplyVerifier : IRouteReplyVerifier
{
    private readonly IRouteReplyKeyResolver _keyResolver;
    private readonly ISignalProtocolService _signalProtocol;
    private readonly ILogger<Ed25519RouteReplyVerifier> _logger;

    /// <summary>
    /// Creates the verifier.
    /// </summary>
    /// <param name="keyResolver">Resolves an RREP source UHID to its Ed25519 public key. A null
    /// result (unknown signer) causes the RREP to be rejected.</param>
    /// <param name="signalProtocol">Provides the Ed25519 <c>VerifySignature</c> primitive.</param>
    /// <param name="logger">Optional logger; a null logger is used when omitted.</param>
    public Ed25519RouteReplyVerifier(
        IRouteReplyKeyResolver keyResolver,
        ISignalProtocolService signalProtocol,
        ILogger<Ed25519RouteReplyVerifier>? logger = null)
    {
        _keyResolver = keyResolver ?? throw new ArgumentNullException(nameof(keyResolver));
        _signalProtocol = signalProtocol ?? throw new ArgumentNullException(nameof(signalProtocol));
        _logger = logger ?? NullLogger<Ed25519RouteReplyVerifier>.Instance;
    }

    /// <inheritdoc />
    public Task<bool> VerifyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routeReply);

        // No signature → cannot be trusted. (MeshPacket.Signature defaults to an empty array.)
        if (routeReply.Signature is null || routeReply.Signature.Length == 0)
        {
            _logger.LogWarning("RREP from {Source} rejected: unsigned", routeReply.SourceUhid);
            return Task.FromResult(false);
        }

        // Resolve the claimed source's public key. Unknown signer → reject (fail-closed):
        // an unresolvable key can never produce a signature we would accept.
        var publicKey = _keyResolver.ResolvePublicKey(routeReply.SourceUhid);
        if (publicKey is null || publicKey.Length == 0)
        {
            _logger.LogWarning("RREP from {Source} rejected: source public key unknown", routeReply.SourceUhid);
            return Task.FromResult(false);
        }

        // Verify the Ed25519 signature over the canonical signable bytes — the SAME layout the
        // source signed and every other language implementation shares.
        var signableData = PacketSigningService.BuildSignableData(routeReply);
        var valid = _signalProtocol.VerifySignature(publicKey, signableData, routeReply.Signature);

        if (!valid)
            _logger.LogWarning("RREP from {Source} rejected: invalid signature", routeReply.SourceUhid);

        return Task.FromResult(valid);
    }
}
