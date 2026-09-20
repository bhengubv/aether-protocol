// SPDX-License-Identifier: MIT

using Android.Content;
using Android.Net.Wifi;
using AetherNet.Sample.Shared.Services.Cast;
using AndroidApp = global::Android.App.Application;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Android's Wi-Fi <c>MulticastLock</c>, wrapped as an <see cref="IMulticastHold"/>. Held around SSDP
/// discovery so the Wi-Fi chip stops filtering the multicast search and its replies — the reason a first
/// attempt at casting found no TVs. Reference-counted, so overlapping discoveries share one lock, and
/// released the moment the last one is done so the radio can power its filter back down.
/// </summary>
public sealed class AndroidMulticastHold : IMulticastHold
{
    public IDisposable Acquire()
    {
        try
        {
            if (AndroidApp.Context.GetSystemService(Context.WifiService) is not WifiManager wifi)
                return Noop.Instance;

            var multicastLock = wifi.CreateMulticastLock("aether-cast");
            if (multicastLock is null) return Noop.Instance;
            multicastLock.SetReferenceCounted(true);
            multicastLock.Acquire();
            return new Release(multicastLock);
        }
        catch (Exception)
        {
            // No lock is better than a crash — discovery simply runs without it and may find less.
            return Noop.Instance;
        }
    }

    private sealed class Release(WifiManager.MulticastLock multicastLock) : IDisposable
    {
        public void Dispose()
        {
            try { if (multicastLock.IsHeld) multicastLock.Release(); multicastLock.Dispose(); }
            catch (Exception) { /* already released or torn down */ }
        }
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
