namespace AetherNet.Sample.Shared.Cache;

/// <summary>
/// The line on a device where AetherNetService does not run (an iPhone, a Mac): every call says so, rather than the app
/// pretending there is nobody there to ask.
/// </summary>
public sealed class NoServiceCall : IServiceCall
{
    public bool IsConnected => false;

    public event Action<int, byte[]>? Told { add { } remove { } }

    public event Action? Connected { add { } remove { } }

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.FromException(Absent());

    public Task<byte[]> CallAsync(int code, byte[]? args = null, CancellationToken cancellationToken = default)
        => Task.FromException<byte[]>(Absent());

    public byte[] Call(int code, byte[]? args = null) => throw Absent();

    private static AetherNetNodeService.AetherNodeException Absent()
        => new(AetherNetNodeService.AetherNodeErrorCode.NodeUnavailable, "AetherNetService does not run on this device");
}
