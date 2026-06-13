// SPDX-License-Identifier: MIT

using AetherNet.Protocol;

namespace AetherNet.Incentive;

/// <summary>
/// Sends and receives generic mesh tip packets (<see cref="PacketType.TipPacket"/>).
///
/// <para>
/// A tip is a settlement-free protocol signal: a node addresses a
/// <see cref="TipPacketPayload"/> to a recipient to credit it for some kind of
/// relayed traffic. The protocol carries the signal end-to-end; it attaches NO
/// value semantics, NO policy, and NO settlement. What (if anything) a tip is
/// worth is decided by the host via
/// <c>IAetherNetIncentiveProvider.SettleMeshTipAsync</c> — a bare node accepts and
/// relays tips but settles nothing.
/// </para>
///
/// <para>
/// Flow:
/// <list type="number">
///   <item>The local node calls <see cref="SendTipAsync"/>, which builds and signs a
///     <see cref="TipPacketPayload"/>, wraps it in a signed
///     <see cref="MeshPacket"/> of type <see cref="PacketType.TipPacket"/>, and
///     routes it toward the recipient.</item>
///   <item>A receiving node pumps the inbound packet into
///     <see cref="HandleTipPacketAsync"/>, which deserialises the payload, makes a
///     best-effort signature check, hands it to the host's settlement provider, and
///     lets normal routing relay it onward toward the addressed recipient.</item>
/// </list>
/// </para>
/// </summary>
public interface IMeshTipService
{
    /// <summary>
    /// Build, sign, and route a <see cref="PacketType.TipPacket"/> addressed to
    /// <paramref name="recipientUhid"/>.
    ///
    /// <para>
    /// <paramref name="amount"/> is the caller's input verbatim. The protocol imposes
    /// NO policy on it — no unit, no minimum, no maximum, no rounding. It is signed
    /// into the payload and carried as-is.
    /// </para>
    /// </summary>
    /// <param name="recipientUhid">UHID of the node the tip is addressed to.</param>
    /// <param name="amount">Generic, unit-less value to credit. Passed through unaltered.</param>
    /// <param name="trafficType">Free-form tag describing the relayed traffic
    ///   (e.g. <c>"message-relay"</c>, <c>"gateway-share"</c>). Opaque to the protocol.</param>
    /// <param name="referenceId">Optional correlation id linking the tip to a unit of work.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The signed <see cref="MeshPacket"/> that was routed onto the mesh.</returns>
    Task<MeshPacket> SendTipAsync(
        string recipientUhid,
        decimal amount,
        string trafficType,
        Guid? referenceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Process an inbound <see cref="PacketType.TipPacket"/> received off the mesh.
    ///
    /// <para>
    /// Implementations deserialise the <see cref="TipPacketPayload"/>, make a
    /// best-effort check of its signature, invoke
    /// <c>IAetherNetIncentiveProvider.SettleMeshTipAsync</c> with the payload, and let
    /// normal routing relay the packet onward toward its addressed recipient. A
    /// malformed or unverifiable payload is logged and dropped — never thrown.
    /// </para>
    /// </summary>
    /// <param name="packet">The received <see cref="PacketType.TipPacket"/> packet.</param>
    /// <param name="ct">Cancellation token.</param>
    Task HandleTipPacketAsync(MeshPacket packet, CancellationToken ct = default);
}
