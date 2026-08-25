// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Which kind of authority to accept over somebody's phone — the least the system will settle for.
///
/// <para>
/// When a tap installs this app, provisioning stops and <b>asks the app a question</b>: here are the
/// modes I will allow, pick one. Answering with anything outside that list fails the whole thing, and
/// not answering at all fails it too — an app that ignores the question is downloaded and then
/// abandoned halfway.
/// </para>
///
/// <para>
/// <b>That question is a gift and it is the reason this file exists.</b> The obvious reading of
/// provisioning is that the app becomes the owner of the phone, which for something about sovereignty
/// is a grotesque sentence to put on a stranger's screen. But the mode is not fixed by whoever sent
/// the tap — it is chosen here, at the moment of install, from whatever the system offers. So we take
/// the smallest thing on the table every time, and if the only thing on the table is ownership of
/// somebody's entire handset, we say no and let the install fail.
/// </para>
/// </summary>
public static class ProvisioningChoice
{
    /// <summary>
    /// The app owns the whole device. Everything on the phone is ours to police.
    /// </summary>
    /// <remarks>
    /// The value Android uses for this, mirrored rather than referenced so the decision can be tested
    /// without a handset. It is deliberately the one we refuse.
    /// </remarks>
    public const int WholeDevice = 1;

    /// <summary>
    /// The app owns a work profile and nothing outside it.
    /// </summary>
    /// <remarks>
    /// A separate, badged space alongside the person's own. Their photos, their messages and their
    /// other apps are on the other side of a wall we cannot see over — which is the correct amount of
    /// power for something a friend handed them in a taxi.
    /// </remarks>
    public const int OwnProfileOnly = 2;

    /// <summary>Nothing acceptable was offered.</summary>
    public const int Refuse = 0;

    /// <summary>
    /// Rank of what we are willing to accept, least invasive first.
    /// </summary>
    /// <remarks>
    /// A list rather than a comparison so that a mode Android adds later is <i>not</i> silently
    /// accepted by being numerically smaller. Anything unrecognised is refused, which is the safe
    /// direction: the cost of refusing is a failed install, and the cost of accepting wrongly is
    /// somebody's phone.
    /// </remarks>
    private static readonly int[] Acceptable = [OwnProfileOnly];

    /// <summary>
    /// Pick the least authority the system will accept.
    /// </summary>
    /// <param name="allowed">What provisioning says it will permit. Order is not meaningful.</param>
    /// <returns>The chosen mode, or <see cref="Refuse"/> when nothing offered is acceptable.</returns>
    public static int Least(IEnumerable<int>? allowed)
    {
        if (allowed is null) return Refuse;

        var offered = new HashSet<int>(allowed);

        foreach (var mode in Acceptable)
            if (offered.Contains(mode)) return mode;

        return Refuse;
    }

    /// <summary>
    /// Whether taking a mode means taking the whole phone.
    /// </summary>
    /// <remarks>
    /// Exists so the refusal can be explained rather than merely happening. A person watching an
    /// install stop is owed a reason, and "this asked for control of your entire phone, so it stopped"
    /// is a better thing to read than a spinner that gives up.
    /// </remarks>
    public static bool IsTheWholePhone(int mode) => mode == WholeDevice;

    /// <summary>What to say when we walk away from an install.</summary>
    public static string Refusal(IEnumerable<int>? allowed) =>
        allowed is not null && new HashSet<int>(allowed).Contains(WholeDevice)
            ? "This phone would only let Aether install by handing it control of the whole device. "
              + "It isn't worth that, so nothing was installed."
            : "This phone didn't offer a way to install Aether that keeps your own apps separate, "
              + "so nothing was installed.";
}
