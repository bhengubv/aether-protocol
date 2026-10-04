// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace AetherNet.Core.Diagnostics;

/// <summary>
/// Saying that a packet was thrown away, in a way that tells one bad packet apart from a feature gone deaf.
/// </summary>
/// <remarks>
/// <para>
/// A mesh takes packets from anyone, so a payload that will not parse is ordinary and must not make a noise — one
/// peer sending nonsense is that peer's problem. But the same line, logged at Debug and never counted, is also what
/// a wire format that has changed under us looks like: every packet of a kind dropped, nothing understood, and the
/// only outward sign a feature that has quietly stopped working. The two are indistinguishable without a count.
/// </para>
/// <para>
/// So each drop is still said at Debug, with who sent it, and the count is what speaks up: once it passes
/// <see cref="Enough"/>, and then every <see cref="ThenEvery"/> after that. A trickle stays quiet. A flood does not.
/// </para>
/// </remarks>
public static class DropLog
{
    /// <summary>How many have to go before a trickle is no longer a fair reading of it.</summary>
    public const int Enough = 100;

    /// <summary>And how often to say so after that, so a flood is one line now and then rather than a flood.</summary>
    public const int ThenEvery = 1_000;

    /// <summary>
    /// Note a packet thrown away because it would not parse, and say so if it has happened enough times to mean
    /// something other than a bad packet.
    /// </summary>
    /// <param name="logger">Where it is said.</param>
    /// <param name="count">
    /// The caller's running total for this kind of packet — one field per kind, so kinds are counted apart. Passed
    /// by reference because the count is the whole point, and a shared one would say nothing about which is deaf.
    /// </param>
    /// <param name="ex">What went wrong reading it.</param>
    /// <param name="what">The kind of packet, as a person would name it — "Heartbeat", "PreKeyRequest".</param>
    /// <param name="source">Who sent it, when that is known.</param>
    public static void Dropped(this ILogger? logger, ref int count, Exception ex, string what, string? source = null)
    {
        var dropped = System.Threading.Interlocked.Increment(ref count);
        if (logger is null)
        {
            return;
        }

        logger.LogDebug(ex, "{What} from {Source}: malformed payload — dropped", what, source);

        if (dropped == Enough || (dropped > Enough && dropped % ThenEvery == 0))
        {
            logger.LogWarning(
                "{Count} {What} payloads dropped as malformed. One is somebody sending nonsense; this many is this "
                + "device no longer understanding them at all.",
                dropped,
                what);
        }
    }
}
