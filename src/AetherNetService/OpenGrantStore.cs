// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Node;
using AetherNet.Node.Host;

namespace AetherNetService;

/// <summary>
/// AetherNetService is a network cable: it has no gate of its own. Security is upstream, at the phone's
/// lock — biometrics, pattern, PIN — so no access to the phone means no access to the service. Every app
/// that calls it is admitted; there is no approval step and no screen to approve on.
/// </summary>
internal sealed class OpenGrantStore : IGrantStore
{
    public AppGrant Get(string appId) => new(appId, GrantState.Bound, DateTimeOffset.UtcNow);

    public AppGrant Save(AppGrant grant) => grant;

    public IReadOnlyList<AppGrant> All() => Array.Empty<AppGrant>();
}
#endif
