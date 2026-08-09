// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AetherNet.Content.Sqlite;

/// <summary>DI helpers for the durable SQLite content store.</summary>
public static class SqliteContentStoreServiceCollectionExtensions
{
    /// <summary>
    /// Register a durable SQLite-backed <see cref="IContentStore"/> at <paramref name="databasePath"/>.
    /// Call this BEFORE <c>AddContent()</c> so the content service resolves this store instead of the
    /// in-memory default (the same register-first seam every other AetherNet store uses).
    /// </summary>
    public static IServiceCollection AddSqliteContentStore(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        services.TryAddSingleton<IContentStore>(_ => new SqliteContentStore(databasePath));
        return services;
    }
}
