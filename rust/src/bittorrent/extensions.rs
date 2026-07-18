// SPDX-License-Identifier: MIT
//! BEP-10 extension protocol + BEP-9 ut_metadata + BEP-11 ut_pex.

use super::bencode::{self, Bencode};
use super::dht::{self, PeerAddr};
use sha1::{Digest, Sha1};
use std::collections::BTreeMap;

pub const EXTENDED_MESSAGE_ID: u8 = 20;
pub const EXTENSION_HANDSHAKE_ID: u8 = 0;
pub const METADATA_REQUEST: i64 = 0;
pub const METADATA_DATA: i64 = 1;
pub const METADATA_REJECT: i64 = 2;
pub const METADATA_PIECE_SIZE: usize = 16384;

pub fn wrap_extended(sub_id: u8, body: &[u8]) -> Vec<u8> {
    let mut out = vec![sub_id];
    out.extend_from_slice(body);
    out
}

pub fn split_extended(payload: &[u8]) -> Result<(u8, Vec<u8>), String> {
    if payload.is_empty() {
        return Err("empty extended payload".into());
    }
    Ok((payload[0], payload[1..].to_vec()))
}

pub fn build_extension_handshake(supported: &[(&str, i64)], metadata_size: i64) -> Vec<u8> {
    let mut m = BTreeMap::new();
    for (name, id) in supported {
        m.insert(name.as_bytes().to_vec(), Bencode::Int(*id));
    }
    let mut d = BTreeMap::new();
    d.insert(b"m".to_vec(), Bencode::Dict(m));
    if metadata_size > 0 {
        d.insert(b"metadata_size".to_vec(), Bencode::Int(metadata_size));
    }
    wrap_extended(EXTENSION_HANDSHAKE_ID, &bencode::encode(&Bencode::Dict(d)))
}

pub struct ExtensionHandshake {
    pub supported: BTreeMap<String, i64>,
    pub metadata_size: i64,
}

pub fn parse_extension_handshake(body: &[u8]) -> Result<ExtensionHandshake, String> {
    let d = bencode::decode(body)?;
    let dd = d.as_dict().ok_or("not a dict")?;
    let mut supported = BTreeMap::new();
    if let Some(m) = dd.get(b"m".as_slice()).and_then(|v| v.as_dict()) {
        for (k, v) in m {
            if let Some(id) = v.as_int() {
                supported.insert(String::from_utf8_lossy(k).into_owned(), id);
            }
        }
    }
    let metadata_size = dd.get(b"metadata_size".as_slice()).and_then(|v| v.as_int()).unwrap_or(0);
    Ok(ExtensionHandshake { supported, metadata_size })
}

pub fn build_metadata_request(piece: i64) -> Vec<u8> {
    let mut d = BTreeMap::new();
    d.insert(b"msg_type".to_vec(), Bencode::Int(METADATA_REQUEST));
    d.insert(b"piece".to_vec(), Bencode::Int(piece));
    bencode::encode(&Bencode::Dict(d))
}

pub fn build_metadata_data(piece: i64, total_size: i64, data: &[u8]) -> Vec<u8> {
    let mut d = BTreeMap::new();
    d.insert(b"msg_type".to_vec(), Bencode::Int(METADATA_DATA));
    d.insert(b"piece".to_vec(), Bencode::Int(piece));
    d.insert(b"total_size".to_vec(), Bencode::Int(total_size));
    let mut out = bencode::encode(&Bencode::Dict(d));
    out.extend_from_slice(data);
    out
}

pub fn build_metadata_reject(piece: i64) -> Vec<u8> {
    let mut d = BTreeMap::new();
    d.insert(b"msg_type".to_vec(), Bencode::Int(METADATA_REJECT));
    d.insert(b"piece".to_vec(), Bencode::Int(piece));
    bencode::encode(&Bencode::Dict(d))
}

pub struct MetadataMessage {
    pub msg_type: i64,
    pub piece: i64,
    pub total_size: i64,
    pub data: Vec<u8>,
}

pub fn parse_metadata(body: &[u8]) -> Result<MetadataMessage, String> {
    let (v, n) = bencode::decode_n(body, 0)?;
    let d = v.as_dict().ok_or("not a dict")?;
    Ok(MetadataMessage {
        msg_type: d.get(b"msg_type".as_slice()).and_then(|x| x.as_int()).unwrap_or(0),
        piece: d.get(b"piece".as_slice()).and_then(|x| x.as_int()).unwrap_or(0),
        total_size: d.get(b"total_size".as_slice()).and_then(|x| x.as_int()).unwrap_or(0),
        data: body[n..].to_vec(),
    })
}

pub fn metadata_verify(pieces: &[Vec<u8>], total_size: usize, info_hash: &[u8; 20]) -> Option<Vec<u8>> {
    let out: Vec<u8> = pieces.concat();
    if out.len() != total_size {
        return None;
    }
    let mut h = Sha1::new();
    h.update(&out);
    if h.finalize().as_slice() == info_hash {
        Some(out)
    } else {
        None
    }
}

pub fn build_pex_added(added: &[PeerAddr]) -> Vec<u8> {
    let mut d = BTreeMap::new();
    d.insert(b"added".to_vec(), Bencode::Bytes(dht::encode_compact_peers(added)));
    bencode::encode(&Bencode::Dict(d))
}

pub fn parse_pex_added(body: &[u8]) -> Result<Vec<PeerAddr>, String> {
    let d = bencode::decode(body)?;
    if let Some(added) = d.as_dict().and_then(|dd| dd.get(b"added".as_slice())).and_then(|v| v.as_bytes()) {
        return dht::decode_compact_peers(added);
    }
    Ok(vec![])
}
