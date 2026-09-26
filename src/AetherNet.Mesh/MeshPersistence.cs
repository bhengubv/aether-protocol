// SPDX-License-Identifier: MIT

namespace AetherNet.Mesh;

/// <summary>
/// The persistence <see cref="CircleDirectory"/> needs from its host: the peer routing keys this device
/// holds, learned inside established sessions and never on the wire. The store itself lives in the host
/// — the sample keeps these rows in its on-device SQLite database — so the mesh only reads and writes
/// them and owns no storage of its own.
/// </summary>
public interface IPeerRoutingKeyStore
{
    /// <summary>Every peer routing key this device holds, by AetherTag.</summary>
    IReadOnlyDictionary<string, byte[]> GetPeerRoutingKeys();

    /// <summary>Remember a contact's routing key, replacing any earlier one for the same contact.</summary>
    void UpsertPeerRoutingKey(string tag, byte[] routingKey);

    /// <summary>Forget a contact's routing key — they can no longer be recognised behind a rotating address.</summary>
    void RemovePeerRoutingKey(string tag);
}

/// <summary>A contact as <see cref="ProxyDirectory"/> sees it: who they are, and whether the add is mutual.</summary>
public sealed record ProxyContact(string Tag, bool IsMutual);

/// <summary>
/// The persistence <see cref="ProxyDirectory"/> needs from its host: the gateway on/off flag (persisted
/// across restarts) and the roster of contacts, so the directory can offer relaying only to people this
/// device has a mutual relationship with. Backed by the host's own store.
/// </summary>
public interface IProxyDirectoryStore
{
    /// <summary>Read a persisted boolean flag (e.g. whether this device is offering to relay).</summary>
    bool GetFlag(string key);

    /// <summary>Persist a boolean flag.</summary>
    void SetFlag(string key, bool value);

    /// <summary>The contacts this device knows, as the proxy directory needs to see them.</summary>
    IReadOnlyList<ProxyContact> GetContacts();
}
