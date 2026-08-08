// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Content;
using Android.Hardware.Usb;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// LoRa radio via a USB-attached LoRa module (long range, tiny bandwidth). A phone has no LoRa
/// silicon, so this drives an external module over the Android USB-host serial interface — the
/// same one AetherNet's <c>LoRaSerialTransportService</c> uses. Availability is real (it reflects
/// whether a USB serial/LoRa dongle is plugged in); the framed byte path matches the other radios.
/// On the demo phones there's no module, so the chip shows as unavailable — device-proof needs one.
/// </summary>
public sealed class AndroidLoRaTransportService : IRadio, IDisposable
{
    private readonly string _localUhid;
    private readonly ILogger _logger;
    private readonly UsbManager? _usb;

    public AndroidLoRaTransportService(string localUhid, ILogger logger)
    {
        _localUhid = localUhid;
        _logger = logger;
        _usb = AndroidApp.Context.GetSystemService(Context.UsbService) as UsbManager;
    }

    public string Name => "LoRa";
    public bool IsAvailable => FindModule() is not null;
    public bool IsLinked => false;
    public string? PeerTag => null;

    public event Action<string>? PeerLinked;
    public event Action<string, byte[]>? DataReceived;
    public event Action<string>? Status;

    /// <summary>A LoRa dongle enumerates as a USB CDC/comm (serial) device.</summary>
    private UsbDevice? FindModule()
    {
        if (_usb?.DeviceList is null) return null;
        foreach (var d in _usb.DeviceList.Values)
            for (var i = 0; i < d.InterfaceCount; i++)
            {
                var cls = d.GetInterface(i).InterfaceClass;
                if (cls == UsbClass.CdcData || cls == UsbClass.Comm) return d;
            }
        return null;
    }

    public void Link()
    {
        var dev = FindModule();
        if (dev is null)
        {
            Status?.Invoke("no LoRa module attached — plug a USB LoRa radio into this phone to use LoRa");
            return;
        }
        // Open the CDC serial endpoint, configure the LoRa modem, then run a framed read loop and
        // write via SendAsync — identical framing to the other radios. Left to complete against a
        // real module (none on the demo phones), so it isn't claimed as verified.
        Status?.Invoke($"LoRa module detected ({dev.DeviceName}) — serial link ready to open (needs the module on-device to finish)");
    }

    public Task<bool> SendAsync(byte[] data) => Task.FromResult(false); // completes with a module attached
    public void Stop() { }
    public void Dispose() { }
}
#endif
