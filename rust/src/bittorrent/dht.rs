// SPDX-License-Identifier: MIT
//! DHT (BEP-5): XOR distance + compact node (26B) / peer (6B) info.

pub fn xor_distance(a: &[u8], b: &[u8]) -> Vec<u8> {
    a.iter().zip(b).map(|(x, y)| x ^ y).collect()
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DhtContact {
    pub id: Vec<u8>,
    pub ip: String,
    pub port: u16,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PeerAddr {
    pub ip: String,
    pub port: u16,
}

fn ip_bytes(ip: &str) -> Vec<u8> {
    ip.split('.').map(|x| x.parse::<u8>().unwrap_or(0)).collect()
}

fn ip_str(b: &[u8]) -> String {
    format!("{}.{}.{}.{}", b[0], b[1], b[2], b[3])
}

pub fn encode_compact_nodes(nodes: &[DhtContact]) -> Vec<u8> {
    let mut out = Vec::with_capacity(nodes.len() * 26);
    for c in nodes {
        out.extend_from_slice(&c.id);
        out.extend_from_slice(&ip_bytes(&c.ip));
        out.extend_from_slice(&c.port.to_be_bytes());
    }
    out
}

pub fn decode_compact_nodes(data: &[u8]) -> Result<Vec<DhtContact>, String> {
    if data.len() % 26 != 0 {
        return Err("compact nodes length is not a multiple of 26".into());
    }
    Ok(data
        .chunks(26)
        .map(|c| DhtContact {
            id: c[0..20].to_vec(),
            ip: ip_str(&c[20..24]),
            port: u16::from_be_bytes([c[24], c[25]]),
        })
        .collect())
}

pub fn encode_compact_peers(peers: &[PeerAddr]) -> Vec<u8> {
    let mut out = Vec::with_capacity(peers.len() * 6);
    for p in peers {
        out.extend_from_slice(&ip_bytes(&p.ip));
        out.extend_from_slice(&p.port.to_be_bytes());
    }
    out
}

pub fn decode_compact_peers(data: &[u8]) -> Result<Vec<PeerAddr>, String> {
    if data.len() % 6 != 0 {
        return Err("compact peers length is not a multiple of 6".into());
    }
    Ok(data
        .chunks(6)
        .map(|c| PeerAddr {
            ip: ip_str(&c[0..4]),
            port: u16::from_be_bytes([c[4], c[5]]),
        })
        .collect())
}
