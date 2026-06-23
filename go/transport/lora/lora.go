// SPDX-License-Identifier: MIT

// Package lora is a real LoRa (Aether Red / CircleLink) transport over a serial-attached LoRa module
// that speaks the RYLR-class AT command set (Reyax RYLR896/RYLR998 and compatibles) on an SX127x/
// SX126x radio. It mirrors the C# LoRaSerialTransportService: it opens the serial port, configures the
// radio, sends with AT+SEND, and surfaces inbound +RCV frames.
//
// Verification status: this is a real driver and it compiles, but it is runtime-UNVERIFIED — it has
// not been exercised against a physical module. On-radio bring-up (two modules exchanging a frame) is
// the open step. IsAvailable reflects whether the configured serial port actually opened.
package lora

import (
	"bufio"
	"context"
	"encoding/hex"
	"fmt"
	"strconv"
	"strings"
	"sync"

	"go.bug.st/serial"

	"github.com/bhengubv/aether-protocol/go/transport"
)

// Options configures a RYLR-class serial LoRa module.
type Options struct {
	PortName        string // "COM5" or "/dev/ttyUSB0"
	BaudRate        int    // default 115200
	Address         uint16 // this node's LoRa address (1-65535)
	NetworkID       int    // RYLR network id
	BandHz          int64  // EU868 = 868500000; US915 = 915000000
	SpreadingFactor int    // 7-12
	BandwidthIndex  int    // 7=125kHz, 8=250, 9=500
	CodingRate      int    // 1=4/5
	PreambleLength  int
}

func (o *Options) withDefaults() {
	if o.BaudRate == 0 {
		o.BaudRate = 115200
	}
	if o.Address == 0 {
		o.Address = 1
	}
	if o.NetworkID == 0 {
		o.NetworkID = 18
	}
	if o.BandHz == 0 {
		o.BandHz = 868_500_000
	}
	if o.SpreadingFactor == 0 {
		o.SpreadingFactor = 9
	}
	if o.BandwidthIndex == 0 {
		o.BandwidthIndex = 7
	}
	if o.CodingRate == 0 {
		o.CodingRate = 1
	}
	if o.PreambleLength == 0 {
		o.PreambleLength = 12
	}
}

// LoRaSerialTransport implements transport.TransportService over a serial LoRa module.
type LoRaSerialTransport struct {
	opts    Options
	metrics *transport.PerTransportMetrics

	mu        sync.Mutex
	port      serial.Port
	available bool
	onData    func(peerUhid string, data []byte)
	peerAddrs map[string]uint16
	stop      chan struct{}
}

var _ transport.TransportService = (*LoRaSerialTransport)(nil)

// New creates a transport for the given module options (call Open to bring it up).
func New(opts Options) *LoRaSerialTransport {
	opts.withDefaults()
	return &LoRaSerialTransport{
		opts:      opts,
		metrics:   transport.NewPerTransportMetrics(),
		peerAddrs: make(map[string]uint16),
	}
}

// OnDataReceived registers the handler for inbound bytes (the receive surface).
func (t *LoRaSerialTransport) OnDataReceived(h func(peerUhid string, data []byte)) {
	t.mu.Lock()
	t.onData = h
	t.mu.Unlock()
}

// Open opens the serial port and configures the radio. Sets IsAvailable on success.
func (t *LoRaSerialTransport) Open() error {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.available {
		return nil
	}
	port, err := serial.Open(t.opts.PortName, &serial.Mode{BaudRate: t.opts.BaudRate})
	if err != nil {
		return fmt.Errorf("lora: open %s: %w", t.opts.PortName, err)
	}
	t.port = port
	t.configure(port)
	t.stop = make(chan struct{})
	t.available = true
	go t.readLoop(port, t.stop)
	return nil
}

func (t *LoRaSerialTransport) configure(port serial.Port) {
	cmds := []string{
		fmt.Sprintf("AT+ADDRESS=%d", t.opts.Address),
		fmt.Sprintf("AT+NETWORKID=%d", t.opts.NetworkID),
		fmt.Sprintf("AT+BAND=%d", t.opts.BandHz),
		fmt.Sprintf("AT+PARAMETER=%d,%d,%d,%d",
			t.opts.SpreadingFactor, t.opts.BandwidthIndex, t.opts.CodingRate, t.opts.PreambleLength),
	}
	for _, cmd := range cmds {
		_, _ = port.Write([]byte(cmd + "\r\n"))
	}
}

// ── transport.TransportService ───────────────────────────────────────────────

func (t *LoRaSerialTransport) Name() string { return "Aether Red (LoRa/CircleLink)" }

func (t *LoRaSerialTransport) IsAvailable() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.available
}

func (t *LoRaSerialTransport) MaxBandwidthBps() int64    { return 37_500 } // SF7/BW125 ≈ 37.5 kbps
func (t *LoRaSerialTransport) MaxRangeMeters() int32     { return 15_000 } // up to ~15 km LOS
func (t *LoRaSerialTransport) PowerCostRelative() int32  { return 8 }      // high TX power (1-10 scale)
func (t *LoRaSerialTransport) MaxConcurrentPeers() int32 { return 255 }

func (t *LoRaSerialTransport) Metrics() *transport.PerTransportMetrics { return t.metrics }

func (t *LoRaSerialTransport) SendAsync(_ context.Context, peerUhid string, data []byte) (bool, error) {
	if len(data) == 0 {
		return false, fmt.Errorf("lora: data cannot be empty")
	}
	t.mu.Lock()
	if !t.available || t.port == nil {
		t.mu.Unlock()
		return false, fmt.Errorf("lora: transport not available")
	}
	addr := uint16(0) // 0 = broadcast (managed-flood mesh)
	if a, ok := t.peerAddrs[peerUhid]; ok {
		addr = a
	}
	port := t.port
	t.mu.Unlock()

	payload := strings.ToUpper(hex.EncodeToString(data)) // hex so it survives the AT text protocol
	cmd := fmt.Sprintf("AT+SEND=%d,%d,%s\r\n", addr, len(payload), payload)
	if _, err := port.Write([]byte(cmd)); err != nil {
		t.metrics.RecordSample(0, false, 0)
		return false, err
	}
	t.metrics.RecordSample(0, true, int64(len(data)))
	return true, nil
}

func (t *LoRaSerialTransport) SendStreamAsync(ctx context.Context, peerUhid string, data []byte) (bool, error) {
	return t.SendAsync(ctx, peerUhid, data)
}

func (t *LoRaSerialTransport) IsConnected(string) bool { return t.IsAvailable() } // connectionless

// RegisterPeer maps an AetherNet peer UHID to a numeric LoRa node address (1-65535) for directed sends.
func (t *LoRaSerialTransport) RegisterPeer(peerUhid string, address uint16) {
	if peerUhid == "" {
		return
	}
	t.mu.Lock()
	t.peerAddrs[peerUhid] = address
	t.mu.Unlock()
}

// Close stops the read loop and closes the serial port.
func (t *LoRaSerialTransport) Close() error {
	t.mu.Lock()
	if !t.available {
		t.mu.Unlock()
		return nil
	}
	t.available = false
	if t.stop != nil {
		close(t.stop)
		t.stop = nil
	}
	port := t.port
	t.port = nil
	t.mu.Unlock()
	if port != nil {
		return port.Close()
	}
	return nil
}

func (t *LoRaSerialTransport) readLoop(port serial.Port, stop chan struct{}) {
	scanner := bufio.NewScanner(port)
	for scanner.Scan() {
		select {
		case <-stop:
			return
		default:
		}
		t.handleLine(strings.TrimSpace(scanner.Text()))
	}
}

func (t *LoRaSerialTransport) handleLine(line string) {
	// RYLR inbound frame: +RCV=<address>,<length>,<hexdata>,<rssi>,<snr>
	if !strings.HasPrefix(line, "+RCV=") {
		return
	}
	parts := strings.Split(line[5:], ",")
	if len(parts) < 3 {
		return
	}
	addr, err := strconv.ParseUint(parts[0], 10, 16)
	if err != nil {
		return
	}
	data, err := hex.DecodeString(parts[2])
	if err != nil {
		return
	}
	t.mu.Lock()
	h := t.onData
	t.mu.Unlock()
	if h != nil {
		h(strconv.FormatUint(addr, 10), data)
	}
}
