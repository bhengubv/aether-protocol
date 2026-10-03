// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/Geo.kt (TrackerMatch)
// (github.com/offgridpete/fieldwatch, cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>What sort of thing might be tracking: a finder tag, a beacon, or a wearable.</summary>
public enum TrackerKind
{
    Finder,
    Beacon,
    Wearable,
}

/// <summary>Tells finder tags (AirTag, SmartTag, Tile, …) from beacons and wearables.</summary>
public static class TrackerMatch
{
    private static readonly string[] FinderTokens =
        ["airtag", "smarttag", "tile", "chipolo", "pebblebee", "moto tag", "find my", "find hub", "dult"];

    private static readonly string[] BeaconTokens = ["ibeacon", "minew", "estimote", "kontakt"];

    private static readonly string[] WearableTokens = ["garmin", "fitbit", "oura"];

    /// <summary>Apple Continuity and pairing types: a phone, Mac or earbuds — not Offline Finding (0x12).</summary>
    private static readonly HashSet<string> AppleContinuityPrefixes =
        ["05", "07", "08", "09", "0A", "0B", "0C", "0D", "0E", "0F", "10"];

    /// <summary>A signature name that names a finder tag.</summary>
    public static bool IsTrackerFleet(string name)
    {
        var lower = name.ToLowerInvariant();
        return FinderTokens.Any(t => lower.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>The names of the radio's signatures that name a finder tag.</summary>
    public static IReadOnlyList<string> FleetHits(Sighting device, IReadOnlyDictionary<string, string> names) =>
        NameHits(device, names, FinderTokens);

    /// <summary>Apple's Find My (Offline Finding) payload with no Continuity beside it: a tag, not a phone.</summary>
    public static bool IsFindMyPayload(Sighting device)
    {
        if (IsAppleContinuity(device))
        {
            return false;
        }
        if (device.Facts.MfgRecords.Any(r => r.CompanyId == 0x004C && MfgPrefix(r) == "12"))
        {
            return true;
        }
        return device.ManufacturerId == 0x004C &&
            Strings.Take(Strings.LettersAndDigits(device.ManufacturerDataHex), 2).ToUpperInvariant() == "12";
    }

    /// <summary>Nearby Info, Handoff, AirDrop, AirPods…: a phone, Mac or earbuds, not a tag.</summary>
    public static bool IsAppleContinuity(Sighting device) =>
        SignatureEngine.MfgRecords(device).Any(r => r.CompanyId == 0x004C && AppleContinuityPrefixes.Contains(MfgPrefix(r)));

    public static bool IsAppleCompany(Sighting device) =>
        device.ManufacturerId == 0x004C || device.Facts.MfgRecords.Any(r => r.CompanyId == 0x004C);

    public static bool IsTracker(Sighting device, IReadOnlyDictionary<string, string> names) =>
        KindOf(device, names) == TrackerKind.Finder;

    /// <summary>
    /// A loud Apple advert that stayed loud — a pocketed iPhone. Not every Apple TV on the block, and not an
    /// iBeacon even though its payload is Apple's.
    /// </summary>
    public static bool IsCarriedApple(Sighting device, IReadOnlyDictionary<string, string> names)
    {
        if (device.Kind != RadioKind.Ble)
        {
            return false;
        }
        if (device.RssiMax < -55 || device.RssiMin < -70)
        {
            return false;
        }
        if (NameHits(device, names, BeaconTokens).Count > 0)
        {
            return false;
        }
        if (IsAppleCompany(device))
        {
            return true;
        }
        return device.FleetIds.Any(id =>
            names.TryGetValue(id, out var n) && n.Contains("apple", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finder, beacon, wearable, or none (Fieldwatch's <c>kind</c>). <paramref name="names"/> maps signature id to
    /// signature name.
    /// </summary>
    public static TrackerKind? KindOf(Sighting device, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(names);
        if (NameHits(device, names, FinderTokens).Count > 0 || IsFindMyPayload(device))
        {
            return TrackerKind.Finder;
        }
        if (NameHits(device, names, BeaconTokens).Count > 0)
        {
            return TrackerKind.Beacon;
        }
        if (NameHits(device, names, WearableTokens).Count > 0)
        {
            return TrackerKind.Wearable;
        }
        return IsCarriedApple(device, names) ? TrackerKind.Finder : null;
    }

    /// <summary>The signature names that make it a tracker, or a plain description.</summary>
    public static string Label(Sighting device, IReadOnlyDictionary<string, string> names)
    {
        foreach (var tokens in new[] { FinderTokens, BeaconTokens, WearableTokens })
        {
            var hits = NameHits(device, names, tokens);
            if (hits.Count > 0)
            {
                return string.Join(" + ", hits);
            }
        }
        if (IsFindMyPayload(device))
        {
            return "Apple Find My / Offline Finding";
        }
        return IsCarriedApple(device, names) ? "Apple BLE (phone / Continuity)" : "tracker-like";
    }

    private static List<string> NameHits(Sighting device, IReadOnlyDictionary<string, string> names, string[] tokens)
    {
        var output = new List<string>();
        foreach (var id in device.FleetIds)
        {
            if (names.TryGetValue(id, out var name))
            {
                var lower = name.ToLowerInvariant();
                if (tokens.Any(t => lower.Contains(t, StringComparison.Ordinal)))
                {
                    output.Add(name);
                }
            }
        }
        return output;
    }

    private static string MfgPrefix(MfgRecord rec) =>
        Strings.Take(Strings.LettersAndDigits(rec.DataHex), 2).ToUpperInvariant();
}
