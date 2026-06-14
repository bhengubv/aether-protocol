// SPDX-License-Identifier: MIT
//
// Proof-of-Vicinity token model and canonical signable-body codec. Rust port of
// `AetherNet.Market.Models.PoVToken` / `PoVTransportType` / `PoVScore` and
// `AetherNet.Market.PoVTokenCodec` (and the Go `market` package).
//
// The canonical body that BOTH the witness and the subject sign with their real Ed25519 identity keys
// must stay byte-identical across every language implementation so a token signed by one node
// verifies on any other:
//
//   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
//
// timestamp_ticks is .NET DateTime.Ticks (100ns intervals since 0001-01-01).

use serde::de::{self, Deserialize, Deserializer};
use serde::{Serialize, Serializer};

/// The transport used for a co-presence Proof-of-Vicinity exchange. Only short-range transports are
/// valid (prevents remote minting). The wire byte (ble=0, nfc=1, nearlink=2) MUST match the C#
/// `PoVTransportType` enum.
///
/// Serialises as its numeric wire byte (matching the C# System.Text.Json default for enums and the
/// Go `PoVTransportType byte` JSON form) via the hand-written [`Serialize`] / [`Deserialize`] impls
/// below — no extra crate dependency.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PoVTransportType {
    /// Bluetooth Low Energy (short range — prevents remote forgery).
    Ble = 0,
    /// Near-Field Communication (requires physical proximity).
    Nfc = 1,
    /// Huawei NearLink (short range, similar to BLE).
    NearLink = 2,
}

impl Serialize for PoVTransportType {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_u8(self.as_byte())
    }
}

impl<'de> Deserialize<'de> for PoVTransportType {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let byte = u8::deserialize(deserializer)?;
        PoVTransportType::from_byte(byte)
            .ok_or_else(|| de::Error::custom(format!("invalid PoVTransportType byte: {byte}")))
    }
}

impl PoVTransportType {
    /// Maps a wire byte to a transport, or `None` for an unknown (non-short-range) value.
    pub fn from_byte(value: u8) -> Option<Self> {
        match value {
            0 => Some(PoVTransportType::Ble),
            1 => Some(PoVTransportType::Nfc),
            2 => Some(PoVTransportType::NearLink),
            _ => None,
        }
    }

    /// The wire byte for this transport.
    pub fn as_byte(self) -> u8 {
        self as u8
    }

    /// Reports whether the transport is a valid short-range PoV channel. All three known transports
    /// are short-range; this exists so callers can reject arbitrary byte values.
    pub fn is_short_range(self) -> bool {
        matches!(
            self,
            PoVTransportType::Ble | PoVTransportType::Nfc | PoVTransportType::NearLink
        )
    }

    /// The lowercase wire name of the transport.
    pub fn as_str(self) -> &'static str {
        match self {
            PoVTransportType::Ble => "ble",
            PoVTransportType::Nfc => "nfc",
            PoVTransportType::NearLink => "nearlink",
        }
    }
}

/// Number of .NET DateTime ticks (100ns) per second.
const TICKS_PER_SECOND: i64 = 10_000_000;

/// The .NET DateTime.Ticks value at the Unix epoch (1970-01-01T00:00:00Z), i.e. ticks between
/// 0001-01-01 and 1970-01-01. Used to convert between .NET ticks and a Unix instant.
const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;

/// A Proof-of-Vicinity token issued by one node (the witness) to another (the subject) during a
/// physical co-presence event. Both parties must countersign — this prevents unilateral forgery. The
/// token is transmitted over a short-range transport (BLE/NFC/NearLink only) to prevent remote
/// minting. The JSON wire form is snake_case, matching the C# serializer.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct PoVToken {
    /// UHID of the node issuing the voucher.
    pub witness_uhid: String,

    /// UHID of the node being vouched for.
    pub subject_uhid: String,

    /// The co-presence event time as .NET DateTime.Ticks (100ns since 0001-01-01). Stored as ticks
    /// (not a `DateTime`) so the signed canonical body is byte-identical to C#.
    pub timestamp_ticks: i64,

    /// The transport channel used (must be short-range).
    pub transport_used: PoVTransportType,

    /// The Ed25519 signature by the witness over the canonical body.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub witness_signature: Option<Vec<u8>>,

    /// The Ed25519 countersignature by the subject — required for token validity.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub subject_signature: Option<Vec<u8>>,
}

/// Builds the canonical signable bytes for a PoV token body. The same layout is signed by the witness
/// (on issue) and counter-signed by the subject (on accept).
///
/// ```text
///   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
/// ```
pub fn build_signable_token_data(
    subject_uhid: &str,
    timestamp_ticks: i64,
    transport: PoVTransportType,
) -> Vec<u8> {
    let subject_bytes = subject_uhid.as_bytes();
    let mut data = Vec::with_capacity(4 + subject_bytes.len() + 8 + 1);

    data.extend_from_slice(&(subject_bytes.len() as i32).to_le_bytes());
    data.extend_from_slice(subject_bytes);
    data.extend_from_slice(&timestamp_ticks.to_le_bytes());
    data.push(transport.as_byte());

    data
}

impl PoVToken {
    /// The canonical signable bytes for this token.
    pub fn signable_data(&self) -> Vec<u8> {
        build_signable_token_data(&self.subject_uhid, self.timestamp_ticks, self.transport_used)
    }

    /// Serialises the token to its snake_case UTF-8 JSON wire form.
    pub fn to_json(&self) -> Result<Vec<u8>, serde_json::Error> {
        serde_json::to_vec(self)
    }

    /// Deserialises a snake_case UTF-8 JSON PoV token.
    pub fn from_json(data: &[u8]) -> Result<Self, serde_json::Error> {
        serde_json::from_slice(data)
    }
}

/// Converts a .NET DateTime.Ticks value to a Unix-millisecond instant. Provided for hosts that want a
/// wall-clock time; the canonical body always uses the raw ticks.
pub fn ticks_to_unix_ms(ticks: i64) -> i64 {
    (ticks - UNIX_EPOCH_TICKS) / (TICKS_PER_SECOND / 1000)
}

/// Converts a Unix-millisecond instant to a .NET DateTime.Ticks value.
pub fn unix_ms_to_ticks(unix_ms: i64) -> i64 {
    unix_ms * (TICKS_PER_SECOND / 1000) + UNIX_EPOCH_TICKS
}

/// The Proof-of-Vicinity trust score for a node — a purely local anti-Sybil routing/identity signal
/// that attaches NO value semantics.
#[derive(Debug, Clone, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct PoVScore {
    /// UHID of the scored node.
    pub uhid: String,
    /// Number of distinct witnesses who have issued PoV tokens to this node.
    pub unique_witnesses: usize,
    /// Weighted score (0.0–1.0).
    pub weighted_score: f64,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn transport_byte_and_name_mapping() {
        assert_eq!(PoVTransportType::Ble.as_byte(), 0);
        assert_eq!(PoVTransportType::Nfc.as_byte(), 1);
        assert_eq!(PoVTransportType::NearLink.as_byte(), 2);
        assert_eq!(PoVTransportType::Ble.as_str(), "ble");
        assert_eq!(PoVTransportType::Nfc.as_str(), "nfc");
        assert_eq!(PoVTransportType::NearLink.as_str(), "nearlink");
        assert_eq!(PoVTransportType::from_byte(3), None);
    }

    #[test]
    fn canonical_body_layout() {
        // Subject "ab" (len 2) || ticks=1 || transport ble(0).
        let body = build_signable_token_data("ab", 1, PoVTransportType::Ble);
        let mut want = Vec::new();
        want.extend_from_slice(&2i32.to_le_bytes());
        want.extend_from_slice(b"ab");
        want.extend_from_slice(&1i64.to_le_bytes());
        want.push(0);
        assert_eq!(body, want);
    }

    #[test]
    fn ticks_unix_round_trip_is_lossless_at_ms() {
        // Fixture timestamp is tick-exact at 100ns; round-trip at ms granularity is lossless for a
        // ms-aligned value.
        let ticks = 638_000_000_000_000_000i64; // ms-aligned (ends in 0000000 ticks)
        assert_eq!(unix_ms_to_ticks(ticks_to_unix_ms(ticks)), ticks);
    }

    #[test]
    fn json_round_trip_preserves_transport_and_signatures() {
        let tok = PoVToken {
            witness_uhid: "aether:witness:zz".to_string(),
            subject_uhid: "aether:subject:01".to_string(),
            timestamp_ticks: 638_000_000_000_000_000,
            transport_used: PoVTransportType::Nfc,
            witness_signature: Some(vec![1, 2, 3]),
            subject_signature: None,
        };
        let js = tok.to_json().unwrap();
        let text = String::from_utf8(js.clone()).unwrap();
        // transport_used must serialise as the numeric wire byte (1), not a string.
        assert!(text.contains("\"transport_used\":1"), "transport must be numeric: {text}");
        let back = PoVToken::from_json(&js).unwrap();
        assert_eq!(back, tok);
        assert_eq!(back.signable_data(), tok.signable_data());
    }
}
