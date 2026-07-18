// SPDX-License-Identifier: MIT
//! Torrent metainfo, info-hash (SHA-1 of the raw info dict), magnet, and builder.

use super::bencode::{self, Bencode};
use sha1::{Digest, Sha1};
use std::collections::{BTreeMap, HashSet};

pub fn build_single_file_torrent(name: &str, data: &[u8], piece_length: usize, announce: &str) -> Vec<u8> {
    let piece_count = data.len().div_ceil(piece_length);
    let mut pieces = Vec::with_capacity(piece_count * 20);
    for i in 0..piece_count {
        let start = i * piece_length;
        let end = (start + piece_length).min(data.len());
        let mut h = Sha1::new();
        h.update(&data[start..end]);
        pieces.extend_from_slice(&h.finalize());
    }
    let mut info: BTreeMap<Vec<u8>, Bencode> = BTreeMap::new();
    info.insert(b"length".to_vec(), Bencode::Int(data.len() as i64));
    info.insert(b"name".to_vec(), Bencode::Bytes(name.as_bytes().to_vec()));
    info.insert(b"piece length".to_vec(), Bencode::Int(piece_length as i64));
    info.insert(b"pieces".to_vec(), Bencode::Bytes(pieces));
    let mut root: BTreeMap<Vec<u8>, Bencode> = BTreeMap::new();
    if !announce.trim().is_empty() {
        root.insert(b"announce".to_vec(), Bencode::Bytes(announce.as_bytes().to_vec()));
    }
    root.insert(b"info".to_vec(), Bencode::Dict(info));
    bencode::encode(&Bencode::Dict(root))
}

pub struct TorrentMetainfo {
    pub info_hash_v1: [u8; 20],
    pub name: String,
    pub piece_length: i64,
    pub piece_hashes: Vec<Vec<u8>>,
    pub total_length: i64,
    pub announce_urls: Vec<String>,
    pub is_single_file: bool,
}

impl TorrentMetainfo {
    pub fn info_hash_v1_hex(&self) -> String {
        bencode::to_hex(&self.info_hash_v1)
    }
}

pub fn parse_torrent(data: &[u8]) -> Result<TorrentMetainfo, String> {
    let root = bencode::decode(data)?;
    let root_d = root.as_dict().ok_or("metainfo is not a dictionary")?;
    let info = root_d
        .get(b"info".as_slice())
        .and_then(|v| v.as_dict())
        .ok_or("metainfo has no 'info' dictionary")?;

    let mut h = Sha1::new();
    h.update(extract_info_span(data)?);
    let mut info_hash = [0u8; 20];
    info_hash.copy_from_slice(&h.finalize());

    let name = String::from_utf8(
        info.get(b"name".as_slice()).and_then(|v| v.as_bytes()).ok_or("info has no 'name'")?.to_vec(),
    )
    .map_err(|_| "bad name")?;
    let piece_length = info.get(b"piece length".as_slice()).and_then(|v| v.as_int()).ok_or("info has no 'piece length'")?;
    let pieces = info.get(b"pieces".as_slice()).and_then(|v| v.as_bytes()).ok_or("info has no 'pieces'")?;
    if pieces.len() % 20 != 0 {
        return Err("'pieces' length is not a multiple of 20".into());
    }
    let piece_hashes: Vec<Vec<u8>> = pieces.chunks(20).map(|c| c.to_vec()).collect();

    let mut total = 0i64;
    let mut is_single = false;
    if let Some(files) = info.get(b"files".as_slice()).and_then(|v| v.as_list()) {
        for f in files {
            if let Some(len) = f.as_dict().and_then(|fd| fd.get(b"length".as_slice())).and_then(|v| v.as_int()) {
                total += len;
            }
        }
    } else {
        is_single = true;
        total = info.get(b"length".as_slice()).and_then(|v| v.as_int()).ok_or("single-file info has no 'length'")?;
    }

    let mut announce = Vec::new();
    let mut seen = HashSet::new();
    if let Some(a) = root_d.get(b"announce".as_slice()).and_then(|v| v.as_bytes()) {
        let u = String::from_utf8_lossy(a).into_owned();
        if !u.is_empty() && seen.insert(u.clone()) {
            announce.push(u);
        }
    }
    if let Some(al) = root_d.get(b"announce-list".as_slice()).and_then(|v| v.as_list()) {
        for tier in al {
            if let Some(ts) = tier.as_list() {
                for t in ts {
                    if let Some(b) = t.as_bytes() {
                        let u = String::from_utf8_lossy(b).into_owned();
                        if !u.is_empty() && seen.insert(u.clone()) {
                            announce.push(u);
                        }
                    }
                }
            }
        }
    }

    Ok(TorrentMetainfo {
        info_hash_v1: info_hash,
        name,
        piece_length,
        piece_hashes,
        total_length: total,
        announce_urls: announce,
        is_single_file: is_single,
    })
}

fn extract_info_span(data: &[u8]) -> Result<&[u8], String> {
    if data.is_empty() || data[0] != b'd' {
        return Err("metainfo is not a bencoded dictionary".into());
    }
    let mut pos = 1;
    while pos < data.len() && data[pos] != b'e' {
        let (kv, kn) = bencode::decode_n(data, pos)?;
        pos = kn;
        let val_start = pos;
        let (_, vn) = bencode::decode_n(data, pos)?;
        pos = vn;
        if kv.as_bytes() == Some(b"info".as_slice()) {
            return Ok(&data[val_start..pos]);
        }
    }
    Err("metainfo has no 'info' key".into())
}

pub struct MagnetLink {
    pub info_hash: [u8; 20],
    pub display_name: String,
    pub trackers: Vec<String>,
}

pub fn parse_magnet(uri: &str) -> Result<MagnetLink, String> {
    let rest = uri.strip_prefix("magnet:?").ok_or("not a magnet URI")?;
    let mut info_hash: Option<[u8; 20]> = None;
    let mut display_name = String::new();
    let mut trackers = Vec::new();
    for pair in rest.split('&') {
        let (k, v) = pair.split_once('=').unwrap_or((pair, ""));
        let val = url_decode(v);
        match k {
            "xt" => {
                if let Some(h) = val.strip_prefix("urn:btih:") {
                    let b = decode_info_hash(h)?;
                    let mut arr = [0u8; 20];
                    arr.copy_from_slice(&b);
                    info_hash = Some(arr);
                }
            }
            "dn" => display_name = val,
            "tr" => trackers.push(val),
            _ => {}
        }
    }
    Ok(MagnetLink {
        info_hash: info_hash.ok_or("magnet has no xt=urn:btih: topic")?,
        display_name,
        trackers,
    })
}

fn decode_info_hash(s: &str) -> Result<Vec<u8>, String> {
    match s.len() {
        40 => Ok(bencode::from_hex(s)),
        32 => base32_decode(s),
        n => Err(format!("info-hash must be 40 hex or 32 base32 chars, got {n}")),
    }
}

fn base32_decode(s: &str) -> Result<Vec<u8>, String> {
    const ALPHABET: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    let mut bits = 0u32;
    let mut value = 0u32;
    let mut out = Vec::new();
    for ch in s.to_ascii_uppercase().bytes() {
        let idx = ALPHABET.iter().position(|&a| a == ch).ok_or("invalid base32")? as u32;
        value = (value << 5) | idx;
        bits += 5;
        if bits >= 8 {
            bits -= 8;
            out.push(((value >> bits) & 0xff) as u8);
        }
    }
    Ok(out)
}

fn url_decode(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = Vec::new();
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            b'%' if i + 2 < bytes.len() => {
                if let Ok(b) = u8::from_str_radix(&s[i + 1..i + 3], 16) {
                    out.push(b);
                    i += 3;
                } else {
                    out.push(bytes[i]);
                    i += 1;
                }
            }
            b'+' => {
                out.push(b' ');
                i += 1;
            }
            c => {
                out.push(c);
                i += 1;
            }
        }
    }
    String::from_utf8_lossy(&out).into_owned()
}
