// SPDX-License-Identifier: MIT

using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace AetherNet.Transport.Windows;

/// <summary>
/// Whether this computer's Bluetooth can be used right now, and if not, why — in the words of the person at it, with
/// where to change it. A computer with Bluetooth switched off in Windows answers a scan with "device not ready"
/// (0x800710DF) and nothing else; this says it plainly instead.
/// </summary>
internal static class WindowsBluetooth
{
    /// <summary>How long one look is trusted. Asking Windows takes a few milliseconds, and the mesh asks often.</summary>
    private const long FreshForMs = 5_000;

    private static readonly object Gate = new();
    private static (bool Usable, string? Why) _last;
    private static long _lookedAt;

    /// <summary>Usable now, or why not.</summary>
    public static (bool Usable, string? Why) Now()
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (_lookedAt != 0 && now - _lookedAt < FreshForMs) return _last;
            _last = Look();
            _lookedAt = now;
            return _last;
        }
    }

    private static (bool, string?) Look()
    {
        try
        {
            var adapter = BluetoothAdapter.GetDefaultAsync().AsTask().GetAwaiter().GetResult();
            if (adapter is null) return (false, "this computer has no Bluetooth");
            if (!adapter.IsLowEnergySupported) return (false, "this computer's Bluetooth is too old to find phones");
            if (!adapter.IsCentralRoleSupported) return (false, "this computer's Bluetooth cannot look for phones");

            var radio = adapter.GetRadioAsync().AsTask().GetAwaiter().GetResult();
            return radio?.State == RadioState.On
                ? (true, null)
                : (false, "Bluetooth is off on this computer — switch it on in Windows' Settings, under Bluetooth & devices");
        }
        catch (Exception ex)
        {
            return (false, $"Windows would not say whether Bluetooth is on ({ex.GetType().Name} 0x{ex.HResult:X8})");
        }
    }
}
