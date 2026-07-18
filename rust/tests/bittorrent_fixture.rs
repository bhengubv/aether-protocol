// SPDX-License-Identifier: MIT
//! Cross-language BitTorrent fixture verifier: the Rust SDK asserts byte-identity against
//! fixtures/bittorrent/vectors.json (Go-oracle + C#-cross-verified corpus).

use aethernet_protocol::bittorrent::bencode::{self, Bencode};
use aethernet_protocol::bittorrent::{dht, krpc, merkle, metainfo, utp, wire};
use serde_json::Value;
use std::collections::BTreeMap;
use std::path::PathBuf;

fn corpus() -> Value {
    let mut dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    for _ in 0..12 {
        let f = dir.join("fixtures").join("bittorrent").join("vectors.json");
        if f.exists() {
            return serde_json::from_slice(&std::fs::read(f).unwrap()).unwrap();
        }
        dir = dir.parent().unwrap().to_path_buf();
    }
    panic!("fixtures/bittorrent/vectors.json not found");
}

fn fill(n: usize, mult: i64, add: i64) -> Vec<u8> {
    (0..n).map(|i| ((i as i64 * mult + add) & 0xff) as u8).collect()
}

#[test]
fn bencode_roundtrip() {
    for hs in corpus()["bencode_roundtrip"].as_array().unwrap() {
        let s = hs.as_str().unwrap();
        let raw = bencode::from_hex(s);
        assert_eq!(bencode::to_hex(&bencode::encode(&bencode::decode(&raw).unwrap())), s);
    }
}

#[test]
fn info_hash() {
    for ic in corpus()["info_hash"].as_array().unwrap() {
        let content = fill(ic["size"].as_u64().unwrap() as usize, ic["mult"].as_i64().unwrap(), ic["add"].as_i64().unwrap());
        let tb = metainfo::build_single_file_torrent(
            ic["name_str"].as_str().unwrap(),
            &content,
            ic["piece_length"].as_u64().unwrap() as usize,
            "",
        );
        assert_eq!(metainfo::parse_torrent(&tb).unwrap().info_hash_v1_hex(), ic["info_hash_hex"].as_str().unwrap());
    }
}

#[test]
fn peer_messages() {
    for pm in corpus()["peer_messages"].as_array().unwrap() {
        let a = pm["a"].as_u64().unwrap() as u32;
        let msg = match pm["kind"].as_str().unwrap() {
            "keepalive" => wire::keep_alive(),
            "choke" => wire::choke(),
            "unchoke" => wire::unchoke(),
            "interested" => wire::interested(),
            "have" => wire::have(a),
            "request" => wire::request(a, pm["b"].as_u64().unwrap() as u32, pm["c"].as_u64().unwrap() as u32),
            "port" => wire::port(a as u16),
            k => panic!("unknown kind {k}"),
        };
        assert_eq!(bencode::to_hex(&msg.to_bytes()), pm["wire_hex"].as_str().unwrap());
    }
}

#[test]
fn utp_packets() {
    for uc in corpus()["utp_packets"].as_array().unwrap() {
        let p = utp::UtpPacket {
            packet_type: uc["type"].as_u64().unwrap() as u8,
            conn_id: uc["conn_id"].as_u64().unwrap() as u16,
            timestamp: uc["timestamp"].as_u64().unwrap() as u32,
            timestamp_diff: uc["timestamp_diff"].as_u64().unwrap() as u32,
            window: uc["window"].as_u64().unwrap() as u32,
            seq: uc["seq"].as_u64().unwrap() as u16,
            ack: uc["ack"].as_u64().unwrap() as u16,
            payload: bencode::from_hex(uc["payload_hex"].as_str().unwrap()),
        };
        assert_eq!(bencode::to_hex(&p.to_bytes()), uc["wire_hex"].as_str().unwrap());
    }
}

#[test]
fn merkle_roots() {
    for mc in corpus()["merkle"].as_array().unwrap() {
        let content = fill(mc["size"].as_u64().unwrap() as usize, mc["mult"].as_i64().unwrap(), mc["add"].as_i64().unwrap());
        assert_eq!(bencode::to_hex(&merkle::merkle_root(&content)), mc["root_hex"].as_str().unwrap());
    }
}

#[test]
fn compact() {
    for cc in corpus()["compact"].as_array().unwrap() {
        let wire_hex = cc["wire_hex"].as_str().unwrap();
        let data = bencode::from_hex(wire_hex);
        match cc["kind"].as_str().unwrap() {
            "node" => assert_eq!(bencode::to_hex(&dht::encode_compact_nodes(&dht::decode_compact_nodes(&data).unwrap())), wire_hex),
            "peers" => {
                let peers: Vec<dht::PeerAddr> = cc["peers"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .map(|p| dht::PeerAddr { ip: p["ip"].as_str().unwrap().to_string(), port: p["port"].as_u64().unwrap() as u16 })
                    .collect();
                assert_eq!(bencode::to_hex(&dht::encode_compact_peers(&peers)), wire_hex);
            }
            _ => {}
        }
    }
}

#[test]
fn krpc_messages() {
    for kc in corpus()["krpc"].as_array().unwrap() {
        let tx = bencode::from_hex(kc["tx_hex"].as_str().unwrap());
        let enc = match kc["kind"].as_str().unwrap() {
            "get_peers" => {
                let mut args: BTreeMap<Vec<u8>, Bencode> = BTreeMap::new();
                args.insert(b"id".to_vec(), Bencode::Bytes(bencode::from_hex(kc["id_hex"].as_str().unwrap())));
                args.insert(b"info_hash".to_vec(), Bencode::Bytes(bencode::from_hex(kc["info_hash_hex"].as_str().unwrap())));
                krpc::encode_query(&tx, "get_peers", args)
            }
            "error" => krpc::encode_error(&tx, kc["error_code"].as_i64().unwrap(), kc["error_message"].as_str().unwrap()),
            _ => panic!("unknown krpc kind"),
        };
        assert_eq!(bencode::to_hex(&enc), kc["wire_hex"].as_str().unwrap());
    }
}
