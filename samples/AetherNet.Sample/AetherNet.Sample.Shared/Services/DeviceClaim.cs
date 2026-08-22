// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// One device, one driver.
///
/// <para>
/// There is a single camera in a phone, and a single object in this app that owns it along with the
/// encoder, the overlay it draws on and the table of who is on screen. That object was injected into
/// both the 1:1 call service and the group call service — each a singleton, each subscribed to the
/// radio from the moment the app starts, neither aware the other existed. Between them they had seven
/// places that tore the whole thing down and three that brought it up.
/// </para>
///
/// <para>
/// The failure that falls out of that needs no unusual timing at all: be in a group video call,
/// decline an unrelated 1:1 call, and the decline runs a full teardown — every decoder released,
/// every tile removed, the overlay gone. The group call continues with no picture, and nothing
/// reports an error, because from the declining service's point of view it did exactly the right
/// thing with its own object.
/// </para>
///
/// <para>
/// A claim makes that unsayable. It is deliberately the smallest possible thing — three operations
/// and one field — and it lives here rather than beside the camera so it can be tested without one.
/// </para>
/// </summary>
public sealed class DeviceClaim
{
    private readonly object _gate = new();
    private object? _owner;

    /// <summary>Whether anyone is using it.</summary>
    public bool IsHeld
    {
        get { lock (_gate) return _owner is not null; }
    }

    /// <summary>
    /// Take it, or confirm you already have it.
    /// </summary>
    /// <returns>False only when somebody else holds it.</returns>
    /// <remarks>
    /// Reclaiming your own succeeds on purpose. Every path that needs the camera calls this, and most
    /// of them are reached repeatedly during a call — a claim that failed the second time would make
    /// the caller's own success depend on how it got there.
    /// </remarks>
    public bool Claim(object? owner)
    {
        if (owner is null) return false;

        lock (_gate)
        {
            if (_owner is not null && !ReferenceEquals(_owner, owner)) return false;
            _owner = owner;
            return true;
        }
    }

    /// <summary>Whether this particular thing is the one currently driving.</summary>
    public bool HeldBy(object? owner)
    {
        if (owner is null) return false;
        lock (_gate) return ReferenceEquals(_owner, owner);
    }

    /// <summary>
    /// Give it back.
    /// </summary>
    /// <returns>
    ///   True when it was yours and is now free — the caller uses this to decide whether to tear
    ///   anything down. False means somebody else holds it, and the answer is to do nothing at all.
    /// </returns>
    public bool Release(object? owner)
    {
        if (owner is null) return false;

        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner)) return false;
            _owner = null;
            return true;
        }
    }
}
