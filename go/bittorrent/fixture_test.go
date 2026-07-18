// SPDX-License-Identifier: MIT

package bittorrent

import (
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

// Cross-language BitTorrent fixture verifier: asserts this implementation reproduces
// every vector in fixtures/bittorrent/vectors.json byte-for-byte. Every language SDK
// ships the equivalent test; any wire drift fails on the language that diverges.

type btCorpus struct {
	BencodeRoundtrip []string `json:"bencode_roundtrip"`
	InfoHash         []struct {
		Name        string `json:"name"`
		Size        int    `json:"size"`
		Mult        int    `json:"mult"`
		Add         int    `json:"add"`
		NameStr     string `json:"name_str"`
		PieceLength int    `json:"piece_length"`
		InfoHashHex string `json:"info_hash_hex"`
	} `json:"info_hash"`
	PeerMessages []struct {
		Name    string `json:"name"`
		Kind    string `json:"kind"`
		A       uint32 `json:"a"`
		B       uint32 `json:"b"`
		C       uint32 `json:"c"`
		WireHex string `json:"wire_hex"`
	} `json:"peer_messages"`
	UtpPackets []struct {
		Name       string `json:"name"`
		Type       int    `json:"type"`
		ConnID     uint16 `json:"conn_id"`
		Timestamp  uint32 `json:"timestamp"`
		TimestampD uint32 `json:"timestamp_diff"`
		Window     uint32 `json:"window"`
		Seq        uint16 `json:"seq"`
		Ack        uint16 `json:"ack"`
		PayloadHex string `json:"payload_hex"`
		WireHex    string `json:"wire_hex"`
	} `json:"utp_packets"`
	Merkle []struct {
		Name    string `json:"name"`
		Size    int    `json:"size"`
		Mult    int    `json:"mult"`
		Add     int    `json:"add"`
		RootHex string `json:"root_hex"`
	} `json:"merkle"`
	Compact []struct {
		Name    string `json:"name"`
		Kind    string `json:"kind"`
		WireHex string `json:"wire_hex"`
	} `json:"compact"`
	Krpc []struct {
		Name         string `json:"name"`
		Kind         string `json:"kind"`
		TxHex        string `json:"tx_hex"`
		IDHex        string `json:"id_hex"`
		InfoHashHex  string `json:"info_hash_hex"`
		ErrorCode    int64  `json:"error_code"`
		ErrorMessage string `json:"error_message"`
		WireHex      string `json:"wire_hex"`
	} `json:"krpc"`
}

func loadCorpus(t *testing.T) btCorpus {
	t.Helper()
	_, here, _, _ := runtime.Caller(0)
	root := filepath.Dir(filepath.Dir(filepath.Dir(here))) // go/bittorrent → repo root
	raw, err := os.ReadFile(filepath.Join(root, "fixtures", "bittorrent", "vectors.json"))
	if err != nil {
		t.Fatalf("read vectors.json: %v", err)
	}
	var c btCorpus
	if err := json.Unmarshal(raw, &c); err != nil {
		t.Fatalf("parse vectors.json: %v", err)
	}
	return c
}

func fillBytes(n, mult, add int) []byte {
	b := make([]byte, n)
	for i := range b {
		b[i] = byte(i*mult + add)
	}
	return b
}

func TestFixtures_Bencode(t *testing.T) {
	for _, hs := range loadCorpus(t).BencodeRoundtrip {
		raw, _ := hex.DecodeString(hs)
		v, err := Decode(raw)
		if err != nil {
			t.Fatalf("decode %s: %v", hs, err)
		}
		if got := hex.EncodeToString(Encode(v)); got != hs {
			t.Fatalf("bencode roundtrip %s -> %s", hs, got)
		}
	}
}

func TestFixtures_InfoHash(t *testing.T) {
	for _, ic := range loadCorpus(t).InfoHash {
		tb, err := BuildSingleFileTorrent(ic.NameStr, fillBytes(ic.Size, ic.Mult, ic.Add), ic.PieceLength, "")
		if err != nil {
			t.Fatal(err)
		}
		m, err := ParseTorrent(tb)
		if err != nil {
			t.Fatal(err)
		}
		if m.InfoHashV1Hex() != ic.InfoHashHex {
			t.Fatalf("%s info-hash %s want %s", ic.Name, m.InfoHashV1Hex(), ic.InfoHashHex)
		}
	}
}

func TestFixtures_PeerMessages(t *testing.T) {
	for _, pm := range loadCorpus(t).PeerMessages {
		var msg PeerMessage
		switch pm.Kind {
		case "keepalive":
			msg = KeepAlive()
		case "choke":
			msg = Choke()
		case "unchoke":
			msg = Unchoke()
		case "interested":
			msg = Interested()
		case "have":
			msg = Have(pm.A)
		case "request":
			msg = Request(pm.A, pm.B, pm.C)
		case "port":
			msg = Port(uint16(pm.A))
		default:
			t.Fatalf("unknown kind %s", pm.Kind)
		}
		if got := hex.EncodeToString(msg.ToBytes()); got != pm.WireHex {
			t.Fatalf("%s wire %s want %s", pm.Name, got, pm.WireHex)
		}
	}
}

func TestFixtures_Utp(t *testing.T) {
	for _, uc := range loadCorpus(t).UtpPackets {
		payload, _ := hex.DecodeString(uc.PayloadHex)
		p := UtpPacket{
			Type: UtpPacketType(uc.Type), ConnectionID: uc.ConnID, TimestampMicros: uc.Timestamp,
			TimestampDiff: uc.TimestampD, WindowSize: uc.Window, SeqNr: uc.Seq, AckNr: uc.Ack, Payload: payload,
		}
		if got := hex.EncodeToString(p.ToBytes()); got != uc.WireHex {
			t.Fatalf("%s wire %s want %s", uc.Name, got, uc.WireHex)
		}
	}
}

func TestFixtures_Merkle(t *testing.T) {
	for _, mc := range loadCorpus(t).Merkle {
		if got := hex.EncodeToString(MerkleRoot(fillBytes(mc.Size, mc.Mult, mc.Add))); got != mc.RootHex {
			t.Fatalf("%s root %s want %s", mc.Name, got, mc.RootHex)
		}
	}
}

func TestFixtures_Compact(t *testing.T) {
	for _, cc := range loadCorpus(t).Compact {
		wire, _ := hex.DecodeString(cc.WireHex)
		var reencoded []byte
		switch cc.Kind {
		case "node":
			nodes, err := DecodeCompactNodes(wire)
			if err != nil {
				t.Fatal(err)
			}
			reencoded = EncodeCompactNodes(nodes)
		case "peers":
			peers, err := DecodeCompactPeers(wire)
			if err != nil {
				t.Fatal(err)
			}
			reencoded = EncodeCompactPeers(peers)
		}
		if hex.EncodeToString(reencoded) != cc.WireHex {
			t.Fatalf("%s compact roundtrip mismatch", cc.Name)
		}
	}
}

func TestFixtures_Krpc(t *testing.T) {
	for _, kc := range loadCorpus(t).Krpc {
		tx, _ := hex.DecodeString(kc.TxHex)
		var m KrpcMessage
		switch kc.Kind {
		case "get_peers":
			id, _ := hex.DecodeString(kc.IDHex)
			ih, _ := hex.DecodeString(kc.InfoHashHex)
			args := NewBDict()
			_ = args.Add("id", BStr(id))
			_ = args.Add("info_hash", BStr(ih))
			m = KrpcMessage{TransactionID: tx, Type: KrpcQuery, Method: "get_peers", Arguments: args}
		case "error":
			m = KrpcMessage{TransactionID: tx, Type: KrpcError, ErrorCode: kc.ErrorCode, ErrorMessage: kc.ErrorMessage}
		}
		enc, err := m.Encode()
		if err != nil {
			t.Fatal(err)
		}
		if hex.EncodeToString(enc) != kc.WireHex {
			t.Fatalf("%s krpc %s want %s", kc.Name, hex.EncodeToString(enc), kc.WireHex)
		}
	}
}
