// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/SignatureEngine.kt (github.com/offgridpete/fieldwatch,
// cf6562d). suggestFleet ("create a signature from this radio") is not ported yet.
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

using System.Text.RegularExpressions;

namespace AetherNet.Aware;

/// <summary>
/// Matches what was heard against signatures. The catalog's thousands of address prefixes are indexed once per
/// signature list (by reference), so matching stays cheap on every refresh.
/// </summary>
public sealed class SignatureEngine
{
    /// <summary>802.11 WPA (Microsoft) and RSN (IEEE) vendor-element OUIs: protocol tags, not a product.</summary>
    private static readonly HashSet<string> WifiProtocolIeOuis = ["0050F2", "000FAC"];

    private const string IBeaconFleet = "fleet-ibeacon";

    private volatile CompiledCatalog? _cache;

    /// <summary>
    /// The signatures each radio matches, keyed by <see cref="Sighting.Key"/>. Every radio given gets an entry, empty
    /// when nothing matched. <paramref name="now"/> (Unix ms) only matters to signatures that need several radios
    /// heard together.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Match(
        IReadOnlyCollection<Sighting> devices,
        IReadOnlyList<Fleet> fleets,
        long? now = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(fleets);
        var compiled = CompiledFor(fleets);
        var byKey = new Dictionary<string, List<string>>(devices.Count);
        foreach (var device in devices)
        {
            var hits = new List<string>();
            var ouiHit = compiled.OuiHits(device);
            foreach (var idx in compiled.PlansFor(device.Kind))
            {
                var plan = compiled.Plans[idx];
                if (!plan.Cluster && FleetHits(device, plan, ouiHit[idx]))
                {
                    AddDistinct(hits, plan.Id);
                }
            }
            DropProtocolIBeacon(hits, compiled);
            DropDjiWhenOsmoCamera(hits);
            DropAirTagsWhenAppleDevice(hits, device);
            DropCiscoWhenMeraki(hits);
            byKey[device.Key] = hits;
        }
        ApplyClusters(devices, compiled, byKey, now ?? TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
        var result = new Dictionary<string, IReadOnlyList<string>>(byKey.Count);
        foreach (var (key, ids) in byKey)
        {
            result[key] = ids;
        }
        return result;
    }

    private static void AddDistinct(List<string> list, string id)
    {
        if (!list.Contains(id))
        {
            list.Add(id);
        }
    }

    private static bool NeedsCluster(Fleet fleet) => fleet.MinPeers > 0 || fleet.ClusterByOui || fleet.SequentialMac;

    private Compiled CompiledFor(IReadOnlyList<Fleet> fleets)
    {
        var hit = _cache;
        if (hit is not null && ReferenceEquals(hit.Fleets, fleets))
        {
            return hit.Compiled;
        }
        var next = Compile(fleets);
        _cache = new CompiledCatalog(fleets, next);
        return next;
    }

    private static Compiled Compile(IReadOnlyList<Fleet> fleets)
    {
        var plans = new List<FleetPlan>(fleets.Count);
        var bssidWifi = new Dictionary<string, List<int>>(4096);
        var bssidBle = new Dictionary<string, List<int>>(256);
        var vendorIe = new Dictionary<string, List<int>>(4096);
        var longOui = new List<LongOui>(8);
        var wifiPlans = new List<int>(fleets.Count);
        var blePlans = new List<int>(fleets.Count);
        for (var idx = 0; idx < fleets.Count; idx++)
        {
            var fleet = fleets[idx];
            var active = fleet.Rules.Where(r => r.Enabled).ToList();
            var other = new List<FastRule>(8);
            foreach (var rule in active)
            {
                switch (rule.Kind)
                {
                    case RuleKind.Oui:
                        IndexOui(rule.Text, rule.Radio, idx, bssid: true, vendor: true, bssidWifi, bssidBle, vendorIe, longOui);
                        break;
                    case RuleKind.MacPrefix:
                        IndexOui(rule.Text, rule.Radio, idx, bssid: true, vendor: false, bssidWifi, bssidBle, vendorIe, longOui);
                        break;
                    case RuleKind.VendorIeOui:
                        IndexOui(rule.Text, rule.Radio, idx, bssid: false, vendor: true, bssidWifi, bssidBle, vendorIe, longOui);
                        break;
                    default:
                        if (CompileOther(rule) is { } fast)
                        {
                            other.Add(fast);
                        }
                        break;
                }
            }
            plans.Add(new FleetPlan(fleet.Id, fleet, NeedsCluster(fleet), fleet.MatchAny, other, active));
            switch (RadioScope(active))
            {
                case RadioKind.Wifi:
                    wifiPlans.Add(idx);
                    break;
                case RadioKind.Ble:
                    blePlans.Add(idx);
                    break;
                default:
                    wifiPlans.Add(idx);
                    blePlans.Add(idx);
                    break;
            }
        }
        return new Compiled(plans, [.. wifiPlans], [.. blePlans], Freeze(bssidWifi), Freeze(bssidBle), Freeze(vendorIe), longOui);
    }

    private static void IndexOui(
        string text,
        RadioKind? radio,
        int fleetIdx,
        bool bssid,
        bool vendor,
        Dictionary<string, List<int>> bssidWifi,
        Dictionary<string, List<int>> bssidBle,
        Dictionary<string, List<int>> vendorIe,
        List<LongOui> longOui)
    {
        var hex = Strings.HexOnly(text);
        if (hex.Length == 0)
        {
            return;
        }
        if (hex.Length != 6)
        {
            longOui.Add(new LongOui(hex, radio, bssid, vendor, fleetIdx));
            return;
        }
        if (bssid)
        {
            if (radio != RadioKind.Ble)
            {
                AddIdx(bssidWifi, hex, fleetIdx);
            }
            if (radio != RadioKind.Wifi)
            {
                AddIdx(bssidBle, hex, fleetIdx);
            }
        }
        if (vendor && radio != RadioKind.Ble && !WifiProtocolIeOuis.Contains(hex))
        {
            AddIdx(vendorIe, hex, fleetIdx);
        }
    }

    private static void AddIdx(Dictionary<string, List<int>> map, string key, int idx)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<int>(2);
            map[key] = list;
        }
        if (!list.Contains(idx))
        {
            list.Add(idx);
        }
    }

    private static Dictionary<string, int[]> Freeze(Dictionary<string, List<int>> map)
    {
        var output = new Dictionary<string, int[]>(map.Count, StringComparer.Ordinal);
        foreach (var (k, v) in map)
        {
            output[k] = [.. v];
        }
        return output;
    }

    /// <summary>
    /// Wi-Fi: every rule is Wi-Fi-only. BLE: every rule is Bluetooth-only. Null: mixed or unscoped, searched on both.
    /// </summary>
    private static RadioKind? RadioScope(List<MatchRule> rules)
    {
        var wifi = false;
        var ble = false;
        var both = false;
        foreach (var rule in rules)
        {
            switch (RuleScope(rule))
            {
                case RadioKind.Wifi:
                    wifi = true;
                    break;
                case RadioKind.Ble:
                    ble = true;
                    break;
                default:
                    both = true;
                    break;
            }
        }
        if (both || (wifi && ble))
        {
            return null;
        }
        return wifi ? RadioKind.Wifi : ble ? RadioKind.Ble : null;
    }

    private static RadioKind? RuleScope(MatchRule rule) => rule.Kind switch
    {
        RuleKind.VendorIeOui or RuleKind.HiddenSsid => RadioKind.Wifi,
        RuleKind.RadioKind => rule.Radio,
        RuleKind.ServiceUuid or RuleKind.ServiceData or RuleKind.ManufacturerId or RuleKind.ManufacturerData =>
            rule.Radio ?? RadioKind.Ble,
        _ => rule.Radio,
    };

    private static FastRule? CompileOther(MatchRule rule)
    {
        switch (rule.Kind)
        {
            case RuleKind.NameContains:
                return Strings.IsBlank(rule.Text) ? null : new ContainsRule(rule.Text, rule.Radio);
            case RuleKind.NameGlob:
                return Strings.IsBlank(rule.Text)
                    ? null
                    : new GlobRule(new Regex(TextMatch.GlobRegex(rule.Text), TextMatch.GlobOptions), rule.Radio);
            case RuleKind.ServiceUuid:
                return Strings.IsBlank(rule.Text) ? null : new UuidRule(Uuids.Aliases(rule.Text), rule.Radio);
            case RuleKind.ManufacturerId:
                return new MfgIdRule(rule.CompanyId, rule.Radio);
            case RuleKind.ManufacturerData:
            {
                var prefix = Strings.HexOnly(rule.DataPrefixHex);
                return prefix.Length == 0 ? null : new MfgDataRule(rule.CompanyId, prefix, rule.Radio);
            }
            case RuleKind.ServiceData:
            {
                var prefix = Strings.HexOnly(rule.DataPrefixHex);
                var aliases = Uuids.Aliases(rule.Text).Where(a => !Strings.IsBlank(a)).ToHashSet(StringComparer.Ordinal);
                if (prefix.Length == 0 && aliases.Count == 0)
                {
                    return null;
                }
                return new SvcDataRule(aliases, prefix, rule.Radio, contains: Strings.IsBlank(rule.Text) && prefix.Length > 0);
            }
            case RuleKind.RadioKind:
                return new RadioRule(rule.Radio);
            case RuleKind.HiddenSsid:
                return new HiddenRule();
            default:
                return null;
        }
    }

    private static bool FleetHits(Sighting device, FleetPlan plan, bool ouiHit)
    {
        if (plan.Id == IBeaconFleet && IsTeslaPhoneKeyIBeacon(device))
        {
            return false;
        }
        if (plan.RawRules.Count == 0)
        {
            return false;
        }
        if (!plan.MatchAny)
        {
            return plan.RawRules.All(r => RuleHits(device, r));
        }
        if (ouiHit)
        {
            return true;
        }
        return plan.OtherRules.Count > 0 && plan.OtherRules.Any(r => r.Hits(device));
    }

    /// <summary>
    /// iBeacon is a payload layout, not a product. If the radio already matched a signature that is not a beacon
    /// (a Sony TV, a Tesla phone key…), drop the iBeacon label. Beacon-class rows stay dual-labelled.
    /// </summary>
    private static void DropProtocolIBeacon(List<string> hits, Compiled compiled)
    {
        if (!hits.Contains(IBeaconFleet))
        {
            return;
        }
        var otherProduct = hits.Any(id =>
            id != IBeaconFleet && compiled.Plans.Any(p => p.Id == id && p.Fleet.Kind != SignatureClass.Beacon));
        if (otherProduct)
        {
            hits.Remove(IBeaconFleet);
        }
    }

    /// <summary>Osmo cameras share DJI's company ID; prefer the Osmo row.</summary>
    private static void DropDjiWhenOsmoCamera(List<string> hits)
    {
        if (hits.Contains("fleet-osmo"))
        {
            hits.Remove("fleet-dji");
        }
    }

    /// <summary>Meraki access points carry Cisco's vendor element; the BSSID is Meraki, so do not also label Cisco.</summary>
    private static void DropCiscoWhenMeraki(List<string> hits)
    {
        if (hits.Contains("fleet-meraki"))
        {
            hits.Remove("fleet-cisco");
        }
    }

    /// <summary>
    /// Offline Finding (0x12) is the Find My network's protocol, not an AirTag: iPhones send it too. Drop the AirTag
    /// label when the radio is already an Apple device, unless it is named AirTag or carries Find My's FD44 UUID.
    /// </summary>
    private static void DropAirTagsWhenAppleDevice(List<string> hits, Sighting device)
    {
        if (!hits.Contains("fleet-airtag"))
        {
            return;
        }
        if (device.Name.Contains("AirTag", StringComparison.OrdinalIgnoreCase) || HasFindMyAccessoryUuid(device))
        {
            return;
        }
        var appleProduct = hits.Contains("fleet-apple-device") || hits.Contains("fleet-apple-audio");
        if (appleProduct || TrackerMatch.IsAppleContinuity(device))
        {
            hits.Remove("fleet-airtag");
        }
    }

    private static bool HasFindMyAccessoryUuid(Sighting device)
    {
        var want = Uuids.Aliases("FD44");
        return AdvertisedUuids(device).Any(uuid => Uuids.Aliases(uuid).Any(want.Contains));
    }

    /// <summary>Tesla phone-key adverts use Apple's iBeacon layout so iOS can find the car: Tesla, not a beacon.</summary>
    private static bool IsTeslaPhoneKeyIBeacon(Sighting device) =>
        MfgRecords(device).Any(r =>
            r.CompanyId == 0x004C &&
            Strings.HexOnly(r.DataHex).StartsWith(StockSignatures.TeslaIBeaconMfgPrefix, StringComparison.Ordinal));

    private static bool RuleHits(Sighting device, MatchRule rule)
    {
        if (rule.Kind != RuleKind.RadioKind && rule.Radio is { } radio && device.Kind != radio)
        {
            return false;
        }
        switch (rule.Kind)
        {
            case RuleKind.Oui:
            case RuleKind.MacPrefix:
                return MacUtil.MatchesPrefix(device.Mac, rule.Text) ||
                    (rule.Kind == RuleKind.Oui && WifiVendorIeHitsOui(device, rule.Text)) ||
                    (rule.Kind == RuleKind.Oui && RecoveredWifiOuiHits(device, rule.Text));
            case RuleKind.NameContains:
                return !Strings.IsBlank(device.Name) && TextMatch.Contains(device.Name, rule.Text);
            case RuleKind.NameGlob:
                return !Strings.IsBlank(device.Name) && TextMatch.Glob(device.Name, rule.Text);
            case RuleKind.ServiceUuid:
            {
                var want = Uuids.Aliases(rule.Text);
                return AdvertisedUuids(device).Any(uuid => Uuids.Aliases(uuid).Any(want.Contains));
            }
            case RuleKind.ManufacturerId:
                return MfgRecords(device).Any(r => r.CompanyId == rule.CompanyId);
            case RuleKind.ManufacturerData:
            {
                var prefix = Strings.HexOnly(rule.DataPrefixHex);
                return prefix.Length > 0 && MfgRecords(device).Any(r =>
                    (rule.CompanyId == 0 || r.CompanyId == rule.CompanyId) &&
                    Strings.HexOnly(r.DataHex).StartsWith(prefix, StringComparison.Ordinal));
            }
            case RuleKind.ServiceData:
            {
                var prefix = Strings.HexOnly(rule.DataPrefixHex);
                var aliases = Uuids.Aliases(rule.Text).Where(a => !Strings.IsBlank(a)).ToHashSet(StringComparer.Ordinal);
                return (prefix.Length > 0 || aliases.Count > 0) &&
                    ServiceDataHits(device, aliases, prefix, contains: Strings.IsBlank(rule.Text) && prefix.Length > 0);
            }
            case RuleKind.RadioKind:
                return rule.Radio is null || device.Kind == rule.Radio;
            case RuleKind.HiddenSsid:
                return device.HiddenSsid;
            case RuleKind.VendorIeOui:
                return WifiVendorIeHitsOui(device, rule.Text);
            default:
                return false;
        }
    }

    /// <summary>A virtual BSSID (guest, mesh) sets the local bit on the maker's own 24-bit OUI.</summary>
    private static bool RecoveredWifiOuiHits(Sighting device, string prefix)
    {
        if (device.Kind != RadioKind.Wifi || MacUtil.WifiOui24Universal(device.Mac) is not { } universal)
        {
            return false;
        }
        var want = Strings.LettersAndDigits(prefix).ToUpperInvariant();
        return want.Length == 6 && universal == want;
    }

    /// <summary>Wi-Fi vendor elements, not the BSSID. WPA and RSN tags are protocol, not the product.</summary>
    private static bool WifiVendorIeHitsOui(Sighting device, string prefix)
    {
        if (device.Kind != RadioKind.Wifi)
        {
            return false;
        }
        var want = Strings.HexOnly(prefix);
        if (want.Length == 0 || WifiProtocolIeOuis.Contains(Strings.Take(want, 6)))
        {
            return false;
        }
        return device.VendorIeOuis.Any(ie =>
        {
            var hex = Strings.HexOnly(ie);
            return !WifiProtocolIeOuis.Contains(Strings.Take(hex, 6)) && hex.StartsWith(want, StringComparison.Ordinal);
        });
    }

    private static IEnumerable<string> AdvertisedUuids(Sighting device) =>
        device.ServiceUuids.Concat(device.Facts.ServiceData.Select(s => s.Uuid));

    internal static IReadOnlyList<MfgRecord> MfgRecords(Sighting device)
    {
        if (device.Facts.MfgRecords.Count > 0)
        {
            return device.Facts.MfgRecords;
        }
        return device.ManufacturerId is { } id ? [new MfgRecord(id, device.ManufacturerDataHex)] : [];
    }

    private static bool ServiceDataHits(Sighting device, IReadOnlySet<string> aliases, string prefix, bool contains)
    {
        IReadOnlyList<string> needles = [prefix];
        if (contains)
        {
            var reversed = ReverseHexBytes(prefix);
            if (reversed.Length > 0 && reversed != prefix)
            {
                needles = [prefix, reversed];
            }
        }
        return device.Facts.ServiceData.Any(rec =>
        {
            if (aliases.Count > 0 && !Uuids.Aliases(rec.Uuid).Any(aliases.Contains))
            {
                return false;
            }
            var data = Strings.HexOnly(rec.DataHex);
            return contains
                ? needles.Any(n => data.Contains(n, StringComparison.Ordinal))
                : data.StartsWith(prefix, StringComparison.Ordinal);
        });
    }

    private static string ReverseHexBytes(string hex)
    {
        var h = Strings.HexOnly(hex);
        if (h.Length < 2 || h.Length % 2 != 0)
        {
            return "";
        }
        var chars = new char[h.Length];
        for (int i = h.Length - 2, o = 0; i >= 0; i -= 2, o += 2)
        {
            chars[o] = h[i];
            chars[o + 1] = h[i + 1];
        }
        return new string(chars);
    }

    private static void ApplyClusters(
        IReadOnlyCollection<Sighting> devices,
        Compiled compiled,
        Dictionary<string, List<string>> byKey,
        long now)
    {
        for (var idx = 0; idx < compiled.Plans.Count; idx++)
        {
            var plan = compiled.Plans[idx];
            if (!plan.Cluster)
            {
                continue;
            }
            var fleet = plan.Fleet;
            var windowMs = (fleet.PeerWindowSec <= 0 ? 60 : fleet.PeerWindowSec) * 1000L;
            var live = devices.Where(d => now - d.LastSeen <= windowMs).ToList();
            foreach (var a in live)
            {
                var eligible = plan.RawRules.Count == 0 ||
                    FleetHits(a, plan, compiled.OuiHits(a)[idx]) ||
                    (fleet.ClusterByOui && (Strings.IsBlank(a.Name) || a.Randomized));
                if (!eligible)
                {
                    continue;
                }
                var peers = 1;
                foreach (var b in live)
                {
                    if (b.Key == a.Key)
                    {
                        continue;
                    }
                    if (fleet.ClusterByOui && a.Oui != b.Oui)
                    {
                        continue;
                    }
                    if (fleet.SequentialMac && Math.Abs(MacUtil.Last16(a.Mac) - MacUtil.Last16(b.Mac)) > 64)
                    {
                        continue;
                    }
                    if (plan.RawRules.Count > 0 && !fleet.ClusterByOui && !FleetHits(b, plan, compiled.OuiHits(b)[idx]))
                    {
                        continue;
                    }
                    peers++;
                }
                var need = fleet.MinPeers <= 0 ? 3 : fleet.MinPeers;
                if (peers >= need)
                {
                    if (!byKey.TryGetValue(a.Key, out var ids))
                    {
                        ids = [];
                        byKey[a.Key] = ids;
                    }
                    AddDistinct(ids, plan.Id);
                }
            }
        }
    }

    private sealed record CompiledCatalog(IReadOnlyList<Fleet> Fleets, Compiled Compiled);

    private sealed record FleetPlan(
        string Id,
        Fleet Fleet,
        bool Cluster,
        bool MatchAny,
        List<FastRule> OtherRules,
        List<MatchRule> RawRules);

    private sealed record LongOui(string Hex, RadioKind? Radio, bool Bssid, bool Vendor, int FleetIdx);

    private sealed class Compiled(
        List<FleetPlan> plans,
        int[] wifiPlans,
        int[] blePlans,
        Dictionary<string, int[]> bssidWifi,
        Dictionary<string, int[]> bssidBle,
        Dictionary<string, int[]> vendorIe,
        List<LongOui> longOui)
    {
        public List<FleetPlan> Plans { get; } = plans;

        public int[] PlansFor(RadioKind kind) => kind == RadioKind.Wifi ? wifiPlans : blePlans;

        public bool[] OuiHits(Sighting device)
        {
            var hits = new bool[Plans.Count];
            var macHex = Strings.HexOnly(device.Mac);
            var oui6 = Strings.Take(macHex, 6);
            var bssidMap = device.Kind == RadioKind.Wifi ? bssidWifi : bssidBle;
            Mark(hits, bssidMap.GetValueOrDefault(oui6));
            if (device.Kind == RadioKind.Wifi)
            {
                if (MacUtil.WifiOui24Universal(device.Mac) is { } universal)
                {
                    Mark(hits, bssidMap.GetValueOrDefault(universal));
                }
                foreach (var ie in device.VendorIeOuis)
                {
                    var hex = Strings.HexOnly(ie);
                    if (hex.Length < 6)
                    {
                        continue;
                    }
                    var ie6 = hex[..6];
                    if (!WifiProtocolIeOuis.Contains(ie6))
                    {
                        Mark(hits, vendorIe.GetValueOrDefault(ie6));
                    }
                }
            }
            if (longOui.Count > 0 && macHex.Length > 0)
            {
                foreach (var rule in longOui)
                {
                    if (rule.Radio is { } radio && radio != device.Kind)
                    {
                        continue;
                    }
                    if (rule.Bssid && macHex.StartsWith(rule.Hex, StringComparison.Ordinal))
                    {
                        hits[rule.FleetIdx] = true;
                    }
                    if (rule.Vendor && device.Kind == RadioKind.Wifi)
                    {
                        foreach (var ie in device.VendorIeOuis)
                        {
                            var hex = Strings.HexOnly(ie);
                            if (!WifiProtocolIeOuis.Contains(Strings.Take(hex, 6)) && hex.StartsWith(rule.Hex, StringComparison.Ordinal))
                            {
                                hits[rule.FleetIdx] = true;
                            }
                        }
                    }
                }
            }
            return hits;
        }

        private static void Mark(bool[] hits, int[]? idxs)
        {
            if (idxs is null)
            {
                return;
            }
            foreach (var i in idxs)
            {
                hits[i] = true;
            }
        }
    }

    private abstract class FastRule
    {
        public abstract bool Hits(Sighting device);

        protected static bool RadioOk(Sighting device, RadioKind? radio) => radio is null || device.Kind == radio;
    }

    private sealed class ContainsRule(string needle, RadioKind? radio) : FastRule
    {
        public override bool Hits(Sighting device) =>
            RadioOk(device, radio) && !Strings.IsBlank(device.Name) && TextMatch.Contains(device.Name, needle);
    }

    private sealed class GlobRule(Regex regex, RadioKind? radio) : FastRule
    {
        public override bool Hits(Sighting device) =>
            RadioOk(device, radio) && !Strings.IsBlank(device.Name) && regex.IsMatch(device.Name);
    }

    private sealed class UuidRule(IReadOnlySet<string> aliases, RadioKind? radio) : FastRule
    {
        public override bool Hits(Sighting device) =>
            RadioOk(device, radio) && AdvertisedUuids(device).Any(uuid => Uuids.Aliases(uuid).Any(aliases.Contains));
    }

    private sealed class MfgIdRule(int id, RadioKind? radio) : FastRule
    {
        public override bool Hits(Sighting device) =>
            RadioOk(device, radio) && MfgRecords(device).Any(r => r.CompanyId == id);
    }

    private sealed class MfgDataRule(int id, string prefix, RadioKind? radio) : FastRule
    {
        public override bool Hits(Sighting device) =>
            RadioOk(device, radio) && MfgRecords(device).Any(r =>
                (id == 0 || r.CompanyId == id) && Strings.HexOnly(r.DataHex).StartsWith(prefix, StringComparison.Ordinal));
    }

    private sealed class SvcDataRule(IReadOnlySet<string> aliases, string prefix, RadioKind? radio, bool contains) : FastRule
    {
        public override bool Hits(Sighting device) =>
            RadioOk(device, radio) && ServiceDataHits(device, aliases, prefix, contains);
    }

    private sealed class RadioRule(RadioKind? kind) : FastRule
    {
        public override bool Hits(Sighting device) => kind is null || device.Kind == kind;
    }

    private sealed class HiddenRule : FastRule
    {
        public override bool Hits(Sighting device) => device.HiddenSsid;
    }
}
