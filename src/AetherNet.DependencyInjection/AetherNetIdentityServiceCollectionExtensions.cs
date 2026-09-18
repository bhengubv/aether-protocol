// SPDX-License-Identifier: MIT

using System;
using AetherNet.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AetherNet.DependencyInjection;

/// <summary>
/// One-call wiring for the device's node identity and its portability surface.
/// </summary>
public static class AetherNetIdentityServiceCollectionExtensions
{
    /// <summary>
    /// Register this device's node identity: <see cref="INodeIdentity"/> for everyday use — get the tag,
    /// sign, derive purpose keys — and <see cref="INodeIdentityRecovery"/> for moving it — adopt a seed,
    /// export or restore a recovery phrase. Both are the reference implementations, and both share the ONE
    /// <see cref="INodeIdentityStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store is NOT registered here, on purpose: where a device keeps its identity, and how the lock
    /// screen gates it, is platform-specific, so the platform registers its own <see cref="INodeIdentityStore"/>
    /// (a device-wide, cross-application one — a store private to a single app produces exactly the
    /// per-application identities this whole arrangement exists to prevent). This call needs one to be
    /// registered to resolve.
    /// </para>
    /// <para>
    /// Registering identity and recovery from one call, over one store, is the point: it is how a consumer
    /// gets "one device, one node, portable" without hand-rolling the pieces and reproducing the tag a
    /// slightly different way each time. Uses <c>TryAddSingleton</c> throughout, so a platform that needs a
    /// bespoke identity or recovery can register its own first and still win.
    /// </para>
    /// </remarks>
    /// <param name="services">The host's service collection.</param>
    public static IServiceCollection AddNodeIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<INodeIdentity, NodeIdentity>();
        services.TryAddSingleton<INodeIdentityRecovery, NodeIdentityRecovery>();
        return services;
    }
}
