# CircleAether → aether-protocol Parity Guide

This document lists every concrete change needed to bring the internal
`Shared.CircleAether` / `CircleAetherAPI` implementation up to parity with the
public `bhengubv/aether-protocol` contracts. Apply them in order — later items
depend on earlier ones.

---

## 1 — Cryptography: ECDSA P-256 → Ed25519 / X25519

**Impact:** breaking wire change. Packets signed with different key types will
fail verification across implementations. Migrate internal keys before any
cross-implementation peer testing.

### 1a — `MauiKeyStorageService.cs` — key generation

Replace the existing `ECDsa.Create(ECCurve.NamedCurves.nistP256)` block:

```csharp
// BEFORE
using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var privateKeyBytes = ecdsa.ExportECPrivateKey();
var publicKeyBytes  = ecdsa.ExportSubjectPublicKeyInfo();

// AFTER — Ed25519 via NSec or Bouncy Castle
// Option A: NSec (recommended, MIT)
//   dotnet add package NSec.Cryptography
using NSec.Cryptography;
var algorithm  = SignatureAlgorithm.Ed25519;
using var key  = Key.Create(algorithm, new KeyCreationParameters
{
    ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
});
var privateKeyBytes = key.Export(KeyBlobFormat.RawPrivateKey);
var publicKeyBytes  = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
```

Store `privateKeyBytes` (32 bytes) and `publicKeyBytes` (32 bytes) in
`SecureStorage` under the same keys you use today — just replace the PEM/DER
blobs with the raw 32-byte representations.

### 1b — `SimpleAesEncryption.cs` / `SignalProtocolService.cs` — ECDH → X25519

Replace `ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)` with X25519:

```csharp
// AFTER — X25519 via NSec
using NSec.Cryptography;
var x25519 = KeyAgreementAlgorithm.X25519;
using var myKey      = Key.Create(x25519, ...);
using var theirKey   = PublicKey.Import(x25519, theirPublicKeyBytes, KeyBlobFormat.RawPublicKey);
var sharedSecret     = x25519.Agree(myKey, theirKey)!;
// derive with HKDF as before
```

### 1c — `NodeInitializationService.cs`

Update the UHID derivation to match the public contract:

```csharp
// public repo: AetherTag.FromPublicKey(ed25519PublicKeyBytes)
var uhid = AetherTag.FromPublicKey(publicKeyBytes).ToString();
```

`AetherTag.FromPublicKey` is in `Aether.Core` (`Aether.Core.Identity` namespace)
— add a project/package reference to `Aether.Core` if not already present.

---

## 2 — Implement `IAetherIncentiveProvider` on `AetherRewardService`

File: `code/Shared/TheGeekNetwork.Shared.CircleAether/Incentives/Services/AetherRewardService.cs`

```csharp
using Aether.Extensibility;   // IAetherIncentiveProvider
using Aether.Protocol;        // MeshPacket

public sealed class AetherRewardService : IAetherIncentiveProvider
{
    // ... existing fields ...

    /// <inheritdoc/>
    public Task RecordRelayAsync(
        string relayNodeUhid,
        MeshPacket packet,
        CancellationToken cancellationToken = default)
    {
        // Map to existing QueueRewardAsync logic:
        var reward = new PendingReward
        {
            NodeUhid  = relayNodeUhid,
            PacketId  = packet.PacketId.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
        };
        return QueueRewardAsync(reward, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> ShouldPrioritizeAsync(
        MeshPacket packet,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false); // extend later via reputation score
}
```

---

## 3 — Implement `IAetherBackendClient` on `AetherApiClient`

File: `code/Shared/TheGeekNetwork.Shared.CircleAether/ApiClients/AetherApiClient.cs`

```csharp
using Aether.Extensibility;
using Aether.Models;   // AetherNode, DtnBundle, SosAlert

public sealed class AetherApiClient : IAetherBackendClient
{
    /// <inheritdoc/>
    public async Task<bool> SyncNodeAsync(
        AetherNode node,
        CancellationToken cancellationToken = default)
    {
        // Use existing RegisterNodeAsync / HeartbeatAsync logic.
        // Map AetherNode fields → your existing DTO.
        try
        {
            await RegisterNodeAsync(new NodeRegistrationDto
            {
                Uhid         = node.Uhid,
                PublicKey    = node.PublicKey,
                Capabilities = (int)node.Capabilities,
                Geohash      = node.Geohash,
            }, cancellationToken);
            return true;
        }
        catch { return false; }
    }

    /// <inheritdoc/>
    public async Task<byte[]?> FetchPreKeyBundleAsync(
        string targetUhid,
        CancellationToken cancellationToken = default)
    {
        // Call existing GET /nodes/{uhid}/prekey endpoint if you have one,
        // or return null until you add that endpoint.
        return null;
    }

    /// <inheritdoc/>
    public async Task<bool> SyncDtnBundleAsync(
        DtnBundle bundle,
        CancellationToken cancellationToken = default)
    {
        // Map DtnBundle → your existing DTN relay DTO and POST it.
        // Return false (offline) if the call fails — the mesh handles retry.
        try
        {
            await PostDtnBundleAsync(bundle, cancellationToken);
            return true;
        }
        catch { return false; }
    }

    /// <inheritdoc/>
    public async Task<bool> SyncSosAsync(
        SosAlert alert,
        CancellationToken cancellationToken = default)
    {
        // Forward to your existing SosBridgeService / CircleAetherAPI SOS endpoint.
        try
        {
            await PostSosAlertAsync(alert, cancellationToken);
            return true;
        }
        catch { return false; }
    }

    /// <inheritdoc/>
    public async Task<bool> RelayMessageAsync(
        string senderUhid,
        string recipientUhid,
        byte[] encryptedContent,
        byte priority,
        CancellationToken cancellationToken = default)
    {
        // Map to existing POST /relay endpoint (message_relay table).
        try
        {
            await PostMessageRelayAsync(new RelayMessageRequest
            {
                SenderUhid    = senderUhid,
                RecipientUhid = recipientUhid,
                Payload       = encryptedContent,
                Priority      = priority,
            }, cancellationToken);
            return true;
        }
        catch { return false; }
    }
}
```

---

## 4 — Implement `IAetherFeatureFlagProvider` on `AetherFeatureFlagService`

File: `code/Shared/TheGeekNetwork.Shared.CircleAether/FeatureFlags/AetherFeatureFlagService.cs`

```csharp
using Aether.Extensibility;

public sealed class AetherFeatureFlagService : IAetherFeatureFlagProvider
{
    /// <inheritdoc/>
    public Task<bool> IsEnabledAsync(
        string featureName,
        CancellationToken cancellationToken = default)
    {
        // Delegate to your existing IsEnabled(string) method.
        // The existing SQLite cache + API fetch path handles TTL and fallback.
        return Task.FromResult(IsEnabled(featureName));
    }
}
```

---

## 5 — Storage: Register `SqliteKeyValueStore` as `IKeyValueStore`

The public `Aether.Storage` package's `KeyValueDtnBundleStore`,
`KeyValueMessageStore`, `KeyValueRouteStore`, etc. all depend on `IKeyValueStore`.
Providing a SQLite-backed implementation lets you retire the hand-rolled CRUD
in `AetherStorageService` one table at a time.

Create: `code/Shared/TheGeekNetwork.Shared.CircleAether/Storage/SqliteKeyValueStore.cs`

```csharp
using Aether.Storage;
using Dapper;
using Microsoft.Data.Sqlite;

/// <summary>
/// IKeyValueStore backed by the existing aether_local.db SQLite database.
/// Uses a single `kv_store` table (key TEXT PK, value BLOB, expires_at INTEGER).
/// </summary>
public sealed class SqliteKeyValueStore : IKeyValueStore
{
    private readonly string _connectionString;

    public SqliteKeyValueStore(string connectionString)
        => _connectionString = connectionString;

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        var row = await conn.QueryFirstOrDefaultAsync<(byte[]? Value, long? ExpiresAt)>(
            "SELECT value, expires_at FROM kv_store WHERE key = @key", new { key });
        if (row.ExpiresAt.HasValue && row.ExpiresAt.Value < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            await RemoveAsync(key, ct);
            return null;
        }
        return row.Value;
    }

    public async Task SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        long? expiresAt = ttl.HasValue
            ? DateTimeOffset.UtcNow.Add(ttl.Value).ToUnixTimeSeconds()
            : null;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT INTO kv_store (key, value, expires_at) VALUES (@key, @value, @expiresAt)
            ON CONFLICT(key) DO UPDATE SET value = @value, expires_at = @expiresAt
            """,
            new { key, value, expiresAt });
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM kv_store WHERE key = @key", new { key });
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM kv_store WHERE key = @key", new { key }) > 0;
    }
}
```

Add the migration (append to your last migration file or create a new one):

```sql
CREATE TABLE IF NOT EXISTS kv_store (
    key        TEXT    NOT NULL PRIMARY KEY,
    value      BLOB    NOT NULL,
    expires_at INTEGER          -- Unix seconds; NULL = never expires
);
CREATE INDEX IF NOT EXISTS ix_kv_store_expires ON kv_store(expires_at)
    WHERE expires_at IS NOT NULL;
```

---

## 6 — DI alignment: wire `AddCircleAether()` through `IAetherProtocolBuilder`

File: `code/Shared/TheGeekNetwork.Shared.CircleAether/AetherExtensions.cs`

Register CircleAether's concrete implementations **before** calling
`AddAetherProtocol` so `TryAddSingleton` picks them up instead of the in-memory
defaults:

```csharp
using Aether.DependencyInjection;
using Aether.Extensibility;
using Aether.Storage;

public static class AetherExtensions
{
    public static IServiceCollection AddCircleAether(
        this IServiceCollection services,
        string localUhid)
    {
        // 1. Register CircleAether-specific seam implementations first.
        //    TryAddSingleton means the public builder's defaults are skipped
        //    for anything registered here.
        services.TryAddSingleton<IAetherBackendClient, AetherApiClient>();
        services.TryAddSingleton<IAetherIncentiveProvider, AetherRewardService>();
        services.TryAddSingleton<IAetherFeatureFlagProvider, AetherFeatureFlagService>();
        services.TryAddSingleton<IKeyValueStore>(sp =>
            new SqliteKeyValueStore(AetherStorageService.ConnectionString));

        // 2. Platform BLE / Wi-Fi Direct — registered in MauiProgram.cs via
        //    #if ANDROID / #if IOS guards as before. No change needed there.

        // 3. Wire the public protocol stack.
        services
            .AddAetherProtocol(opts => opts.LocalUhid = localUhid)
            .AddSignalProtocol()
            .AddRouting()
            .AddDtn()
            .AddSosBroadcast()
            .AddMessaging()
            .AddStreaming()
            .AddWatchTogether()
            .AddVideoCall()
            .AddGroupVideo()
            .AddVoice()
            .AddGroupVoice()
            .AddContent()
            .AddReputation()
            .AddGossip()
            .AddHandshake()
            .AddHealthChecks();

        // 4. Remaining CircleAether-only services (no public equivalent).
        services.AddSingleton<AetherFeatureFlagService>();   // also concrete for GetCachedFlagsAsync
        services.AddSingleton<NodeInitializationService>();
        services.AddSingleton<MeshProtocolService>();        // heartbeat orchestrator
        services.AddSingleton<GatewayService>();
        services.AddSingleton<CacheService>();

        return services;
    }
}
```

---

## 7 — NuGet package references to add to `Shared.CircleAether.csproj`

```xml
<PackageReference Include="Aether.Core"                   Version="1.*" />
<PackageReference Include="Aether.Security"               Version="1.*" />
<PackageReference Include="Aether.Messaging"              Version="1.*" />
<PackageReference Include="Aether.Transport"              Version="1.*" />
<PackageReference Include="Aether.Storage"                Version="1.*" />
<PackageReference Include="Aether.Streaming"              Version="1.*" />
<PackageReference Include="Aether.Voice"                  Version="1.*" />
<PackageReference Include="Aether.Content"                Version="1.*" />
<PackageReference Include="Aether.DependencyInjection"    Version="1.*" />
<PackageReference Include="NSec.Cryptography"             Version="24.*" />
```

Remove the private NuGet feed reference for `Aether.Protocol.*` once the above
are resolving from nuget.org.

---

## Migration order

1. Add NuGet references (§7)
2. Cryptography migration (§1) — test key round-trip before touching anything else
3. Implement interfaces (§2, §3, §4) — each is independent
4. Add `SqliteKeyValueStore` + migration (§5)
5. Rewrite `AddCircleAether()` (§6)
6. Delete hand-rolled services that are now covered by the public stack
   (`RoutingService`, `MessageSyncService`, `MeshMessagingService`,
   `MessageRelayService` — verify each is replaceable before deleting)
7. Run cross-language fixture tests: `dotnet test --filter WireCompatibility`
