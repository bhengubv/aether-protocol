// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/FilterEngine.kt and the FilterState / FilterPreset
// parts of Models.kt (github.com/offgridpete/fieldwatch, cf6562d). "Arrivals only" is not ported.
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>How the optional filters combine: all must pass, or any one.</summary>
public enum FilterLogic
{
    And,
    Or,
}

/// <summary>Which radios to show.</summary>
public sealed record FilterState
{
    /// <summary>Only radios a signature names.</summary>
    public bool NamedOnly { get; init; }

    /// <summary>Only radios the person has named.</summary>
    public bool CustomNamesOnly { get; init; }

    /// <summary>Only watched signatures, or named radios with alerts on.</summary>
    public bool WatchedOnly { get; init; }

    public bool ExcludeSignatures { get; init; }

    /// <summary>The signatures to hide when <see cref="ExcludeSignatures"/> is on.</summary>
    public IReadOnlySet<string> FleetIds { get; init; } = new HashSet<string>();

    public bool IncludeSignatures { get; init; }

    /// <summary>The signatures to keep when <see cref="IncludeSignatures"/> is on.</summary>
    public IReadOnlySet<string> IncludeFleetIds { get; init; } = new HashSet<string>();

    public bool ShowWifi { get; init; } = true;

    public bool ShowBle { get; init; } = true;

    public int RssiMin { get; init; } = -100;

    public string NameQuery { get; init; } = "";

    public string OuiQuery { get; init; } = "";

    public FilterLogic Logic { get; init; } = FilterLogic.And;

    public bool MovingWithYou { get; init; }

    /// <summary>Hide radios that only match the account-key Fast Pair signature (pairing mode still shows).</summary>
    public bool HideFastPairAccountKey { get; init; }

    public bool UseClassFilter { get; init; }

    public bool ExcludeClasses { get; init; }

    public IReadOnlySet<SignatureClass> Classes { get; init; } = new HashSet<SignatureClass>();
}

/// <summary>A named filter.</summary>
public sealed record FilterPreset(string Id, string Name, FilterState Filter);

public static class FilterEngine
{
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>();
    private static readonly IReadOnlyDictionary<string, SignatureClass> NoClasses = new Dictionary<string, SignatureClass>();

    /// <summary>
    /// Whether the radio passes the filter. <paramref name="classByFleetId"/> gives each signature's class;
    /// <paramref name="namedRadioKeys"/>, <paramref name="watchedFleetIds"/> and <paramref name="alertDeviceKeys"/>
    /// are the person's named radios, watched signatures and radios with alerts on. "Moving with you" needs
    /// <paramref name="travel"/> and <paramref name="now"/> (Unix ms).
    /// </summary>
    public static bool Pass(
        Sighting device,
        FilterState filter,
        CoTravel.Ctx? travel = null,
        long? now = null,
        IReadOnlyDictionary<string, SignatureClass>? classByFleetId = null,
        IReadOnlySet<string>? namedRadioKeys = null,
        IReadOnlySet<string>? watchedFleetIds = null,
        IReadOnlySet<string>? alertDeviceKeys = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(filter);
        classByFleetId ??= NoClasses;
        namedRadioKeys ??= NoKeys;
        watchedFleetIds ??= NoKeys;
        alertDeviceKeys ??= NoKeys;

        var named = device.FleetIds.Count > 0;
        var namedOk = !filter.NamedOnly || named;
        var customNamedOk = !filter.CustomNamesOnly || namedRadioKeys.Contains(device.Key);
        var watchedOk = !filter.WatchedOnly ||
            alertDeviceKeys.Contains(device.Key) || device.FleetIds.Any(watchedFleetIds.Contains);
        var typeOk = device.Kind == RadioKind.Wifi ? filter.ShowWifi : filter.ShowBle;
        var hideOk = !(filter.ExcludeSignatures && filter.FleetIds.Count > 0) ||
            !device.FleetIds.Any(filter.FleetIds.Contains);
        var includeOk = !filter.IncludeSignatures || filter.IncludeFleetIds.Count == 0 ||
            device.FleetIds.Any(filter.IncludeFleetIds.Contains);
        var deviceClasses = device.FleetIds
            .Where(classByFleetId.ContainsKey)
            .Select(id => classByFleetId[id])
            .ToHashSet();
        var hideClassOk = !(filter.ExcludeClasses && filter.Classes.Count > 0) ||
            !deviceClasses.Any(filter.Classes.Contains);
        var includeClassOk = !filter.UseClassFilter || filter.ExcludeClasses || filter.Classes.Count == 0 ||
            deviceClasses.Any(filter.Classes.Contains);
        var rssiOk = device.Rssi >= filter.RssiMin;
        var nameOk = Strings.IsBlank(filter.NameQuery) ||
            TextMatch.Contains(device.Name, filter.NameQuery) ||
            TextMatch.Contains(device.Mac, filter.NameQuery);
        var ouiOk = Strings.IsBlank(filter.OuiQuery) ||
            TextMatch.Contains(device.Mac, filter.OuiQuery) ||
            (device.Vendor is { } vendor && TextMatch.Contains(vendor, filter.OuiQuery));

        bool gates;
        if (filter.Logic == FilterLogic.And)
        {
            gates = namedOk && customNamedOk && watchedOk && typeOk && hideOk && includeOk && hideClassOk &&
                includeClassOk && rssiOk && nameOk && ouiOk;
        }
        else
        {
            var optional = new List<bool>();
            if (filter.IncludeSignatures)
            {
                optional.Add(includeOk);
            }
            if (filter.UseClassFilter && !filter.ExcludeClasses)
            {
                optional.Add(includeClassOk);
            }
            if (!Strings.IsBlank(filter.NameQuery))
            {
                optional.Add(nameOk);
            }
            if (!Strings.IsBlank(filter.OuiQuery))
            {
                optional.Add(ouiOk);
            }
            if (filter.RssiMin > -100)
            {
                optional.Add(rssiOk);
            }
            var any = optional.Count == 0 || optional.Contains(true);
            gates = namedOk && customNamedOk && watchedOk && typeOk && hideOk && hideClassOk && any;
        }
        if (!gates)
        {
            return false;
        }
        if (filter.HideFastPairAccountKey && FastPair.IsAccountKeyOnly(device))
        {
            return false;
        }
        if (!filter.MovingWithYou)
        {
            return true;
        }
        return CoTravel.WithYou(device, travel ?? CoTravel.Ctx.None, now ?? TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
    }

    /// <summary>Fieldwatch's stock filters.</summary>
    public static IReadOnlyList<FilterPreset> DefaultPresets() =>
    [
        new("all", "All traffic", new FilterState()),
        new("wifi", "Wi-Fi only", new FilterState { ShowBle = false }),
        new("ble", "BLE only", new FilterState { ShowWifi = false }),
        new("strong", "Strong signal", new FilterState { RssiMin = -70 }),
        new("with-you", "Moving with you", new FilterState { MovingWithYou = true, ShowWifi = false }),
        new("watched", "Watched only", new FilterState { WatchedOnly = true }),
    ];
}
