// SPDX-License-Identifier: MIT

//! Cross-language rendezvous parity: the Rust port must reproduce the C# reference vectors
//! (fixtures/meeting/meeting_basic.json) byte-for-byte.

use aethernet_protocol::meeting::{Meeting, LENGTH};

fn load_fixture() -> serde_json::Value {
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../fixtures/meeting/meeting_basic.json");
    let raw = std::fs::read_to_string(path).expect("read fixtures/meeting/meeting_basic.json");
    serde_json::from_str(&raw).expect("parse meeting_basic.json")
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

#[test]
fn meeting_byte_parity_with_csharp_fixture() {
    let f = load_fixture();
    assert_eq!(f["info"].as_str().unwrap(), "aether-meeting-v1");
    assert_eq!(f["length"].as_u64().unwrap() as usize, LENGTH);
    let alphabet = f["alphabet"].as_str().unwrap();

    for c in f["cases"].as_array().unwrap() {
        let name = c["name"].as_str().unwrap();
        let m = Meeting::with(c["my_tag"].as_str().unwrap(), c["their_tag"].as_str().unwrap())
            .unwrap_or_else(|| panic!("{name}: expected a meeting"));

        assert_eq!(m.rendezvous, c["rendezvous"].as_str().unwrap(), "{name} rendezvous");
        assert_eq!(m.i_start, c["i_start"].as_bool().unwrap(), "{name} i_start");
        assert_eq!(m.uuid().to_string(), c["uuid_string"].as_str().unwrap(), "{name} uuid_string");
        // to_bytes_le() is .NET's Guid.ToByteArray() layout — compare to the recorded hex.
        assert_eq!(hex(&m.uuid().to_bytes_le()), c["uuid"].as_str().unwrap(), "{name} uuid");

        for (bits, want) in c["address"].as_object().unwrap() {
            let bits: u32 = bits.parse().unwrap();
            let want = want.as_u64().unwrap() as u32;
            assert_eq!(m.address(bits), want, "{name} addr@{bits}");
        }

        assert_eq!(m.rendezvous.len(), LENGTH, "{name} length");
        assert!(m.rendezvous.chars().all(|ch| alphabet.contains(ch)), "{name} alphabet");
    }

    // The same pair, either way round, meets at the same place with opposite host roles.
    let a = Meeting::with("BH8CZ-B09CA", "DY5CF-84G9T").unwrap();
    let b = Meeting::with("DY5CF-84G9T", "BH8CZ-B09CA").unwrap();
    assert_eq!(a.rendezvous, b.rendezvous);
    assert_eq!(a.uuid(), b.uuid());
    assert_ne!(a.i_start, b.i_start);

    // Every rejected input yields no meeting.
    for r in f["rejects"].as_array().unwrap() {
        let name = r["name"].as_str().unwrap();
        let my = r["my_tag"].as_str().unwrap_or("");
        let their = r["their_tag"].as_str().unwrap_or("");
        assert!(Meeting::with(my, their).is_none(), "{name}: expected no meeting");
    }
}
