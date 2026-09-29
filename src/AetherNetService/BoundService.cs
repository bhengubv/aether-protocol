// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App;
using AetherNetNodeService.Android;

namespace AetherNetService;

/// <summary>
/// The service other apps bind to: <c>com.bhengubv.aethernet.service</c>. Declared here, in the one app that hosts
/// it, and nowhere else — the behaviour is the library's <see cref="AetherNodeAndroidService"/>, set up by
/// <see cref="MainApplication"/> at startup.
/// </summary>
[Service(Exported = true, Name = AetherNodeAndroidService.ServiceName)]
[IntentFilter([AetherNodeAndroidService.BindAction])]
public sealed class BoundService : AetherNodeAndroidService
{
}
#endif
