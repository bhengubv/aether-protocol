// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services.Cast;

/// <summary>
/// Holds Android's Wi-Fi <c>MulticastLock</c> for as long as it is kept. Without it, the Wi-Fi chip
/// filters out multicast and broadcast traffic to save power — which silently kills SSDP, the multicast
/// "who's out there?" that finds a TV to cast to. Acquire it around discovery; dispose to release.
/// A no-op on heads that do not need it (the web head, tests).
/// </summary>
public interface IMulticastHold
{
    IDisposable Acquire();
}

/// <summary>The default: nothing to hold. Used where multicast is not power-gated.</summary>
public sealed class NoMulticastHold : IMulticastHold
{
    public IDisposable Acquire() => Nothing.Instance;

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
