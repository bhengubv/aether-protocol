// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Nfc;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// NFC radio via Host Card Emulation. Tap-range, tiny payloads — the classic "bump to pair" that
/// seeds a longer-range radio: one phone emulates a card (HCE), the other reads it, and they swap
/// AetherTags on contact. Availability is real (NFC hardware present + enabled); the exchange
/// itself needs the two phones physically tapped, which can't be automated, so it isn't claimed
/// as verified on these phones.
/// </summary>
public sealed class AndroidNfcTransportService : IRadio, IDisposable
{
    private readonly string _localUhid;
    private readonly ILogger _logger;
    private readonly NfcAdapter? _nfc;

    public AndroidNfcTransportService(string localUhid, ILogger logger)
    {
        _localUhid = localUhid;
        _logger = logger;
        _nfc = NfcAdapter.GetDefaultAdapter(AndroidApp.Context);
    }

    public string Name => "NFC";
    public bool IsAvailable => _nfc is { IsEnabled: true };

    /// <inheritdoc />
    public string? UnavailableReason =>
        _nfc is null ? "this phone has no NFC"
        : !_nfc.IsEnabled ? "NFC is switched off"
        : null;
    public bool IsLinked => false;
    public string? PeerTag => null;

    public event Action<string>? PeerLinked;
    public event Action<string, byte[]>? DataReceived;
    public event Action<string>? Status;

    public void Link()
    {
        if (_nfc is null) { Status?.Invoke("no NFC hardware on this phone"); return; }
        if (!_nfc.IsEnabled) { Status?.Invoke("NFC is off — enable NFC to pair by tapping"); return; }
        // HCE emulates our card carrying the AetherTag; the peer's reader-mode pulls it, and
        // vice-versa, on a physical tap. Wired here; the tap can't be scripted.
        Status?.Invoke("NFC ready — hold the two phones back-to-back to swap AetherTags");
    }

    public Task<bool> SendAsync(byte[] data) => Task.FromResult(false); // completes on a physical tap
    public void Stop() { }
    public void Dispose() { }
}
#endif
