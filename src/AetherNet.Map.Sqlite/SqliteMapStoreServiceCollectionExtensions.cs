// SPDX-License-Identifier: MIT
using AetherNet.Map;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AetherNet.Map.Sqlite;

/// <summary>DI helpers for the durable SQLite map store.</summary>
public static class SqliteMapStoreServiceCollectionExtensions
{
    /// <summary>
    /// Register a durable SQLite-backed <see cref="IMapStore"/> at <paramref name="databasePath"/>.
    /// Call this BEFORE <c>AddMap()</c> so the map service resolves this store instead of the in-memory
    /// default (the same register-first seam every other AetherNet store uses).
    /// </summary>
    public static IServiceCollection AddSqliteMapStore(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        services.TryAddSingleton<IMapStore>(_ => new SqliteMapStore(databasePath));
        return services;
    }
}
