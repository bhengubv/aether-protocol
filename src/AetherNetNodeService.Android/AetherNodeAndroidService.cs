// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using AetherNetNodeService;
using AetherNetNodeService.Host;

namespace AetherNetNodeService.Android;

/// <summary>
/// The device side of the bind. An exported Android service that hosts the one <see cref="IAetherNodeClient"/>
/// (the reference in-process host) and answers consumer apps across the process boundary.
///
/// <para>
/// A consumer binds with the action <see cref="BindAction"/>; the returned <see cref="NodeServiceBinder"/>
/// serves each transaction on a binder thread, where <c>Binder.CallingUid</c> is valid — so the node knows
/// exactly which app is calling and checks its grant against that, not against anything the caller sends.
/// Only the results of sign / send / etc. cross back; the private key never leaves this process.
/// </para>
///
/// <para>
/// The node app wires the host once, at startup, via <see cref="Configure"/> — before any consumer can bind.
/// </para>
///
/// <para>
/// Abstract, and declared by nobody here. Only the app that hosts the service declares it — a one-line subclass
/// carrying <c>[Service(Exported = true, Name = ServiceName)]</c> and <c>[IntentFilter([BindAction])]</c>. It used
/// to carry those attributes itself, and since both apps use this library, Aether declared an exported service
/// too: one that nobody had set up, so a bind to it would have crashed Aether.
/// </para>
/// </summary>
public abstract class AetherNodeAndroidService : Service
{
    /// <summary>The service's stable class name, so consumers can address it explicitly.</summary>
    public const string ServiceName = "com.bhengubv.aethernet.service";

    /// <summary>The intent action a consumer binds with.</summary>
    public const string BindAction = "com.bhengubv.aethernet.service.BIND";

    private static Func<IAetherNodeClient>? _hostResolver;
    private static IGrantStore? _grants;

    /// <summary>
    /// Wire the node host and grant store once, at node-app startup, before any consumer binds. The resolver
    /// is called per bind so the host can be a scoped/lazy singleton.
    /// </summary>
    public static void Configure(Func<IAetherNodeClient> hostResolver, IGrantStore grants)
    {
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
    }

    public override IBinder? OnBind(Intent? intent)
    {
        if (_hostResolver is null || _grants is null)
        {
            throw new InvalidOperationException(
                "AetherNodeAndroidService.Configure(host, grants) must run at node-app startup before a consumer binds.");
        }

        return new NodeServiceBinder(_hostResolver(), _grants, PackageManager!);
    }
}
#endif
