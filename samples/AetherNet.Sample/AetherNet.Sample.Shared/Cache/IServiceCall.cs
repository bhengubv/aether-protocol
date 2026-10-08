namespace AetherNet.Sample.Shared.Cache;

/// <summary>
/// The app's line to AetherNetService: a call goes out as a number and its bytes, and the answer comes back as bytes.
/// The service also tells the app when something changes, unasked.
/// </summary>
public interface IServiceCall
{
    /// <summary>Calls the service and waits for its answer.</summary>
    Task<byte[]> CallAsync(int code, byte[]? args = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls the service and waits for its answer on this thread, for a page that needs it while it draws.
    /// </summary>
    /// <remarks>
    /// Only once connected. Connecting finishes on the app's main thread, so waiting for it there would wait forever;
    /// this says so instead.
    /// </remarks>
    /// <exception cref="AetherNetNodeService.AetherNodeException">Not connected yet (<c>NodeUnavailable</c>), or the service said no.</exception>
    byte[] Call(int code, byte[]? args = null);

    /// <summary>Whether the app is connected to the service right now.</summary>
    bool IsConnected { get; }

    /// <summary>Connects to the service, if the app is not connected already.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The app reached the service: the first time, and again after the service went away and came back. Whatever the
    /// app was told before may no longer be true.
    /// </summary>
    event Action? Connected;

    /// <summary>The service telling the app something changed: what changed, and the bytes it sent.</summary>
    event Action<int, byte[]>? Told;
}
