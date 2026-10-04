// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.Net.Wifi;
using Android.OS;
using AetherNet.Aware;
using AetherNetNodeService;
using AetherNetNodeService.Help;
using Java.Util;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;
using AndroidScanResult = Android.Bluetooth.LE.ScanResult;
using WifiScanResult = Android.Net.Wifi.ScanResult;
// Aware and the node contract each name these, and MAUI brings its own Location: say which is meant.
using HelpAdvertForm = AetherNetNodeService.HelpAdvertForm;
using Location = Android.Locations.Location;

namespace AetherNetService;

/// <summary>
/// What this phone's radios hear, and the one thing Quiet help puts back on the air — both off a single Bluetooth
/// scan, because a second scan would cost the battery twice and tell us nothing more.
///
/// <para>
/// Listening only, except for Quiet help: the scan never connects, never pairs and never answers. It takes every
/// advert in the air rather than only AetherNet's, which is what lets Aware name a finder tag or a drone — and it
/// keeps a match-all filter in its filter list so Android goes on delivering results with the screen off.
/// </para>
/// </summary>
internal sealed class AndroidAwareRadio : IHelpRadio, IDisposable
{
    /// <summary>Android allows an app four Wi-Fi scans in two minutes; ask for one every half minute and stay inside it.</summary>
    private const int WifiEverySeconds = 30;

    /// <summary>Adverts arrive in bursts; hand them over in batches rather than one call per packet.</summary>
    private const int PumpEveryMs = 2_000;

    private readonly ILogger? _log;
    private readonly DeviceStore _devices;
    private readonly OperatorPath _walk = new();
    private readonly IReadOnlyList<Fleet> _fleets = StockSignatures.Fleets;
    private readonly object _gate = new();
    private readonly List<Observation> _heard = [];

    private BluetoothAdapter? _adapter;
    private BluetoothLeScanner? _scanner;
    private Scan? _scanCallback;
    private BluetoothLeAdvertiser? _advertiser;
    private LegacyAdvert? _legacy;
    private ExtendedAdvert? _extended;
    private WifiManager? _wifi;
    private LocationManager? _locations;
    private Fix? _fix;
    private System.Threading.Timer? _pump;
    private long _lastWifiAt;
    private long _said;
    private bool _running;
    private bool _disposed;

    public AndroidAwareRadio(ILogger? log = null)
    {
        _log = log;
        _devices = new DeviceStore();
    }

    /// <summary>What this phone has heard around it.</summary>
    public DeviceStore Devices => _devices;

    /// <summary>This phone's own walk, which is what "moving with you" measures a radio's trail against.</summary>
    public OperatorPath Walk => _walk;

    /// <summary>Where this phone was when it last knew, for a help message it hears to be pinned to somewhere.</summary>
    public GpsSample? Here { get; private set; }

    /// <summary>The battery, as a help message would report it.</summary>
    public int? Battery { get; private set; }

    /// <inheritdoc />
    public event Action<RadioFacts, int?, GpsSample?>? Heard;

    /// <summary>Raised when what was heard changed enough for a screen to redraw.</summary>
    public event Action? Changed;

    /// <summary>Start listening. Says plainly when it cannot, rather than looking as though it is working.</summary>
    public void Start()
    {
        if (_running || _disposed)
        {
            return;
        }

        if (!Allowed(global::Android.Manifest.Permission.AccessFineLocation))
        {
            // Android hands out neither Bluetooth scan results nor Wi-Fi results without it, on every version the
            // service runs on. AetherNetService has no screen, so the person grants it in the phone's own settings.
            _log?.LogWarning("Aether Aware is not listening: it needs permission to find devices nearby");
            return;
        }

        _adapter = (AndroidApp.Context.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter;
        _wifi = AndroidApp.Context.GetSystemService(Context.WifiService) as WifiManager;
        _running = true;

        StartBluetooth();
        StartPosition();
        ReadBattery();
        _pump = new System.Threading.Timer(_ => Pump(), null, PumpEveryMs, PumpEveryMs);
        _log?.LogInformation("Aether Aware is listening — every advert in the air, not only AetherNet's");

        // And say plainly what this phone can put back on the air for Quiet help, because that differs by phone and
        // decides whether a guardian standing next to the person hears them at all.
        foreach (var form in Enum.GetValues<HelpAdvertForm>())
        {
            _log?.LogInformation(
                "Quiet help on the air as {Form}: {Answer}", form, Can(form) ? "yes" : Why(form) ?? "no");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
        }

        try
        {
            if (_scanCallback is not null)
            {
                _scanner?.StopScan(_scanCallback);
            }
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "stopping the Aware scan");
        }

        StopAdvertising();
        StopPosition();
        _pump?.Dispose();
        _pump = null;
        _log?.LogInformation("Aether Aware has stopped listening");
    }

    // ── Quiet help on the air ────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Can(HelpAdvertForm form)
    {
        var advertiser = Advertiser();
        if (advertiser is null || !Allowed(AdvertisePermission))
        {
            return false;
        }

        return form != HelpAdvertForm.AetherNet128 || Extended;
    }

    /// <inheritdoc />
    public string? Why(HelpAdvertForm form)
    {
        if (!Allowed(AdvertisePermission))
        {
            return "needs permission to find devices nearby";
        }

        if (Advertiser() is null)
        {
            return "Bluetooth is off, or this phone cannot advertise";
        }

        if (form == HelpAdvertForm.AetherNet128 && !Extended)
        {
            return "this phone's Bluetooth is too old for the longer advert (Bluetooth 5)";
        }

        return null;
    }

    /// <inheritdoc />
    public void Advertise(byte[] message, HelpAdvertForm form, ushort? registeredId = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var advertiser = Advertiser() ?? throw new InvalidOperationException("this phone cannot advertise");
        var uuid = form == HelpAdvertForm.Registered16
            ? Sig(registeredId ?? throw new InvalidOperationException("the standard advert needs a registered 16-bit service id"))
            : HelpAdvert.ServiceUuid;

        // Android composes the advert itself: a SIG-base UUID goes out as 2 bytes, ours as 16. The 23-byte payload
        // is the same either way, which is why a guardian reads both.
        var data = new AdvertiseData.Builder()!
            .SetIncludeDeviceName(false)!
            .SetIncludeTxPowerLevel(false)!
            .AddServiceData(new ParcelUuid(UUID.FromString(uuid.ToString())!), message)!
            .Build()!;

        StopAdvertising();
        if (form == HelpAdvertForm.AetherNet128)
        {
            // Past the 31 bytes a standard advert holds, so it needs Bluetooth 5's longer one.
            var parameters = new AdvertisingSetParameters.Builder()!
                .SetLegacyMode(false)!
                .SetConnectable(false)!
                .SetScannable(true)!
                .SetInterval(AdvertisingSetParameters.IntervalMedium)!
                .SetTxPowerLevel(AdvertiseTxPower.Medium)!
                .Build()!;
            _extended = new ExtendedAdvert(this);
            advertiser.StartAdvertisingSet(parameters, data, null, null, null, _extended);
        }
        else
        {
            var settings = new AdvertiseSettings.Builder()!
                .SetAdvertiseMode(AdvertiseMode.Balanced)!
                .SetConnectable(false)!
                .SetTxPowerLevel(AdvertiseTx.PowerHigh)!
                .Build()!;
            _legacy = new LegacyAdvert(this);
            advertiser.StartAdvertising(settings, data, _legacy);
        }
    }

    private void StopAdvertising()
    {
        var advertiser = _advertiser;
        try
        {
            if (_legacy is not null)
            {
                advertiser?.StopAdvertising(_legacy);
            }

            if (_extended is not null)
            {
                advertiser?.StopAdvertisingSet(_extended);
            }
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "stopping the help advert");
        }

        _legacy = null;
        _extended = null;
    }

    /// <summary>Bluetooth 5's longer advert, which ours needs and an older phone does not have.</summary>
    private bool Extended =>
        OperatingSystem.IsAndroidVersionAtLeast(26) && (_adapter?.IsLeExtendedAdvertisingSupported ?? false);

    private BluetoothLeAdvertiser? Advertiser()
    {
        _adapter ??= (AndroidApp.Context.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter;
        if (_adapter is null || !_adapter.IsEnabled)
        {
            return null;
        }

        try
        {
            return _advertiser ??= _adapter.BluetoothLeAdvertiser;
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "no advertiser on this phone");
            return null;
        }
    }

    private static string AdvertisePermission => OperatingSystem.IsAndroidVersionAtLeast(31)
        ? global::Android.Manifest.Permission.BluetoothAdvertise
        : global::Android.Manifest.Permission.AccessFineLocation;

    /// <summary>A 16-bit id as the Bluetooth SIG's base UUID, which Android sends back as 2 bytes on the air.</summary>
    private static Guid Sig(ushort id) =>
        Guid.Parse($"0000{id:X4}-0000-1000-8000-00805F9B34FB");

    // ── listening ───────────────────────────────────────────────────────────

    private void StartBluetooth()
    {
        if (_adapter is null || !_adapter.IsEnabled)
        {
            _log?.LogInformation("Aether Aware: Bluetooth is off, so nothing is heard over it");
            return;
        }

        if (!Allowed(ScanPermission))
        {
            _log?.LogWarning("Aether Aware: no permission to scan Bluetooth");
            return;
        }

        try
        {
            _scanner = _adapter.BluetoothLeScanner;
            if (_scanner is null)
            {
                return;
            }

            var settings = new ScanSettings.Builder()!.SetScanMode(global::Android.Bluetooth.LE.ScanMode.Balanced)!;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                // Take the longer adverts too — a drone's Remote ID and our own Quiet help ride in them.
                settings = settings.SetLegacy(false)!.SetPhy((global::Android.Bluetooth.BluetoothPhy)ScanSettingsPhy.AllSupported)!;
            }

            // One filter that matches everything. An empty filter list makes Android stop delivering results once
            // the screen goes off; a non-empty one keeps them coming, which is the whole point of a service.
            var filters = new List<ScanFilter> { new ScanFilter.Builder()!.Build()! };
            _scanCallback = new Scan(this);
            _scanner.StartScan(filters, settings.Build(), _scanCallback);
            _log?.LogInformation("Aether Aware: listening to Bluetooth");
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Aether Aware could not listen to Bluetooth");
        }
    }

    private static string ScanPermission => OperatingSystem.IsAndroidVersionAtLeast(31)
        ? global::Android.Manifest.Permission.BluetoothScan
        : global::Android.Manifest.Permission.AccessFineLocation;

    private void StartPosition()
    {
        try
        {
            _locations = AndroidApp.Context.GetSystemService(Context.LocationService) as LocationManager;
            if (_locations is null)
            {
                return;
            }

            _fix = new Fix(this);
            // The phone's own GPS, never a network lookup: no Google Play services here.
            if (_locations.IsProviderEnabled(LocationManager.GpsProvider))
            {
                _locations.RequestLocationUpdates(LocationManager.GpsProvider, 2_000L, 8f, _fix);
            }

            if (_locations.IsProviderEnabled(LocationManager.NetworkProvider))
            {
                _locations.RequestLocationUpdates(LocationManager.NetworkProvider, 4_000L, 15f, _fix);
            }
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "Aether Aware: no position on this phone");
        }
    }

    private void StopPosition()
    {
        try
        {
            if (_fix is not null)
            {
                _locations?.RemoveUpdates(_fix);
            }
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "stopping position updates");
        }

        _fix = null;
    }

    private void ReadBattery()
    {
        try
        {
            using var status = AndroidApp.Context.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
            var level = status?.GetIntExtra(BatteryManager.ExtraLevel, -1) ?? -1;
            var scale = status?.GetIntExtra(BatteryManager.ExtraScale, -1) ?? -1;
            if (level >= 0 && scale > 0)
            {
                Battery = (int)Math.Round(level * 100.0 / scale);
            }
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "reading the battery");
        }
    }

    /// <summary>Hand over what was heard, ask for a Wi-Fi scan when it is allowed again, and forget what is old.</summary>
    private void Pump()
    {
        try
        {
            if (!_running)
            {
                return;
            }

            Observation[] batch;
            lock (_gate)
            {
                batch = [.. _heard];
                _heard.Clear();
            }

            if (batch.Length > 0)
            {
                _devices.IngestBatch(batch, _fleets);
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastWifiAt >= WifiEverySeconds * 1000L)
            {
                _lastWifiAt = now;
                ScanWifi(now);
            }

            _devices.Refresh(_fleets, staleSec: 45);
            ReadBattery();
            Changed?.Invoke();

            // A line a minute, so a log says what the radios are actually hearing without drowning it.
            if (now - _said >= 60_000L)
            {
                _said = now;
                var stats = _devices.Stats;
                var withYou = WithYou().Count;
                _log?.LogInformation(
                    "Aether Aware hears {Total} ({Wifi} Wi-Fi, {Ble} Bluetooth), {Named} named, {WithYou} moving with me; walked {Walk:F0} m",
                    stats.DevicesSeen, stats.WifiNow, stats.BleNow, stats.NamedNow, withYou, _walk.LengthM);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Aether Aware stumbled while taking in what it heard");
        }
    }

    /// <summary>The finder tags that have stayed with this phone while it moved.</summary>
    public IReadOnlyList<Sighting> WithYou()
    {
        var travel = CoTravel.Ctx.Of(_walk.Copy());
        if (!travel.Ready)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fleet in _fleets)
        {
            names[fleet.Id] = fleet.Name;
        }

        var output = new List<Sighting>();
        foreach (var device in _devices.Devices)
        {
            if (CoTravel.WithYou(device, travel, now) && TrackerMatch.IsTracker(device, names))
            {
                output.Add(device);
            }
        }

        return output;
    }

    private void ScanWifi(long now)
    {
        var wifi = _wifi;
        if (wifi is null)
        {
            return;
        }

        if (!wifi.IsWifiEnabled)
        {
            // Said once a minute at most, with the count, so a log showing no access points explains itself.
            _log?.LogInformation("Aether Aware: Wi-Fi is off on this phone, so no access points are heard");
            return;
        }

        try
        {
            var results = wifi.ScanResults;
            if (results is { Count: > 0 })
            {
                var batch = new List<Observation>(results.Count);
                foreach (var result in results)
                {
                    batch.Add(FromWifi(result, now, Here));
                }

                _devices.IngestBatch(batch, _fleets);
            }

            // Deprecated since Android 9 and throttled since 10, but it is still the only way an app asks; without
            // it the list only changes when something else on the phone scans.
            wifi.StartScan();
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "Aether Aware: no Wi-Fi results");
        }
    }

    private static bool Allowed(string permission) =>
        AndroidApp.Context.CheckSelfPermission(permission) == Permission.Granted;

    // ── turning what a radio gave us into what Aware reads ──────────────────

    private Observation FromBle(AndroidScanResult result, long at)
    {
        var record = result.ScanRecord;
        var raw = record?.GetBytes();
        var parsed = BleAdParser.Parse(raw);
        var name = parsed.LocalName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = record?.DeviceName;
        }

        var uuids = new List<string>(parsed.Uuids);
        if (record?.ServiceUuids is { } advertised)
        {
            foreach (var uuid in advertised)
            {
                var text = uuid?.ToString()?.ToUpperInvariant();
                if (!string.IsNullOrEmpty(text) && !uuids.Contains(text))
                {
                    uuids.Add(text);
                }
            }
        }

        int? txPower = parsed.TxPower;
        bool? connectable = null;
        string? primaryPhy = null;
        double? periodic = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            if ((int)result.TxPower != AndroidScanResult.TxPowerNotPresent)
            {
                txPower = (int)result.TxPower;
            }

            connectable = result.IsConnectable;
            primaryPhy = Phy(result.PrimaryPhy);
            if (result.PeriodicAdvertisingInterval > 0)
            {
                periodic = result.PeriodicAdvertisingInterval * 1.25;
            }
        }

        var facts = new RadioFacts
        {
            TxPowerDbm = txPower,
            AdvFlags = parsed.Flags,
            Appearance = parsed.Appearance,
            AdvertisingIntervalMs = parsed.AdvertisingIntervalMs,
            PeriodicIntervalMs = periodic,
            Connectable = connectable,
            PrimaryPhy = primaryPhy,
            DeviceClass = parsed.DeviceClass,
            MfgRecords = parsed.Mfg,
            ServiceData = parsed.ServiceData,
        };

        var first = parsed.Mfg.Count > 0 ? parsed.Mfg[0] : null;
        return new Observation
        {
            Kind = AetherNet.Aware.RadioKind.Ble,
            Mac = result.Device?.Address ?? string.Empty,
            Name = name ?? string.Empty,
            Rssi = result.Rssi,
            FrequencyMhz = 2402,
            ServiceUuids = uuids,
            ManufacturerId = first?.CompanyId,
            ManufacturerDataHex = first?.DataHex ?? string.Empty,
            RawHex = raw is null ? string.Empty : Convert.ToHexString(raw),
            At = at,
            Latitude = Here?.Lat,
            Longitude = Here?.Lon,
            Facts = facts,
        };
    }

    private static Observation FromWifi(WifiScanResult result, long at, GpsSample? here)
    {
        var ssid = result.Ssid ?? string.Empty;
        var hidden = string.IsNullOrEmpty(ssid) || ssid == "<unknown ssid>";
        var parsed = ReadIes(result);
        var vendorOuis = new List<string>(parsed.VendorIes.Count);
        foreach (var ie in parsed.VendorIes)
        {
            vendorOuis.Add(ie.Oui);
        }

        return new Observation
        {
            Kind = AetherNet.Aware.RadioKind.Wifi,
            Mac = result.Bssid ?? string.Empty,
            Name = hidden ? string.Empty : ssid,
            Rssi = result.Level,
            Channel = parsed.ChannelFromDs ?? Channel(result.Frequency),
            FrequencyMhz = result.Frequency,
            HiddenSsid = hidden,
            At = at,
            Latitude = here?.Lat,
            Longitude = here?.Lon,
            VendorIeOuis = vendorOuis,
            Facts = new RadioFacts
            {
                Capabilities = result.Capabilities,
                Security = parsed.Security,
                SupportedRates = parsed.Rates,
                VendorIes = parsed.VendorIes,
            },
        };
    }

    /// <summary>
    /// A beacon's own information elements, which Android only hands over from 11 onwards. Before that there is the
    /// capability string and nothing else — so on an older phone Aware names an access point by its address and its
    /// name, and not by what its beacon carries.
    /// </summary>
    private static WifiIeParser.Parsed ReadIes(WifiScanResult result)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return new WifiIeParser.Parsed { Security = Blank(result.Capabilities) };
        }

        try
        {
            var elements = result.InformationElements;
            if (elements is null or { Count: 0 })
            {
                return new WifiIeParser.Parsed { Security = Blank(result.Capabilities) };
            }

            var ies = new List<WifiIeParser.Ie>(elements.Count);
            foreach (var element in elements)
            {
                var buffer = element?.Bytes;
                if (buffer is null)
                {
                    continue;
                }

                var bytes = new byte[buffer.Remaining()];
                buffer.Duplicate()!.Get(bytes);
                ies.Add(new WifiIeParser.Ie(element!.Id, bytes));
            }

            return WifiIeParser.ParseIes(ies, result.Capabilities);
        }
        catch (Exception)
        {
            return new WifiIeParser.Parsed { Security = Blank(result.Capabilities) };
        }
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static int Channel(int frequencyMhz) => frequencyMhz switch
    {
        2412 => 1,
        >= 2412 and <= 2472 => ((frequencyMhz - 2412) / 5) + 1,
        2484 => 14,
        >= 5160 and <= 5885 => (frequencyMhz - 5000) / 5,
        >= 5955 and <= 7115 => (frequencyMhz - 5950) / 5,
        _ => 0,
    };

    private static string? Phy(global::Android.Bluetooth.LE.ScanSettingsPhy phy) => (int)phy switch
    {
        1 => "LE 1M",
        2 => "LE 2M",
        3 => "LE Coded",
        _ => null,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _devices.ClearGpsTrails();
    }

    /// <summary>Every advert in the air. Collected here and handed over in batches by <see cref="Pump"/>.</summary>
    private sealed class Scan(AndroidAwareRadio owner) : ScanCallback
    {
        public override void OnScanResult(ScanCallbackType callbackType, AndroidScanResult? result)
        {
            if (result is null)
            {
                return;
            }

            try
            {
                var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var observation = owner.FromBle(result, at);
                lock (owner._gate)
                {
                    owner._heard.Add(observation);
                }

                // Quiet help listens to the same scan: a guardian reads a help message straight off the air.
                owner.Heard?.Invoke(observation.Facts, result.Rssi, owner.Here);
            }
            catch (Exception ex)
            {
                owner._log?.LogDebug(ex, "an advert could not be read");
            }
        }

        public override void OnBatchScanResults(IList<AndroidScanResult>? results)
        {
            foreach (var result in results ?? [])
            {
                OnScanResult(ScanCallbackType.AllMatches, result);
            }
        }

        public override void OnScanFailed(ScanFailure errorCode)
            => owner._log?.LogWarning("Aether Aware: Bluetooth refused the scan ({Why})", errorCode);
    }

    /// <summary>This phone's own position, which "moving with you" measures against.</summary>
    private sealed class Fix(AndroidAwareRadio owner) : Java.Lang.Object, ILocationListener
    {
        public void OnLocationChanged(Location location)
        {
            try
            {
                var accuracy = location.HasAccuracy ? location.Accuracy : (float?)null;
                var at = location.Time > 0 ? location.Time : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (owner._walk.Accept(location.Latitude, location.Longitude, at, accuracy))
                {
                    owner.Here = new GpsSample(at, location.Latitude, location.Longitude);
                }
            }
            catch (Exception ex)
            {
                owner._log?.LogDebug(ex, "a position could not be taken");
            }
        }

        public void OnProviderDisabled(string provider)
        {
        }

        public void OnProviderEnabled(string provider)
        {
        }

        public void OnStatusChanged(string? provider, Availability status, Bundle? extras)
        {
        }
    }

    private sealed class LegacyAdvert(AndroidAwareRadio owner) : AdvertiseCallback
    {
        public override void OnStartFailure(AdvertiseFailure errorCode)
            => owner._log?.LogWarning("Quiet help could not go on the air ({Why})", errorCode);
    }

    private sealed class ExtendedAdvert(AndroidAwareRadio owner) : AdvertisingSetCallback
    {
        public override void OnAdvertisingSetStarted(AdvertisingSet? advertisingSet, int txPower, AdvertiseResult status)
        {
            if (status != AdvertiseResult.Success)
            {
                owner._log?.LogWarning("Quiet help could not go on the air (status {Why})", status);
            }
        }
    }
}
#endif
