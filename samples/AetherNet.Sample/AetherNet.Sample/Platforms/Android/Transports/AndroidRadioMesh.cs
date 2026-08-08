// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Identity;
using AetherNet.Protocol;
using AetherNet.Sample.Shared.Services;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;
using System.Text;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// The real over-the-air mesh on Android, inside this one APK. Owns one Ed25519 identity and a
/// set of native radios (Wi-Fi Direct, BLE, … — all <see cref="IRadio"/>). The UI picks a radio;
/// this links over it and moves real <see cref="MeshPacket"/>s to the linked peer phone.
/// </summary>
public sealed class AndroidRadioMesh : IRadioMesh, IDisposable
{
    private readonly object _gate = new();
    private readonly List<string> _log = new();
    private readonly Dictionary<string, IRadio> _radios = new(StringComparer.Ordinal);
    private readonly List<IRadio> _order = new();
    private readonly string _localUhid;
    private IRadio _selected;

    public AndroidRadioMesh(ILogger<AndroidRadioMesh> logger)
    {
        var (_, pub) = Ed25519SigningService.GenerateKeyPair();
        LocalTag = AetherNetTag.FromPublicKey(pub).Value;
        _localUhid = LocalTag;

        Register(new AndroidWifiDirectTransportService(global::Android.App.Application.Context!, _localUhid, logger));
        Register(new AndroidBleTransportService("BLE",
            "61657468-6572-0001-0000-000000000001", "61657468-6572-0003-0000-000000000001",
            "61657468-6572-0002-0000-000000000001", _localUhid, logger));
        Register(new AndroidBleTransportService("NearLink",
            "6e65726c-696e-0001-0000-000000000001", "6e65726c-696e-0003-0000-000000000001",
            "6e65726c-696e-0002-0000-000000000001", _localUhid, logger));
        Register(new AndroidNfcTransportService(_localUhid, logger));
        Register(new AndroidLoRaTransportService(_localUhid, logger));
        _selected = _order[0]; // Wi-Fi Direct — the most capable, fully-proven radio
    }

    private void Register(IRadio r)
    {
        _radios[r.Name] = r;
        _order.Add(r);
        r.Status += s => Emit($"[{r.Name}] {s}");
        r.PeerLinked += p => Emit($"[{r.Name}] ● linked with {p}");
        r.DataReceived += (from, bytes) =>
        {
            try
            {
                var pkt = PacketSerializer.Deserialize(bytes);
                Emit($"[{r.Name}] ◀ from {from}: \"{Encoding.UTF8.GetString(pkt.Payload)}\"");
            }
            catch { Emit($"[{r.Name}] ◀ {bytes.Length} bytes from {from}"); }
        };
    }

    public string LocalTag { get; }
    public IReadOnlyList<RadioInfo> Radios => _order.Select(r => new RadioInfo(r.Name, r.IsAvailable)).ToArray();
    public string SelectedRadio => _selected.Name;
    public bool IsSupported => _selected.IsAvailable;
    public bool IsLinked => _selected.IsLinked;
    public string? PeerTag => _selected.PeerTag;

    public event Action? Changed;
    public IReadOnlyList<string> Log { get { lock (_gate) { return _log.ToArray(); } } }

    public void SelectRadio(string name)
    {
        if (_radios.TryGetValue(name, out var r)) { _selected = r; RaiseChanged(); }
    }

    public void Link()
    {
        Emit($"[{_selected.Name}] linking…");
        _selected.Link();
    }

    public async Task SendTestAsync(string text)
    {
        if (_selected.PeerTag is null) { Emit($"[{_selected.Name}] no peer linked yet"); return; }
        var pkt = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = _localUhid,
            DestinationUhid = _selected.PeerTag,
            Payload = Encoding.UTF8.GetBytes(text),
            Ttl = 7,
        };
        var ok = await _selected.SendAsync(PacketSerializer.Serialize(pkt)).ConfigureAwait(false);
        Emit(ok ? $"[{_selected.Name}] ▶ sent: \"{text}\"" : $"[{_selected.Name}] ▶ send failed");
    }

    public void Stop()
    {
        foreach (var r in _order) r.Stop();
        Emit("stopped");
    }

    public void Dispose()
    {
        foreach (var r in _order)
            if (r is IDisposable d) d.Dispose();
    }

    private void Emit(string line)
    {
        lock (_gate)
        {
            _log.Add(line);
            if (_log.Count > 300) _log.RemoveAt(0);
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();
}
#endif
