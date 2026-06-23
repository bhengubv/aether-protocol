// SPDX-License-Identifier: MIT

import Foundation
#if canImport(Glibc)
import Glibc
private func cOpen(_ path: UnsafePointer<CChar>, _ flags: Int32) -> Int32 { Glibc.open(path, flags) }
private func cClose(_ fd: Int32) -> Int32 { Glibc.close(fd) }
private func cRead(_ fd: Int32, _ buf: UnsafeMutableRawPointer, _ n: Int) -> Int { Glibc.read(fd, buf, n) }
private func cWrite(_ fd: Int32, _ buf: UnsafeRawPointer, _ n: Int) -> Int { Glibc.write(fd, buf, n) }
#elseif canImport(Darwin)
import Darwin
private func cOpen(_ path: UnsafePointer<CChar>, _ flags: Int32) -> Int32 { Darwin.open(path, flags) }
private func cClose(_ fd: Int32) -> Int32 { Darwin.close(fd) }
private func cRead(_ fd: Int32, _ buf: UnsafeMutableRawPointer, _ n: Int) -> Int { Darwin.read(fd, buf, n) }
private func cWrite(_ fd: Int32, _ buf: UnsafeRawPointer, _ n: Int) -> Int { Darwin.write(fd, buf, n) }
#endif

/// Configuration for a RYLR-class serial LoRa module.
public struct LoRaOptions {
    public var portName: String          // "/dev/ttyUSB0" — required
    public var baudRate: Int32
    public var address: UInt16           // this node's LoRa address (1-65535)
    public var networkId: Int
    public var bandHz: Int64             // EU868 = 868500000; US915 = 915000000
    public var spreadingFactor: Int      // 7-12
    public var bandwidthIndex: Int       // 7=125kHz, 8=250, 9=500
    public var codingRate: Int           // 1=4/5
    public var preambleLength: Int

    public init(portName: String, baudRate: Int32 = 115200, address: UInt16 = 1, networkId: Int = 18,
                bandHz: Int64 = 868_500_000, spreadingFactor: Int = 9, bandwidthIndex: Int = 7,
                codingRate: Int = 1, preambleLength: Int = 12) {
        self.portName = portName
        self.baudRate = baudRate
        self.address = address
        self.networkId = networkId
        self.bandHz = bandHz
        self.spreadingFactor = spreadingFactor
        self.bandwidthIndex = bandwidthIndex
        self.codingRate = codingRate
        self.preambleLength = preambleLength
    }
}

/// Real LoRa (Aether Red / CircleLink) transport over a serial-attached RYLR-class SX127x/SX126x
/// module. POSIX (termios), matching the C driver; mirrors the C#/Go/Rust drivers: opens the serial
/// port, configures the radio, sends with `AT+SEND`, and surfaces inbound `+RCV` frames.
///
/// Verification status: real driver; compiles on the Swift macOS/Linux toolchain. iOS sandboxes
/// arbitrary serial ports, so `open()` returns false there. Runtime-UNVERIFIED — not exercised
/// against a physical module.
public final class LoRaSerialTransport: TransportService, @unchecked Sendable {
    private let opts: LoRaOptions
    private let lock = NSLock()
    private let _metrics = PerTransportMetrics()

    private var fd: Int32 = -1
    private var _available = false
    private var running = false
    private var onData: ((String, Data) -> Void)?
    private var peerAddrs: [String: UInt16] = [:]
    private var readerThread: Thread?

    public init(options: LoRaOptions) {
        self.opts = options
    }

    /// Register the handler for inbound bytes (the receive surface).
    public func onDataReceived(_ callback: @escaping (String, Data) -> Void) {
        lock.withLock { onData = callback }
    }

    /// Map an AetherNet peer UHID to a numeric LoRa node address (1-65535) for directed sends.
    public func registerPeer(peerUhid: String, address: UInt16) {
        guard !peerUhid.isEmpty else { return }
        lock.withLock { peerAddrs[peerUhid] = address }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    /// Open the serial port, configure the radio, and start the reader thread. Returns false on failure.
    @discardableResult
    public func open() -> Bool {
        lock.lock()
        if _available { lock.unlock(); return true }
        let f = opts.portName.withCString { cOpen($0, O_RDWR | O_NOCTTY) }
        if f < 0 { lock.unlock(); return false }

        var tio = termios()
        if tcgetattr(f, &tio) != 0 { _ = cClose(f); lock.unlock(); return false }
        cfmakeraw(&tio)
        let speed = baudCode(opts.baudRate)
        cfsetispeed(&tio, speed)
        cfsetospeed(&tio, speed)
        tio.c_cflag |= tcflag_t(CLOCAL | CREAD)
        withUnsafeMutablePointer(to: &tio.c_cc) { ptr in
            ptr.withMemoryRebound(to: cc_t.self, capacity: Int(NCCS)) { cc in
                cc[Int(VMIN)] = 0    // non-blocking-ish: return after VTIME even with no data
                cc[Int(VTIME)] = 10  // 1.0s read timeout so the reader can poll its run flag
            }
        }
        if tcsetattr(f, TCSANOW, &tio) != 0 { _ = cClose(f); lock.unlock(); return false }

        fd = f
        configure()
        running = true
        _available = true
        lock.unlock()

        let thread = Thread { [weak self] in self?.readLoop() }
        thread.name = "lora-reader"
        thread.stackSize = 1 << 20
        readerThread = thread
        thread.start()
        return true
    }

    private func configure() {
        for cmd in [
            "AT+ADDRESS=\(opts.address)",
            "AT+NETWORKID=\(opts.networkId)",
            "AT+BAND=\(opts.bandHz)",
            "AT+PARAMETER=\(opts.spreadingFactor),\(opts.bandwidthIndex),\(opts.codingRate),\(opts.preambleLength)",
        ] {
            writeLine(cmd)
        }
    }

    private func writeLine(_ s: String) {
        let bytes = Array((s + "\r\n").utf8)
        bytes.withUnsafeBytes { raw in
            if let base = raw.baseAddress { _ = cWrite(fd, base, raw.count) }
        }
    }

    /// Close the serial port and stop the reader thread.
    public func close() {
        lock.lock()
        running = false
        _available = false
        let f = fd
        fd = -1
        lock.unlock()
        if f >= 0 { _ = cClose(f) }
    }

    // ── TransportService ───────────────────────────────────────────────────────

    public var name: String { "Aether Red (LoRa/CircleLink)" }
    public var isAvailable: Bool { lock.withLock { _available } }
    public var maxBandwidthBps: Int64 { 37_500 }     // SF7/BW125 ~= 37.5 kbps
    public var maxRangeMeters: Int32 { 15_000 }      // up to ~15 km LOS
    public var powerCostRelative: Int32 { 8 }        // high TX power (1-10 scale)
    public var maxConcurrentPeers: Int32 { 255 }
    public var metrics: PerTransportMetrics? { _metrics }

    public func sendAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken?) async -> Bool {
        if data.isEmpty { return false }
        lock.lock()
        if !_available || fd < 0 { lock.unlock(); return false }
        let addr = peerAddrs[peerUhid] ?? 0 // 0 = broadcast (managed-flood mesh)
        lock.unlock()

        // Hex-encode so the payload survives the AT text protocol; length field is the hex length.
        let hex = data.map { String(format: "%02X", $0) }.joined()
        let cmd = "AT+SEND=\(addr),\(hex.count),\(hex)\r\n"
        let bytes = Array(cmd.utf8)
        let ok = bytes.withUnsafeBytes { raw -> Bool in
            guard let base = raw.baseAddress else { return false }
            return cWrite(fd, base, raw.count) == raw.count
        }
        _metrics.recordSample(rttMs: 0, success: ok, bytesTransferred: ok ? data.count : 0)
        return ok
    }

    public func sendStreamAsync(peerUhid: String, data: Data, cancellationToken: CancellationToken?) async -> Bool {
        await sendAsync(peerUhid: peerUhid, data: data, cancellationToken: cancellationToken)
    }

    public func isConnected(peerUhid: String) -> Bool { isAvailable } // connectionless broadcast medium

    // ── Receive ────────────────────────────────────────────────────────────────

    private func readLoop() {
        var line = [UInt8]()
        var byte: UInt8 = 0
        while true {
            lock.lock(); let run = running; let f = fd; lock.unlock()
            if !run || f < 0 { break }
            let n = withUnsafeMutableBytes(of: &byte) { raw -> Int in
                guard let base = raw.baseAddress else { return -1 }
                return cRead(f, base, 1)
            }
            if n < 0 { break }
            if n == 0 { continue }
            if byte == 0x0A || byte == 0x0D { // \n or \r
                if !line.isEmpty {
                    handleLine(String(decoding: line, as: UTF8.self))
                    line.removeAll(keepingCapacity: true)
                }
            } else {
                line.append(byte)
            }
        }
    }

    private func handleLine(_ raw: String) {
        // RYLR inbound frame: +RCV=<address>,<length>,<hexdata>,<rssi>,<snr>
        let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard line.hasPrefix("+RCV=") else { return }
        let parts = line.dropFirst(5).split(separator: ",", omittingEmptySubsequences: false)
        guard parts.count >= 3, let addr = Int(parts[0]) else { return }
        guard let data = hexDecode(String(parts[2])) else { return }
        lock.lock(); let handler = onData; lock.unlock()
        handler?(String(addr), data)
    }

    private func hexDecode(_ hex: String) -> Data? {
        let chars = Array(hex)
        guard chars.count % 2 == 0 else { return nil }
        var out = Data(capacity: chars.count / 2)
        var i = 0
        while i < chars.count {
            guard let hi = chars[i].hexDigitValue, let lo = chars[i + 1].hexDigitValue else { return nil }
            out.append(UInt8(hi << 4 | lo))
            i += 2
        }
        return out
    }

    private func baudCode(_ baud: Int32) -> speed_t {
        switch baud {
        case 9600: return speed_t(B9600)
        case 19200: return speed_t(B19200)
        case 38400: return speed_t(B38400)
        case 57600: return speed_t(B57600)
        case 115200: return speed_t(B115200)
        default: return speed_t(B115200)
        }
    }
}
