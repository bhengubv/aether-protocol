// SPDX-License-Identifier: MIT

//! Transport-backed WebRTC signalling carrier — the Rust twin of the C#
//! `AetherNet.Transport.WebRtc.RelayWebRtcSignaling`.
//!
//! Where [`InMemorySignalingBus`](super::webrtc::InMemorySignalingBus) routes
//! [`Signal`](super::webrtc::Signal) structs directly between endpoints in one
//! process, this carrier rides a real [`TransportService`] — typically the
//! AetherNet QUIC/HTTP relay or the circuit-relay-v2 transport, but any channel
//! that delivers ordered bytes works — so two *separate* nodes can exchange the
//! SDP/ICE handshake with no dedicated signalling server. Once the data channel
//! is open the media/app traffic flows peer-to-peer; only the short handshake
//! ever touches the carrying transport.
//!
//! ## Framing (byte-identical to C# for cross-language interop)
//! Each signal is framed as a 4-byte magic prefix **`AWS1`** ("Aether WebRtc
//! Signal", framing v1) followed by a compact JSON body. The JSON keys, order,
//! integer enum discriminants and null-omission match the C# source-generated
//! contract exactly, so a Rust node and a C# node interoperate on the wire:
//!
//! ```text
//! 41 57 53 31  {"FromUhid":"alice","ToUhid":"bob","Type":0,"Sdp":"v=0…","SdpMLineIndex":0}
//! └── AWS1 ──┘ └────────────────────────── UTF-8 JSON ──────────────────────────────────┘
//! ```
//!
//! Inbound bytes on the carrying transport that lack the `AWS1` prefix are
//! ignored — they are ordinary application traffic, not signalling. As in C#,
//! give this a transport whose receive surface is dedicated to signalling (e.g.
//! a relay connection reserved for control traffic) so the prefixed control
//! frames never collide with the application data path.
//!
//! This is **out-of-band** signalling: it changes no mesh wire-serialization
//! and touches no protocol fixture. The `AWS1`+JSON frame is a private control
//! envelope between the two signalling carriers, not a mesh packet.

use std::sync::{Arc, Mutex};

use serde::Deserialize;

use super::webrtc::{Signal, SignalType, Signaling};
use super::TransportService;

/// "AWS1" = Aether WebRtc Signal, framing v1. Byte-identical to the C# magic
/// (`{ (byte)'A', (byte)'W', (byte)'S', (byte)'1' }`).
const MAGIC: [u8; 4] = [b'A', b'W', b'S', b'1'];

// ── Wire DTO ────────────────────────────────────────────────────────────────
//
// A private serialization DTO whose shape is locked to the C#
// `WebRtcSignalJsonContext` source-generated contract:
//   * PascalCase field names, verbatim (no naming policy in C#);
//   * declaration order FromUhid, ToUhid, Type, Sdp, Candidate, SdpMLineIndex,
//     SdpMid — serde_json preserves struct field order, matching STJ source-gen;
//   * `Type` is the integer enum discriminant (C# registers no string-enum
//     converter), so it is carried as a `u8` (Offer=0, Answer=1, IceCandidate=2);
//   * `Sdp` / `Candidate` / `SdpMid` are omitted when null
//     (`DefaultIgnoreCondition = WhenWritingNull`);
//   * `SdpMLineIndex` is a non-nullable value type, always present.
//
// This DTO is a control-plane envelope only. It is deliberately *not* any mesh
// packet DTO and shares no bytes with `crate::protocol` — no fixture is affected.
//
// Used for the DESERIALIZE (parse) path only. Serialization is hand-built in
// [`serialize_signal_body`] so string escaping matches STJ's default encoder
// (`serde_json` leaves `+ < > &` literal and would diverge from C#); the
// `#[serde(rename ...)]` names here still document the exact wire keys.
#[derive(Deserialize)]
struct SignalDto {
    #[serde(rename = "FromUhid")]
    from_uhid: String,
    #[serde(rename = "ToUhid")]
    to_uhid: String,
    #[serde(rename = "Type")]
    type_: u8,
    #[serde(rename = "Sdp", skip_serializing_if = "Option::is_none", default)]
    sdp: Option<String>,
    #[serde(rename = "Candidate", skip_serializing_if = "Option::is_none", default)]
    candidate: Option<String>,
    #[serde(rename = "SdpMLineIndex")]
    sdp_mline_index: u16,
    #[serde(rename = "SdpMid", skip_serializing_if = "Option::is_none", default)]
    sdp_mid: Option<String>,
}

impl SignalDto {
    fn into_signal(self) -> Option<Signal> {
        Some(Signal {
            from_uhid: self.from_uhid,
            to_uhid: self.to_uhid,
            signal_type: signal_type_from_u8(self.type_)?,
            sdp: self.sdp,
            candidate: self.candidate,
            sdp_mid: self.sdp_mid,
            sdp_mline_index: self.sdp_mline_index,
        })
    }
}

fn signal_type_to_u8(t: SignalType) -> u8 {
    match t {
        SignalType::Offer => 0,
        SignalType::Answer => 1,
        SignalType::IceCandidate => 2,
    }
}

fn signal_type_from_u8(v: u8) -> Option<SignalType> {
    match v {
        0 => Some(SignalType::Offer),
        1 => Some(SignalType::Answer),
        2 => Some(SignalType::IceCandidate),
        _ => None,
    }
}

/// Frames a [`Signal`] as `AWS1` + JSON. Public within the crate so the
/// interop/acceptance tests can assert the exact bytes against the C# fixture
/// shape without duplicating the framing.
///
/// The JSON body is built **by hand** (not `serde_json::to_vec`) so its string
/// escaping matches `System.Text.Json`'s default `JavaScriptEncoder.Default`
/// byte-for-byte — including STJ's escaping of `+ < > & ' \`` and every
/// non-ASCII code unit as uppercase `\uXXXX`. `serde_json` leaves `+ < > &`
/// literal and would diverge from the C# reference on real SDP fingerprints
/// (base64 `+`) and any non-ASCII ICE candidate. Field order, always-present
/// `Type`/`SdpMLineIndex`, and null-omission are unchanged. `SignalDto` is
/// retained solely for the deserialize path ([`parse_frame`]).
pub(crate) fn frame_signal(signal: &Signal) -> Vec<u8> {
    let body = serialize_signal_body(signal);
    let mut frame = Vec::with_capacity(MAGIC.len() + body.len());
    frame.extend_from_slice(&MAGIC);
    frame.extend_from_slice(body.as_bytes());
    frame
}

/// Serialises the signal body byte-identically to C# `System.Text.Json`.
///
/// Mirrors the TypeScript `serializeSignalBody` in
/// `typescript/src/transport/webrtc/RelayWebRtcSignaling.ts`: PascalCase keys in
/// C# record declaration order (`FromUhid`, `ToUhid`, `Type`, `Sdp`,
/// `Candidate`, `SdpMLineIndex`, `SdpMid`); `FromUhid`/`ToUhid` always written;
/// numeric `Type`/`SdpMLineIndex` always written (even when 0); nullable
/// `Sdp`/`Candidate`/`SdpMid` omitted when `None`; strings escaped via
/// [`stj_string`].
fn serialize_signal_body(signal: &Signal) -> String {
    let mut out = String::from("{");

    out.push_str("\"FromUhid\":");
    stj_string(&signal.from_uhid, &mut out);

    out.push_str(",\"ToUhid\":");
    stj_string(&signal.to_uhid, &mut out);

    out.push_str(",\"Type\":");
    out.push_str(&signal_type_to_u8(signal.signal_type).to_string());

    if let Some(sdp) = &signal.sdp {
        out.push_str(",\"Sdp\":");
        stj_string(sdp, &mut out);
    }

    if let Some(candidate) = &signal.candidate {
        out.push_str(",\"Candidate\":");
        stj_string(candidate, &mut out);
    }

    // Non-nullable C# ushort: always written, even when 0.
    out.push_str(",\"SdpMLineIndex\":");
    out.push_str(&signal.sdp_mline_index.to_string());

    if let Some(sdp_mid) = &signal.sdp_mid {
        out.push_str(",\"SdpMid\":");
        stj_string(sdp_mid, &mut out);
    }

    out.push('}');
    out
}

/// True when the ASCII code unit `c` (0x20–0x7E) is one that
/// `System.Text.Json`'s default encoder escapes as `\uXXXX` even though plain
/// JSON would leave it literal. Empirically captured from STJ: `" & ' + < > \``
/// (0x22, 0x26, 0x27, 0x2B, 0x3C, 0x3E, 0x60).
fn is_stj_escaped_ascii(c: u16) -> bool {
    matches!(c, 0x22 | 0x26 | 0x27 | 0x2B | 0x3C | 0x3E | 0x60)
}

/// Appends `s` as a JSON string literal (including the surrounding quotes),
/// escaped exactly as `System.Text.Json`'s default `JavaScriptEncoder.Default`.
///
/// Per **UTF-16 code unit** (a Rust `char` is a Unicode scalar, so BMP scalars
/// emit one unit and astral scalars emit a surrogate pair via
/// [`char::encode_utf16`]):
///  - `0x08→\b`, `0x09→\t`, `0x0A→\n`, `0x0C→\f`, `0x0D→\r`, `0x5C→\\`;
///  - else if `0x20 ≤ c ≤ 0x7E` and `c` is not STJ-escaped ASCII → literal
///    (this leaves `/` literal, matching STJ and `JSON.stringify`);
///  - else → `\u` + UPPERCASE 4-hex of the code unit (so `"`, `&`, `'`, `+`,
///    `<`, `>`, `` ` ``, all C0 controls without a short escape, and every
///    non-ASCII code unit become `\uXXXX`).
fn stj_string(s: &str, out: &mut String) {
    out.push('"');
    let mut buf = [0u16; 2];
    for ch in s.chars() {
        for &code in ch.encode_utf16(&mut buf).iter() {
            match code {
                0x08 => out.push_str("\\b"),
                0x09 => out.push_str("\\t"),
                0x0A => out.push_str("\\n"),
                0x0C => out.push_str("\\f"),
                0x0D => out.push_str("\\r"),
                0x5C => out.push_str("\\\\"),
                c if (0x20..=0x7E).contains(&c) && !is_stj_escaped_ascii(c) => {
                    // Safe printable ASCII → literal. `c` is in 0x20..=0x7E so
                    // it is a single-byte ASCII scalar.
                    out.push(c as u8 as char);
                }
                c => {
                    out.push_str("\\u");
                    // UPPERCASE, zero-padded to 4 hex digits.
                    for shift in [12, 8, 4, 0] {
                        let nibble = ((c >> shift) & 0xF) as u8;
                        out.push(char::from_digit(nibble as u32, 16).unwrap().to_ascii_uppercase());
                    }
                }
            }
        }
    }
    out.push('"');
}

/// Parses an `AWS1`-framed signalling frame. Returns `None` for bytes that do
/// not carry the magic prefix (ordinary app traffic) or whose body is not a
/// well-formed signal — the carrier silently ignores both, exactly like C#.
pub(crate) fn parse_frame(data: &[u8]) -> Option<Signal> {
    if data.len() < MAGIC.len() || data[..MAGIC.len()] != MAGIC {
        return None; // not a signalling frame — ordinary app bytes
    }
    let body = &data[MAGIC.len()..];
    serde_json::from_slice::<SignalDto>(body)
        .ok()
        .and_then(SignalDto::into_signal)
}

// ── Carrier ───────────────────────────────────────────────────────────────

type SignalHandler = Box<dyn Fn(Signal) + Send + Sync>;

/// Carries WebRTC SDP/ICE signalling over an existing [`TransportService`] so
/// two distant peers can negotiate a direct data channel without a dedicated
/// signalling server. The Rust equivalent of the C# `RelayWebRtcSignaling`.
///
/// Construct one per node, hand it the transport reserved for control traffic,
/// then pass it (as `Arc<dyn Signaling>`) to
/// [`WebRtcTransport::new`](super::webrtc::WebRtcTransport::new). Sends frame
/// `AWS1`+JSON and dispatch via the transport's `send_async`; receives via the
/// transport's shared data handler, ignoring any non-`AWS1` bytes.
pub struct RelayWebRtcSignaling {
    channel: Arc<dyn TransportService>,
    handler: Arc<Mutex<Option<SignalHandler>>>,
}

impl RelayWebRtcSignaling {
    /// Wires a carrier onto `channel`. The carrier subscribes to the channel's
    /// shared inbound-data surface ([`TransportService::set_shared_data_handler`])
    /// so signalling frames addressed to this node surface as [`Signal`]s.
    ///
    /// The transport must support the shared (`&self`) receive seam — the
    /// circuit-relay transport and the loopback pair used in tests do. Returns
    /// `Err` if the channel cannot accept a shared handler (i.e. it only allows
    /// `&mut self` handler registration before being shared), which would leave
    /// the carrier unable to receive.
    pub fn new(channel: Arc<dyn TransportService>) -> Result<Arc<Self>, &'static str> {
        let handler: Arc<Mutex<Option<SignalHandler>>> = Arc::new(Mutex::new(None));

        let sink = Arc::clone(&handler);
        let registered = channel.set_shared_data_handler(Arc::new(move |from: &str, data: &[u8]| {
            // Non-AWS1 bytes are ordinary app traffic — ignored, never surfaced.
            if let Some(signal) = parse_frame(data) {
                let _ = from; // sender UHID is already inside the signed-envelope-free body
                let guard = sink.lock().unwrap();
                if let Some(h) = guard.as_ref() {
                    h(signal);
                }
            }
        }));

        if !registered {
            return Err(
                "webrtc signalling: transport does not support a shared (&self) data handler",
            );
        }

        Ok(Arc::new(RelayWebRtcSignaling { channel, handler }))
    }
}

#[async_trait::async_trait]
impl Signaling for RelayWebRtcSignaling {
    async fn send_signal(&self, peer_uhid: &str, signal: Signal) -> bool {
        let frame = frame_signal(&signal);
        self.channel
            .send_async(peer_uhid, &frame)
            .await
            .unwrap_or(false)
    }

    fn on_signal(&self, handler: Box<dyn Fn(Signal) + Send + Sync>) {
        *self.handler.lock().unwrap() = Some(handler);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // ── Byte-layout locks (interop with C# RelayWebRtcSignaling) ─────────────
    //
    // These assert the exact JSON the carrier puts on the wire, so a drift away
    // from the C# source-generated shape fails here rather than silently on a
    // cross-language link. They exercise pure framing — no transport, no ICE —
    // so they run on every platform including headless Windows.

    #[test]
    fn offer_frame_is_byte_identical_to_csharp() {
        let signal = Signal {
            from_uhid: "alice".into(),
            to_uhid: "bob".into(),
            signal_type: SignalType::Offer,
            sdp: Some("v=0\r\no=- 1 1 IN IP4 0.0.0.0".into()),
            candidate: None,
            sdp_mid: None,
            sdp_mline_index: 0,
        };
        let frame = frame_signal(&signal);
        assert_eq!(&frame[..4], b"AWS1", "magic prefix");
        let json = std::str::from_utf8(&frame[4..]).unwrap();
        assert_eq!(
            json,
            r#"{"FromUhid":"alice","ToUhid":"bob","Type":0,"Sdp":"v=0\r\no=- 1 1 IN IP4 0.0.0.0","SdpMLineIndex":0}"#
        );
    }

    #[test]
    fn ice_candidate_frame_is_byte_identical_to_csharp() {
        let signal = Signal {
            from_uhid: "alice".into(),
            to_uhid: "bob".into(),
            signal_type: SignalType::IceCandidate,
            sdp: None,
            candidate: Some("candidate:1 1 udp 2 10.0.0.1 5000 typ host".into()),
            sdp_mid: Some("0".into()),
            sdp_mline_index: 0,
        };
        let frame = frame_signal(&signal);
        let json = std::str::from_utf8(&frame[4..]).unwrap();
        // Order matches the C# record declaration: …Candidate, SdpMLineIndex, SdpMid.
        assert_eq!(
            json,
            r#"{"FromUhid":"alice","ToUhid":"bob","Type":2,"Candidate":"candidate:1 1 udp 2 10.0.0.1 5000 typ host","SdpMLineIndex":0,"SdpMid":"0"}"#
        );
    }

    // ── STJ exotic-char escaping locks (byte-identity with C# / TS / Python) ──
    //
    // These golden frames are asserted verbatim by the TypeScript and Python
    // references too, so passing here proves the Rust carrier is byte-identical
    // to the C# `System.Text.Json` reference INCLUDING its escaping of
    // `+ < > & ' \`` and non-ASCII as uppercase `\uXXXX` — the exact behaviour
    // `serde_json` did NOT reproduce (it left `+ < > &` literal).

    #[test]
    fn offer_frame_escapes_exotic_ascii_like_stj() {
        // sdp carries base64 `+`, `<`, `>`, `&`, `=` and literal `/` — STJ
        // escapes `+ < > &` as \uXXXX (uppercase hex) but leaves `/`, `=`, and
        // spaces literal. serde_json would emit `+ < > &` literal and diverge.
        let signal = Signal {
            from_uhid: "a".into(),
            to_uhid: "b".into(),
            signal_type: SignalType::Offer,
            sdp: Some("a=fingerprint:sha-256 AB+/CD=xy <t> &z ual/set+ice".into()),
            candidate: None,
            sdp_mid: None,
            sdp_mline_index: 0,
        };
        let frame = frame_signal(&signal);
        assert_eq!(&frame[..4], b"AWS1", "magic prefix");
        let json = std::str::from_utf8(&frame[4..]).unwrap();
        // Byte-identical to the TypeScript (CS_ESCAPING_JSON) and Python
        // (_CS_RISKY-style) STJ references. Full frame == "AWS1" + this body.
        assert_eq!(
            json,
            "{\"FromUhid\":\"a\",\"ToUhid\":\"b\",\"Type\":0,\"Sdp\":\"a=fingerprint:sha-256 AB\\u002B/CD=xy \\u003Ct\\u003E \\u0026z ual/set\\u002Bice\",\"SdpMLineIndex\":0}",
            "STJ escapes + < > & as uppercase \\uXXXX; / = and space stay literal"
        );
    }

    #[test]
    fn candidate_frame_escapes_non_ascii_like_stj() {
        // candidate carries `+ / = < > & :` plus non-ASCII `ç é 世` — every
        // non-ASCII code unit becomes an uppercase \uXXXX (世 is BMP → one unit);
        // sdp_mid carries `/ +`. Proves the encode_utf16 non-ASCII path.
        let signal = Signal {
            from_uhid: "u".into(),
            to_uhid: "v".into(),
            signal_type: SignalType::IceCandidate,
            sdp: None,
            candidate: Some("a+b/c=d<e>f&g:h ç é 世".into()),
            sdp_mid: Some("m/i+d".into()),
            sdp_mline_index: 3,
        };
        let frame = frame_signal(&signal);
        assert_eq!(&frame[..4], b"AWS1", "magic prefix");
        let json = std::str::from_utf8(&frame[4..]).unwrap();
        // Byte-identical to the Python _CS_RISKY_BODY reference (decoded) and the
        // Kotlin/TypeScript STJ references. Full frame == "AWS1" + this body.
        assert_eq!(
            json,
            "{\"FromUhid\":\"u\",\"ToUhid\":\"v\",\"Type\":2,\"Candidate\":\"a\\u002Bb/c=d\\u003Ce\\u003Ef\\u0026g:h \\u00E7 \\u00E9 \\u4E16\",\"SdpMLineIndex\":3,\"SdpMid\":\"m/i\\u002Bd\"}",
            "STJ escapes + < > & and every non-ASCII scalar (ç é 世) as uppercase \\uXXXX"
        );
    }

    #[test]
    fn stj_string_escapes_control_and_short_escapes() {
        // C0 controls: those with a short escape use it; others → \uXXXX (upper).
        let mut out = String::new();
        stj_string("\u{08}\t\n\u{0C}\r\\\u{01}\u{1F}", &mut out);
        assert_eq!(out, "\"\\b\\t\\n\\f\\r\\\\\\u0001\\u001F\"");
    }

    #[test]
    fn stj_string_emits_surrogate_pair_for_astral_scalar() {
        // U+1F600 (astral) → two \uXXXX units (surrogate pair), uppercase hex.
        let mut out = String::new();
        stj_string("\u{1F600}", &mut out);
        assert_eq!(out, "\"\\uD83D\\uDE00\"");
    }

    #[test]
    fn stj_string_leaves_safe_ascii_and_slash_literal() {
        // Printable ASCII except the STJ set stays literal — including `/`.
        let mut out = String::new();
        stj_string("Az0 /:=~!", &mut out);
        assert_eq!(out, "\"Az0 /:=~!\"");
    }

    #[test]
    fn round_trips_through_frame_and_parse() {
        let signal = Signal {
            from_uhid: "n1".into(),
            to_uhid: "n2".into(),
            signal_type: SignalType::Answer,
            sdp: Some("answer-sdp".into()),
            candidate: None,
            sdp_mid: None,
            sdp_mline_index: 7,
        };
        let back = parse_frame(&frame_signal(&signal)).expect("parses");
        assert_eq!(back.from_uhid, "n1");
        assert_eq!(back.to_uhid, "n2");
        assert_eq!(back.signal_type, SignalType::Answer);
        assert_eq!(back.sdp.as_deref(), Some("answer-sdp"));
        assert_eq!(back.sdp_mline_index, 7);
    }

    #[test]
    fn non_aws1_bytes_are_ignored() {
        assert!(parse_frame(b"ordinary app data").is_none());
        assert!(parse_frame(b"AWS").is_none()); // too short
        assert!(parse_frame(b"AWX1{}").is_none()); // wrong magic
        assert!(parse_frame(b"").is_none());
    }

    #[test]
    fn parse_tolerates_a_malformed_body_after_valid_magic() {
        // AWS1 magic but garbage JSON body → ignored, not a panic.
        assert!(parse_frame(b"AWS1not-json").is_none());
    }
}
