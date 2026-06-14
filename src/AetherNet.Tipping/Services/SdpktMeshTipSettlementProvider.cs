// SPDX-License-Identifier: MIT

using AetherNet.ApiClients;
using AetherNet.Extensibility;
using AetherNet.Incentive;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;

namespace AetherNet.Tipping.Services;

/// <summary>
/// The SDPKT host implementation of the protocol-level mesh-tip settlement hook
/// <see cref="IAetherNetIncentiveProvider.SettleMeshTipAsync"/>. This is the seam the
/// generic <see cref="IMeshTipService"/> calls when a signed
/// <see cref="PacketType.TipPacket"/> (24) is received off the mesh — wiring this
/// provider is what turns the protocol's settlement-free tip signal into real value
/// moving between two SDPKT wallets.
///
/// <para>
/// It mirrors <see cref="TipEventHandler.HandleTipPacketAsync"/>: forward the signed
/// tip envelope to the backend (<see cref="IAetherApiClient.MeshSettleTipAsync"/>,
/// <c>POST /api/aether/tips/mesh-settle</c>) which performs the ledger transfer into
/// the recipient operator's wallet. A gateway node with internet settles; settlement
/// failure is logged and swallowed so the packet can still be relayed and retried —
/// never thrown back into the receive path.
/// </para>
///
/// <para>
/// This handles the inbound <em>off-mesh</em> tip envelope. It is distinct from
/// <see cref="IAetherTipProvider"/> (the simple from→to→amount wallet-settlement
/// contract a host can call directly) and from
/// <see cref="IAetherNetIncentiveProvider.RecordCreatorTipAsync"/> (a direct creator
/// tip initiated by the local user).
/// </para>
/// </summary>
public sealed class SdpktMeshTipSettlementProvider : IAetherNetIncentiveProvider
{
    private readonly IAetherApiClient _apiClient;
    private readonly ILogger<SdpktMeshTipSettlementProvider> _logger;

    public SdpktMeshTipSettlementProvider(
        IAetherApiClient apiClient,
        ILogger<SdpktMeshTipSettlementProvider> logger)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Settle a tip received off the mesh by forwarding its signed envelope to the
    /// backend for ledger settlement into the recipient's SDPKT wallet. Mirrors
    /// <see cref="TipEventHandler.HandleTipPacketAsync"/>'s gateway-settlement call.
    /// Never throws — a settlement failure is logged so the inbound packet can still
    /// be relayed and the tip retried.
    /// </summary>
    public async Task SettleMeshTipAsync(TipPacketPayload tip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tip);

        try
        {
            await _apiClient.MeshSettleTipAsync(new
            {
                tipper_uhid = tip.TipperUhid,
                recipient_uhid = tip.RecipientUhid,
                amount = tip.Amount,
                traffic_type = tip.TrafficType,
                reference_id = tip.ReferenceId,
                signature = tip.Signature,
                timestamp = tip.Timestamp
            });

            _logger.LogInformation("Mesh tip settled via SDPKT: {Amount} ZAR from {Tipper} to {Recipient}",
                tip.Amount,
                LogSanitizer.SanitizeUhid(tip.TipperUhid),
                LogSanitizer.SanitizeUhid(tip.RecipientUhid));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SDPKT mesh-tip settlement failed — tip may be retried");
        }
    }
}
