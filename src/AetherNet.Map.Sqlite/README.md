# AetherNet.Map.Sqlite

Durable, queryable on-device store for **AetherNet.Map** — the SQLite backing for the neighbourhood-map CRDT.

Backs `IMapStore` with a `Microsoft.Data.Sqlite` database: one row per map feature, the merge-authoritative
CRDT state held as a blob, and a **geohash range index** so a proximity query ("what features are near me?")
is an indexed range scan rather than a full-table walk.

Kept as a **separate package** so the `AetherNet.Map` core stays pure managed code with no third-party
dependencies — and portable, byte-for-byte, to the other seven language SDKs. Take `AetherNet.Map` alone for
the in-memory CRDT; add this package when you want the durable on-device store.
