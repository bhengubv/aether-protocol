// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Getting a broken secure session back, without the two phones fighting each other over it.
///
/// <para>
/// When a link comes up both phones start a handshake at the same moment. Each builds its own session,
/// the ratchets diverge, and from then on nothing either of them sends can be read by the other:
/// <c>The computed authentication tag did not match the input authentication tag</c>. Receipts stop
/// arriving, every message times out, and the conversation shows failure after failure over a radio
/// link that is working perfectly.
/// </para>
///
/// <para>
/// A payload that will not decrypt is the signal to throw the session away and build a new one, and the
/// phone that could not read it does the building. Handing that job to the peer instead — on some tidy
/// rule both sides can compute, like comparing tags — looks safer and is not: a diverged ratchet
/// usually breaks one direction only, so the peer is still sending happily and has no idea there is
/// anything to fix. A P30 Lite spent an afternoon deferring to merlin over a link that held 4m21s,
/// while merlin, which could read everything perfectly, never said a word.
/// </para>
///
/// <para>
/// What is left to guard is repetition: a burst of unreadable frames must be one repair, not one each,
/// and each attempt needs long enough to finish before another starts.
/// </para>
///
/// <para>Measured on hardware 2026-08-13, P30 Lite ↔ merlin.</para>
/// </summary>
public class SessionRepairTests
{
    private const string Higher = "ZZZZZ-ZZZZZ";

    private static readonly DateTime Start = new(2026, 8, 13, 14, 4, 0, DateTimeKind.Utc);

    // ── A broken session is thrown away ───────────────────────────────────────

    [Fact]
    public void An_unreadable_payload_starts_a_repair()
    {
        var repair = new SessionRepair();

        Assert.True(repair.ShouldRestart(Higher, Start));
    }

    [Fact]
    public void Each_peer_is_judged_on_its_own()
    {
        var repair = new SessionRepair();
        repair.ShouldRestart(Higher, Start);

        Assert.True(repair.ShouldRestart("MMMMM-MMMMM", Start),
            "one peer's broken session suppressed the repair of an unrelated one");
    }

    // ── The two phones must not fight ─────────────────────────────────────────

    /// <summary>
    /// Six unreadable receipts arrived inside two seconds on the P30. Reacting to each one would tear
    /// the session down six times and hand the peer six half-built replacements.
    /// </summary>
    [Fact]
    public void A_burst_of_failures_is_one_repair_not_six()
    {
        var repair = new SessionRepair();

        var restarts = 0;
        for (var i = 0; i < 6; i++)
            if (repair.ShouldRestart(Higher, Start.AddMilliseconds(300 * i))) restarts++;

        Assert.Equal(1, restarts);
    }

    [Fact]
    public void A_repair_can_be_tried_again_once_the_last_one_has_had_its_chance()
    {
        var repair = new SessionRepair();
        repair.ShouldRestart(Higher, Start);

        Assert.True(repair.ShouldRestart(Higher, Start.Add(SessionRepair.Cooldown)));
    }

    /// <summary>
    /// The cooldown has to outlast a whole handshake — request a bundle, wait for it over the radio,
    /// adopt it — or the repair is abandoned and retried before it could possibly have worked.
    /// </summary>
    [Fact]
    public void The_cooldown_outlasts_a_handshake()
    {
        Assert.True(SessionRepair.Cooldown >= TimeSpan.FromSeconds(15),
            $"a {SessionRepair.Cooldown.TotalSeconds:0}s cooldown gives up before a handshake can finish");
    }

    // ── Repairing again later ─────────────────────────────────────────────────

    /// <summary>
    /// A session that recovers and later breaks again is repaired again — the clock is per episode,
    /// not a one-off allowance.
    /// </summary>
    [Fact]
    public void A_session_that_comes_good_and_breaks_again_is_repaired_again()
    {
        var repair = new SessionRepair();
        repair.ShouldRestart(Higher, Start);

        repair.Forget(Higher);   // traffic flowed again

        Assert.True(repair.ShouldRestart(Higher, Start.AddSeconds(1)));
    }
}
