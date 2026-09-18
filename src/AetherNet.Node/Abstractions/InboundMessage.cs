// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNet.Node;

/// <summary>
/// A message that arrived at this device, projected for a bound consumer.
///
/// <para>
/// The sender is given as a stable <see cref="AetherNetTag"/> even though the mesh addresses by UHID on
/// the wire — the node resolves any rotating wire address (ERID) back to the stable tag before handing
/// the message out, so a consumer never sees, and never has to resolve, a rotating address.
/// </para>
/// </summary>
/// <param name="From">The stable AetherTag of the sender.</param>
/// <param name="Payload">The application payload (already decrypted by the node's Signal session).</param>
/// <param name="Kind">An application-defined kind/label for the payload (e.g. "text", "app-package").</param>
/// <param name="ReceivedAt">When the node received the message.</param>
/// <param name="Id">A stable identifier for de-duplication.</param>
public sealed record InboundMessage(
    AetherNetTag From,
    ReadOnlyMemory<byte> Payload,
    string Kind,
    DateTimeOffset ReceivedAt,
    Guid Id);

/// <summary>
/// The outcome of a <c>SendAsync</c>. A send is not a delivery: with no reachable peer the node queues
/// the message honestly rather than pretending to have sent it, and says so here.
/// </summary>
/// <param name="Accepted">True when the node took the message (sent or queued); false when it refused.</param>
/// <param name="Detail">A short human label: "sent", "queued", or the reason for a refusal.</param>
public sealed record OutboundResult(bool Accepted, string? Detail = null)
{
    /// <summary>The node sent the message to a linked peer.</summary>
    public static OutboundResult Sent { get; } = new(true, "sent");

    /// <summary>The node accepted the message but no peer is reachable yet, so it is queued.</summary>
    public static OutboundResult Queued { get; } = new(true, "queued");

    /// <summary>The node refused the message; <paramref name="why"/> says why.</summary>
    public static OutboundResult Refused(string why) => new(false, why);
}
