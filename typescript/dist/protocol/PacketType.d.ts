/**
 * Packet type enumeration matching C# values exactly
 * SPDX-License-Identifier: MIT
 */
export declare enum PacketType {
    RouteRequest = 1,
    RouteReply = 2,
    Data = 3,
    Ack = 4,
    SosBroadcast = 5,
    SosAck = 6,
    ChannelMessage = 7,
    ChunkRequest = 8,
    ChunkData = 9,
    Heartbeat = 10,
    StreamAnnounce = 11,
    StreamSegment = 12,
    StreamSubscribe = 13,
    StreamUnsubscribe = 14,
    VoicePtt = 15,
    VoiceCall = 16,
    VoiceSignaling = 17,
    DtnBundle = 18,
    DtnCustodyAck = 19,
    DtnDeliveryReceipt = 20,
    PresenceBeacon = 21,
    PresenceQuery = 22,
    ProfileSync = 23,
    TipPacket = 24,
    PreKeyRequest = 25,
    PreKeyResponse = 26,
    VideoCall = 27,
    VideoSignaling = 28,
    WatchSync = 29,
    WatchReaction = 30,
    VideoFrame = 31,
    ScreenShare = 32,
    WatchChunkRequest = 33,
    TorrentMetadata = 34,
    /**
     * SpaceBreadcrumb — aether-space geo-pinned community noticeboard. A node
     * broadcasts this when it drops (or re-hosts) a breadcrumb at a geohash cell so
     * passing peers with the aether-space extension can pin and re-host it — fully
     * offline. Payload is a UTF-8 JSON-encoded SpaceBreadcrumbPayload. Matches the
     * C# reference value 40.
     */
    SpaceBreadcrumb = 40,
    /**
     * ForgeAnnounce — aether-forge package-cache announcement. A node broadcasts this
     * when it caches a new package artifact so mesh peers with the aethernet.forge/v1
     * capability learn where the artifact lives. Payload is a UTF-8 JSON-encoded
     * ForgeAnnouncePayload. Matches the C# reference value 41.
     */
    ForgeAnnounce = 41,
    /**
     * VaultShardRequest — aether-vault erasure-coded-storage shard request. A node
     * broadcasts this to ask the mesh for a shard it needs to recover a file. Payload
     * is a UTF-8 JSON-encoded VaultShardRequestPayload. Matches the C# reference value 42.
     */
    VaultShardRequest = 42,
    /**
     * PoVTokenExchange — on-mesh Proof-of-Vicinity token exchange. A witness node
     * issues a directed (TTL 1), Ed25519-signed PoVToken to a co-present subject;
     * the subject verifies the witness signature, counter-signs the same canonical
     * body, and records it as a local anti-Sybil routing/identity signal. Carried
     * as a UTF-8 JSON-encoded PoVToken. Two-key (witness + subject) co-presence
     * proof — attaches NO value semantics. Matches the C# / Go reference value 43.
     */
    PoVTokenExchange = 43,
    /**
     * NamePublish — application-layer name resolution. Sent by IDirectoryService
     * to announce a (name -> ContentDescriptor) binding to the mesh, or in
     * response to an inbound NameQuery from a peer that asked for the binding.
     * Payload is a UTF-8 JSON-encoded NamePublishPayload. Added in v1.2.0 —
     * closes Issue #60 surfaced by Wave 16.
     */
    NamePublish = 38,
    /**
     * NameQuery — application-layer name resolution. Sent by IDirectoryService
     * when resolve() misses the local cache; flooded across the mesh so any
     * node holding the binding can reply with a NamePublish carrying the
     * matching ContentDescriptor. Payload is a UTF-8 JSON-encoded
     * NameQueryPayload. Added in v1.2.0 — closes Issue #60.
     */
    NameQuery = 39,
    /**
     * Capability handshake — sender announces supported protocol-version range
     * + capability flags. Sent on first contact with an unknown peer. The
     * payload is a UTF-8 JSON-encoded HelloPayload. Unauthenticated and
     * unencrypted — peer identity is verified later via Ed25519 packet
     * signatures.
     */
    Hello = 50,
    /**
     * Reply to a Hello — receiver echoes back the agreed (highest
     * mutually-supported) protocol version and the intersection of capability
     * flags. Same JSON payload shape as Hello.
     */
    HelloAck = 51,
    /**
     * BandwidthProbe — active probe packet sent to a peer to measure RTT and
     * delivery rate. Payload carries four timestamps per RFC 5136 §3.
     * Part of the AetherNet Bandwidth Measurement Framework (ABMF, W18-5).
     */
    BandwidthProbe = 53,
    /**
     * BandwidthAck — ACK response to a BandwidthProbe.  Carries the receiver
     * timestamps so the sender can compute RTT without clock synchronisation.
     */
    BandwidthAck = 54,
    /**
     * BandwidthGossip — gossip payload broadcast during handshake to warm-start
     * a new session's BtlBw estimate from a previously measured value.
     * Unique to AetherNet; QUIC/TCP always cold-start.
     */
    BandwidthGossip = 55,
    /**
     * EridAnnounce — directed transport of an already-Signal-encrypted ERID announcement. A node
     * shares its rotating-address routing key with an ESTABLISHED peer by sending the opaque encrypted
     * announcement directly (never broadcast). The plaintext frame is an EridAnnouncementCodec frame;
     * this type only carries the encrypted blob. Wire byte 56 matches the C# PacketType.EridAnnounce.
     */
    EridAnnounce = 56,
    /**
     * CircuitRelayControl — carries one native circuit-relay-v2 hop's frame
     * (reserve/connect/stop/data + responses) as a serialized RelayFrame in the packet
     * body. Wire byte 57 matches the C# PacketType.CircuitRelayControl so a relayed hop
     * is byte-identical across languages; an un-upgraded node drops the unknown type.
     * The relay Transport processes these via its MeshRelayLink; only a DATA frame
     * delivered to the final destination surfaces as tunnelled app data.
     */
    CircuitRelayControl = 57
}
export declare function packetTypeToString(type: PacketType): string;
//# sourceMappingURL=PacketType.d.ts.map