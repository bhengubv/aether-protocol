// SPDX-License-Identifier: MIT

// Command bittorrentfixturegen emits fixtures/bittorrent/vectors.json — the canonical,
// byte-identical BitTorrent codec corpus every AetherNet language SDK asserts against.
// The inputs are reconstructable in every language (fill formulas + explicit fields) and
// the expected hex is computed by the Go reference implementation. The C# reference
// (already MonoTorrent-validated) cross-asserts the same corpus, double-anchoring it.
package main

import (
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"runtime"

	bt "github.com/bhengubv/aether-protocol/go/bittorrent"
)

func fill(n, mult, add int) []byte {
	b := make([]byte, n)
	for i := range b {
		b[i] = byte(i*mult + add)
	}
	return b
}

func h(b []byte) string { return hex.EncodeToString(b) }

type corpus struct {
	BencodeRoundtrip []string       `json:"bencode_roundtrip"`
	InfoHash         []infoHashCase `json:"info_hash"`
	PeerMessages     []peerMsgCase  `json:"peer_messages"`
	UtpPackets       []utpCase      `json:"utp_packets"`
	Merkle           []merkleCase   `json:"merkle"`
	Compact          []compactCase  `json:"compact"`
	Krpc             []krpcCase     `json:"krpc"`
}

type infoHashCase struct {
	Name        string `json:"name"`
	Size        int    `json:"size"`
	Mult        int    `json:"mult"`
	Add         int    `json:"add"`
	NameStr     string `json:"name_str"`
	PieceLength int    `json:"piece_length"`
	InfoHashHex string `json:"info_hash_hex"`
}

type peerMsgCase struct {
	Name    string `json:"name"`
	Kind    string `json:"kind"`
	A       uint32 `json:"a"`
	B       uint32 `json:"b"`
	C       uint32 `json:"c"`
	WireHex string `json:"wire_hex"`
}

type utpCase struct {
	Name        string `json:"name"`
	Type        int    `json:"type"`
	ConnID      uint16 `json:"conn_id"`
	Timestamp   uint32 `json:"timestamp"`
	TimestampD  uint32 `json:"timestamp_diff"`
	Window      uint32 `json:"window"`
	Seq         uint16 `json:"seq"`
	Ack         uint16 `json:"ack"`
	PayloadHex  string `json:"payload_hex"`
	WireHex     string `json:"wire_hex"`
}

type merkleCase struct {
	Name    string `json:"name"`
	Size    int    `json:"size"`
	Mult    int    `json:"mult"`
	Add     int    `json:"add"`
	RootHex string `json:"root_hex"`
}

type compactCase struct {
	Name    string `json:"name"`
	Kind    string `json:"kind"` // "node" | "peers"
	IDHex   string `json:"id_hex,omitempty"`
	Peers   []peer `json:"peers,omitempty"`
	WireHex string `json:"wire_hex"`
}

type peer struct {
	IP   string `json:"ip"`
	Port uint16 `json:"port"`
}

type krpcCase struct {
	Name         string `json:"name"`
	Kind         string `json:"kind"` // "get_peers" | "error"
	TxHex        string `json:"tx_hex"`
	IDHex        string `json:"id_hex,omitempty"`
	InfoHashHex  string `json:"info_hash_hex,omitempty"`
	ErrorCode    int64  `json:"error_code,omitempty"`
	ErrorMessage string `json:"error_message,omitempty"`
	WireHex      string `json:"wire_hex"`
}

func main() {
	var c corpus

	// ── bencode round-trip: each must decode + canonically re-encode to itself ──
	c.BencodeRoundtrip = []string{}
	for _, s := range []string{
		"i0e", "i42e", "i-42e", "4:spam", "le", "li1ei2ee",
		"de", "d3:cow3:moo4:spam4:eggse", "d4:infod6:lengthi3ee4:name3:bare",
	} {
		c.BencodeRoundtrip = append(c.BencodeRoundtrip, h([]byte(s)))
	}

	// ── info-hash (SHA-1 of raw bencoded info dict) ──
	for _, ic := range []infoHashCase{
		{Name: "single_small", Size: 43, Mult: 7, Add: 1, NameStr: "payload.bin", PieceLength: 16384},
		{Name: "multi_piece", Size: 70000, Mult: 31, Add: 7, NameStr: "movie.bin", PieceLength: 32768},
	} {
		content := fill(ic.Size, ic.Mult, ic.Add)
		tb, err := bt.BuildSingleFileTorrent(ic.NameStr, content, ic.PieceLength, "")
		must(err)
		m, err := bt.ParseTorrent(tb)
		must(err)
		ic.InfoHashHex = m.InfoHashV1Hex()
		c.InfoHash = append(c.InfoHash, ic)
	}

	// ── peer-wire message framing ──
	c.PeerMessages = []peerMsgCase{
		{Name: "keepalive", Kind: "keepalive", WireHex: h(bt.KeepAlive().ToBytes())},
		{Name: "choke", Kind: "choke", WireHex: h(bt.Choke().ToBytes())},
		{Name: "unchoke", Kind: "unchoke", WireHex: h(bt.Unchoke().ToBytes())},
		{Name: "interested", Kind: "interested", WireHex: h(bt.Interested().ToBytes())},
		{Name: "have5", Kind: "have", A: 5, WireHex: h(bt.Have(5).ToBytes())},
		{Name: "request", Kind: "request", A: 1, B: 0, C: 16384, WireHex: h(bt.Request(1, 0, 16384).ToBytes())},
		{Name: "port", Kind: "port", A: 6881, WireHex: h(bt.Port(6881).ToBytes())},
	}

	// ── µTP packets ──
	dataPkt := bt.UtpPacket{Type: bt.UtpData, ConnectionID: 0x1234, TimestampMicros: 0x00ABCDEF, WindowSize: 0x00010000, SeqNr: 5, AckNr: 4, Payload: []byte{0xDE, 0xAD}}
	synPkt := bt.UtpPacket{Type: bt.UtpSyn, ConnectionID: 42, SeqNr: 1, WindowSize: 1024}
	c.UtpPackets = []utpCase{
		{Name: "data", Type: int(bt.UtpData), ConnID: 0x1234, Timestamp: 0x00ABCDEF, Window: 0x00010000, Seq: 5, Ack: 4, PayloadHex: "dead", WireHex: h(dataPkt.ToBytes())},
		{Name: "syn", Type: int(bt.UtpSyn), ConnID: 42, Seq: 1, Window: 1024, PayloadHex: "", WireHex: h(synPkt.ToBytes())},
	}

	// ── v2 merkle roots ──
	for _, mc := range []merkleCase{
		{Name: "one_block", Size: 100, Mult: 7, Add: 1},
		{Name: "two_blocks", Size: bt.MerkleBlockSize + 50, Mult: 7, Add: 1},
		{Name: "three_blocks", Size: 2*bt.MerkleBlockSize + 10, Mult: 7, Add: 1},
	} {
		mc.RootHex = h(bt.MerkleRoot(fill(mc.Size, mc.Mult, mc.Add)))
		c.Merkle = append(c.Merkle, mc)
	}

	// ── compact node / peers ──
	var nodeID bt.NodeID
	for i := range nodeID {
		nodeID[i] = byte(i)
	}
	node := bt.DhtContact{ID: nodeID, IP: netIP(1, 2, 3, 4), Port: 6881}
	peers := []bt.PeerAddr{{IP: netIP(10, 0, 0, 1), Port: 1000}, {IP: netIP(8, 8, 8, 8), Port: 2000}}
	c.Compact = []compactCase{
		{Name: "node", Kind: "node", IDHex: h(nodeID[:]), WireHex: h(bt.EncodeCompactNodes([]bt.DhtContact{node}))},
		{Name: "peers", Kind: "peers", Peers: []peer{{"10.0.0.1", 1000}, {"8.8.8.8", 2000}}, WireHex: h(bt.EncodeCompactPeers(peers))},
	}

	// ── KRPC ──
	idHex := "aabbccddeeff00112233445566778899aabbccdd"
	infoHex := "0123456789abcdef0123456789abcdef01234567"
	idB, _ := hex.DecodeString(idHex)
	infoB, _ := hex.DecodeString(infoHex)
	args := bt.NewBDict()
	_ = args.Add("id", bt.BStr(idB))
	_ = args.Add("info_hash", bt.BStr(infoB))
	q := bt.KrpcMessage{TransactionID: []byte("aa"), Type: bt.KrpcQuery, Method: "get_peers", Arguments: args}
	qWire, err := q.Encode()
	must(err)
	e := bt.KrpcMessage{TransactionID: []byte("aa"), Type: bt.KrpcError, ErrorCode: 201, ErrorMessage: "A Generic Error Ocurred"}
	eWire, err := e.Encode()
	must(err)
	c.Krpc = []krpcCase{
		{Name: "get_peers", Kind: "get_peers", TxHex: h([]byte("aa")), IDHex: idHex, InfoHashHex: infoHex, WireHex: h(qWire)},
		{Name: "error", Kind: "error", TxHex: h([]byte("aa")), ErrorCode: 201, ErrorMessage: "A Generic Error Ocurred", WireHex: h(eWire)},
	}

	// write fixtures/bittorrent/vectors.json
	_, here, _, _ := runtime.Caller(0)
	root := filepath.Dir(filepath.Dir(filepath.Dir(filepath.Dir(here)))) // .../go/cmd/bittorrentfixturegen → repo root
	dir := filepath.Join(root, "fixtures", "bittorrent")
	must(os.MkdirAll(dir, 0o755))
	out, err := json.MarshalIndent(c, "", "  ")
	must(err)
	must(os.WriteFile(filepath.Join(dir, "vectors.json"), append(out, '\n'), 0o644))
	fmt.Printf("wrote %s (%d bencode, %d info-hash, %d peer-msg, %d utp, %d merkle, %d compact, %d krpc)\n",
		filepath.Join(dir, "vectors.json"), len(c.BencodeRoundtrip), len(c.InfoHash), len(c.PeerMessages), len(c.UtpPackets), len(c.Merkle), len(c.Compact), len(c.Krpc))
}

func netIP(a, b, cc, d byte) []byte { return []byte{a, b, cc, d} }

func must(err error) {
	if err != nil {
		panic(err)
	}
}
