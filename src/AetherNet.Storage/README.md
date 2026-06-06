# AetherNet.Storage

Pluggable key-value storage abstraction (in-memory, filesystem, encrypted-at-rest), with KeyValue-backed adapters for DtnBundleStore, MessageStore, RouteStore, PreKeyStore, SignalSessionStore. Encrypted-at-rest wraps any store with AES-GCM envelope encryption.

```bash
dotnet add package AetherNet.Storage
```

```csharp
using AetherNet.Storage;

// Pick a backend
IKeyValueStore kv = new FileSystemKeyValueStore(rootPath: "./aether-state");

// Wrap with at-rest encryption
var keyProvider = new DerivedDataAtRestKeyProvider(deviceMasterKey);
IKeyValueStore encrypted = new EncryptedKeyValueStore(kv, keyProvider);

// Use as the backing store for any AetherNet adapter
var routeStore = new KeyValueRouteStore(encrypted);
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
