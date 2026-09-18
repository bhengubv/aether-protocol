// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AetherNet.Node.Host;

/// <summary>DI registration for the in-process node host.</summary>
public static class AetherNodeServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="AetherNodeService"/> as <see cref="IAetherNodeClient"/> plus an in-memory
    /// <see cref="IGrantStore"/>. The caller must have already registered <c>INodeIdentity</c> (e.g. via
    /// <c>AddNodeIdentity()</c>) and must provide <see cref="INodeMessaging"/> and
    /// <see cref="INodeLinkSource"/> adapters over the platform's messaging and radios.
    /// </summary>
    public static IServiceCollection AddAetherNode(this IServiceCollection services)
    {
        services.TryAddSingleton<IGrantStore, InMemoryGrantStore>();
        services.TryAddSingleton<IAetherNodeClient, AetherNodeService>();
        return services;
    }
}
