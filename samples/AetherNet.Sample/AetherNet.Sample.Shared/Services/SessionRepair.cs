// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// When to throw a secure session away and start again, and who does the starting.
///
/// <para>
/// Two phones that link at the same moment both begin a handshake, each ends up with its own session,
/// and the ratchets no longer agree. Nothing either sends can be read by the other after that. The only
/// honest read of a payload that will not decrypt is that the session is finished, so it goes and a new
/// one is built.
/// </para>
///
/// <para>
/// The repair is led by whichever phone cannot read, because on a diverged ratchet that is the only
/// phone that knows anything is wrong. Breakage is usually one-directional — one side's sending chain
/// and the other's receiving chain fall out of step, and the sender goes on transmitting perfectly
/// happily, seeing nothing. Deciding the leader some tidier way, by comparing tags say, hands the job
/// to a phone with no idea it has one: watched on hardware 2026-08-13, a P30 Lite detected the failure,
/// deferred to merlin, and merlin never spoke up because from where it stood the conversation was fine.
/// </para>
///
/// <para>
/// A cooldown keeps a burst of unreadable frames — six arrived within two seconds on that P30 — from
/// becoming six teardowns, gives each attempt long enough to actually finish before another starts, and
/// bounds the damage in the rarer case where both sides go deaf at once and both lead.
/// </para>
/// </summary>
public sealed class SessionRepair
{
    /// <summary>
    /// How long a repair is left alone before another is allowed. It has to outlast a full handshake —
    /// ask for the peer's bundle, wait for it to come back over the radio, adopt it — or an attempt is
    /// abandoned and replaced before it could have worked.
    /// </summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, DateTime> _lastRepair = new(StringComparer.Ordinal);

    /// <summary>
    /// A payload from this peer would not decrypt. Is it time to throw the session away and rebuild?
    /// True at most once per <see cref="Cooldown"/> per peer, so a burst of unreadable frames from one
    /// broken session is one repair rather than one each.
    /// </summary>
    public bool ShouldRestart(string peerTag, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(peerTag)) return false;

        if (_lastRepair.TryGetValue(peerTag, out var last) && nowUtc - last < Cooldown)
            return false;

        _lastRepair[peerTag] = nowUtc;
        return true;
    }

    /// <summary>The session is healthy again; the next failure starts a fresh clock.</summary>
    public void Forget(string peerTag)
    {
        if (!string.IsNullOrEmpty(peerTag)) _lastRepair.TryRemove(peerTag, out _);
    }
}
