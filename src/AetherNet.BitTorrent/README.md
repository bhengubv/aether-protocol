# AetherNet.BitTorrent

A from-scratch, interoperable **BitTorrent** implementation for .NET — the real protocol, not a look-alike. It speaks bencode, `.torrent`/magnet parsing, the SHA-1 info-hash, the BEP-3 peer-wire with rarest-first piece selection, HTTP + UDP trackers (BEP-3/15/23), Mainline DHT + PEX + ut_metadata (BEP-5/11/9/10), µTP (BEP-29), and BitTorrent v2 SHA-256 merkle hashing (BEP-52). Verified against **MonoTorrent**: given the same file, both compute the identical info-hash, so any real torrent client interoperates with it.

```bash
dotnet add package AetherNet.BitTorrent
```

```csharp
using AetherNet.BitTorrent.Metainfo;

// Build a .torrent from a single file, then read back its info-hash
byte[] torrent = TorrentBuilder.CreateSingleFile("release.iso", fileBytes, pieceLength: 262144);
TorrentMetainfo meta = TorrentMetainfo.Parse(torrent);
Console.WriteLine(meta.InfoHashV1Hex);   // 40-hex SHA-1 — identical to any real BitTorrent client

// Parse a magnet link
MagnetLink magnet = MagnetLink.Parse("magnet:?xt=urn:btih:...");
Console.WriteLine(magnet.InfoHashV1Hex);
```

Part of [aether-protocol](https://github.com/bhengubv/aether-protocol) — an open-source, offline-first
mesh networking protocol. See the
[BitTorrent section](https://github.com/bhengubv/aether-protocol#bittorrent--real-and-bridged-into-the-mesh)
for how a node joins a real swarm and bridges content into the offline mesh; `AetherNet.BitTorrent.Gateway`
provides that mesh bridge.
