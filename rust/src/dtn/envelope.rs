// SPDX-License-Identifier: MIT

//! Canonical binary DTN envelope (wire format v1). Byte-identical across all
//! eight AetherNet SDKs; the Go encoder (`go/cmd/dtnfixturegen`) is the oracle
//! and `fixtures/dtn/expected/*.bin` pins the bytes.
//!
//! Layout — every multi-byte integer is LITTLE-ENDIAN, except the 16-byte
//! bundle id which is RFC-4122 BIG-ENDIAN (`Uuid::as_bytes`, mirroring the
//! packet serializer; never the legacy .NET mixed-endian Guid layout).
//! Cleartext routing fields come first and the opaque `encrypted_payload` is
//! last, so the future T1 privacy bump can move sender/recipient into the
//! ciphertext without a re-layout.
//!
//! The Rust [`DtnBundle`] stores `created_at`/`expires_at` as Unix **seconds**;
//! the wire carries milliseconds, so we multiply/divide by 1000 (every fixture
//! timestamp is whole-second-aligned, so this round-trips exactly).

use uuid::Uuid;

use crate::models::{BundlePriority, BundleStatus, DtnBundle};

const VERSION: u8 = 0x01;
const MAX_PAYLOAD: usize = 16 * 1024 * 1024; // AETHERNET_MAX_PAYLOAD_LEN

// ─────────────────────────────── DtnBundle ───────────────────────────────

pub fn serialize_bundle(b: &DtnBundle) -> Vec<u8> {
    let mut out = Vec::with_capacity(64 + b.encrypted_payload.len());
    out.push(VERSION);
    out.extend_from_slice(b.id.as_bytes()); // 16 bytes, RFC-4122 big-endian
    out.push(b.priority.as_u8());
    out.push(b.status.as_u8());
    write_i32(&mut out, b.copy_count);
    write_i32(&mut out, b.max_copies);
    write_i32(&mut out, b.hop_count);
    write_i64(&mut out, (b.created_at as i64) * 1000);
    write_i64(&mut out, (b.expires_at as i64) * 1000);
    write_str(&mut out, &b.sender_uhid);
    write_str(&mut out, &b.recipient_uhid);
    write_str(&mut out, b.sender_geohash.as_deref().unwrap_or(""));
    write_str(&mut out, b.recipient_last_geohash.as_deref().unwrap_or(""));
    write_bytes32(&mut out, &b.encrypted_payload);
    out
}

pub fn deserialize_bundle(data: &[u8]) -> Option<DtnBundle> {
    let mut r = Reader::new(data);
    r.version()?;
    let id = r.uuid()?;
    let priority = r.u8()?;
    if priority > 3 {
        return None;
    }
    let status = r.u8()?;
    if status > 4 {
        return None;
    }
    let copy_count = r.i32()?;
    let max_copies = r.i32()?;
    let hop_count = r.i32()?;
    let created_at_ms = r.i64()?;
    let expires_at_ms = r.i64()?;
    let sender_uhid = r.string()?;
    let recipient_uhid = r.string()?;
    let sender_geohash = r.string()?;
    let recipient_last_geohash = r.string()?;
    let encrypted_payload = r.bytes32()?;
    Some(DtnBundle {
        id,
        sender_uhid,
        recipient_uhid,
        encrypted_payload,
        priority: BundlePriority::from_u8(priority),
        status: BundleStatus::from_u8(status),
        copy_count,
        max_copies,
        sender_geohash: empty_to_none(sender_geohash),
        recipient_last_geohash: empty_to_none(recipient_last_geohash),
        hop_count,
        created_at: (created_at_ms / 1000) as u64,
        expires_at: (expires_at_ms / 1000) as u64,
    })
}

// ─────────────────────── CustodyAck (18 bytes fixed) ──────────────────────

pub fn serialize_custody_ack(bundle_id: &Uuid, accepted: bool) -> Vec<u8> {
    let mut out = Vec::with_capacity(18);
    out.push(VERSION);
    out.extend_from_slice(bundle_id.as_bytes());
    out.push(if accepted { 1 } else { 0 });
    out
}

pub fn deserialize_custody_ack(data: &[u8]) -> Option<(Uuid, bool)> {
    let mut r = Reader::new(data);
    r.version()?;
    let id = r.uuid()?;
    let accepted = r.u8()? != 0;
    Some((id, accepted))
}

// ───────────────────────────── DeliveryReceipt ────────────────────────────

pub fn serialize_delivery_receipt(
    bundle_id: &Uuid,
    recipient_uhid: &str,
    total_hops: i32,
    total_custody_transfers: i32,
    delivered_at_ms: i64,
) -> Vec<u8> {
    let mut out = Vec::with_capacity(64);
    out.push(VERSION);
    out.extend_from_slice(bundle_id.as_bytes());
    write_str(&mut out, recipient_uhid);
    write_i32(&mut out, total_hops);
    write_i32(&mut out, total_custody_transfers);
    write_i64(&mut out, delivered_at_ms);
    out
}

/// Returns `(bundle_id, recipient_uhid, total_hops, total_custody_transfers, delivered_at_ms)`.
pub fn deserialize_delivery_receipt(data: &[u8]) -> Option<(Uuid, String, i32, i32, i64)> {
    let mut r = Reader::new(data);
    r.version()?;
    let id = r.uuid()?;
    let recipient = r.string()?;
    let total_hops = r.i32()?;
    let total_custody = r.i32()?;
    let delivered_at_ms = r.i64()?;
    Some((id, recipient, total_hops, total_custody, delivered_at_ms))
}

// ─────────────────────────────── primitives ───────────────────────────────

fn empty_to_none(s: String) -> Option<String> {
    if s.is_empty() {
        None
    } else {
        Some(s)
    }
}

fn write_i32(out: &mut Vec<u8>, v: i32) {
    out.extend_from_slice(&v.to_le_bytes());
}

fn write_i64(out: &mut Vec<u8>, v: i64) {
    out.extend_from_slice(&v.to_le_bytes());
}

fn write_str(out: &mut Vec<u8>, s: &str) {
    let bytes = s.as_bytes();
    let len = bytes.len().min(u16::MAX as usize);
    out.extend_from_slice(&(len as u16).to_le_bytes());
    out.extend_from_slice(&bytes[..len]);
}

fn write_bytes32(out: &mut Vec<u8>, b: &[u8]) {
    out.extend_from_slice(&(b.len() as i32).to_le_bytes());
    out.extend_from_slice(b);
}

struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn new(data: &'a [u8]) -> Self {
        Self { data, pos: 0 }
    }

    fn version(&mut self) -> Option<()> {
        if self.u8()? == VERSION {
            Some(())
        } else {
            None
        }
    }

    fn u8(&mut self) -> Option<u8> {
        let b = *self.data.get(self.pos)?;
        self.pos += 1;
        Some(b)
    }

    fn take(&mut self, n: usize) -> Option<&'a [u8]> {
        let end = self.pos.checked_add(n)?;
        let slice = self.data.get(self.pos..end)?;
        self.pos = end;
        Some(slice)
    }

    fn uuid(&mut self) -> Option<Uuid> {
        let slice = self.take(16)?;
        let mut arr = [0u8; 16];
        arr.copy_from_slice(slice);
        Some(Uuid::from_bytes(arr))
    }

    fn i32(&mut self) -> Option<i32> {
        let slice = self.take(4)?;
        Some(i32::from_le_bytes(slice.try_into().ok()?))
    }

    fn i64(&mut self) -> Option<i64> {
        let slice = self.take(8)?;
        Some(i64::from_le_bytes(slice.try_into().ok()?))
    }

    fn u16(&mut self) -> Option<u16> {
        let slice = self.take(2)?;
        Some(u16::from_le_bytes(slice.try_into().ok()?))
    }

    fn string(&mut self) -> Option<String> {
        let n = self.u16()? as usize;
        let slice = self.take(n)?;
        String::from_utf8(slice.to_vec()).ok()
    }

    fn bytes32(&mut self) -> Option<Vec<u8>> {
        let n = self.i32()?;
        if n < 0 || n as usize > MAX_PAYLOAD {
            return None;
        }
        let slice = self.take(n as usize)?;
        Some(slice.to_vec())
    }
}
