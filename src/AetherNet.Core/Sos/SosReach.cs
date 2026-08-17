// SPDX-License-Identifier: MIT

namespace AetherNet.Models;

/// <summary>
/// Who an SOS reaches. The sender chooses this per alert — the protocol imposes no policy.
/// </summary>
public enum SosReach
{
    /// <summary>
    /// Directed only to the sender's trusted contacts — private, but limited to people they already
    /// know are in range. A contacts-only alert can auto-widen to <see cref="Both"/> if the sender does
    /// not mark itself safe within its check-in window.
    /// </summary>
    Contacts,

    /// <summary>
    /// Flooded to any node nearby — the best chance a stranger can help or pass it onward, but everyone
    /// in range sees the alert.
    /// </summary>
    Nearby,

    /// <summary>Both at once: a directed send to contacts AND a flood to everyone nearby.</summary>
    Both,
}
