// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/RadioFacts.kt (github.com/offgridpete/fieldwatch, cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>Manufacturer-specific data from a Bluetooth advert: the company ID and the bytes after it, as hex.</summary>
public sealed record MfgRecord(int CompanyId, string DataHex);

/// <summary>A Wi-Fi vendor-specific element: its OUI ("AA:BB:CC"), type, and payload as hex.</summary>
public sealed record VendorIeRecord(string Oui, int Type, string DataHex);

/// <summary>Service data from a Bluetooth advert: the service UUID and its bytes as hex.</summary>
public sealed record ServiceDataRecord(string Uuid, string DataHex);

/// <summary>What a radio's adverts or beacons said about it, merged over every one heard.</summary>
public sealed record RadioFacts
{
    public static readonly RadioFacts Empty = new();

    public int? TxPowerDbm { get; init; }

    public int? AdvFlags { get; init; }

    public int? Appearance { get; init; }

    public string? AddressType { get; init; }

    public double? AdvertisingIntervalMs { get; init; }

    public double? PeriodicIntervalMs { get; init; }

    public bool? Connectable { get; init; }

    public string? PrimaryPhy { get; init; }

    public string? SecondaryPhy { get; init; }

    public int? DeviceClass { get; init; }

    public string? WifiStandard { get; init; }

    public string? ChannelWidth { get; init; }

    public int? CenterFreq0 { get; init; }

    public int? CenterFreq1 { get; init; }

    public string? Capabilities { get; init; }

    public string? SupportedRates { get; init; }

    public string? Security { get; init; }

    public IReadOnlyList<MfgRecord> MfgRecords { get; init; } = [];

    public IReadOnlyList<VendorIeRecord> VendorIes { get; init; } = [];

    public IReadOnlyList<ServiceDataRecord> ServiceData { get; init; } = [];

    /// <summary>These facts updated by a newer advert: newer values win, nothing known is cleared.</summary>
    public RadioFacts Merge(RadioFacts newer)
    {
        ArgumentNullException.ThrowIfNull(newer);
        return this with
        {
            TxPowerDbm = newer.TxPowerDbm ?? TxPowerDbm,
            AdvFlags = newer.AdvFlags ?? AdvFlags,
            Appearance = newer.Appearance ?? Appearance,
            AddressType = newer.AddressType ?? AddressType,
            AdvertisingIntervalMs = newer.AdvertisingIntervalMs ?? AdvertisingIntervalMs,
            PeriodicIntervalMs = newer.PeriodicIntervalMs ?? PeriodicIntervalMs,
            // Scan responses report not connectable; keep Yes once any advert was.
            Connectable = Connectable == true || newer.Connectable == true ? true : newer.Connectable ?? Connectable,
            PrimaryPhy = newer.PrimaryPhy ?? PrimaryPhy,
            SecondaryPhy = newer.SecondaryPhy ?? SecondaryPhy,
            DeviceClass = newer.DeviceClass ?? DeviceClass,
            WifiStandard = newer.WifiStandard ?? WifiStandard,
            ChannelWidth = newer.ChannelWidth ?? ChannelWidth,
            CenterFreq0 = newer.CenterFreq0 ?? CenterFreq0,
            CenterFreq1 = newer.CenterFreq1 ?? CenterFreq1,
            Capabilities = NonBlank(newer.Capabilities) ?? Capabilities,
            SupportedRates = NonBlank(newer.SupportedRates) ?? SupportedRates,
            Security = NonBlank(newer.Security) ?? Security,
            MfgRecords = MergeMfg(MfgRecords, newer.MfgRecords),
            VendorIes = MergeVendorIes(VendorIes, newer.VendorIes),
            ServiceData = MergeServiceData(ServiceData, newer.ServiceData),
        };
    }

    private static string? NonBlank(string? s) => Strings.IsBlank(s) ? null : s;

    private static IReadOnlyList<MfgRecord> MergeMfg(IReadOnlyList<MfgRecord> old, IReadOnlyList<MfgRecord> extra)
    {
        if (extra.Count == 0)
        {
            return old;
        }
        if (old.Count == 0)
        {
            return extra;
        }
        var output = new List<MfgRecord>(old.Count + extra.Count);
        output.AddRange(old);
        foreach (var next in extra)
        {
            var prefix = Strings.Take(next.DataHex, 2).ToUpperInvariant();
            var idx = output.FindIndex(r => r.CompanyId == next.CompanyId && Strings.Take(r.DataHex, 2).ToUpperInvariant() == prefix);
            if (idx < 0)
            {
                output.Add(next);
            }
            else if (next.DataHex.Length >= output[idx].DataHex.Length)
            {
                output[idx] = next;
            }
        }
        return output.Count <= 8 ? output : output.GetRange(0, 8);
    }

    private static IReadOnlyList<VendorIeRecord> MergeVendorIes(IReadOnlyList<VendorIeRecord> old, IReadOnlyList<VendorIeRecord> extra)
    {
        if (extra.Count == 0)
        {
            return old;
        }
        return old.Concat(extra)
            .DistinctBy(r => (r.Oui, r.Type, Strings.Take(r.DataHex, 16)))
            .Take(12)
            .ToList();
    }

    private static IReadOnlyList<ServiceDataRecord> MergeServiceData(IReadOnlyList<ServiceDataRecord> old, IReadOnlyList<ServiceDataRecord> extra)
    {
        if (extra.Count == 0)
        {
            return old;
        }
        var by = new OrderedDictionary<string, ServiceDataRecord>();
        foreach (var rec in old)
        {
            by[ServiceDataMergeKey(rec)] = rec;
        }
        foreach (var rec in extra)
        {
            var key = ServiceDataMergeKey(rec);
            if (!by.TryGetValue(key, out var prev) || rec.DataHex.Length >= prev.DataHex.Length)
            {
                by[key] = rec;
            }
        }
        return by.Values.ToList();
    }

    /// <summary>Eddystone FEAA rotates UID / URL / TLM frames; keep one slot per frame type.</summary>
    private static string ServiceDataMergeKey(ServiceDataRecord rec)
    {
        var uuidHex = Strings.HexOnly(rec.Uuid);
        var shortUuid = uuidHex.Length switch
        {
            4 => uuidHex,
            32 when uuidHex.StartsWith("0000", StringComparison.Ordinal) => uuidHex.Substring(4, 4),
            _ => uuidHex,
        };
        if (shortUuid == "FEAA")
        {
            var frame = Strings.Take(Strings.HexOnly(rec.DataHex), 2);
            if (frame.Length == 2)
            {
                return "FEAA:" + frame;
            }
        }
        return Strings.IsBlank(uuidHex) ? rec.Uuid : uuidHex;
    }
}
