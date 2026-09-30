// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AetherNetNodeService;
using AetherNetNodeService.Client;

namespace AetherNetNodeService.Android;

/// <summary>
/// The Android <see cref="INodeConnector"/>: detects whether the node service is installed and binds to it,
/// handing back a live <see cref="IAetherNodeClient"/>. It returns null from <see cref="TryBindAsync"/> when
/// the node is present but this app has not been granted a link — the caller then shows "awaiting grant". A
/// node that is bound but locked is still returned (its calls surface <c>NodeUnavailable</c> until unlocked),
/// because that is a live binding, not an absent one.
/// </summary>
public sealed class AndroidNodeConnector : INodeConnector
{
    /// <summary>How long a bind, and the first call on it, get before they are abandoned and tried again.</summary>
    /// <remarks>
    /// Long enough for AetherNetService to start from cold on a cheap phone; short enough that an app never
    /// waits on a bind that is not coming back. While the service is being updated Android can leave a bind with
    /// no answer at all, and every call queued behind it waited with it — sends sat at "pending" until the app
    /// was restarted (P30, 2026-09-30).
    /// </remarks>
    private static readonly TimeSpan BindWithin = TimeSpan.FromSeconds(20);

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

        IBinder? binder;
        try
        {
            binder = await connection.Bound.WaitAsync(BindWithin, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            binder = null;   // no answer — let go of it, so the next attempt starts a fresh bind
        }
        catch (System.OperationCanceledException)
        {
            SafeUnbind(connection);
            throw;
        }

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
            _ = await client.GetTagAsync(cancellationToken).WaitAsync(BindWithin, cancellationToken).ConfigureAwait(false);
            return client;   // installed, granted, unlocked
        }
        catch (TimeoutException)
        {
            client.Dispose();   // bound, but not answering — unbind and let the caller try again
            return null;
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
        catch (System.OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            // Died between the bind and the first call — what an update does. Let go of the binding rather than
            // leak it, and say so in the terms every caller already handles.
            client.Dispose();
            throw new AetherNodeException(AetherNodeErrorCode.NodeUnavailable, $"AetherNetService stopped answering: {ex.Message}", ex);
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

        // The binding itself is dead: Android says so when the service's app is updated or reinstalled while a
        // bind is open or on its way. Nothing will ever arrive on it again — the only way on is a new bind.
        // Unanswered, the bind waited forever, and so did every call behind it.
        public void OnBindingDied(ComponentName? name) => _bound.TrySetResult(null);

        // The service answered with no interface at all.
        public void OnNullBinding(ComponentName? name) => _bound.TrySetResult(null);
    }
}
#endif
