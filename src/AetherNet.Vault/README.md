# AetherNet.Vault

Erasure-coded distributed encrypted backup. Reed-Solomon (K=10, N=14 by default), each shard distributed across mesh peers, any K-of-N reconstructs the original. Self-healing replication. Production loss probability ~10⁻¹¹ per vault-year (see formal/vault-erasure-stochastic/).

```bash
dotnet add package AetherNet.Vault
```

```csharp
using AetherNet.Vault;

IVaultService vault = sp.GetRequiredService<IVaultService>();

var manifest = await vault.StoreAsync(documentStream, label: "Land deed - Erf 4231 Soweto");

// Health check
var health = await vault.CheckHealthAsync(manifest);
Console.WriteLine($"Shards reachable: {health.ReachableShards}/{health.TotalShards}");

// Recover
var recovered = await vault.RecoverAsync(manifest);
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
