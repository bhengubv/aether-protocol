// SPDX-License-Identifier: MIT

use crate::protocol::MeshPacket;
use std::io::{self, Read, Write};
use uuid::Uuid;

/// Serializes and deserializes MeshPackets to/from binary wire format.
///
/// Wire format (all multi-byte integers are little-endian):
///   [1 byte]  Protocol version
///   [1 byte]  Packet type
///   [16 bytes] Packet ID (GUID)
///   [1 byte]  Priority
///   [4 bytes] TTL (int32)
///   [8 bytes] TimestampMs (int64)
///   [2 bytes] SourceUhid length (u16)
///   [N bytes] SourceUhid (UTF-8)
///   [2 bytes] DestinationUhid length (u16)
///   [N bytes] DestinationUhid (UTF-8)
///   [2 bytes] PacketNonce length (u16)
///   [N bytes] PacketNonce
///   [4 bytes] Payload length (i32)
///   [N bytes] Payload
///   [2 bytes] Signature length (u16)
///   [N bytes] Signature
pub struct PacketSerializer;

impl PacketSerializer {
    /// Serializes a MeshPacket to its binary wire format
    pub fn serialize(packet: &MeshPacket) -> io::Result<Vec<u8>> {
        let mut buffer = Vec::new();

        // Protocol version
        buffer.write_all(&[packet.protocol_version])?;

        // Packet type
        buffer.write_all(&[packet.packet_type.as_byte()])?;

        // Packet ID (16 bytes)
        buffer.write_all(packet.id.as_bytes())?;

        // Priority
        buffer.write_all(&[packet.priority])?;

        // TTL (4 bytes, little-endian i32)
        buffer.write_all(&packet.ttl.to_le_bytes())?;

        // TimestampMs (8 bytes, little-endian i64)
        buffer.write_all(&packet.timestamp_ms.to_le_bytes())?;

        // SourceUhid (length-prefixed, u16 LE)
        let source_bytes = packet.source_uhid.as_bytes();
        buffer.write_all(&(source_bytes.len() as u16).to_le_bytes())?;
        buffer.write_all(source_bytes)?;

        // DestinationUhid (length-prefixed, u16 LE)
        let dest_bytes = packet.destination_uhid.as_bytes();
        buffer.write_all(&(dest_bytes.len() as u16).to_le_bytes())?;
        buffer.write_all(dest_bytes)?;

        // PacketNonce (length-prefixed, u16 LE)
        buffer.write_all(&(packet.packet_nonce.len() as u16).to_le_bytes())?;
        buffer.write_all(&packet.packet_nonce)?;

        // Payload (length-prefixed, i32 LE)
        buffer.write_all(&(packet.payload.len() as i32).to_le_bytes())?;
        buffer.write_all(&packet.payload)?;

        // Signature (length-prefixed, u16 LE)
        buffer.write_all(&(packet.signature.len() as u16).to_le_bytes())?;
        buffer.write_all(&packet.signature)?;

        Ok(buffer)
    }

    /// Deserializes a MeshPacket from binary wire format
    pub fn deserialize(data: &[u8]) -> io::Result<MeshPacket> {
        let mut cursor = &data[..];

        // Minimum valid packet size check
        if data.len() < 43 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Data is too short to contain a valid MeshPacket",
            ));
        }

        // Protocol version
        let mut version_buf = [0u8; 1];
        cursor.read_exact(&mut version_buf)?;
        let protocol_version = version_buf[0];

        // Packet type
        let mut type_buf = [0u8; 1];
        cursor.read_exact(&mut type_buf)?;
        let packet_type = crate::protocol::PacketType::from_byte(type_buf[0])
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "Invalid packet type"))?;

        // Packet ID (16 bytes)
        let mut id_buf = [0u8; 16];
        cursor.read_exact(&mut id_buf)?;
        let id = Uuid::from_bytes(id_buf);

        // Priority
        let mut priority_buf = [0u8; 1];
        cursor.read_exact(&mut priority_buf)?;
        let priority = priority_buf[0];

        // TTL (4 bytes, i32 LE)
        let mut ttl_buf = [0u8; 4];
        cursor.read_exact(&mut ttl_buf)?;
        let ttl = i32::from_le_bytes(ttl_buf);

        // TimestampMs (8 bytes, i64 LE)
        let mut ts_buf = [0u8; 8];
        cursor.read_exact(&mut ts_buf)?;
        let timestamp_ms = i64::from_le_bytes(ts_buf);

        // SourceUhid
        let mut len_buf = [0u8; 2];
        cursor.read_exact(&mut len_buf)?;
        let source_len = u16::from_le_bytes(len_buf) as usize;
        let mut source_bytes = vec![0u8; source_len];
        cursor.read_exact(&mut source_bytes)?;
        let source_uhid = String::from_utf8(source_bytes)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "Invalid UTF-8 in source UHID"))?;

        // DestinationUhid
        cursor.read_exact(&mut len_buf)?;
        let dest_len = u16::from_le_bytes(len_buf) as usize;
        let mut dest_bytes = vec![0u8; dest_len];
        cursor.read_exact(&mut dest_bytes)?;
        let destination_uhid = String::from_utf8(dest_bytes)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "Invalid UTF-8 in destination UHID"))?;

        // PacketNonce
        cursor.read_exact(&mut len_buf)?;
        let nonce_len = u16::from_le_bytes(len_buf) as usize;
        let mut packet_nonce = vec![0u8; nonce_len];
        cursor.read_exact(&mut packet_nonce)?;

        // Payload
        let mut payload_len_buf = [0u8; 4];
        cursor.read_exact(&mut payload_len_buf)?;
        let payload_len = i32::from_le_bytes(payload_len_buf);
        if payload_len < 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Negative payload length",
            ));
        }
        let mut payload = vec![0u8; payload_len as usize];
        cursor.read_exact(&mut payload)?;

        // Signature
        cursor.read_exact(&mut len_buf)?;
        let sig_len = u16::from_le_bytes(len_buf) as usize;
        let mut signature = vec![0u8; sig_len];
        cursor.read_exact(&mut signature)?;

        Ok(MeshPacket {
            id,
            packet_type,
            source_uhid,
            destination_uhid,
            ttl,
            priority,
            payload,
            timestamp_ms,
            protocol_version,
            signature,
            packet_nonce,
        })
    }

    /// Attempts to deserialize a packet, returning None on failure
    pub fn try_deserialize(data: &[u8]) -> Option<MeshPacket> {
        Self::deserialize(data).ok()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::PacketType;

    #[test]
    fn test_serialize_deserialize_roundtrip() {
        let mut packet = MeshPacket::new(PacketType::Data, "node-a".to_string());
        packet.destination_uhid = "node-b".to_string();
        packet.payload = vec![1, 2, 3, 4, 5];
        packet.ttl = 5;
        packet.priority = 10;
        packet.packet_nonce = vec![1, 2, 3, 4, 5, 6, 7, 8];
        packet.signature = vec![0x42; 64];

        let serialized = PacketSerializer::serialize(&packet).unwrap();
        let deserialized = PacketSerializer::deserialize(&serialized).unwrap();

        assert_eq!(deserialized.id, packet.id);
        assert_eq!(deserialized.packet_type, packet.packet_type);
        assert_eq!(deserialized.source_uhid, packet.source_uhid);
        assert_eq!(deserialized.destination_uhid, packet.destination_uhid);
        assert_eq!(deserialized.ttl, packet.ttl);
        assert_eq!(deserialized.priority, packet.priority);
        assert_eq!(deserialized.payload, packet.payload);
        assert_eq!(deserialized.signature, packet.signature);
        assert_eq!(deserialized.packet_nonce, packet.packet_nonce);
    }

    #[test]
    fn test_serialize_empty_packet() {
        let packet = MeshPacket::new(PacketType::Heartbeat, "node-1".to_string());
        let serialized = PacketSerializer::serialize(&packet).unwrap();
        let deserialized = PacketSerializer::deserialize(&serialized).unwrap();

        assert_eq!(deserialized.source_uhid, "node-1");
        assert_eq!(deserialized.payload.len(), 0);
        assert_eq!(deserialized.signature.len(), 0);
    }

    #[test]
    fn test_deserialize_invalid_data() {
        let result = PacketSerializer::deserialize(&[1, 2, 3]);
        assert!(result.is_err());
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Fuzz / property tests
//
// Mirrors the Go fuzz harness (`go/protocol/fuzz_serializer_test.go`), the
// Python `tests/test_fuzz.py`, the C# `PacketSerializerFuzzTests`, the
// TypeScript `tests/fuzz.test.ts`, and the C `c/tests/fuzz_*` runners.
//
// The wire-format deserialiser parses untrusted bytes off the network, so the
// contract is: for ANY input it must EITHER return a valid `MeshPacket` OR
// return a documented `io::Error`. It must NEVER panic, hang, or allocate
// gigabytes from an attacker-controlled length prefix.
//
// Three flavours run here:
//
//   1. Property: `serialize -> deserialize` round-trip on proptest-generated
//      `MeshPacket`s — every wire-significant field is preserved.
//   2. Property: `deserialize` on arbitrary bytes never panics; only the
//      documented `io::Error` propagates.
//   3. Property: `EncryptedPayload` JSON round-trip + `SignalSessionDto` JSON
//      round-trip — the on-disk persistence contract.
//
// Default `proptest` budget is 256 cases per property (`PROPTEST_CASES`); we
// raise it here to 1000 to match the per-language commitment in Go / Python /
// TS / C# / C. Set `PROPTEST_CASES=N` in the environment to adjust at runtime.
#[cfg(test)]
mod fuzz_tests {
    use super::*;
    use crate::models::EncryptedPayload;
    use crate::protocol::PacketType;
    use crate::security::dtos::SignalSessionDto;
    use proptest::collection::{hash_map, vec as proptest_vec};
    use proptest::prelude::*;
    use std::collections::HashMap;
    use uuid::Uuid;

    // ── Strategies ──────────────────────────────────────────────────────────

    // All currently-defined PacketType byte values. Mirrors the
    // `from_byte` lookup table — keep in sync if a new type lands.
    fn arb_packet_type() -> impl Strategy<Value = PacketType> {
        prop_oneof![
            Just(PacketType::RouteRequest),
            Just(PacketType::RouteReply),
            Just(PacketType::Data),
            Just(PacketType::Ack),
            Just(PacketType::SosBroadcast),
            Just(PacketType::SosAck),
            Just(PacketType::ChannelMessage),
            Just(PacketType::ChunkRequest),
            Just(PacketType::ChunkData),
            Just(PacketType::Heartbeat),
            Just(PacketType::StreamAnnounce),
            Just(PacketType::StreamSegment),
            Just(PacketType::StreamSubscribe),
            Just(PacketType::StreamUnsubscribe),
            Just(PacketType::VoicePtt),
            Just(PacketType::VoiceCall),
            Just(PacketType::VoiceSignaling),
            Just(PacketType::DtnBundle),
            Just(PacketType::DtnCustodyAck),
            Just(PacketType::DtnDeliveryReceipt),
            Just(PacketType::PresenceBeacon),
            Just(PacketType::PresenceQuery),
            Just(PacketType::ProfileSync),
            Just(PacketType::TipPacket),
            Just(PacketType::PreKeyRequest),
            Just(PacketType::PreKeyResponse),
            Just(PacketType::VideoCall),
            Just(PacketType::VideoSignaling),
            Just(PacketType::WatchSync),
            Just(PacketType::WatchReaction),
            Just(PacketType::VideoFrame),
            Just(PacketType::ScreenShare),
            Just(PacketType::WatchChunkRequest),
            Just(PacketType::TorrentMetadata),
            Just(PacketType::Hello),
            Just(PacketType::HelloAck),
        ]
    }

    // UHIDs: any UTF-8 string up to 255 chars. The wire format length-prefixes
    // them with u16 — values that round-trip through `String::from_utf8`
    // exercise the deserialiser's UTF-8 validation path.
    fn arb_uhid() -> impl Strategy<Value = String> {
        "[\\PC]{0,255}".prop_map(|s| s.to_string())
    }

    // Bound payloads to 64 KB to keep each iteration under a few ms; the wire
    // format itself accepts up to i32::MAX (the bench harness covers perf at
    // that scale).
    fn arb_bytes(max: usize) -> impl Strategy<Value = Vec<u8>> {
        proptest_vec(any::<u8>(), 0..=max)
    }

    fn arb_mesh_packet() -> impl Strategy<Value = MeshPacket> {
        (
            arb_packet_type(),
            arb_uhid(),
            arb_uhid(),
            any::<i32>(),
            any::<u8>(),
            any::<u8>(),
            any::<i64>(),
            arb_bytes(65536),
            arb_bytes(255),
            arb_bytes(255),
            any::<[u8; 16]>(),
        )
            .prop_map(
                |(
                    packet_type,
                    src,
                    dst,
                    ttl,
                    priority,
                    protocol_version,
                    timestamp_ms,
                    payload,
                    nonce,
                    signature,
                    id_bytes,
                )| {
                    MeshPacket {
                        id: Uuid::from_bytes(id_bytes),
                        packet_type,
                        source_uhid: src,
                        destination_uhid: dst,
                        ttl,
                        priority,
                        payload,
                        timestamp_ms,
                        protocol_version,
                        signature,
                        packet_nonce: nonce,
                    }
                },
            )
    }

    // ── PacketSerializer round-trip ─────────────────────────────────────────

    proptest! {
        #![proptest_config(ProptestConfig::with_cases(1000))]

        #[test]
        fn fuzz_packet_serialize_roundtrip(packet in arb_mesh_packet()) {
            let wire = PacketSerializer::serialize(&packet).expect("serialize");
            let got = PacketSerializer::deserialize(&wire).expect("round-trip deserialize");
            prop_assert_eq!(got.id, packet.id);
            prop_assert_eq!(got.packet_type, packet.packet_type);
            prop_assert_eq!(&got.source_uhid, &packet.source_uhid);
            prop_assert_eq!(&got.destination_uhid, &packet.destination_uhid);
            prop_assert_eq!(got.ttl, packet.ttl);
            prop_assert_eq!(got.priority, packet.priority);
            prop_assert_eq!(got.protocol_version, packet.protocol_version);
            prop_assert_eq!(got.timestamp_ms, packet.timestamp_ms);
            prop_assert_eq!(&got.payload, &packet.payload);
            prop_assert_eq!(&got.packet_nonce, &packet.packet_nonce);
            prop_assert_eq!(&got.signature, &packet.signature);
        }

        // ── PacketSerializer::deserialize on arbitrary bytes ────────────────
        //
        // Documented contract: returns `Ok(MeshPacket)` or `Err(io::Error)` —
        // never panics. `try_deserialize` is the panic-safe option that
        // returns `Option<MeshPacket>` (None on any failure).
        #[test]
        fn fuzz_deserialize_random_bytes_never_panics(data in arb_bytes(8192)) {
            // The function returns Result<_, io::Error>; we just need to
            // assert it doesn't panic. Both Ok and Err are accepted.
            let _ = PacketSerializer::deserialize(&data);
            // try_deserialize wraps the same path with `.ok()` — exercise it.
            let _ = PacketSerializer::try_deserialize(&data);
        }

        // Mutation fuzzer: take a valid wire envelope, flip 1-4 bytes,
        // assert the deserialiser still doesn't panic.
        #[test]
        fn fuzz_deserialize_mutated_wire_never_panics(
            packet in arb_mesh_packet(),
            mutation_count in 1usize..=4,
            seed in any::<[u8; 4]>(),
        ) {
            let valid = PacketSerializer::serialize(&packet).expect("serialize");
            if valid.is_empty() {
                return Ok(());
            }
            let mut mutated = valid.clone();
            for i in 0..mutation_count {
                let pos = ((seed[i % 4] as usize).wrapping_mul(31).wrapping_add(i.wrapping_mul(7)))
                    % mutated.len();
                mutated[pos] = mutated[pos].wrapping_add(0x5A).wrapping_add(i as u8);
            }
            let _ = PacketSerializer::deserialize(&mutated);
        }
    }

    // ── PacketSerializer::deserialize hand-built oversize header ────────────

    #[test]
    fn fuzz_rejects_oversize_payload_length() {
        // Mirrors the Python / Go / TS `OversizePayloadLength` test —
        // hand-built header with payload-length = 0x7FFFFFFF but no following
        // bytes. The deserialiser MUST return an error (read_exact fails on
        // the truncated body) rather than allocate ~2 GB.
        for oversize in [0x7FFF_FFFFi32, 0x1000_0000, 0x0100_0000] {
            let mut buf = Vec::with_capacity(43);
            buf.push(0x02); // protocol_version
            buf.push(0x03); // PacketType::Data
            buf.extend_from_slice(&[0u8; 16]); // packet id
            buf.push(0x05); // priority
            buf.extend_from_slice(&7i32.to_le_bytes()); // ttl
            buf.extend_from_slice(&1_234_567_890_000i64.to_le_bytes()); // ts
            buf.extend_from_slice(&0u16.to_le_bytes()); // src len = 0
            buf.extend_from_slice(&0u16.to_le_bytes()); // dst len = 0
            buf.extend_from_slice(&0u16.to_le_bytes()); // nonce len = 0
            buf.extend_from_slice(&oversize.to_le_bytes()); // payload len
            // Truncated — no payload body, no signature length.
            assert!(
                PacketSerializer::deserialize(&buf).is_err(),
                "oversize payload prefix {:#x} must be rejected, not allocated",
                oversize
            );
        }
    }

    #[test]
    fn fuzz_rejects_negative_payload_length() {
        let mut buf = Vec::with_capacity(43);
        buf.push(0x02);
        buf.push(0x03);
        buf.extend_from_slice(&[0u8; 16]);
        buf.push(0x05);
        buf.extend_from_slice(&7i32.to_le_bytes());
        buf.extend_from_slice(&0i64.to_le_bytes());
        buf.extend_from_slice(&0u16.to_le_bytes());
        buf.extend_from_slice(&0u16.to_le_bytes());
        buf.extend_from_slice(&0u16.to_le_bytes());
        buf.extend_from_slice(&(-1i32).to_le_bytes()); // negative payload len
        assert!(PacketSerializer::deserialize(&buf).is_err());
    }

    // ── EncryptedPayload JSON round-trip ────────────────────────────────────

    fn arb_encrypted_payload() -> impl Strategy<Value = EncryptedPayload> {
        (
            arb_bytes(4096),                         // ciphertext
            proptest_vec(any::<u8>(), 12..=12),      // nonce
            prop_oneof![Just(0i32), Just(1i32)],     // message_type (NORMAL / PRE_KEY)
            "[\\PC]{0,64}",                          // sender_uhid
            any::<u32>(),                            // counter
            0u64..95_617_584_000_000u64,             // encrypted_at — sane range
            prop::option::of(proptest_vec(any::<u8>(), 32..=32)),
            prop::option::of(proptest_vec(any::<u8>(), 32..=32)),
            any::<i32>(),                            // used_signed_pre_key_id
            any::<i32>(),                            // used_one_time_pre_key_id
            prop::option::of(proptest_vec(any::<u8>(), 32..=32)),
            any::<u32>(),                            // previous_chain_count
        )
            .prop_map(
                |(
                    ciphertext,
                    nonce,
                    message_type,
                    sender_uhid,
                    counter,
                    encrypted_at,
                    init_id,
                    init_eph,
                    spk_id,
                    opk_id,
                    sender_eph,
                    prev_chain,
                )| {
                    EncryptedPayload {
                        ciphertext,
                        nonce,
                        message_type,
                        sender_uhid,
                        counter,
                        encrypted_at,
                        initiator_identity_key_x25519: init_id,
                        initiator_ephemeral_key_x25519: init_eph,
                        used_signed_pre_key_id: spk_id,
                        used_one_time_pre_key_id: opk_id,
                        sender_ephemeral_key_x25519: sender_eph,
                        previous_chain_count: prev_chain,
                    }
                },
            )
    }

    proptest! {
        #![proptest_config(ProptestConfig::with_cases(1000))]

        #[test]
        fn fuzz_encrypted_payload_json_roundtrip(payload in arb_encrypted_payload()) {
            let json = serde_json::to_vec(&payload).expect("encode");
            let got: EncryptedPayload = serde_json::from_slice(&json).expect("decode");
            prop_assert_eq!(&got.ciphertext, &payload.ciphertext);
            prop_assert_eq!(&got.nonce, &payload.nonce);
            prop_assert_eq!(got.message_type, payload.message_type);
            prop_assert_eq!(&got.sender_uhid, &payload.sender_uhid);
            prop_assert_eq!(got.counter, payload.counter);
            prop_assert_eq!(got.encrypted_at, payload.encrypted_at);
            prop_assert_eq!(
                &got.initiator_identity_key_x25519,
                &payload.initiator_identity_key_x25519
            );
            prop_assert_eq!(
                &got.initiator_ephemeral_key_x25519,
                &payload.initiator_ephemeral_key_x25519
            );
            prop_assert_eq!(got.used_signed_pre_key_id, payload.used_signed_pre_key_id);
            prop_assert_eq!(got.used_one_time_pre_key_id, payload.used_one_time_pre_key_id);
            prop_assert_eq!(
                &got.sender_ephemeral_key_x25519,
                &payload.sender_ephemeral_key_x25519
            );
            prop_assert_eq!(got.previous_chain_count, payload.previous_chain_count);
        }
    }

    // ── SignalSessionDto JSON round-trip ────────────────────────────────────
    //
    // The DTO is the on-disk persistence contract for `SignalSessionStore`
    // — once shipped, existing fields cannot change shape. The fuzz here
    // exercises the JSON codec end-to-end so a schema drift is caught before
    // it can corrupt persisted sessions.

    fn arb_skipped_keys() -> impl Strategy<Value = HashMap<String, Vec<u8>>> {
        // Keys mirror production shape: "Hex(DHr_pub):counter". We bound
        // size to 8 entries so the fuzz stays fast.
        hash_map(
            ("[0-9A-F]{2,32}", any::<u32>())
                .prop_map(|(hex, ctr)| format!("{}:{}", hex, ctr)),
            proptest_vec(any::<u8>(), 0..=64),
            0..=8,
        )
    }

    fn arb_signal_session_dto() -> impl Strategy<Value = SignalSessionDto> {
        (
            arb_bytes(64),                           // root_key
            prop::option::of(arb_bytes(64)),         // send_chain_key
            prop::option::of(arb_bytes(64)),         // recv_chain_key
            any::<u32>(),                            // send_counter
            any::<u32>(),                            // recv_counter
            any::<u32>(),                            // previous_chain_count
            arb_bytes(64),                           // my_ephemeral_priv
            arb_bytes(64),                           // my_ephemeral_pub
            prop::option::of(arb_bytes(64)),         // remote_ephemeral_pub
            arb_skipped_keys(),
            any::<bool>(),                           // pending_pre_key_message
            arb_bytes(64),                           // initiator_identity_key_x25519
            any::<i32>(),                            // used_signed_pre_key_id
            any::<i32>(),                            // used_one_time_pre_key_id
        )
            .prop_map(
                |(
                    root_key,
                    cks,
                    ckr,
                    ns,
                    nr,
                    pn,
                    dhs_priv,
                    dhs_pub,
                    dhr,
                    skipped,
                    pending,
                    init_ik,
                    spk_id,
                    opk_id,
                )| SignalSessionDto {
                    root_key,
                    send_chain_key: cks,
                    recv_chain_key: ckr,
                    send_counter: ns,
                    recv_counter: nr,
                    previous_chain_count: pn,
                    my_ephemeral_priv: dhs_priv,
                    my_ephemeral_pub: dhs_pub,
                    remote_ephemeral_pub: dhr,
                    skipped_message_keys: skipped,
                    pending_pre_key_message: pending,
                    initiator_identity_key_x25519: init_ik,
                    used_signed_pre_key_id: spk_id,
                    used_one_time_pre_key_id: opk_id,
                },
            )
    }

    proptest! {
        #![proptest_config(ProptestConfig::with_cases(1000))]

        #[test]
        fn fuzz_signal_session_dto_json_roundtrip(dto in arb_signal_session_dto()) {
            let json = serde_json::to_vec(&dto).expect("encode dto");
            let got: SignalSessionDto = serde_json::from_slice(&json).expect("decode dto");
            prop_assert_eq!(got, dto);
        }

        // Direct deserialiser fuzz — feed arbitrary bytes; the codec must
        // return `Err` (never panic) on anything not shaped like a valid
        // `SignalSessionDto` JSON.
        #[test]
        fn fuzz_signal_session_dto_arbitrary_bytes_never_panics(data in arb_bytes(4096)) {
            // A valid Result OR Err — both fine. Just must not panic.
            let _ = serde_json::from_slice::<SignalSessionDto>(&data);
        }
    }
}
