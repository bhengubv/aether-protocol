// SPDX-License-Identifier: MIT

using AetherMesh.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherMesh.DependencyInjection;

/// <summary>
/// Optional encryption-at-rest extension for <see cref="IAetherMeshProtocolBuilder"/>.
/// Wraps whatever <see cref="IKeyValueStore"/> the host has registered with an
/// <see cref="EncryptedKeyValueStore"/> using a host-supplied
/// <see cref="IDataAtRestKeyProvider"/>.
///
/// <para>
/// Usage:
/// </para>
/// <code>
/// services.AddSingleton&lt;IKeyValueStore&gt;(_ =&gt; new FileSystemKeyValueStore(rootDir));
/// services.AddAetherMeshProtocol()
///         .AddEncryptedAtRest(new StaticDataAtRestKeyProvider(masterKey))
///         .AddSignalProtocol()
///         .AddRouting()
///         .AddDtn()
///         .AddMessaging();
/// </code>
///
/// <para>
/// Order matters: the encryption wrapper is applied to whatever
/// <see cref="IKeyValueStore"/> is currently registered when this method is
/// called. Hosts that want a specific inner store should register it before
/// calling <c>AddEncryptedAtRest</c>; otherwise the wrapper is layered over
/// an <see cref="InMemoryKeyValueStore"/> default which is mostly useful in
/// tests.
/// </para>
/// </summary>
public static class AetherMeshProtocolBuilderEncryptionExtensions
{
    /// <summary>
    /// Wrap the registered <see cref="IKeyValueStore"/> with an
    /// <see cref="EncryptedKeyValueStore"/> using the supplied key provider.
    /// </summary>
    /// <param name="builder">The Aether protocol builder.</param>
    /// <param name="keyProvider">
    /// Supplies the AES-256 master key(s). Hosts derive these bytes however
    /// they like — passphrase via PBKDF2, OS keychain, hardware enclave, etc.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAetherMeshProtocolBuilder AddEncryptedAtRest(
        this IAetherMeshProtocolBuilder builder,
        IDataAtRestKeyProvider keyProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(keyProvider);

        // If no IKeyValueStore is registered yet, default to in-memory so the
        // chain still resolves. Hosts that want durability register
        // FileSystemKeyValueStore (or their own) before calling this method.
        builder.Services.TryAddSingleton<IKeyValueStore, InMemoryKeyValueStore>();

        // Replace the registered IKeyValueStore with one that wraps it through
        // the encryption layer. We grab the existing registration's
        // implementation factory (or type) and rebuild around it.
        var existing = FindKeyValueStoreRegistration(builder.Services)
            ?? throw new InvalidOperationException(
                "AddEncryptedAtRest could not locate an IKeyValueStore registration. " +
                "Register one (e.g. FileSystemKeyValueStore) before calling AddEncryptedAtRest.");

        builder.Services.Remove(existing);

        builder.Services.AddSingleton(keyProvider);
        builder.Services.AddSingleton<IKeyValueStore>(sp =>
        {
            var inner = BuildInner(existing, sp);
            var logger = sp.GetService<ILogger<EncryptedKeyValueStore>>()
                ?? NullLogger<EncryptedKeyValueStore>.Instance;
            return new EncryptedKeyValueStore(inner, keyProvider, logger);
        });

        return builder;
    }

    private static ServiceDescriptor? FindKeyValueStoreRegistration(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IKeyValueStore))
                return services[i];
        }
        return null;
    }

    private static IKeyValueStore BuildInner(ServiceDescriptor descriptor, IServiceProvider sp)
    {
        if (descriptor.ImplementationInstance is IKeyValueStore instance)
            return instance;

        if (descriptor.ImplementationFactory is not null)
            return (IKeyValueStore)descriptor.ImplementationFactory(sp);

        if (descriptor.ImplementationType is not null)
            return (IKeyValueStore)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);

        throw new InvalidOperationException(
            "IKeyValueStore registration has neither an instance, factory, nor implementation type.");
    }
}
