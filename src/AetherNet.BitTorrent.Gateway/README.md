# AetherNet.BitTorrent.Gateway

The bridge between **BitTorrent** and the **AetherNet mesh**. `TorrentMeshGateway` takes a `.torrent`
(or torrent content) and re-packages it into AetherNet's own SHA-256 content chunks in an
`IContentStore` — so a node with internet can pull a file from a public BitTorrent swarm and re-share it
across the offline mesh, and a peer with **no internet at all** still receives it, hop by hop.

```bash
dotnet add package AetherNet.BitTorrent.Gateway
```

```csharp
using AetherNet.BitTorrent.Gateway;

// Ingest torrent bytes into the mesh content store as AetherNet chunks
ContentDescriptor descriptor = await TorrentMeshGateway.IngestAsync(
    store,            // your IContentStore
    "release.iso",
    torrentBytes);

// `descriptor` now identifies the content in the mesh store; an offline peer
// reassembles the identical file over the AetherNet chunk protocol.
```

Part of [aether-protocol](https://github.com/bhengubv/aether-protocol) — an open-source, offline-first
mesh networking protocol. Pairs with `AetherNet.BitTorrent` (the protocol core) and `AetherNet.Content`
(the mesh chunk store). See the
[BitTorrent section](https://github.com/bhengubv/aether-protocol#bittorrent--real-and-bridged-into-the-mesh).
