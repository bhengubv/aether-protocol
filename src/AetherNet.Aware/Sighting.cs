// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/Models.kt and SignatureFieldDecoder.kt
// (github.com/offgridpete/fieldwatch, cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>One loudness reading: when, and how loud (dBm).</summary>
public sealed record RssiSample(long At, int Rssi);

/// <summary>Where this device was (WGS84) when it heard something, and how loud it was then.</summary>
public sealed record GpsSample(long At, double Lat, double Lon, int Rssi = 0);

/// <summary>A stretch of time a radio was heard; <see cref="End"/> is null while it still is.</summary>
public sealed record PresenceSpan(long Start, long? End);

/// <summary>A decoded value a signature asked to show beside its name (for example "separated" on a finder tag).</summary>
public sealed record LiveDecodeChip(string Text, bool Emphasis, string Note = "")
{
    /// <summary>The text with its first letter upper-cased; the pack stores it lower-case.</summary>
    public string ReportLabel()
    {
        var trimmed = Text.Trim();
        return trimmed.Length == 0 ? trimmed : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}

/// <summary>One advert or beacon as a radio heard it, before it is merged into a <see cref="Sighting"/>.</summary>
public sealed record Observation
{
    public required RadioKind Kind { get; init; }

    public required string Mac { get; init; }

    /// <summary>The Bluetooth local name or the Wi-Fi network name (SSID); empty when none.</summary>
    public string Name { get; init; } = "";

    public int Rssi { get; init; }

    public int Channel { get; init; }

    public int FrequencyMhz { get; init; }

    public bool HiddenSsid { get; init; }

    public IReadOnlyList<string> ServiceUuids { get; init; } = [];

    public int? ManufacturerId { get; init; }

    public string ManufacturerDataHex { get; init; } = "";

    public string RawHex { get; init; } = "";

    public string Extras { get; init; } = "";

    /// <summary>When it was heard, Unix milliseconds. Zero means now.</summary>
    public long At { get; init; }

    /// <summary>Where this device was when it heard it, if known.</summary>
    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    /// <summary>False for a Wi-Fi result repeated from an older scan.</summary>
    public bool Fresh { get; init; } = true;

    public IReadOnlyList<string> VendorIeOuis { get; init; } = [];

    public RadioFacts Facts { get; init; } = RadioFacts.Empty;
}

/// <summary>A radio heard around this device, with everything learned about it so far.</summary>
public sealed record Sighting
{
    public required string Key { get; init; }

    public required RadioKind Kind { get; init; }

    public required string Mac { get; init; }

    public string Name { get; init; } = "";

    public int Rssi { get; init; }

    public int RssiMin { get; init; }

    public int RssiMax { get; init; }

    public int Channel { get; init; }

    public int FrequencyMhz { get; init; }

    /// <summary>The maker's name from the address, when a lookup supplies one.</summary>
    public string? Vendor { get; init; }

    /// <summary>The address is a private, changing one.</summary>
    public bool Randomized { get; init; }

    public bool HiddenSsid { get; init; }

    public IReadOnlyList<string> ServiceUuids { get; init; } = [];

    public int? ManufacturerId { get; init; }

    public string ManufacturerDataHex { get; init; } = "";

    public string RawHex { get; init; } = "";

    public string Extras { get; init; } = "";

    public long FirstSeen { get; init; }

    public long LastSeen { get; init; }

    public int HitCount { get; init; }

    /// <summary>The signatures it matches, in order, no repeats.</summary>
    public IReadOnlyList<string> FleetIds { get; init; } = [];

    public IReadOnlyList<RssiSample> RssiHistory { get; init; } = [];

    public IReadOnlyList<PresenceSpan> Presence { get; init; } = [];

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    /// <summary>Not heard for longer than the linger.</summary>
    public bool Gone { get; init; }

    public IReadOnlyList<string> VendorIeOuis { get; init; } = [];

    public RadioFacts Facts { get; init; } = RadioFacts.Empty;

    /// <summary>Where this device was each time it heard the radio (spread over the session).</summary>
    public IReadOnlyList<GpsSample> GpsTrail { get; init; } = [];

    /// <summary>Advertised a Fast Pair pairing-mode model ID this session.</summary>
    public bool FastPairPairing { get; init; }

    /// <summary>Where the radio says it is (a drone's Remote ID), kept across packets.</summary>
    public double? PayloadLat { get; init; }

    public double? PayloadLon { get; init; }

    public double? PayloadAlt { get; init; }

    /// <summary>Where a drone's pilot is, when the drone says.</summary>
    public double? PayloadOpLat { get; init; }

    public double? PayloadOpLon { get; init; }

    /// <summary>A drone's broadcast ID (Remote ID Basic ID), steadier than its address.</summary>
    public string? PayloadUasId { get; init; }

    public string? PayloadSelfId { get; init; }

    public double? PayloadHeading { get; init; }

    public double? PayloadSpeed { get; init; }

    public double? PayloadVspeed { get; init; }

    /// <summary>Decoded values a signature asked to show beside its name.</summary>
    public IReadOnlyList<LiveDecodeChip> LiveDecode { get; init; } = [];

    /// <summary>The advertised name, else "&lt;hidden&gt;" for a hidden network, else the address.</summary>
    public string DisplayName => !Strings.IsBlank(Name) ? Name : HiddenSsid ? "<hidden>" : Mac;

    /// <summary>The address's first three bytes, "AA:BB:CC".</summary>
    public string Oui => Strings.Take(Mac, 8);

    /// <summary>Average loudness over the last <paramref name="windowMs"/>, else the last reading, else <paramref name="floor"/>.</summary>
    public double AverageRssi(long windowMs, long now, int floor = -100)
    {
        var from = now - windowMs;
        var heard = RssiHistory.Where(s => s.At >= from && AetherNet.Aware.Rssi.Measured(s.Rssi)).ToList();
        if (heard.Count > 0)
        {
            return heard.Average(s => s.Rssi);
        }
        return AetherNet.Aware.Rssi.Measured(Rssi) ? Rssi : floor;
    }
}

public static class SightingNotes
{
    /// <summary>Signature name and caution for every matched signature that has one.</summary>
    public static IReadOnlyList<(string Name, string Note)> AttentionNotes(this Sighting device, IReadOnlyList<Fleet> fleets) =>
        NotesOf(device, fleets, f => f.AttentionNote);

    /// <summary>Signature name and notes for every matched signature that has notes.</summary>
    public static IReadOnlyList<(string Name, string Note)> SignatureNotes(this Sighting device, IReadOnlyList<Fleet> fleets) =>
        NotesOf(device, fleets, f => f.Notes);

    private static IReadOnlyList<(string Name, string Note)> NotesOf(Sighting device, IReadOnlyList<Fleet> fleets, Func<Fleet, string> pick)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(fleets);
        if (device.FleetIds.Count == 0)
        {
            return [];
        }
        var byId = new Dictionary<string, Fleet>();
        foreach (var fleet in fleets)
        {
            byId[fleet.Id] = fleet;
        }
        var output = new List<(string, string)>();
        foreach (var id in device.FleetIds)
        {
            if (byId.TryGetValue(id, out var fleet) && pick(fleet).Trim() is { Length: > 0 } note)
            {
                output.Add((fleet.Name, note));
            }
        }
        return output;
    }
}

/// <summary>Counts for a status line.</summary>
public sealed record ScanStats
{
    public long WifiFrames { get; init; }

    public long BleAdvs { get; init; }

    public int DevicesSeen { get; init; }

    public int NamedNow { get; init; }

    public int WifiNow { get; init; }

    public int BleNow { get; init; }

    public bool Scanning { get; init; }

    public long LastWifiScanAt { get; init; }

    public string ThrottleHint { get; init; } = "";
}
