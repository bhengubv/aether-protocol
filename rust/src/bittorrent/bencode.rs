// SPDX-License-Identifier: MIT
//! Strict BEP-3 bencoding — byte-identical to the C#/Go/Python/TS AetherNet references.

use std::collections::BTreeMap;

/// A decoded bencode value. Byte strings hold raw bytes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Bencode {
    Int(i64),
    Bytes(Vec<u8>),
    List(Vec<Bencode>),
    /// Dictionary with raw byte-string keys, kept sorted (canonical) by BTreeMap.
    Dict(BTreeMap<Vec<u8>, Bencode>),
}

impl Bencode {
    pub fn as_int(&self) -> Option<i64> {
        if let Bencode::Int(i) = self { Some(*i) } else { None }
    }
    pub fn as_bytes(&self) -> Option<&[u8]> {
        if let Bencode::Bytes(b) = self { Some(b) } else { None }
    }
    pub fn as_list(&self) -> Option<&[Bencode]> {
        if let Bencode::List(l) = self { Some(l) } else { None }
    }
    pub fn as_dict(&self) -> Option<&BTreeMap<Vec<u8>, Bencode>> {
        if let Bencode::Dict(d) = self { Some(d) } else { None }
    }
}

pub fn decode(data: &[u8]) -> Result<Bencode, String> {
    let (v, n) = decode_n(data, 0)?;
    if n != data.len() {
        return Err(format!("bencode: {} trailing byte(s)", data.len() - n));
    }
    Ok(v)
}

pub fn decode_n(data: &[u8], pos: usize) -> Result<(Bencode, usize), String> {
    if pos >= data.len() {
        return Err("bencode: empty input".into());
    }
    match data[pos] {
        b'i' => decode_int(data, pos),
        b'l' => decode_list(data, pos),
        b'd' => decode_dict(data, pos),
        b'0'..=b'9' => {
            let (b, n) = decode_str(data, pos)?;
            Ok((Bencode::Bytes(b), n))
        }
        c => Err(format!("bencode: unexpected byte 0x{c:02x}")),
    }
}

fn decode_int(data: &[u8], pos: usize) -> Result<(Bencode, usize), String> {
    let end = data[pos..].iter().position(|&b| b == b'e').map(|i| pos + i)
        .ok_or("bencode: integer has no terminating 'e'")?;
    let body = &data[pos + 1..end];
    if body.is_empty() {
        return Err("bencode: empty integer".into());
    }
    if body == b"-0" {
        return Err("bencode: negative zero is not allowed".into());
    }
    let digits = if body[0] == b'-' { &body[1..] } else { body };
    if digits.is_empty() {
        return Err("bencode: bare minus sign".into());
    }
    if digits.len() > 1 && digits[0] == b'0' {
        return Err("bencode: leading zero".into());
    }
    if !digits.iter().all(|b| b.is_ascii_digit()) {
        return Err("bencode: non-digit in integer".into());
    }
    let s = std::str::from_utf8(body).map_err(|_| "bencode: bad integer")?;
    let v: i64 = s.parse().map_err(|_| "bencode: integer overflow")?;
    Ok((Bencode::Int(v), end + 1))
}

fn decode_str(data: &[u8], pos: usize) -> Result<(Vec<u8>, usize), String> {
    let colon = data[pos..].iter().position(|&b| b == b':').map(|i| pos + i)
        .ok_or("bencode: byte string has no ':'")?;
    let len_bytes = &data[pos..colon];
    if len_bytes.is_empty() {
        return Err("bencode: empty length".into());
    }
    if len_bytes.len() > 1 && len_bytes[0] == b'0' {
        return Err("bencode: leading zero in length".into());
    }
    if !len_bytes.iter().all(|b| b.is_ascii_digit()) {
        return Err("bencode: non-digit in length".into());
    }
    let n: usize = std::str::from_utf8(len_bytes).unwrap().parse().map_err(|_| "bencode: length overflow")?;
    let start = colon + 1;
    if start + n > data.len() {
        return Err("bencode: byte string runs past end".into());
    }
    Ok((data[start..start + n].to_vec(), start + n))
}

fn decode_list(data: &[u8], mut pos: usize) -> Result<(Bencode, usize), String> {
    pos += 1;
    let mut out = Vec::new();
    loop {
        if pos >= data.len() {
            return Err("bencode: list has no terminating 'e'".into());
        }
        if data[pos] == b'e' {
            return Ok((Bencode::List(out), pos + 1));
        }
        let (v, n) = decode_n(data, pos)?;
        out.push(v);
        pos = n;
    }
}

fn decode_dict(data: &[u8], mut pos: usize) -> Result<(Bencode, usize), String> {
    pos += 1;
    let mut out: BTreeMap<Vec<u8>, Bencode> = BTreeMap::new();
    let mut prev: Option<Vec<u8>> = None;
    loop {
        if pos >= data.len() {
            return Err("bencode: dictionary has no terminating 'e'".into());
        }
        if data[pos] == b'e' {
            return Ok((Bencode::Dict(out), pos + 1));
        }
        let (key, kn) = decode_str(data, pos)?;
        pos = kn;
        if let Some(p) = &prev {
            if *p == key {
                return Err("bencode: duplicate dictionary key".into());
            }
            if key < *p {
                return Err("bencode: dictionary keys are not sorted".into());
            }
        }
        prev = Some(key.clone());
        if pos >= data.len() {
            return Err("bencode: dictionary key without a value".into());
        }
        let (v, vn) = decode_n(data, pos)?;
        pos = vn;
        out.insert(key, v);
    }
}

pub fn encode(value: &Bencode) -> Vec<u8> {
    let mut out = Vec::new();
    encode_into(value, &mut out);
    out
}

fn encode_into(value: &Bencode, out: &mut Vec<u8>) {
    match value {
        Bencode::Int(i) => {
            out.push(b'i');
            out.extend_from_slice(i.to_string().as_bytes());
            out.push(b'e');
        }
        Bencode::Bytes(b) => {
            out.extend_from_slice(b.len().to_string().as_bytes());
            out.push(b':');
            out.extend_from_slice(b);
        }
        Bencode::List(l) => {
            out.push(b'l');
            for item in l {
                encode_into(item, out);
            }
            out.push(b'e');
        }
        Bencode::Dict(d) => {
            out.push(b'd');
            // BTreeMap iterates in sorted key order — canonical bencode.
            for (k, v) in d {
                out.extend_from_slice(k.len().to_string().as_bytes());
                out.push(b':');
                out.extend_from_slice(k);
                encode_into(v, out);
            }
            out.push(b'e');
        }
    }
}

/// Lowercase hex of a byte slice.
pub fn to_hex(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        s.push_str(&format!("{b:02x}"));
    }
    s
}

/// Decode a lowercase/uppercase hex string.
pub fn from_hex(s: &str) -> Vec<u8> {
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
}
