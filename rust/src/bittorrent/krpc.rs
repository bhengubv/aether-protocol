// SPDX-License-Identifier: MIT
//! KRPC (BEP-5) DHT messages over bencode.

use super::bencode::{self, Bencode};
use std::collections::BTreeMap;

pub fn encode_query(tx: &[u8], method: &str, args: BTreeMap<Vec<u8>, Bencode>) -> Vec<u8> {
    let mut d = BTreeMap::new();
    d.insert(b"t".to_vec(), Bencode::Bytes(tx.to_vec()));
    d.insert(b"y".to_vec(), Bencode::Bytes(b"q".to_vec()));
    d.insert(b"q".to_vec(), Bencode::Bytes(method.as_bytes().to_vec()));
    d.insert(b"a".to_vec(), Bencode::Dict(args));
    bencode::encode(&Bencode::Dict(d))
}

pub fn encode_response(tx: &[u8], response: BTreeMap<Vec<u8>, Bencode>) -> Vec<u8> {
    let mut d = BTreeMap::new();
    d.insert(b"t".to_vec(), Bencode::Bytes(tx.to_vec()));
    d.insert(b"y".to_vec(), Bencode::Bytes(b"r".to_vec()));
    d.insert(b"r".to_vec(), Bencode::Dict(response));
    bencode::encode(&Bencode::Dict(d))
}

pub fn encode_error(tx: &[u8], code: i64, message: &str) -> Vec<u8> {
    let mut d = BTreeMap::new();
    d.insert(b"t".to_vec(), Bencode::Bytes(tx.to_vec()));
    d.insert(b"y".to_vec(), Bencode::Bytes(b"e".to_vec()));
    d.insert(
        b"e".to_vec(),
        Bencode::List(vec![Bencode::Int(code), Bencode::Bytes(message.as_bytes().to_vec())]),
    );
    bencode::encode(&Bencode::Dict(d))
}
