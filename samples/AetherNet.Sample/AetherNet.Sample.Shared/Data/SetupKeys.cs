// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Data;

/// <summary>Setting keys used by first-run setup. Kept in one place so the gate and the wizard agree.</summary>
public static class SetupKeys
{
    /// <summary>Set once the wizard has been completed; until then every route redirects to it.</summary>
    public const string Complete = "setup.complete";

    /// <summary>Set when the user opted this device in as an internet gateway for nearby phones.</summary>
    public const string GatewayEnabled = "setup.gateway";

    /// <summary>
    /// "light", "dark", or "system" — what the person chose in Settings.
    /// </summary>
    /// <remarks>
    /// Absent means system, which is also what "system" means; the two are kept distinct only so a
    /// deliberate choice to follow the phone can be told from never having chosen.
    /// </remarks>
    public const string Theme = "appearance.theme";

    /// <summary>
    /// Whether AetherNet — the nearby-radio mesh — is switched on. Off means the app never wakes the
    /// physical radios and runs over the internet only.
    /// </summary>
    /// <remarks>
    /// Read as "on unless explicitly off" (absent or "1" =&gt; on; only "0" =&gt; off), so a device that
    /// pre-dates this setting keeps the mesh it already had, and a first run defaults to on.
    /// </remarks>
    public const string AetherNet = "setup.aethernet";
}
