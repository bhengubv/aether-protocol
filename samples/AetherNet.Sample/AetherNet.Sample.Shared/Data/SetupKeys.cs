// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Data;

/// <summary>Setting keys used by first-run setup. Kept in one place so the gate and the wizard agree.</summary>
public static class SetupKeys
{
    /// <summary>Set once the wizard has been completed; until then every route redirects to it.</summary>
    public const string Complete = "setup.complete";

    /// <summary>Set when the user opted this device in as an internet gateway for nearby phones.</summary>
    public const string GatewayEnabled = "setup.gateway";
}
