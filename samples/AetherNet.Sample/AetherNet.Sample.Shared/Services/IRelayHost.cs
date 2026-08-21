// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// A phone that is willing to carry traffic for the people in its Circle.
///
/// <para>
/// The seam exists because the two heads differ in what they can honestly offer. The Android app can
/// run a relay; the web head cannot, and should say so rather than presenting a switch that does
/// nothing. Anything absent here is a host that simply cannot volunteer.
/// </para>
/// </summary>
public interface IRelayHost
{
    /// <summary>Whether this phone is carrying traffic for others right now.</summary>
    bool IsRelaying { get; }

    /// <summary>The address contacts were given, or null when not relaying.</summary>
    string? RelayAddress { get; }

    /// <summary>Start carrying, and tell the Circle where to find us.</summary>
    Task<bool> StartRelayingAsync(CancellationToken cancellationToken = default);

    /// <summary>Stop carrying, and say so — going quiet looks exactly like the network being down.</summary>
    Task<bool> StopRelayingAsync(CancellationToken cancellationToken = default);
}
