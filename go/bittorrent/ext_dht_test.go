// SPDX-License-Identifier: MIT

package bittorrent

import (
	"bytes"
	"crypto/sha1"
	"net"
	"testing"
)

func TestNodeID_XorDistance(t *testing.T) {
	var a, b NodeID
	a[0] = 0xF0
	b[0] = 0x0F
	d := a.DistanceTo(b)
	if d[0] != 0xFF {
		t.Fatalf("distance byte0 0x%02x", d[0])
	}
	var same NodeID
	same[19] = 1
	if same.DistanceTo(same).LeadingZeros() != 160 {
		t.Fatalf("distance to self should be all zero")
	}
}

func TestCompactNode_ByteExactRoundtrip(t *testing.T) {
	var id NodeID
	for i := range id {
		id[i] = byte(i)
	}
	c := DhtContact{ID: id, IP: net.IPv4(1, 2, 3, 4), Port: 6881}
	enc := EncodeCompactNodes([]DhtContact{c})
	if len(enc) != 26 {
		t.Fatalf("len %d", len(enc))
	}
	if !bytes.Equal(enc[20:24], []byte{1, 2, 3, 4}) {
		t.Fatalf("ip % x", enc[20:24])
	}
	if !bytes.Equal(enc[24:26], []byte{0x1a, 0xe1}) {
		t.Fatalf("port % x", enc[24:26])
	}
	back, err := DecodeCompactNodes(enc)
	if err != nil || len(back) != 1 || back[0].Port != 6881 || back[0].ID != id || !back[0].IP.Equal(net.IPv4(1, 2, 3, 4)) {
		t.Fatalf("node roundtrip %v", err)
	}
}

func TestCompactPeers_Roundtrip(t *testing.T) {
	peers := []PeerAddr{{net.IPv4(10, 0, 0, 1), 1000}, {net.IPv4(8, 8, 8, 8), 2000}}
	enc := EncodeCompactPeers(peers)
	if len(enc) != 12 {
		t.Fatalf("len %d", len(enc))
	}
	back, err := DecodeCompactPeers(enc)
	if err != nil || len(back) != 2 || back[1].Port != 2000 || !back[1].IP.Equal(net.IPv4(8, 8, 8, 8)) {
		t.Fatalf("peers roundtrip %v", err)
	}
}

func TestRoutingTable_AddAndClosest(t *testing.T) {
	var self NodeID
	rt := NewRoutingTable(self)
	for i := 1; i <= 20; i++ {
		var id NodeID
		id[0] = byte(i)
		rt.TryAdd(DhtContact{ID: id, IP: net.IPv4(127, 0, 0, 1), Port: uint16(6000 + i)})
	}
	if rt.Count() == 0 {
		t.Fatalf("routing table empty")
	}
	var target NodeID
	target[0] = 1
	closest := rt.ClosestTo(target, 3)
	if len(closest) == 0 || closest[0].ID[0] != 1 {
		t.Fatalf("closest to target should be id starting 0x01, got %v", closest)
	}
}

func TestKrpc_QueryRoundtripAndDeterministic(t *testing.T) {
	args := NewBDict()
	_ = args.Add("id", BStr(bytes.Repeat([]byte{0xAA}, 20)))
	_ = args.Add("info_hash", BStr(bytes.Repeat([]byte{0xBB}, 20)))
	m := KrpcMessage{TransactionID: []byte("aa"), Type: KrpcQuery, Method: "get_peers", Arguments: args}

	enc, err := m.Encode()
	if err != nil {
		t.Fatal(err)
	}
	enc2, _ := m.Encode()
	if !bytes.Equal(enc, enc2) {
		t.Fatalf("KRPC encode not deterministic")
	}
	dec, err := DecodeKrpc(enc)
	if err != nil {
		t.Fatal(err)
	}
	if dec.Type != KrpcQuery || dec.Method != "get_peers" || string(dec.TransactionID) != "aa" {
		t.Fatalf("decoded query wrong: %+v", dec)
	}
	ih, _ := dec.Arguments.Get("info_hash")
	ihb, _ := AsBytes(ih)
	if !bytes.Equal(ihb, bytes.Repeat([]byte{0xBB}, 20)) {
		t.Fatalf("info_hash arg mismatch")
	}
}

func TestKrpc_ErrorRoundtrip(t *testing.T) {
	m := KrpcMessage{TransactionID: []byte("zz"), Type: KrpcError, ErrorCode: 201, ErrorMessage: "Generic Error"}
	enc, _ := m.Encode()
	dec, err := DecodeKrpc(enc)
	if err != nil || dec.Type != KrpcError || dec.ErrorCode != 201 || dec.ErrorMessage != "Generic Error" {
		t.Fatalf("error roundtrip %v %+v", err, dec)
	}
}

func TestExtensions_Handshake(t *testing.T) {
	payload := BuildExtensionHandshake(map[string]int{"ut_metadata": 1, "ut_pex": 2}, 1024)
	sub, body, err := SplitExtended(payload)
	if err != nil || sub != ExtensionHandshakeID {
		t.Fatalf("split %v sub=%d", err, sub)
	}
	h, err := ParseExtensionHandshake(body)
	if err != nil || h.MetadataMessageID() != 1 || h.PexMessageID() != 2 || h.MetadataSize != 1024 {
		t.Fatalf("handshake parse %v %+v", err, h)
	}
}

func TestExtensions_UtMetadata(t *testing.T) {
	req, _ := ParseMetadata(BuildMetadataRequest(3))
	if req.Type != MetadataRequest || req.Piece != 3 {
		t.Fatalf("request %+v", req)
	}
	data, _ := ParseMetadata(BuildMetadataData(0, 100, []byte{1, 2, 3}))
	if data.Type != MetadataData || data.Piece != 0 || data.TotalSize != 100 || !bytes.Equal(data.Data, []byte{1, 2, 3}) {
		t.Fatalf("data %+v", data)
	}
	rej, _ := ParseMetadata(BuildMetadataReject(5))
	if rej.Type != MetadataReject || rej.Piece != 5 {
		t.Fatalf("reject %+v", rej)
	}
}

func TestExtensions_MetadataAssemblerVerifies(t *testing.T) {
	info := []byte("d4:name6:v1.bine") // a small bencode-ish info blob
	ih := sha1.Sum(info)
	asm := NewMetadataAssembler(len(info))
	asm.Add(0, info)
	out, ok := asm.TryFinish(ih)
	if !ok || !bytes.Equal(out, info) {
		t.Fatalf("assembler should finish + verify")
	}
	var wrong [20]byte
	if _, ok := asm.TryFinish(wrong); ok {
		t.Fatalf("assembler should reject wrong info-hash")
	}
}

func TestExtensions_Pex(t *testing.T) {
	peers := []PeerAddr{{net.IPv4(1, 2, 3, 4), 1000}, {net.IPv4(5, 6, 7, 8), 2000}}
	got, err := ParsePexAdded(BuildPexAdded(peers))
	if err != nil || len(got) != 2 || got[0].Port != 1000 || !got[0].IP.Equal(net.IPv4(1, 2, 3, 4)) {
		t.Fatalf("pex roundtrip %v %v", err, got)
	}
}
