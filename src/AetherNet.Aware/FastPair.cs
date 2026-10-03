// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/FastPair.kt (github.com/offgridpete/fieldwatch, cf6562d).
// Fieldwatch's model-name table (FastPairModels.kt) is not ported: its NOTICE says those names come from public
// listings and are not covered by its MIT grant.
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>
/// Google Fast Pair (service UUID FE2C): a 3-byte model ID means the accessory is in pairing mode; anything longer is
/// the account-key advert every paired accessory sends — background noise in a crowd.
/// </summary>
public static class FastPair
{
    public const string FleetId = "fleet-fast-pair";

    /// <summary>The facts carry a Fast Pair pairing-mode advert (a 3-byte model ID).</summary>
    public static bool PairingAdvertised(RadioFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.ServiceData.Any(r => IsFastPairUuid(r.Uuid) && Strings.LettersAndDigits(r.DataHex).Length == 6);
    }

    /// <summary>Only the account-key Fast Pair signature matched, and it never showed pairing mode.</summary>
    public static bool IsAccountKeyOnly(Sighting device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return !device.FastPairPairing && device.FleetIds.Count == 1 && device.FleetIds[0] == FleetId;
    }

    public static bool IsFastPairUuid(string uuid)
    {
        var hex = Strings.LettersAndDigits(uuid).ToUpperInvariant();
        return hex == "FE2C" || (hex.Length >= 8 && hex.Substring(4, 4) == "FE2C");
    }
}
