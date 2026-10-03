// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/data/DeviceStore.kt (github.com/offgridpete/fieldwatch, cf6562d).
// Differences: the clock is a TimeProvider; Devices and Stats are snapshots instead of Kotlin flows; the log-line
// counter is not ported; and no maker name is looked up from the address (Fieldwatch's tables for that are IEEE and
// Bluetooth SIG lists — see NOTICE.md), so Sighting.Vendor stays empty.
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

using System.Collections.Concurrent;
using System.Text;

namespace AetherNet.Aware;

/// <summary>
/// Keeps what the radios heard: one <see cref="Sighting"/> per radio and address, labelled by the signatures,
/// marked gone after the linger, and forgotten after 3 minutes (no signature) or 15 (with one).
/// </summary>
public sealed class DeviceStore
{
    private const int HistoryLimit = 40;
    private const long EvictAfterMs = 15 * 60 * 1000L;
    private const long EvictAnonMs = 3 * 60 * 1000L;

    /// <summary>The steady-state count. Fresh radios inside the linger may push past it.</summary>
    private const int MaxLive = 400;

    /// <summary>The ceiling, so a crowd of rotating Bluetooth addresses cannot grow without bound.</summary>
    private const int HardLive = 900;

    private readonly SignatureEngine _engine;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Sighting> _live = new(StringComparer.Ordinal);

    /// <summary>What each radio's labels were worked out from; unchanged means no new search.</summary>
    private readonly Dictionary<string, string> _matchStamp = new(512, StringComparer.Ordinal);

    private IReadOnlyList<Fleet>? _catalogRef;
    private long _lastWifiBatchAt;
    private volatile bool _wifiHold;
    private volatile bool _bleHold;
    private volatile IReadOnlyList<Sighting> _devices = [];
    private ScanStats _stats = new();

    public DeviceStore(SignatureEngine? engine = null, TimeProvider? timeProvider = null)
    {
        _engine = engine ?? new SignatureEngine();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>What was heard, as of the last <see cref="Refresh"/>.</summary>
    public IReadOnlyList<Sighting> Devices => _devices;

    public ScanStats Stats
    {
        get
        {
            lock (_gate)
            {
                return _stats;
            }
        }
    }

    /// <summary>"WIFI:AA:BB:CC:DD:EE:FF" or "BLE:…".</summary>
    public static string KeyOf(RadioKind kind, string mac) =>
        (kind == RadioKind.Wifi ? "WIFI:" : "BLE:") + MacUtil.Normalize(mac);

    public Sighting Ingest(Observation observation, IReadOnlyList<Fleet> fleets) => IngestBatch([observation], fleets)[0];

    /// <summary>Merges what was just heard and labels it. Returns each radio as it now stands, in the same order.</summary>
    public IReadOnlyList<Sighting> IngestBatch(IReadOnlyList<Observation> observations, IReadOnlyList<Fleet> fleets)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(fleets);
        lock (_gate)
        {
            if (observations.Count == 0)
            {
                return [];
            }
            var output = new List<Sighting>(observations.Count);
            foreach (var observation in observations)
            {
                output.Add(Upsert(observation));
            }
            Relabel(output, fleets);
            for (var i = 0; i < output.Count; i++)
            {
                output[i] = _live.GetValueOrDefault(output[i].Key) ?? output[i];
            }
            return output;
        }
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private Sighting Upsert(Observation observation)
    {
        var mac = MacUtil.Normalize(observation.Mac);
        var key = KeyOf(observation.Kind, mac);
        var now = observation.At > 0 ? observation.At : Now();
        _live.TryGetValue(key, out var existing);
        if (observation.Kind == RadioKind.Wifi && !observation.Fresh && existing is not null)
        {
            var refreshed = existing with
            {
                Name = Strings.IsBlank(observation.Name) ? existing.Name : observation.Name,
                Rssi = Rssi.Measured(observation.Rssi) ? observation.Rssi : existing.Rssi,
                VendorIeOuis = MergeIes(existing.VendorIeOuis, observation.VendorIeOuis),
                Facts = existing.Facts.Merge(observation.Facts),
                FastPairPairing = existing.FastPairPairing || FastPair.PairingAdvertised(observation.Facts),
            };
            _live[key] = refreshed;
            return refreshed;
        }
        var measured = Rssi.Measured(observation.Rssi);
        var sample = measured ? new RssiSample(now, observation.Rssi) : null;
        Sighting merged;
        if (existing is null)
        {
            merged = new Sighting
            {
                Key = key,
                Kind = observation.Kind,
                Mac = mac,
                Name = observation.Name,
                Rssi = observation.Rssi,
                RssiMin = observation.Rssi,
                RssiMax = observation.Rssi,
                Channel = observation.Channel,
                FrequencyMhz = observation.FrequencyMhz,
                Randomized = MacUtil.IsRandomized(mac),
                HiddenSsid = observation.HiddenSsid,
                ServiceUuids = observation.ServiceUuids,
                ManufacturerId = observation.ManufacturerId,
                ManufacturerDataHex = Strings.Take(observation.ManufacturerDataHex, 512),
                RawHex = Strings.Take(observation.RawHex, 1024),
                Extras = Strings.Take(observation.Extras, 160),
                FirstSeen = now,
                LastSeen = now,
                HitCount = 1,
                RssiHistory = sample is null ? [] : [sample],
                Presence = [new PresenceSpan(now, null)],
                Latitude = observation.Latitude,
                Longitude = observation.Longitude,
                VendorIeOuis = observation.VendorIeOuis,
                Facts = observation.Facts,
                GpsTrail = GpsStart(observation),
                FastPairPairing = FastPair.PairingAdvertised(observation.Facts),
            };
        }
        else
        {
            IReadOnlyList<RssiSample> history = sample is null
                ? existing.RssiHistory
                : existing.RssiHistory.Count >= HistoryLimit
                    ? [.. existing.RssiHistory.Skip(existing.RssiHistory.Count - HistoryLimit + 1), sample]
                    : [.. existing.RssiHistory, sample];
            var last = existing.Presence.Count > 0 ? existing.Presence[^1] : null;
            IReadOnlyList<PresenceSpan> presence = last is null || last.End is not null
                ? [.. existing.Presence, new PresenceSpan(now, null)]
                : existing.Presence;
            IReadOnlyList<string> uuids = observation.ServiceUuids.Count == 0
                ? existing.ServiceUuids
                : existing.ServiceUuids.Concat(observation.ServiceUuids).Distinct().ToList();
            merged = existing with
            {
                Name = Strings.IsBlank(observation.Name) ? existing.Name : observation.Name,
                Rssi = measured ? observation.Rssi : existing.Rssi,
                RssiMin = measured
                    ? Rssi.Measured(existing.RssiMin) ? Math.Min(existing.RssiMin, observation.Rssi) : observation.Rssi
                    : existing.RssiMin,
                RssiMax = measured
                    ? Rssi.Measured(existing.RssiMax) ? Math.Max(existing.RssiMax, observation.Rssi) : observation.Rssi
                    : existing.RssiMax,
                Channel = observation.Channel != 0 ? observation.Channel : existing.Channel,
                FrequencyMhz = observation.FrequencyMhz != 0 ? observation.FrequencyMhz : existing.FrequencyMhz,
                HiddenSsid = existing.HiddenSsid || observation.HiddenSsid,
                ServiceUuids = uuids,
                ManufacturerId = existing.ManufacturerId ?? observation.ManufacturerId,
                ManufacturerDataHex = MergeMfgHex(existing.ManufacturerDataHex, observation.ManufacturerDataHex),
                RawHex = observation.RawHex.Length >= existing.RawHex.Length ? Strings.Take(observation.RawHex, 1024) : existing.RawHex,
                LastSeen = now,
                HitCount = existing.HitCount + 1,
                RssiHistory = history,
                Presence = presence,
                Latitude = observation.Latitude ?? existing.Latitude,
                Longitude = observation.Longitude ?? existing.Longitude,
                GpsTrail = GpsAppend(existing.GpsTrail, observation),
                Gone = false,
                VendorIeOuis = MergeIes(existing.VendorIeOuis, observation.VendorIeOuis),
                Facts = existing.Facts.Merge(observation.Facts),
                FastPairPairing = existing.FastPairPairing || FastPair.PairingAdvertised(observation.Facts),
            };
        }
        _live[key] = merged;
        if (observation.Kind == RadioKind.Wifi && observation.Fresh)
        {
            _lastWifiBatchAt = Now();
        }
        return merged;
    }

    private void Relabel(IReadOnlyCollection<Sighting> touched, IReadOnlyList<Fleet> fleets)
    {
        if (!ReferenceEquals(fleets, _catalogRef))
        {
            _catalogRef = fleets;
            _matchStamp.Clear();
        }
        var cluster = fleets.Any(f => f.MinPeers > 0 || f.ClusterByOui || f.SequentialMac);
        List<Sighting> dirty;
        if (cluster || _matchStamp.Count == 0)
        {
            dirty = [.. _live.Values];
        }
        else
        {
            dirty = new List<Sighting>(touched.Count);
            foreach (var d in touched)
            {
                var cur = _live.GetValueOrDefault(d.Key) ?? d;
                if (_matchStamp.GetValueOrDefault(cur.Key) != MatchIdentity(cur))
                {
                    dirty.Add(cur);
                }
            }
        }
        if (dirty.Count == 0)
        {
            return;
        }
        IReadOnlyCollection<Sighting> pool = cluster ? [.. _live.Values] : dirty;
        var matches = _engine.Match(pool, fleets, Now());
        var update = cluster ? [.. _live.Values] : dirty;
        foreach (var d in update)
        {
            if (!_live.TryGetValue(d.Key, out var cur))
            {
                continue;
            }
            var ids = matches.GetValueOrDefault(cur.Key) ?? [];
            var labeled = SameIds(ids, cur.FleetIds) ? cur : cur with { FleetIds = ids };
            var next = PayloadLocation.ApplySticky(labeled, fleets).WithLiveDecode(fleets);
            if (!ReferenceEquals(next, cur))
            {
                _live[cur.Key] = next;
            }
            _matchStamp[cur.Key] = MatchIdentity(next);
        }
    }

    private static bool SameIds(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.All(b.Contains);

    /// <summary>Name, vendor elements, UUIDs and payloads — not loudness, position or gone.</summary>
    private static string MatchIdentity(Sighting device)
    {
        var sb = new StringBuilder(64 + device.Name.Length + (device.VendorIeOuis.Count * 10));
        sb.Append(device.Name).Append('\u0001').Append(device.HiddenSsid ? 'H' : '-').Append('\u0001');
        foreach (var ie in device.VendorIeOuis)
        {
            sb.Append(ie).Append(',');
        }
        sb.Append('\u0001');
        foreach (var uuid in device.ServiceUuids)
        {
            sb.Append(uuid).Append(',');
        }
        sb.Append('\u0001').Append(device.ManufacturerId ?? -1).Append('\u0001').Append(device.ManufacturerDataHex).Append('\u0001');
        foreach (var m in device.Facts.MfgRecords)
        {
            sb.Append(m.CompanyId).Append('=').Append(m.DataHex).Append(',');
        }
        sb.Append('\u0001');
        foreach (var s in device.Facts.ServiceData)
        {
            sb.Append(s.Uuid).Append('=').Append(s.DataHex).Append(',');
        }
        return sb.ToString();
    }

    /// <summary>Radios with a trail that are still being heard stay through a crowd.</summary>
    private static bool CrowdPinned(Sighting device, long now, long lingerMs) =>
        now - device.LastSeen <= lingerMs && device.GpsTrail.Count >= 2;

    /// <summary>Drops unlabelled radios past the linger first; fresh ones stay until <see cref="HardLive"/>.</summary>
    private void EvictOverflow(long now, long lingerMs)
    {
        if (_live.Count <= MaxLive)
        {
            return;
        }
        var expired = _live.Values.Where(d => d.FleetIds.Count == 0 && now - d.LastSeen > lingerMs).OrderBy(d => d.LastSeen).ToList();
        Drop(expired, _live.Count - MaxLive);
        if (_live.Count <= MaxLive || _live.Count <= HardLive)
        {
            return;
        }
        var stillUnnamed = _live.Values.Where(d => d.FleetIds.Count == 0 && !CrowdPinned(d, now, lingerMs)).OrderBy(d => d.LastSeen).ToList();
        Drop(stillUnnamed, _live.Count - HardLive);
        if (_live.Count <= HardLive)
        {
            return;
        }
        var unpinned = _live.Values.Where(d => !CrowdPinned(d, now, lingerMs)).OrderBy(d => d.LastSeen).ToList();
        Drop(unpinned, _live.Count - HardLive);
        if (_live.Count <= HardLive)
        {
            return;
        }
        Drop([.. _live.Values.OrderBy(d => d.LastSeen)], _live.Count - HardLive);
    }

    private void Drop(List<Sighting> rows, int count)
    {
        foreach (var row in rows.Take(Math.Max(count, 0)))
        {
            _live.TryRemove(row.Key, out _);
            _matchStamp.Remove(row.Key);
        }
    }

    /// <summary>
    /// Marks radios gone or back, relabels, forgets old ones, and publishes <see cref="Devices"/> and
    /// <see cref="Stats"/>. A radio is gone when not heard for max(<paramref name="staleSec"/> (at least 15),
    /// <paramref name="decaySec"/>) seconds. <paramref name="tick"/> counts one more frame or advert.
    /// </summary>
    public void Refresh(IReadOnlyList<Fleet> fleets, int staleSec, Observation? tick = null, int decaySec = 0)
    {
        ArgumentNullException.ThrowIfNull(fleets);
        lock (_gate)
        {
            var now = Now();
            var staleMs = Math.Max(staleSec, 15) * 1000L;
            var lingerMs = Math.Max(staleMs, Math.Max(decaySec, 0) * 1000L);
            var wifiScanFresh = _lastWifiBatchAt != 0 && now - _lastWifiBatchAt <= lingerMs;
            foreach (var device in _live.Values)
            {
                var gone = !StillHeard(device.Kind, device.LastSeen, device.Gone, now, lingerMs, wifiScanFresh, _wifiHold, _bleHold);
                var presence = device.Presence;
                var last = presence.Count > 0 ? presence[^1] : null;
                if (gone && last is { End: null })
                {
                    presence = [.. presence.Take(presence.Count - 1), last with { End = device.LastSeen }];
                }
                else if (!gone && last is { End: not null })
                {
                    presence = [.. presence.Take(presence.Count - 1), last with { End = null }];
                }
                if (device.Gone != gone || !ReferenceEquals(presence, device.Presence))
                {
                    _live[device.Key] = device with { Gone = gone, Presence = presence };
                }
            }
            Relabel([.. _live.Values], fleets);
            foreach (var device in _live.Values)
            {
                var cut = device.FleetIds.Count == 0 ? EvictAnonMs : EvictAfterMs;
                if (device.LastSeen < now - cut)
                {
                    _live.TryRemove(device.Key, out _);
                    _matchStamp.Remove(device.Key);
                }
            }
            EvictOverflow(now, lingerMs);
            var published = _live.Values.ToList();
            _devices = published;
            var prev = _stats;
            _stats = prev with
            {
                WifiFrames = prev.WifiFrames + (tick?.Kind == RadioKind.Wifi ? 1 : 0),
                BleAdvs = prev.BleAdvs + (tick?.Kind == RadioKind.Ble ? 1 : 0),
                DevicesSeen = published.Count,
                NamedNow = published.Count(d => !d.Gone && d.FleetIds.Count > 0),
                WifiNow = published.Count(d =>
                    d.Kind == RadioKind.Wifi && (now - d.LastSeen <= staleMs || !wifiScanFresh || _wifiHold)),
                BleNow = published.Count(d => d.Kind == RadioKind.Ble && (!d.Gone || _bleHold)),
                LastWifiScanAt = tick is { Kind: RadioKind.Wifi, Fresh: true } ? now : prev.LastWifiScanAt,
            };
        }
    }

    public void SetScanning(bool on, string throttleHint = "")
    {
        lock (_gate)
        {
            _stats = _stats with { Scanning = on, ThrottleHint = throttleHint };
        }
    }

    /// <summary>
    /// While a radio restarts, keep its live rows from flipping gone. A hold never brings back a row already gone.
    /// </summary>
    public void SetRadioHold(bool? wifi = null, bool? ble = null)
    {
        _wifiHold = wifi ?? _wifiHold;
        _bleHold = ble ?? _bleHold;
    }

    /// <summary>Forgets every radio's trail and position (a new walk).</summary>
    public void ClearGpsTrails()
    {
        lock (_gate)
        {
            foreach (var device in _live.Values)
            {
                if (device.GpsTrail.Count > 0 || device.Latitude is not null || device.Longitude is not null)
                {
                    _live[device.Key] = device with { GpsTrail = [], Latitude = null, Longitude = null };
                }
            }
            _devices = _live.Values.ToList();
        }
    }

    public Sighting? Find(string key) => _live.GetValueOrDefault(key);

    /// <summary>
    /// Still heard: within the linger, or — while its radio is on hold, or Wi-Fi has had no fresh scan — not already
    /// gone. A late scan or a restart never brings back a row that already aged out.
    /// </summary>
    public static bool StillHeard(
        RadioKind kind,
        long lastSeen,
        bool alreadyGone,
        long now,
        long lingerMs,
        bool wifiScanFresh,
        bool wifiHold,
        bool bleHold)
    {
        if (now - lastSeen <= lingerMs)
        {
            return true;
        }
        return kind == RadioKind.Wifi
            ? (wifiHold || !wifiScanFresh) && !alreadyGone
            : bleHold && !alreadyGone;
    }

    private static IReadOnlyList<GpsSample> GpsStart(Observation observation)
    {
        if (observation.Latitude is not { } lat || observation.Longitude is not { } lon)
        {
            return [];
        }
        return [new GpsSample(observation.At, lat, lon, Rssi.Measured(observation.Rssi) ? observation.Rssi : 0)];
    }

    private static IReadOnlyList<GpsSample> GpsAppend(IReadOnlyList<GpsSample> trail, Observation observation)
    {
        if (observation.Latitude is not { } lat || observation.Longitude is not { } lon)
        {
            return trail;
        }
        return Geo.Append(trail, observation.At, lat, lon, Rssi.Measured(observation.Rssi) ? observation.Rssi : 0);
    }

    private static string MergeMfgHex(string old, string extra)
    {
        if (Strings.IsBlank(extra))
        {
            return old;
        }
        if (Strings.IsBlank(old))
        {
            return Strings.Take(extra, 512);
        }
        if (Strings.Take(extra, 2).Equals(Strings.Take(old, 2), StringComparison.OrdinalIgnoreCase) && extra.Length >= old.Length)
        {
            return Strings.Take(extra, 512);
        }
        return old;
    }

    private static IReadOnlyList<string> MergeIes(IReadOnlyList<string> old, IReadOnlyList<string> extra)
    {
        if (extra.Count == 0)
        {
            return old;
        }
        return old.Count == 0 ? extra : old.Concat(extra).Distinct().ToList();
    }
}
