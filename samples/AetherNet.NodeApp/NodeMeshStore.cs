// SPDX-License-Identifier: MIT
#if ANDROID
using System.Text.Json;
using AetherNet.Mesh;

namespace AetherNet.NodeApp;

/// <summary>
/// The node's own persistence for the two mesh directory seams: the peer routing keys it has learned inside
/// established sessions (<see cref="IPeerRoutingKeyStore"/>), and the gateway flags plus contact roster the
/// proxy directory reads (<see cref="IProxyDirectoryStore"/>). The sample keeps these rows in its SQLite
/// database; the node keeps them as small JSON files in its own private directory, so the mesh owns no
/// storage of its own and a restart does not make the node forget who it can recognise behind a rotating
/// address.
/// </summary>
internal sealed class NodeMeshStore : IPeerRoutingKeyStore, IProxyDirectoryStore
{
    private readonly object _gate = new();
    private readonly string _keysPath;
    private readonly string _flagsPath;
    private readonly Dictionary<string, byte[]> _routingKeys;
    private readonly Dictionary<string, bool> _flags;

    public NodeMeshStore(string directory)
    {
        _keysPath = Path.Combine(directory, "aether-node-routing-keys.json");
        _flagsPath = Path.Combine(directory, "aether-node-flags.json");
        _routingKeys = Load<Dictionary<string, byte[]>>(_keysPath) ?? new();
        _flags = Load<Dictionary<string, bool>>(_flagsPath) ?? new();
    }

    public IReadOnlyDictionary<string, byte[]> GetPeerRoutingKeys()
    {
        lock (_gate) { return new Dictionary<string, byte[]>(_routingKeys); }
    }

    public void UpsertPeerRoutingKey(string tag, byte[] routingKey)
    {
        lock (_gate) { _routingKeys[tag] = routingKey; Persist(_keysPath, _routingKeys); }
    }

    public void RemovePeerRoutingKey(string tag)
    {
        lock (_gate) { if (_routingKeys.Remove(tag)) Persist(_keysPath, _routingKeys); }
    }

    public bool GetFlag(string key)
    {
        lock (_gate) { return _flags.TryGetValue(key, out var v) && v; }
    }

    public void SetFlag(string key, bool value)
    {
        lock (_gate) { _flags[key] = value; Persist(_flagsPath, _flags); }
    }

    // The node holds no contact list of its own — contacts live in a consumer app. The proxy directory
    // offers relaying only to mutual contacts, so with none it simply offers to no one, which is the
    // correct default for an identity/transport node.
    public IReadOnlyList<ProxyContact> GetContacts() => Array.Empty<ProxyContact>();

    private static T? Load<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch { return default; }   // a corrupt file must not stop the node — treat it as empty
    }

    private static void Persist<T>(string path, T value)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(value)); }
        catch { /* best effort; the in-memory copy stays authoritative for this run */ }
    }
}
#endif
