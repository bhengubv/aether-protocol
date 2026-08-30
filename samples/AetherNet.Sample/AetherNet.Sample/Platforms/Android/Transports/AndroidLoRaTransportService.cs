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

    /// <inheritdoc />
    public string? UnavailableReason =>
        FindModule() is null ? "plug in a USB LoRa module" : null;
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

    /// <summary>
    /// How many bits of address a LoRa frame has to spare.
    /// </summary>
    /// <remarks>
    /// Nothing like the room a Wi-Fi network name has. Sixteen bits means two pairs can land on the
    /// same address, which costs a dropped frame and never a wrong link — what arrives is still
    /// checked against a key before it is believed.
    /// </remarks>
    private const int AddressBits = 16;

    /// <summary>Who this radio is listening for, once it has been told.</summary>
    private uint? _address;

    /// <summary>
    /// Come up to meet one particular person.
    /// </summary>
    /// <remarks>
    /// LoRa cannot make a network per pair the way Wi-Fi Direct can — there are a handful of
    /// frequencies and everybody shares them. So the meeting becomes an address inside the shared
    /// channel: both phones derive the same one, each ignores everything not addressed to it, and
    /// nothing is discovered.
    /// </remarks>
    public void Link(AetherNet.Sample.Shared.Services.Meeting meeting)
    {
        _address = meeting.Address(AddressBits);
        Status?.Invoke($"meeting {meeting.PeerTag} at {_address:X4}");
        Link();
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
        //
        // The one thing decided already is WHERE to listen: _address, derived from the meeting above,
        // so whenever this is finished it is finished against the same rule as every other radio and
        // does not get to invent a fourth answer to "how do two phones find each other".
        Status?.Invoke(_address is { } at
            ? $"LoRa module detected ({dev.DeviceName}) — would listen at {at:X4} (needs the module on-device to finish)"
            : $"LoRa module detected ({dev.DeviceName}) — serial link ready to open (needs the module on-device to finish)");
    }

    public Task<bool> SendAsync(byte[] data) => Task.FromResult(false); // completes with a module attached
    public void Stop() { }
    public void Dispose() { }
}
#endif
