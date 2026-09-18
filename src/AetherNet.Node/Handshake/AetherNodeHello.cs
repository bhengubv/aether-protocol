// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNet.Node;

/// <summary>
/// The capability tags a node and a consumer negotiate over. A tag names one thing the bind contract can
/// do; the mutually supported set is what a given binding actually offers. New tags are additive — an
/// older peer simply does not list one it does not know.
/// </summary>
public static class NodeCapabilities
{
    /// <summary>Signing on behalf of the device (<c>SignAsync</c>).</summary>
    public const string Sign = "sign";

    /// <summary>Sending messages addressed by tag (<c>SendAsync</c>).</summary>
    public const string Send = "send";

    /// <summary>Reading the inbox and receiving inbound callbacks (<c>GetInboxAsync</c>, <c>OnInbound</c>).</summary>
    public const string Inbox = "inbox";

    /// <summary>Reading link/presence status (<c>GetLinkAsync</c>, <c>OnLinkChanged</c>).</summary>
    public const string Presence = "presence";
}

/// <summary>
/// A consumer's opening message when it binds: the highest protocol version it speaks and the
/// capabilities it wants. The node answers with an <see cref="AetherNodeHelloAck"/>.
/// </summary>
/// <param name="ProtocolVersion">The highest bind-protocol version the consumer supports (>= 1).</param>
/// <param name="Capabilities">The capability tags the consumer wants (see <see cref="NodeCapabilities"/>).</param>
public sealed record AetherNodeHello(int ProtocolVersion, IReadOnlyList<string> Capabilities);

/// <summary>
/// The node's answer to a <see cref="AetherNodeHello"/>: the agreed protocol version, the device's tag,
/// the capabilities actually granted (the mutual set), and where the asking app now stands.
/// </summary>
/// <param name="ProtocolVersion">The agreed version — the lower of the two peers' highest.</param>
/// <param name="NodeTag">This device's AetherTag.</param>
/// <param name="Capabilities">The capabilities both sides support (the intersection).</param>
/// <param name="Grant">Where the asking app stands after this handshake.</param>
public sealed record AetherNodeHelloAck(
    int ProtocolVersion,
    AetherNetTag NodeTag,
    IReadOnlyList<string> Capabilities,
    GrantState Grant);
