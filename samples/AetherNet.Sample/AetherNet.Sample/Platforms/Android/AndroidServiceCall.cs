using Android.Content;
using Android.Content.PM;
using Android.OS;
using AetherNet.Sample.Shared.Cache;
using AetherNetNodeService.Client;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Calls AetherNetService across the binder: bind, send the call, read the answer — in the service's own format.
/// </summary>
/// <remarks>
/// It is also how the app tells whether AetherNetService is on the phone at all, for the install flow to offer it.
/// </remarks>
public sealed class AndroidServiceCall : IServiceCall, INodeConnector
{
    private const string ServicePackage = "com.bhengubv.aethernetservice";
    private const string BindAction = "com.bhengubv.aethernet.service.BIND";
    private const int Subscribe = 7;

    private readonly SemaphoreSlim _binding = new(1, 1);
    private IBinder? _service;
    private Callback? _callback;

    // This binding's wait until the service answers: bound is not answering, since a service that has just started has
    // its binder a moment before its classes.
    private Task? _answering;

    public event Action<int, byte[]>? Told;

    public event Action? Connected;

    public bool IsConnected => _service is { IsBinderAlive: true } && _answering is { IsCompletedSuccessfully: true };

    // No MatchDefaultOnly: a service bound by its action carries no DEFAULT category, so that flag resolves nothing.
    public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(global::Android.App.Application.Context.PackageManager?.ResolveService(BindIntent(), (PackageInfoFlags)0) is not null);

    // Reached, and answering this app: the node. Installed but not letting this app in yet: none.
    public async Task<AetherNetNodeService.IAetherNodeClient?> TryBindAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await BindAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AetherNetNodeService.AetherNodeException ex) when (ex.Code is AetherNetNodeService.AetherNodeErrorCode.GrantRequired
            or AetherNetNodeService.AetherNodeErrorCode.GrantDenied or AetherNetNodeService.AetherNodeErrorCode.NodeUnavailable)
        {
            return null;
        }

        return ServiceMenu.Current is { } menu ? new AetherNetNodeService.NodeFromService(menu) : null;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await BindAsync(cancellationToken).ConfigureAwait(false);

        // A wait that gave up is tried afresh when asked again.
        var answering = _answering;
        if (answering is null || answering.IsFaulted || answering.IsCanceled)
            _answering = answering = AnsweringAsync();
        await answering.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Answering now: say so, so whoever listens can ask for what it shows.
    private async Task AnsweringAsync()
    {
        await ServiceMenu.UntilAnsweringAsync(this).ConfigureAwait(false);
        Connected?.Invoke();
    }

    public async Task<byte[]> CallAsync(int code, byte[]? args = null, CancellationToken cancellationToken = default)
    {
        var service = await BindAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Transact(service, code, data =>
        {
            if (args is not null) data.WriteByteArray(args);
        }), cancellationToken).ConfigureAwait(false);
    }

    // A binder call is made on the calling thread and answered there, the way Android's own bound services are.
    public byte[] Call(int code, byte[]? args = null)
    {
        if (_service is not { IsBinderAlive: true } service)
            throw new AetherNetNodeService.AetherNodeException(AetherNetNodeService.AetherNodeErrorCode.NodeUnavailable, "AetherNetService is not connected yet");
        return Transact(service, code, data =>
        {
            if (args is not null) data.WriteByteArray(args);
        });
    }

    // The service answers 1 and the result, or 0, a code and what went wrong.
    private static byte[] Transact(IBinder service, int code, Action<Parcel> write)
    {
        var data = Parcel.Obtain();
        var reply = Parcel.Obtain();
        try
        {
            write(data);
            service.Transact(code, data, reply, 0);
            if (reply.ReadInt() == 1) return reply.CreateByteArray() ?? [];
            var error = reply.ReadInt();
            throw new AetherNetNodeService.AetherNodeException((AetherNetNodeService.AetherNodeErrorCode)error, reply.ReadString() ?? "");
        }
        finally
        {
            data.Recycle();
            reply.Recycle();
        }
    }

    private static Intent BindIntent() => new Intent(BindAction).SetPackage(ServicePackage);

    private async Task<IBinder> BindAsync(CancellationToken cancellationToken)
    {
        if (_service is { IsBinderAlive: true } bound) return bound;
        IBinder service;
        await _binding.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_service is { IsBinderAlive: true } again) return again;

            var connection = new Connection();
            if (!global::Android.App.Application.Context.BindService(BindIntent(), connection, Bind.AutoCreate))
                throw new AetherNetNodeService.AetherNodeException(AetherNetNodeService.AetherNodeErrorCode.NodeUnavailable, "AetherNetService is not installed");

            service = await connection.Bound.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            _service = service;

            // Hand over a callback, so the service can tell the app when something changes.
            _callback = new Callback(this);
            var callback = _callback;
            Transact(service, Subscribe, data => data.WriteStrongBinder(callback));
        }
        finally
        {
            _binding.Release();
        }

        // A fresh binding: wait, outside the lock, until the service answers it, and say so then.
        _answering = AnsweringAsync();
        return service;
    }

    private sealed class Connection : Java.Lang.Object, IServiceConnection
    {
        public TaskCompletionSource<IBinder> Bound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            if (service is not null) Bound.TrySetResult(service);
        }

        public void OnServiceDisconnected(ComponentName? name)
        {
        }
    }

    private sealed class Callback(AndroidServiceCall owner) : Binder
    {
        protected override bool OnTransact(int code, Parcel? data, Parcel? reply, int flags)
        {
            owner.Told?.Invoke(code, data?.CreateByteArray() ?? []);
            return true;
        }
    }
}
