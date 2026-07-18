// SPDX-License-Identifier: MIT
//! BEP-3 peer-wire: handshake, messages (exact big-endian framing), MSB-first bitfield.

pub const PROTOCOL_STRING: &[u8] = b"BitTorrent protocol";

pub const CHOKE: u8 = 0;
pub const UNCHOKE: u8 = 1;
pub const INTERESTED: u8 = 2;
pub const NOT_INTERESTED: u8 = 3;
pub const HAVE: u8 = 4;
pub const BITFIELD: u8 = 5;
pub const REQUEST: u8 = 6;
pub const PIECE: u8 = 7;
pub const CANCEL: u8 = 8;
pub const PORT: u8 = 9;
pub const EXTENDED: u8 = 20;

pub fn default_reserved() -> [u8; 8] {
    let mut r = [0u8; 8];
    r[5] |= 0x10;
    r[7] |= 0x01;
    r
}

pub struct Handshake {
    pub reserved: [u8; 8],
    pub info_hash: [u8; 20],
    pub peer_id: [u8; 20],
}

impl Handshake {
    pub fn to_bytes(&self) -> Vec<u8> {
        let mut b = Vec::with_capacity(68);
        b.push(19);
        b.extend_from_slice(PROTOCOL_STRING);
        b.extend_from_slice(&self.reserved);
        b.extend_from_slice(&self.info_hash);
        b.extend_from_slice(&self.peer_id);
        b
    }

    pub fn parse(data: &[u8]) -> Result<Handshake, String> {
        if data.len() < 68 {
            return Err(format!("handshake is {} bytes, need 68", data.len()));
        }
        if data[0] != 19 || &data[1..20] != PROTOCOL_STRING {
            return Err("handshake prefix mismatch".into());
        }
        let mut reserved = [0u8; 8];
        reserved.copy_from_slice(&data[20..28]);
        let mut info_hash = [0u8; 20];
        info_hash.copy_from_slice(&data[28..48]);
        let mut peer_id = [0u8; 20];
        peer_id.copy_from_slice(&data[48..68]);
        Ok(Handshake { reserved, info_hash, peer_id })
    }

    pub fn supports_extended(&self) -> bool {
        self.reserved[5] & 0x10 != 0
    }
    pub fn supports_dht(&self) -> bool {
        self.reserved[7] & 0x01 != 0
    }
}

pub struct PeerMessage {
    pub id: Option<u8>,
    pub payload: Vec<u8>,
}

impl PeerMessage {
    pub fn to_bytes(&self) -> Vec<u8> {
        match self.id {
            None => vec![0, 0, 0, 0],
            Some(id) => {
                let len = 1 + self.payload.len();
                let mut b = Vec::with_capacity(4 + len);
                b.extend_from_slice(&(len as u32).to_be_bytes());
                b.push(id);
                b.extend_from_slice(&self.payload);
                b
            }
        }
    }
}

pub fn keep_alive() -> PeerMessage {
    PeerMessage { id: None, payload: vec![] }
}
pub fn choke() -> PeerMessage {
    PeerMessage { id: Some(CHOKE), payload: vec![] }
}
pub fn unchoke() -> PeerMessage {
    PeerMessage { id: Some(UNCHOKE), payload: vec![] }
}
pub fn interested() -> PeerMessage {
    PeerMessage { id: Some(INTERESTED), payload: vec![] }
}
pub fn not_interested() -> PeerMessage {
    PeerMessage { id: Some(NOT_INTERESTED), payload: vec![] }
}
pub fn have(piece_index: u32) -> PeerMessage {
    PeerMessage { id: Some(HAVE), payload: piece_index.to_be_bytes().to_vec() }
}
pub fn request(index: u32, begin: u32, length: u32) -> PeerMessage {
    block_ref(REQUEST, index, begin, length)
}
pub fn cancel(index: u32, begin: u32, length: u32) -> PeerMessage {
    block_ref(CANCEL, index, begin, length)
}
fn block_ref(id: u8, index: u32, begin: u32, length: u32) -> PeerMessage {
    let mut p = Vec::with_capacity(12);
    p.extend_from_slice(&index.to_be_bytes());
    p.extend_from_slice(&begin.to_be_bytes());
    p.extend_from_slice(&length.to_be_bytes());
    PeerMessage { id: Some(id), payload: p }
}
pub fn piece(index: u32, begin: u32, block: &[u8]) -> PeerMessage {
    let mut p = Vec::with_capacity(8 + block.len());
    p.extend_from_slice(&index.to_be_bytes());
    p.extend_from_slice(&begin.to_be_bytes());
    p.extend_from_slice(block);
    PeerMessage { id: Some(PIECE), payload: p }
}
pub fn port(value: u16) -> PeerMessage {
    PeerMessage { id: Some(PORT), payload: value.to_be_bytes().to_vec() }
}
pub fn extended(sub_id: u8, body: &[u8]) -> PeerMessage {
    let mut p = vec![sub_id];
    p.extend_from_slice(body);
    PeerMessage { id: Some(EXTENDED), payload: p }
}

pub struct Bitfield {
    count: usize,
    bits: Vec<u8>,
}

impl Bitfield {
    pub fn new(piece_count: usize) -> Self {
        Bitfield { count: piece_count, bits: vec![0u8; piece_count.div_ceil(8)] }
    }
    pub fn from_bytes(data: &[u8], piece_count: usize) -> Self {
        let need = piece_count.div_ceil(8);
        let mut bits = vec![0u8; need];
        let n = data.len().min(need);
        bits[..n].copy_from_slice(&data[..n]);
        Bitfield { count: piece_count, bits }
    }
    pub fn get(&self, i: usize) -> bool {
        i < self.count && self.bits[i >> 3] & (0x80 >> (i & 7)) != 0
    }
    pub fn set(&mut self, i: usize) {
        if i < self.count {
            self.bits[i >> 3] |= 0x80 >> (i & 7);
        }
    }
    pub fn pop_count(&self) -> usize {
        (0..self.count).filter(|&i| self.get(i)).count()
    }
    pub fn has_all(&self) -> bool {
        self.pop_count() == self.count
    }
    pub fn to_bytes(&self) -> Vec<u8> {
        self.bits.clone()
    }
}
