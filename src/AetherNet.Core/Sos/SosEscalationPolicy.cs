// SPDX-License-Identifier: MIT

using AetherNet.Models;

namespace AetherNet.Sos;

/// <summary>
/// Pure, side-effect-free decisions that drive an active SOS's lifecycle. Keeping the escalation and
/// beacon rules here — separate from the timer that fires them — means the safety-critical behaviour is
/// unit-tested deterministically, with no clock and no waiting.
/// </summary>
public static class SosEscalationPolicy
{
    /// <summary>
    /// A contacts-only SOS auto-widens to a broadcast once its check-in window elapses without the source
    /// marking itself safe. Acknowledgements never factor in — a contact replying "on my way" is not
    /// rescue; only the source marking safe (which cancels the whole lifecycle) stops it.
    /// </summary>
    public static bool ShouldEscalate(SosReach reach, bool alreadyEscalated, TimeSpan sinceOrigin, TimeSpan escalateAfter)
        => reach == SosReach.Contacts && !alreadyEscalated && sinceOrigin >= escalateAfter;

    /// <summary>
    /// Whether the SOS is in a broadcasting state — i.e. it should re-emit a locator beacon every
    /// interval so rescuers can keep receiving it and triangulate. A contacts-only alert does not beacon
    /// until it has escalated (so a quiet, long check-in does not spam contacts for hours).
    /// </summary>
    public static bool IsBroadcasting(SosReach reach, bool escalated)
        => escalated || reach is SosReach.Nearby or SosReach.Both;
}
