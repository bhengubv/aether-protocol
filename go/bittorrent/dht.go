// SPDX-License-Identifier: MIT

package bittorrent

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"math/bits"
	"net"
	"sort"
)

// NodeID is a 160-bit Kademlia node identifier (BEP-5).
type NodeID [20]byte

// DistanceTo returns the XOR distance between two node ids.
func (a NodeID) DistanceTo(b NodeID) NodeID {
	var d NodeID
	for i := range d {
		d[i] = a[i] ^ b[i]
	}
	return d
}

// Compare orders node ids / distances by unsigned big-endian value.
func (a NodeID) Compare(b NodeID) int { return bytes.Compare(a[:], b[:]) }

// LeadingZeros counts the leading zero bits (0..160).
func (a NodeID) LeadingZeros() int {
	for i, by := range a {
		if by != 0 {
			return i*8 + bits.LeadingZeros8(by)
		}
	}
	return 160
}

// DhtContact is a routable DHT node: its id and IPv4 endpoint.
type DhtContact struct {
	ID   NodeID
	IP   net.IP
	Port uint16
}

// PeerAddr is an IPv4 peer endpoint (compact peer, BEP-23).
type PeerAddr struct {
	IP   net.IP
	Port uint16
}

// EncodeCompactNodes serializes contacts as 26-byte records (20 id + 4 IPv4 + 2 port BE).
func EncodeCompactNodes(contacts []DhtContact) []byte {
	out := make([]byte, 0, len(contacts)*26)
	for _, c := range contacts {
		out = append(out, c.ID[:]...)
		out = append(out, c.IP.To4()...)
		var p [2]byte
		binary.BigEndian.PutUint16(p[:], c.Port)
		out = append(out, p[:]...)
	}
	return out
}

// DecodeCompactNodes parses 26-byte compact node records.
func DecodeCompactNodes(data []byte) ([]DhtContact, error) {
	if len(data)%26 != 0 {
		return nil, fmt.Errorf("compact nodes length %d is not a multiple of 26", len(data))
	}
	out := make([]DhtContact, 0, len(data)/26)
	for i := 0; i < len(data); i += 26 {
		var id NodeID
		copy(id[:], data[i:i+20])
		ip := net.IPv4(data[i+20], data[i+21], data[i+22], data[i+23])
		port := binary.BigEndian.Uint16(data[i+24 : i+26])
		out = append(out, DhtContact{ID: id, IP: ip, Port: port})
	}
	return out, nil
}

// EncodeCompactPeers serializes peers as 6-byte records (4 IPv4 + 2 port BE).
func EncodeCompactPeers(peers []PeerAddr) []byte {
	out := make([]byte, 0, len(peers)*6)
	for _, p := range peers {
		out = append(out, p.IP.To4()...)
		var b [2]byte
		binary.BigEndian.PutUint16(b[:], p.Port)
		out = append(out, b[:]...)
	}
	return out
}

// DecodeCompactPeers parses 6-byte compact peer records.
func DecodeCompactPeers(data []byte) ([]PeerAddr, error) {
	if len(data)%6 != 0 {
		return nil, fmt.Errorf("compact peers length %d is not a multiple of 6", len(data))
	}
	out := make([]PeerAddr, 0, len(data)/6)
	for i := 0; i < len(data); i += 6 {
		ip := net.IPv4(data[i], data[i+1], data[i+2], data[i+3])
		port := binary.BigEndian.Uint16(data[i+4 : i+6])
		out = append(out, PeerAddr{IP: ip, Port: port})
	}
	return out, nil
}

// DhtK is the Kademlia bucket size.
const DhtK = 8

// RoutingTable is a Kademlia routing table of 160 k-buckets indexed by shared prefix length.
type RoutingTable struct {
	self    NodeID
	buckets [160][]DhtContact
}

// NewRoutingTable creates a routing table for the local node id.
func NewRoutingTable(self NodeID) *RoutingTable { return &RoutingTable{self: self} }

func (t *RoutingTable) bucketIndex(id NodeID) int {
	lz := t.self.DistanceTo(id).LeadingZeros()
	if lz >= 160 {
		return 159
	}
	return lz
}

// TryAdd inserts or refreshes a contact; returns false if it is us or the bucket is full.
func (t *RoutingTable) TryAdd(c DhtContact) bool {
	if c.ID == t.self {
		return false
	}
	idx := t.bucketIndex(c.ID)
	b := t.buckets[idx]
	for i := range b {
		if b[i].ID == c.ID {
			t.buckets[idx][i] = c
			return true
		}
	}
	if len(b) < DhtK {
		t.buckets[idx] = append(b, c)
		return true
	}
	return false
}

// ClosestTo returns up to count contacts nearest to target by XOR distance.
func (t *RoutingTable) ClosestTo(target NodeID, count int) []DhtContact {
	var all []DhtContact
	for _, b := range t.buckets {
		all = append(all, b...)
	}
	sort.Slice(all, func(i, j int) bool {
		di := all[i].ID.DistanceTo(target)
		dj := all[j].ID.DistanceTo(target)
		return di.Compare(dj) < 0
	})
	if count < len(all) {
		all = all[:count]
	}
	return all
}

// Count returns the total number of contacts.
func (t *RoutingTable) Count() int {
	n := 0
	for _, b := range t.buckets {
		n += len(b)
	}
	return n
}
