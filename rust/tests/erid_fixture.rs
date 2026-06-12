// SPDX-License-Identifier: MIT

//! Cross-language ERID parity: the Rust port must reproduce the C# reference vectors
//! (fixtures/erid/vectors.json) byte-for-byte.

use aethernet_protocol::identity::{
    derive, derive_for_epoch, derive_routing_key, erid_announcement_codec, EridDirectory,
};

fn load_vectors() -> serde_json::Value {
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../fixtures/erid/vectors.json");
    let raw = std::fs::read_to_string(path).expect("read fixtures/erid/vectors.json");
    serde_json::from_str(&raw).expect("parse vectors.json")
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

#[test]
fn erid_byte_parity_with_csharp_fixture() {
    let v = load_vectors();
    let secret = v["secret_ascii"].as_str().unwrap();
    let erid_length = v["erid_length"].as_u64().unwrap() as usize;
    let epoch_seconds = v["epoch_seconds"].as_i64().unwrap();

    let rk = derive_routing_key(secret.as_bytes()).unwrap();
    assert_eq!(hex(&rk), v["routing_key_hex"].as_str().unwrap(), "routingKey");

    for e in v["erids_by_epoch"].as_array().unwrap() {
        let epoch = e["epoch"].as_i64().unwrap();
        let want = e["erid"].as_str().unwrap();
        assert_eq!(
            derive_for_epoch(&rk, epoch, erid_length).unwrap(),
            want,
            "epoch {epoch}"
        );
    }

    for e in v["derive_by_unixseconds"].as_array().unwrap() {
        let unix = e["unix"].as_i64().unwrap();
        let want = e["erid"].as_str().unwrap();
        assert_eq!(
            derive(&rk, unix, epoch_seconds, erid_length).unwrap(),
            want,
            "unix {unix}"
        );
    }

    let enc =
        erid_announcement_codec::encode(&rk, epoch_seconds as i32, erid_length as i32).unwrap();
    assert_eq!(
        hex(&enc),
        v["announcement_encode_hex"].as_str().unwrap(),
        "announcement frame"
    );

    // Round-trip the frame back through the decoder.
    let dec = erid_announcement_codec::try_decode(&enc).expect("decode own frame");
    assert_eq!(hex(&dec.routing_key), v["routing_key_hex"].as_str().unwrap());
    assert_eq!(dec.epoch_seconds, epoch_seconds as i32);
    assert_eq!(dec.erid_length, erid_length as i32);
}

#[test]
fn erid_directory_resolve_and_outsider() {
    let a_key = derive_routing_key(b"identity-A").unwrap();
    let b_key = derive_routing_key(b"identity-B").unwrap();
    let mut alice = EridDirectory::new(&a_key);
    let mut bob = EridDirectory::new(&b_key);
    alice.remember_peer("bob", &b_key);
    bob.remember_peer("alice", &a_key);
    let t = 1_700_000_000;

    // An established peer resolves the other's rotating address, both directions.
    assert_eq!(
        alice.erid_for_peer("bob", t).unwrap(),
        Some(bob.my_erid(t).unwrap())
    );
    assert_eq!(
        bob.resolve_peer(&alice.my_erid(t).unwrap(), t).unwrap(),
        Some("alice".to_string())
    );

    // An outsider holding no routing key cannot.
    let outsider = EridDirectory::new(&derive_routing_key(b"identity-X").unwrap());
    assert_eq!(
        outsider.resolve_peer(&alice.my_erid(t).unwrap(), t).unwrap(),
        None
    );
}
