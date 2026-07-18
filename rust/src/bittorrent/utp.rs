// SPDX-License-Identifier: MIT
//! µTP packet (BEP-29, version 1) — byte-exact 20-byte header.

pub const UTP_DATA: u8 = 0;
pub const UTP_FIN: u8 = 1;
pub const UTP_STATE: u8 = 2;
pub const UTP_RESET: u8 = 3;
pub const UTP_SYN: u8 = 4;

pub const UTP_VERSION: u8 = 1;
pub const UTP_HEADER_SIZE: usize = 20;

#[derive(Debug, Clone)]
pub struct UtpPacket {
    pub packet_type: u8,
    pub conn_id: u16,
    pub timestamp: u32,
    pub timestamp_diff: u32,
    pub window: u32,
    pub seq: u16,
    pub ack: u16,
    pub payload: Vec<u8>,
}

impl UtpPacket {
    pub fn to_bytes(&self) -> Vec<u8> {
        let mut h = vec![0u8; UTP_HEADER_SIZE];
        h[0] = (self.packet_type << 4) | UTP_VERSION;
        h[1] = 0;
        h[2..4].copy_from_slice(&self.conn_id.to_be_bytes());
        h[4..8].copy_from_slice(&self.timestamp.to_be_bytes());
        h[8..12].copy_from_slice(&self.timestamp_diff.to_be_bytes());
        h[12..16].copy_from_slice(&self.window.to_be_bytes());
        h[16..18].copy_from_slice(&self.seq.to_be_bytes());
        h[18..20].copy_from_slice(&self.ack.to_be_bytes());
        h.extend_from_slice(&self.payload);
        h
    }

    pub fn parse(data: &[u8]) -> Result<UtpPacket, String> {
        if data.len() < UTP_HEADER_SIZE {
            return Err(format!("µTP packet is {} bytes, shorter than {}", data.len(), UTP_HEADER_SIZE));
        }
        let version = data[0] & 0x0f;
        if version != UTP_VERSION {
            return Err(format!("unsupported µTP version {version}"));
        }
        let packet_type = data[0] >> 4;
        let mut offset = UTP_HEADER_SIZE;
        let mut next_ext = data[1];
        while next_ext != 0 {
            if offset + 2 > data.len() {
                return Err("truncated µTP extension header".into());
            }
            let this_next = data[offset];
            let ext_len = data[offset + 1] as usize;
            offset += 2 + ext_len;
            if offset > data.len() {
                return Err("truncated µTP extension data".into());
            }
            next_ext = this_next;
        }
        Ok(UtpPacket {
            packet_type,
            conn_id: u16::from_be_bytes([data[2], data[3]]),
            timestamp: u32::from_be_bytes([data[4], data[5], data[6], data[7]]),
            timestamp_diff: u32::from_be_bytes([data[8], data[9], data[10], data[11]]),
            window: u32::from_be_bytes([data[12], data[13], data[14], data[15]]),
            seq: u16::from_be_bytes([data[16], data[17]]),
            ack: u16::from_be_bytes([data[18], data[19]]),
            payload: data[offset..].to_vec(),
        })
    }
}
