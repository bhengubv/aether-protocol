# AetherNet.Map

Durable, conflict-free neighbourhood map for AetherNet — the `aether-map` Phase-2 extension.

A **field-level CRDT** (clocked by a Hybrid Logical Clock) over map *features* — storefronts, sidewalk
accessibility, environmental readings — that merges concurrent multi-author edits **offline,
peer-to-peer, with no server**. Two people editing different fields (or even the same field) of the same
feature while partitioned both survive the merge; the old whole-record last-write-wins would silently
drop one.

This package is **pure managed code with no third-party dependencies** — it is the layer the other seven
language SDKs port, byte-for-byte, pinned by `fixtures/map/`. The durable, queryable on-device store
(SQLite) ships separately in **`AetherNet.Map.Sqlite`** so this core stays dependency-free.

Wire: one feature's CRDT ops serialise via `MapFeatureCodec`, and a node entering a new geohash cell
issues an anti-entropy pull via `MapFeatureRequestCodec`. The packet-type binding and mesh dispatch
live in the consuming app — the map is an app-domain capability layered on the neutral AetherNet pipe —
riding the existing broadcast + DTN geocast paths.
