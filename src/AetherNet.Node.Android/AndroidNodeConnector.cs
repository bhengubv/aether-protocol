// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AetherNet.Node;
using AetherNet.Node.Client;

namespace AetherNet.Node.Android;

/// <summary>
/// The Android <see cref="INodeConnector"/>: detects whether the node service is installed and binds to it,
/// handing back a live <see cref="IAetherNodeClient"/>. It returns null from <see cref="TryBindAsync"/> when
/// the node is present but this app has not been granted a link — the caller then shows "awaiting grant". A
/// node that is bound but locked is still returned (its calls surface <c>NodeUnavailable</c> until unlocked),
/// because that is a live binding, not an absent one.
/// </summary>
public sealed class AndroidNodeConnector : INodeConnector
{
    private readonly Context _context;
    private readonly string? _nodePackage;

    /// <param name="context">An application context.</param>
    /// <param name="nodePackage">The node app's package name to bind explicitly, or null to resolve any installed node.</param>
    public AndroidNodeConnector(Context context, string? nodePackage = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _nodePackage = nodePackage;
    }

    public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        // No MatchDefaultOnly: a service bound by action carries no DEFAULT category, so that flag resolves
        // nothing. Zero flags resolves the exported service by its action (and package, when set).
        var resolved = _context.PackageManager?.ResolveService(BindIntent(), (PackageInfoFlags)0);
        return Task.FromResult(resolved is not null);
    }

    public async Task<IAetherNodeClient?> TryBindAsync(CancellationToken cancellationToken = default)
    {
        var connection = new Connection();
        if (!BindService(connection))
        {
            SafeUnbind(connection);
            return null;
        }

        var binder = await connection.Bound.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (binder is null)
        {
            SafeUnbind(connection);
            return null;
        }

        // The client holds the connection (via onDispose) so the binding lives as long as the client and
        // is released when it is disposed.
        var client = new BinderNodeClient(binder, onDispose: () => SafeUnbind(connection));
        try
        {
            _ = await client.GetTagAsync(cancellationToken).ConfigureAwait(false);
            return client;   // installed, granted, unlocked
        }
        catch (AetherNodeException ex) when (ex.Code is AetherNodeErrorCode.GrantRequired or AetherNodeErrorCode.GrantDenied)
        {
            client.Dispose();   // unbinds
            return null;        // installed, but this app is not linked
        }
        catch (AetherNodeException)
        {
            return client;      // bound but e.g. locked (NodeUnavailable) — still a live binding
        }
    }

    // Where the platform allows it (API 29+), the connection is delivered on a background thread. On the
    // default main-thread delivery, a caller that blocks the main thread waiting for the connection waits
    // forever: the connection it is waiting for can only arrive on the thread it is blocking.
    private bool BindService(Connection connection)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return _context.BindService(BindIntent(), Bind.AutoCreate, ConnectionCallbacks.Value, connection);
        }

        return _context.BindService(BindIntent(), connection, Bind.AutoCreate);
    }

    private static readonly Lazy<Java.Util.Concurrent.IExecutor> ConnectionCallbacks =
        new(() => Java.Util.Concurrent.Executors.NewSingleThreadExecutor()!);

    private Intent BindIntent()
    {
        var intent = new Intent(AetherNodeAndroidService.BindAction);
        if (!string.IsNullOrEmpty(_nodePackage))
        {
            intent.SetPackage(_nodePackage);
        }

        return intent;
    }

    private void SafeUnbind(Connection connection)
    {
        try { _context.UnbindService(connection); } catch { /* was not bound */ }
    }

    private sealed class Connection : Java.Lang.Object, IServiceConnection
    {
        private readonly TaskCompletionSource<IBinder?> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IBinder?> Bound => _bound.Task;

        public void OnServiceConnected(ComponentName? name, IBinder? service) => _bound.TrySetResult(service);

        public void OnServiceDisconnected(ComponentName? name) => _bound.TrySetResult(null);
    }
}
#endif
