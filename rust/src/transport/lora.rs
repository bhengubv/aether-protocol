// SPDX-License-Identifier: MIT

//! Real LoRa (Aether Red / CircleLink) transport over a serial-attached LoRa module that speaks the
//! RYLR-class AT command set (Reyax RYLR896/RYLR998 and compatibles) on an SX127x/SX126x radio.
//! Mirrors the C# `LoRaSerialTransportService` and the Go `lora` package.
//!
//! Verification status: this is a real driver and it compiles, but it is runtime-UNVERIFIED — it has
//! not been exercised against a physical module. `is_available` reflects whether the configured serial
//! port actually opened.

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use async_trait::async_trait;

use super::{PerTransportMetrics, TransportService};

/// Configuration for a RYLR-class serial LoRa module.
#[derive(Clone)]
pub struct LoRaOptions {
    pub port_name: String,
    pub baud_rate: u32,
    pub address: u16,
    pub network_id: i32,
    pub band_hz: i64,
    pub spreading_factor: i32,
    pub bandwidth_index: i32,
    pub coding_rate: i32,
    pub preamble_length: i32,
}

impl LoRaOptions {
    /// Defaults: 115200 baud, address 1, EU868, SF9/BW125.
    pub fn new(port_name: impl Into<String>) -> Self {
        LoRaOptions {
            port_name: port_name.into(),
            baud_rate: 115_200,
            address: 1,
            network_id: 18,
            band_hz: 868_500_000,
            spreading_factor: 9,
            bandwidth_index: 7,
            coding_rate: 1,
            preamble_length: 12,
        }
    }
}

type DataHandler = Box<dyn Fn(&str, &[u8]) + Send + Sync>;

/// LoRa transport implementing [`TransportService`] over a serial module.
pub struct LoRaSerialTransport {
    opts: LoRaOptions,
    metrics: Arc<PerTransportMetrics>,
    inner: Arc<Mutex<Inner>>,
    handler: Arc<Mutex<Option<Arc<DataHandler>>>>,
    peers: Arc<Mutex<HashMap<String, u16>>>,
}

struct Inner {
    port: Option<Box<dyn serialport::SerialPort>>,
    available: bool,
}

impl LoRaSerialTransport {
    pub fn new(opts: LoRaOptions) -> Self {
        LoRaSerialTransport {
            opts,
            metrics: PerTransportMetrics::new(),
            inner: Arc::new(Mutex::new(Inner { port: None, available: false })),
            handler: Arc::new(Mutex::new(None)),
            peers: Arc::new(Mutex::new(HashMap::new())),
        }
    }

    /// Opens the serial port, configures the radio, and starts the read thread.
    pub fn open(&self) -> Result<(), Box<dyn std::error::Error>> {
        let mut inner = self.inner.lock().unwrap();
        if inner.available {
            return Ok(());
        }
        let port = serialport::new(&self.opts.port_name, self.opts.baud_rate)
            .timeout(Duration::from_millis(2000))
            .open()?;

        // Configure the radio on a cloned handle.
        let mut cfg = port.try_clone()?;
        for cmd in self.config_commands() {
            let _ = cfg.write_all(cmd.as_bytes());
            let _ = cfg.write_all(b"\r\n");
        }

        // Read loop on its own cloned handle.
        let read_port = port.try_clone()?;
        let handler = Arc::clone(&self.handler);
        std::thread::spawn(move || read_loop(read_port, handler));

        inner.port = Some(port);
        inner.available = true;
        Ok(())
    }

    fn config_commands(&self) -> Vec<String> {
        vec![
            format!("AT+ADDRESS={}", self.opts.address),
            format!("AT+NETWORKID={}", self.opts.network_id),
            format!("AT+BAND={}", self.opts.band_hz),
            format!(
                "AT+PARAMETER={},{},{},{}",
                self.opts.spreading_factor,
                self.opts.bandwidth_index,
                self.opts.coding_rate,
                self.opts.preamble_length
            ),
        ]
    }

    /// Maps an AetherNet peer UHID to a numeric LoRa node address (1–65535) for directed sends.
    pub fn register_peer(&self, peer_uhid: &str, address: u16) {
        if peer_uhid.is_empty() {
            return;
        }
        self.peers.lock().unwrap().insert(peer_uhid.to_string(), address);
    }

    /// Stops the transport and closes the serial port.
    pub fn close(&self) {
        let mut inner = self.inner.lock().unwrap();
        inner.available = false;
        inner.port = None; // drop closes the port; the read thread's clone errors and exits
    }
}

fn read_loop(port: Box<dyn serialport::SerialPort>, handler: Arc<Mutex<Option<Arc<DataHandler>>>>) {
    let mut reader = BufReader::new(port);
    let mut line = String::new();
    loop {
        match reader.read_line(&mut line) {
            Ok(0) => break, // EOF — port closed
            Ok(_) => {
                handle_line(line.trim(), &handler);
                line.clear();
            }
            Err(ref e) if e.kind() == std::io::ErrorKind::TimedOut => {
                // No full line yet; keep the partial, but cap runaway accumulation on line noise.
                if line.len() > 4096 {
                    line.clear();
                }
            }
            Err(_) => break, // port closed / fatal error
        }
    }
}

fn handle_line(line: &str, handler: &Arc<Mutex<Option<Arc<DataHandler>>>>) {
    // RYLR inbound frame: +RCV=<address>,<length>,<hexdata>,<rssi>,<snr>
    let Some(body) = line.strip_prefix("+RCV=") else {
        return;
    };
    let parts: Vec<&str> = body.split(',').collect();
    if parts.len() < 3 {
        return;
    }
    let Ok(addr) = parts[0].parse::<u16>() else {
        return;
    };
    let Ok(data) = hex_decode(parts[2]) else {
        return;
    };
    let h = handler.lock().unwrap().clone();
    if let Some(h) = h {
        h(&addr.to_string(), &data);
    }
}

fn hex_encode(data: &[u8]) -> String {
    let mut s = String::with_capacity(data.len() * 2);
    for b in data {
        s.push_str(&format!("{b:02X}"));
    }
    s
}

fn hex_decode(s: &str) -> Result<Vec<u8>, ()> {
    if s.len() % 2 != 0 {
        return Err(());
    }
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).map_err(|_| ()))
        .collect()
}

#[async_trait]
impl TransportService for LoRaSerialTransport {
    fn name(&self) -> &str {
        "Aether Red (LoRa/CircleLink)"
    }
    fn is_available(&self) -> bool {
        self.inner.lock().unwrap().available
    }
    fn max_bandwidth_bps(&self) -> i64 {
        37_500
    }
    fn max_range_meters(&self) -> i32 {
        15_000
    }
    fn power_cost_relative(&self) -> i32 {
        8
    }
    fn max_concurrent_peers(&self) -> i32 {
        255
    }

    async fn send_async(
        &self,
        peer_uhid: &str,
        data: &[u8],
    ) -> Result<bool, Box<dyn std::error::Error>> {
        if data.is_empty() {
            return Ok(false);
        }
        let addr = self.peers.lock().unwrap().get(peer_uhid).copied().unwrap_or(0); // 0 = broadcast
        let payload = hex_encode(data);
        let cmd = format!("AT+SEND={},{},{}\r\n", addr, payload.len(), payload);

        let mut inner = self.inner.lock().unwrap();
        if !inner.available {
            return Ok(false);
        }
        let Some(port) = inner.port.as_mut() else {
            return Ok(false);
        };
        match port.write_all(cmd.as_bytes()) {
            Ok(()) => {
                self.metrics.record_sample(0, true, data.len() as u64);
                Ok(true)
            }
            Err(e) => {
                self.metrics.record_sample(0, false, 0);
                Err(Box::new(e))
            }
        }
    }

    async fn send_stream_async(
        &self,
        peer_uhid: &str,
        stream: &mut (dyn Read + Send + Unpin),
    ) -> Result<bool, Box<dyn std::error::Error>> {
        let mut buf = Vec::new();
        stream.read_to_end(&mut buf)?;
        self.send_async(peer_uhid, &buf).await
    }

    fn is_connected(&self, _peer_uhid: &str) -> bool {
        self.is_available() // connectionless broadcast medium
    }

    fn set_data_received_handler(&mut self, handler: Box<dyn Fn(&str, &[u8]) + Send + Sync>) {
        *self.handler.lock().unwrap() = Some(Arc::new(handler));
    }

    fn metrics(&self) -> Option<Arc<PerTransportMetrics>> {
        Some(Arc::clone(&self.metrics))
    }
}
