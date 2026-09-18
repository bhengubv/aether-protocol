// SPDX-License-Identifier: MIT

namespace AetherNet.Node;

/// <summary>
/// Where one app stands with the node. Linking is the user's decision, cleared through a local-auth gate
/// (biometric / pattern / code); this is the state that decision moves through.
/// </summary>
public enum GrantState
{
    /// <summary>The node has never heard of this app.</summary>
    Absent = 0,

    /// <summary>The app has asked to link and is waiting for the user to authorize it.</summary>
    AwaitingGrant = 1,

    /// <summary>The user authorized the link; the app may use the bind contract.</summary>
    Bound = 2,

    /// <summary>The user revoked a previously granted link.</summary>
    Revoked = 3,
}

/// <summary>
/// One app's standing grant with the node — which app, what state, and when it was granted. Immutable;
/// the transition helpers return a new value so a store can compare-and-swap.
/// </summary>
/// <param name="AppId">The consumer app's stable identifier (e.g. reverse-DNS package name).</param>
/// <param name="State">Where this app currently stands.</param>
/// <param name="GrantedAt">When the current grant was authorized, or null when not <see cref="GrantState.Bound"/>.</param>
public sealed record AppGrant(string AppId, GrantState State, DateTimeOffset? GrantedAt = null)
{
    /// <summary>A fresh request from an app the node has not linked before.</summary>
    public static AppGrant Requested(string appId) => new(appId, GrantState.AwaitingGrant);

    /// <summary>The user authorized this app at <paramref name="at"/>.</summary>
    public AppGrant Granted(DateTimeOffset at) => this with { State = GrantState.Bound, GrantedAt = at };

    /// <summary>The user revoked this app.</summary>
    public AppGrant RevokedNow() => this with { State = GrantState.Revoked, GrantedAt = null };

    /// <summary>The app is asking again after being revoked or declined.</summary>
    public AppGrant Reasked() => this with { State = GrantState.AwaitingGrant, GrantedAt = null };

    /// <summary>Whether this app may currently use the bind contract.</summary>
    public bool CanBind => State == GrantState.Bound;
}

/// <summary>The legal moves of a grant. Any transition not listed here is a bug, not a state.</summary>
public static class GrantStates
{
    /// <summary>Whether a grant may move from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool CanTransition(GrantState from, GrantState to) => (from, to) switch
    {
        (GrantState.Absent, GrantState.AwaitingGrant) => true,        // an app asks for the first time
        (GrantState.AwaitingGrant, GrantState.Bound) => true,         // the user authorizes
        (GrantState.AwaitingGrant, GrantState.Absent) => true,        // the user declines
        (GrantState.Bound, GrantState.Revoked) => true,               // the user revokes
        (GrantState.Revoked, GrantState.AwaitingGrant) => true,       // the app asks again
        _ => false,
    };
}
