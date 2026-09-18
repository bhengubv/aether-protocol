// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNet.Node;

/// <summary>
/// The surface a consumer application sees once it has bound to the device's one Aether node.
///
/// <para>
/// This is the cross-process face of <see cref="INodeIdentity"/> plus a messaging and presence slice —
/// a <b>subset</b>, deliberately. A bound app can ask the node to sign, to address and send, to read its
/// inbox, and to report whether it is linked. It cannot obtain the private key, a derived key, or the
/// recovery phrase: those never cross the boundary, so a compromised consumer can ask the node to act,
/// never to hand over the identity. Every member is <see cref="Task"/>-based and callbacks arrive through
/// <see cref="IAetherNodeEvents"/> rather than C# events, because delegates and <c>ValueTask</c> do not
/// survive a process boundary — the same interface serves an in-process host and a remote AIDL binding.
/// </para>
/// </summary>
public interface IAetherNodeClient
{
    /// <summary>This device's AetherTag — the same value every bound app on the device sees.</summary>
    /// <exception cref="AetherNodeException">
    /// <see cref="AetherNodeErrorCode.NodeUnavailable"/> when the node is present but locked, or
    /// <see cref="AetherNodeErrorCode.GrantRequired"/> when this app has not been linked. Never collapse
    /// "unavailable" into "absent": a caller told the identity is absent will try to mint a new one.
    /// </exception>
    Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default);

    /// <summary>The public half of the node's identity — publishable, and what the tag derives from.</summary>
    Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>Sign bytes as this device. Bytes in, signature out — the private key stays in the node.</summary>
    Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Send a payload to another node, addressed by its AetherTag. The node resolves the wire
    /// address and owns the Signal session, so one pair keeps one ratchet across every app.</summary>
    Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>The most recent inbound messages addressed to this device.</summary>
    Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Whether the node is reaching anyone right now, and over which radios. A report, not a picker.</summary>
    Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribe to inbound messages, link changes, and grant changes. Dispose the returned handle to stop.
    /// A callback interface rather than a C# event so the subscription proxies across a process boundary.
    /// </summary>
    IDisposable Subscribe(IAetherNodeEvents listener);
}

/// <summary>
/// The callbacks a bound consumer receives from the node. Delivered on an unspecified thread; an
/// implementation that touches UI must marshal. Modelled as an interface, not C# events, so it can be
/// mirrored by an AIDL <c>oneway</c> callback across processes.
/// </summary>
public interface IAetherNodeEvents
{
    /// <summary>A message addressed to this device has arrived.</summary>
    void OnInbound(InboundMessage message);

    /// <summary>The node's link state changed — a radio came up or down, a peer linked or dropped.</summary>
    void OnLinkChanged(NodeLinkStatus status);

    /// <summary>This app's grant changed — granted, revoked, or reset to awaiting.</summary>
    void OnGrantChanged(GrantState state);
}
