// SPDX-License-Identifier: MIT

using System.Net.NetworkInformation;
using AetherNet.Mesh;
using AetherNet.Transport.Windows.Services;
using Microsoft.Extensions.Logging;

namespace AetherNet.Transport.Windows;

/// <summary>
/// The real over-the-air mesh on Windows: the same <see cref="RadioMesh"/> a phone runs, with the computer's own
/// radios — Wi-Fi Direct, Bluetooth, Wi-Fi Aware, the internet relay, the network the computer is on (Wi-Fi or a
/// cable), and tap-to-add — in the phone's order. Only LoRa (a plugged-in radio a phone drives) and NearLink (HarmonyOS
/// hardware) are not here. Every one is on unless the person switched it off; each says why when it cannot come up.
/// </summary>
public sealed class WindowsRadioMesh : RadioMesh
{
    public WindowsRadioMesh(IIdentityService me, ILoggerFactory logs,
        CircleDirectory? circle = null,
        ProxyDirectory? proxies = null,
        IRadioSwitches? switches = null)
        : base(me, (logs ?? throw new ArgumentNullException(nameof(logs))).CreateLogger<WindowsRadioMesh>(), circle, switches)
    {
        // Wi-Fi Direct: the computer advertises itself and takes connections, as a group owner.
        var wifiDirect = new WinWifiDirectTransportService(LocalUhid, logs.CreateLogger<WinWifiDirectTransportService>());
        Register(new TransportRadio(wifiDirect, LocalUhid,
            unavailableReason: "this computer has no Wi-Fi Direct", name: "Wi-Fi Direct", start: wifiDirect.StartAdvertising));

        // Bluetooth: the computer looks for phones advertising AetherNet and connects to them — when Windows has its
        // Bluetooth on. Off, the radio says so and where to switch it on; it comes up at the next meeting once it is.
        var ble = new WinBleGattTransportService(LocalUhid, logs.CreateLogger<WinBleGattTransportService>());
        Register(new TransportRadio(ble, LocalUhid,
            available: () => WindowsBluetooth.Now().Usable, name: "BLE", start: ble.StartScanning,
            why: () => WindowsBluetooth.Now().Why));

        // Wi-Fi Aware: Windows gives programs no way to it. Listed all the same, with why.
        Register(new AbsentRadio("Wi-Fi Aware", "Windows gives programs no Wi-Fi Aware — some phones have it"));

        // The second leg: through a phone in the Circle that offers to relay. Last resort, as on a phone.
        Register(new InternetRadio(LocalUhid, logs.CreateLogger<InternetRadio>(), proxies,
            NetworkInterface.GetIsNetworkAvailable, "computer"));

        // The network the computer is already on — the one that reaches phones on the same Wi-Fi.
        AddWifi();

        // Tap to add: Windows took NFC between devices away, so it is a phone held right up against the computer, found
        // over Bluetooth only when it is that close. It needs Bluetooth on, like Bluetooth itself.
        var tap = new WinNfcBleTransportService(LocalUhid, logs.CreateLogger<WinNfcBleTransportService>());
        Register(new TransportRadio(tap, LocalUhid,
            available: () => WindowsBluetooth.Now().Usable, name: "NFC", start: tap.StartScanning,
            why: () => WindowsBluetooth.Now().Why));

        // A computer is nearly always on a network, and that is the radio that reaches the phones beside it.
        Prefer("Wi-Fi", "Wi-Fi Direct");
    }
}
