// SPDX-License-Identifier: MIT
//
// Generic "value-earned" relay-tip envelope carried inside a [`PacketType::TipPacket`] (24). Rust
// port of `AetherNet.Incentive.TipPacketPayload`, byte-identical to the C# reference and every other
// language implementation.
//
// This model is deliberately value-agnostic. `amount` is a bare number with NO units, NO policy, and
// NO settlement semantics attached at the protocol layer. The protocol carries the signal that one
// node wishes to credit another for some kind of relayed traffic; what (if anything) that signal is
// worth is entirely the host's business. A bare node accepts and relays the packet but settles
// nothing — only a host that has wired a [`super::MeshTipSettlementProvider`] override decides how to
// interpret the value.
//
// The payload is self-signed by the tipper: `signature` is an Ed25519 signature over the canonical
// byte layout produced by [`TipPacketPayload::build_canonical_data`]. The signature binds the tipper,
// recipient, amount, traffic type, reference, and timestamp together so an intermediate relay cannot
// tamper with any field without invalidating it.

use serde::{Deserialize, Serialize};
use uuid::Uuid;

/// The JSON body (snake_case) carried inside a `TipPacket(24)`.
///
/// `amount` is the INVARIANT decimal string (the .NET `decimal.ToString(InvariantCulture)`
/// round-trip form, e.g. `"12.50"`, `"0.0001"`, `"123456.789"`) — NOT a float. Keeping it a `String`
/// is what makes the signed bytes stable across locales and decimal scales without baking in any unit
/// or fixed-point assumption, and is required for byte-identity with the C# canonical data.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TipPacketPayload {
    /// UHID of the node offering the tip (the signer of this payload).
    pub tipper_uhid: String,

    /// UHID of the node the tip is addressed to.
    pub recipient_uhid: String,

    /// Generic value being credited, as the invariant decimal string. The protocol imposes NO unit,
    /// NO minimum, NO maximum, and NO policy.
    pub amount: String,

    /// Free-form tag describing the kind of relayed traffic this tip is for, e.g. `"message-relay"`
    /// or `"gateway-share"`. Opaque to the protocol.
    pub traffic_type: String,

    /// Optional correlation id linking this tip to some host-defined unit of work. `None` when the
    /// tip stands alone (serialised as 16 zero bytes in the canonical data).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reference_id: Option<Uuid>,

    /// When the tipper created this payload, in Unix milliseconds (.NET
    /// `DateTimeOffset.ToUnixTimeMilliseconds`). The JSON field name is `timestamp`, matching the C#
    /// serializer.
    #[serde(rename = "timestamp")]
    pub timestamp_unix_ms: i64,

    /// Ed25519 signature over [`Self::build_canonical_data`], produced by the tipper's identity key.
    /// `None` until the payload has been signed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub signature: Option<Vec<u8>>,
}

impl TipPacketPayload {
    /// Builds the canonical byte array that is signed/verified for this payload. The `signature`
    /// field itself is excluded from the canonical data.
    ///
    /// Layout (little-endian lengths, matching `PacketSigningService.BuildSignableData`
    /// conventions):
    ///
    /// ```text
    ///   TipperLen(4 LE i32)    || Tipper(UTF-8)
    ///   RecipientLen(4 LE i32) || Recipient(UTF-8)
    ///   AmountLen(4 LE i32)    || Amount(UTF-8, invariant round-trip decimal string)
    ///   TrafficLen(4 LE i32)   || TrafficType(UTF-8)
    ///   ReferenceId(16, all-zero GUID when None, .NET mixed-endian byte order)
    ///   TimestampUnixMs(8 LE i64)
    /// ```
    pub fn build_canonical_data(&self) -> Vec<u8> {
        let tipper_bytes = self.tipper_uhid.as_bytes();
        let recipient_bytes = self.recipient_uhid.as_bytes();
        let amount_bytes = self.amount.as_bytes();
        let traffic_bytes = self.traffic_type.as_bytes();

        let total_length = 4 + tipper_bytes.len()
            + 4 + recipient_bytes.len()
            + 4 + amount_bytes.len()
            + 4 + traffic_bytes.len()
            + 16 // ReferenceId GUID
            + 8; // Timestamp (i64 LE)

        let mut buf = Vec::with_capacity(total_length);

        write_length_prefixed(&mut buf, tipper_bytes);
        write_length_prefixed(&mut buf, recipient_bytes);
        write_length_prefixed(&mut buf, amount_bytes);
        write_length_prefixed(&mut buf, traffic_bytes);

        // ReferenceId — 16 bytes, all-zero when None, .NET GUID byte order otherwise.
        match self.reference_id {
            Some(id) => buf.extend_from_slice(&guid_bytes_dotnet(&id)),
            None => buf.extend_from_slice(&[0u8; 16]),
        }

        // Timestamp — Unix milliseconds, little-endian int64.
        buf.extend_from_slice(&self.timestamp_unix_ms.to_le_bytes());

        buf
    }

    /// Serialises the payload to its snake_case UTF-8 JSON wire form.
    pub fn to_json(&self) -> Result<Vec<u8>, serde_json::Error> {
        serde_json::to_vec(self)
    }

    /// Deserialises a snake_case UTF-8 JSON tip payload.
    pub fn from_json(data: &[u8]) -> Result<Self, serde_json::Error> {
        serde_json::from_slice(data)
    }
}

/// Writes a 4-byte LE int32 length prefix followed by `value`.
fn write_length_prefixed(buf: &mut Vec<u8>, value: &[u8]) {
    buf.extend_from_slice(&(value.len() as i32).to_le_bytes());
    buf.extend_from_slice(value);
}

/// Returns the 16-byte .NET in-memory representation of a UUID, which is what
/// `System.Guid.TryWriteBytes` produces. The `uuid` crate stores the UUID in big-endian (RFC 4122)
/// order; .NET stores the first three groups little-endian (Data1: 4 bytes, Data2: 2 bytes, Data3:
/// 2 bytes) and the final 8 bytes as-is. This mixed-endian layout is required for byte-identity with
/// the C# canonical data.
fn guid_bytes_dotnet(u: &Uuid) -> [u8; 16] {
    let b = u.as_bytes(); // RFC 4122 big-endian order.
    let mut out = [0u8; 16];
    // Data1 (bytes 0..3) — reversed.
    out[0] = b[3];
    out[1] = b[2];
    out[2] = b[1];
    out[3] = b[0];
    // Data2 (bytes 4..5) — reversed.
    out[4] = b[5];
    out[5] = b[4];
    // Data3 (bytes 6..7) — reversed.
    out[6] = b[7];
    out[7] = b[6];
    // Data4 (bytes 8..15) — as-is.
    out[8..16].copy_from_slice(&b[8..16]);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guid_byte_order_matches_dotnet_mixed_endian() {
        // .NET Guid.TryWriteBytes of {11112222-3333-4444-5555-666677778888} produces the first three
        // groups little-endian, the trailing 8 bytes as-is.
        let id = Uuid::parse_str("11112222-3333-4444-5555-666677778888").unwrap();
        let got = guid_bytes_dotnet(&id);
        let want: [u8; 16] = [
            0x22, 0x22, 0x11, 0x11, // Data1 reversed
            0x33, 0x33, // Data2 reversed
            0x44, 0x44, // Data3 reversed
            0x55, 0x55, 0x66, 0x66, 0x77, 0x77, 0x88, 0x88, // Data4 as-is
        ];
        assert_eq!(got, want);
    }

    #[test]
    fn null_reference_id_is_sixteen_zero_bytes() {
        let p = TipPacketPayload {
            tipper_uhid: "a".to_string(),
            recipient_uhid: "b".to_string(),
            amount: "1".to_string(),
            traffic_type: "t".to_string(),
            reference_id: None,
            timestamp_unix_ms: 0,
            signature: None,
        };
        let data = p.build_canonical_data();
        // The 16 GUID bytes sit just before the trailing 8 timestamp bytes.
        let guid_region = &data[data.len() - 24..data.len() - 8];
        assert_eq!(guid_region, &[0u8; 16]);
    }

    #[test]
    fn json_round_trip_preserves_amount_as_string() {
        let p = TipPacketPayload {
            tipper_uhid: "aether:tipper:aa".to_string(),
            recipient_uhid: "aether:recipient:bb".to_string(),
            amount: "12.50".to_string(),
            traffic_type: "message-relay".to_string(),
            reference_id: Some(Uuid::parse_str("11112222-3333-4444-5555-666677778888").unwrap()),
            timestamp_unix_ms: 1_700_000_000_000,
            signature: Some(vec![1, 2, 3]),
        };
        let js = p.to_json().unwrap();
        // amount must serialise as a JSON string, never a number.
        let text = String::from_utf8(js.clone()).unwrap();
        assert!(text.contains("\"amount\":\"12.50\""), "amount must be a string: {text}");

        let back = TipPacketPayload::from_json(&js).unwrap();
        assert_eq!(back, p);
        assert_eq!(back.build_canonical_data(), p.build_canonical_data());
    }
}
