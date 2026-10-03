// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/PayloadLocation.kt (github.com/offgridpete/fieldwatch,
// cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>
/// Where a radio says it is (WGS84), from its own packets — a drone's Remote ID — not this device's GPS. The
/// decode-field ids <see cref="LatId"/>/<see cref="LonId"/> pin it, whatever the signature; <see cref="OpLatId"/>/
/// <see cref="OpLonId"/> are a drone's pilot, never the pin.
/// </summary>
public sealed record PayloadLocation
{
    public const string LatId = "latitude";

    public const string LonId = "longitude";

    public const string OpLatId = "op_lat";

    public const string OpLonId = "op_lon";

    private static readonly HashSet<string> LatIds = ["latitude", "lat"];
    private static readonly HashSet<string> LonIds = ["longitude", "lon", "lng"];
    private static readonly HashSet<string> AltIds = ["alt_geo", "altitude", "alt", "hae"];
    private static readonly HashSet<string> OpLatIds = ["op_lat", "operator_lat"];
    private static readonly HashSet<string> OpLonIds = ["op_lon", "operator_lon"];
    private static readonly HashSet<string> UasIds = ["uas_id", "uasid", "serial"];
    private static readonly HashSet<string> SelfIds = ["self_id", "selfid"];
    private static readonly HashSet<string> HeadingIds = ["heading", "course"];
    private static readonly HashSet<string> SpeedIds = ["speed", "hspeed"];
    private static readonly HashSet<string> VspeedIds = ["vspeed", "vert_speed"];

    public double? Lat { get; init; }

    public double? Lon { get; init; }

    public double? Alt { get; init; }

    public double? OpLat { get; init; }

    public double? OpLon { get; init; }

    public string? UasId { get; init; }

    public string? SelfId { get; init; }

    public double? HeadingDeg { get; init; }

    public double? SpeedMps { get; init; }

    public double? VspeedMps { get; init; }

    public (double Lat, double Lon)? Pin() => ValidCoord(Lat, Lon) ? (Lat!.Value, Lon!.Value) : null;

    /// <summary>
    /// Remote ID rotates message types; a Basic ID packet has no position. Keep the last valid position, pilot
    /// position, ID and motion this session, letting this packet's valid values win.
    /// </summary>
    public PayloadLocation MergeSticky(PayloadLocation? prev)
    {
        var p = prev ?? new PayloadLocation();
        var pin = ValidCoord(Lat, Lon) ? (Lat, Lon) : (p.Lat, p.Lon);
        var op = ValidCoord(OpLat, OpLon) ? (OpLat, OpLon) : (p.OpLat, p.OpLon);
        return new PayloadLocation
        {
            Lat = pin.Item1,
            Lon = pin.Item2,
            Alt = Finite(Alt) ?? p.Alt,
            OpLat = op.Item1,
            OpLon = op.Item2,
            UasId = Strings.IsBlank(UasId) ? p.UasId : UasId,
            SelfId = Strings.IsBlank(SelfId) ? p.SelfId : SelfId,
            HeadingDeg = Finite(HeadingDeg) ?? p.HeadingDeg,
            SpeedMps = Finite(SpeedMps) ?? p.SpeedMps,
            VspeedMps = Finite(VspeedMps) ?? p.VspeedMps,
        };
    }

    public static PayloadLocation FromDecoded(IReadOnlyList<DecodedFieldValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0)
        {
            return new PayloadLocation();
        }
        return new PayloadLocation
        {
            Lat = Num(fields, LatIds),
            Lon = Num(fields, LonIds),
            Alt = Num(fields, AltIds),
            OpLat = Num(fields, OpLatIds),
            OpLon = Num(fields, OpLonIds),
            UasId = TextOf(fields, UasIds),
            SelfId = TextOf(fields, SelfIds),
            HeadingDeg = Num(fields, HeadingIds),
            SpeedMps = Num(fields, SpeedIds),
            VspeedMps = Num(fields, VspeedIds),
        };
    }

    public static PayloadLocation FromSighting(Sighting device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new PayloadLocation
        {
            Lat = device.PayloadLat,
            Lon = device.PayloadLon,
            Alt = device.PayloadAlt,
            OpLat = device.PayloadOpLat,
            OpLon = device.PayloadOpLon,
            UasId = device.PayloadUasId,
            SelfId = device.PayloadSelfId,
            HeadingDeg = device.PayloadHeading,
            SpeedMps = device.PayloadSpeed,
            VspeedMps = device.PayloadVspeed,
        };
    }

    /// <summary>
    /// The radio with its advertised position brought up to date from its signatures' decode maps and its Remote
    /// ID packets. The same instance when nothing changed.
    /// </summary>
    public static Sighting ApplySticky(Sighting device, IReadOnlyList<Fleet> fleets)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(fleets);
        IReadOnlyList<DecodedFieldValue> decoded =
            device.FleetIds.Count == 0 ? [] : SignatureFieldDecoder.DecodeSighting(device, fleets);
        var fromBytes = OpenDroneId.FromFacts(device.Facts);
        if (decoded.Count == 0 &&
            fromBytes.Lat is null && fromBytes.OpLat is null && fromBytes.UasId is null &&
            device.PayloadLat is null && device.PayloadOpLat is null && device.PayloadUasId is null)
        {
            return device;
        }
        var next = fromBytes.MergeSticky(FromDecoded(decoded)).MergeSticky(FromSighting(device));
        if (next.Lat == device.PayloadLat &&
            next.Lon == device.PayloadLon &&
            next.Alt == device.PayloadAlt &&
            next.OpLat == device.PayloadOpLat &&
            next.OpLon == device.PayloadOpLon &&
            next.UasId == device.PayloadUasId &&
            next.SelfId == device.PayloadSelfId &&
            next.HeadingDeg == device.PayloadHeading &&
            next.SpeedMps == device.PayloadSpeed &&
            next.VspeedMps == device.PayloadVspeed)
        {
            return device;
        }
        return device with
        {
            PayloadLat = next.Lat,
            PayloadLon = next.Lon,
            PayloadAlt = next.Alt,
            PayloadOpLat = next.OpLat,
            PayloadOpLon = next.OpLon,
            PayloadUasId = next.UasId,
            PayloadSelfId = next.SelfId,
            PayloadHeading = next.HeadingDeg,
            PayloadSpeed = next.SpeedMps,
            PayloadVspeed = next.VspeedMps,
        };
    }

    /// <summary>A real position: finite, in range, and not 0,0.</summary>
    public static bool ValidCoord(double? lat, double? lon)
    {
        if (lat is not { } la || lon is not { } lo)
        {
            return false;
        }
        if (!double.IsFinite(la) || !double.IsFinite(lo))
        {
            return false;
        }
        if (la == 0.0 && lo == 0.0)
        {
            return false;
        }
        return la is >= -90.0 and <= 90.0 && lo is >= -180.0 and <= 180.0;
    }

    private static double? Finite(double? v) => v is { } x && double.IsFinite(x) ? x : null;

    private static double? Num(IReadOnlyList<DecodedFieldValue> fields, HashSet<string> ids)
    {
        var hit = fields.FirstOrDefault(f => ids.Contains(f.Id.ToLowerInvariant()));
        return hit is null ? null : Finite(hit.Number);
    }

    private static string? TextOf(IReadOnlyList<DecodedFieldValue> fields, HashSet<string> ids)
    {
        var hit = fields.FirstOrDefault(f => ids.Contains(f.Id.ToLowerInvariant()));
        if (hit is null)
        {
            return null;
        }
        var text = hit.Display.Trim().TrimEnd('\u0000');
        return text.Length > 0 ? text : null;
    }
}
