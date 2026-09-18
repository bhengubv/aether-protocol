// SPDX-License-Identifier: MIT

namespace AetherNet.Node;

/// <summary>
/// Why a node operation failed, in terms a bound consumer can act on.
///
/// <para>
/// The distinction that matters most: <see cref="NodeUnavailable"/> (the node is present but sealed —
/// a locked screen, most often) is <b>not</b> <see cref="IdentityAbsent"/> (there is genuinely no
/// identity). Collapsing the two is destructive: an app told the identity is absent will try to mint a
/// replacement, and for an identity that is not recovery but the permanent loss of the device's address.
/// The two therefore have distinct codes and must round-trip across the boundary as distinct codes.
/// </para>
/// </summary>
public enum AetherNodeErrorCode
{
    /// <summary>An unexpected failure inside the node. Retry is reasonable; minting is not.</summary>
    Internal = 0,

    /// <summary>The node has an identity but cannot open it right now (locked screen, sealed key). Temporary. Never mint.</summary>
    NodeUnavailable = 1,

    /// <summary>The device genuinely has no identity yet. Distinct from <see cref="NodeUnavailable"/> on purpose.</summary>
    IdentityAbsent = 2,

    /// <summary>This app has not been linked; the user must authorize it first.</summary>
    GrantRequired = 3,

    /// <summary>The user declined or revoked this app's link.</summary>
    GrantDenied = 4,

    /// <summary>The node is locked and the user has not cleared the local-auth gate for this operation.</summary>
    NodeLocked = 5,

    /// <summary>No mutually supported protocol version between the app and the node.</summary>
    VersionUnsupported = 6,

    /// <summary>The app is calling too fast and the node is shedding load.</summary>
    RateLimited = 7,
}

/// <summary>
/// A failure from the node, carrying a typed <see cref="AetherNodeErrorCode"/> so a consumer branches on
/// the code rather than parsing a message. The code is what crosses a process boundary intact.
/// </summary>
public sealed class AetherNodeException : Exception
{
    /// <summary>Why the operation failed.</summary>
    public AetherNodeErrorCode Code { get; }

    public AetherNodeException(AetherNodeErrorCode code, string message)
        : base(message) => Code = code;

    public AetherNodeException(AetherNodeErrorCode code, string message, Exception? innerException)
        : base(message, innerException) => Code = code;
}
