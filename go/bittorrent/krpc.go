// SPDX-License-Identifier: MIT

package bittorrent

import "fmt"

// KrpcType is the KRPC message kind (the "y" field).
type KrpcType int

const (
	KrpcQuery KrpcType = iota
	KrpcResponse
	KrpcError
)

// KrpcMessage is a KRPC message (BEP-5): a bencoded dict with "t" (transaction id),
// "y" (q/r/e), and one of a query ("q"+"a"), response ("r"), or error ("e").
type KrpcMessage struct {
	TransactionID []byte
	Type          KrpcType
	Method        string // query: the "q" method name
	Arguments     *BDict // query: "a"
	Response      *BDict // response: "r"
	ErrorCode     int64  // error: e[0]
	ErrorMessage  string // error: e[1]
}

// Encode serializes the message to canonical bencode.
func (m KrpcMessage) Encode() ([]byte, error) {
	d := NewBDict()
	if err := d.Add("t", BStr(m.TransactionID)); err != nil {
		return nil, err
	}
	switch m.Type {
	case KrpcQuery:
		_ = d.Add("y", BStr("q"))
		_ = d.Add("q", BStr(m.Method))
		if m.Arguments != nil {
			_ = d.Add("a", m.Arguments)
		} else {
			_ = d.Add("a", NewBDict())
		}
	case KrpcResponse:
		_ = d.Add("y", BStr("r"))
		if m.Response != nil {
			_ = d.Add("r", m.Response)
		} else {
			_ = d.Add("r", NewBDict())
		}
	case KrpcError:
		_ = d.Add("y", BStr("e"))
		e := BList{BInt(m.ErrorCode), BStr(m.ErrorMessage)}
		_ = d.Add("e", e)
	default:
		return nil, fmt.Errorf("unknown KRPC type %d", m.Type)
	}
	return Encode(d), nil
}

// DecodeKrpc parses a KRPC message.
func DecodeKrpc(data []byte) (KrpcMessage, error) {
	var m KrpcMessage
	v, err := Decode(data)
	if err != nil {
		return m, err
	}
	d, err := AsDict(v)
	if err != nil {
		return m, err
	}

	tVal, ok := d.Get("t")
	if !ok {
		return m, fmt.Errorf("KRPC message has no 't'")
	}
	if m.TransactionID, err = AsBytes(tVal); err != nil {
		return m, err
	}

	yVal, ok := d.Get("y")
	if !ok {
		return m, fmt.Errorf("KRPC message has no 'y'")
	}
	y, err := AsText(yVal)
	if err != nil {
		return m, err
	}

	switch y {
	case "q":
		m.Type = KrpcQuery
		qVal, ok := d.Get("q")
		if !ok {
			return m, fmt.Errorf("KRPC query has no 'q'")
		}
		if m.Method, err = AsText(qVal); err != nil {
			return m, err
		}
		if aVal, ok := d.Get("a"); ok {
			m.Arguments, _ = AsDict(aVal)
		}
	case "r":
		m.Type = KrpcResponse
		if rVal, ok := d.Get("r"); ok {
			m.Response, _ = AsDict(rVal)
		}
	case "e":
		m.Type = KrpcError
		if eVal, ok := d.Get("e"); ok {
			if list, err := AsList(eVal); err == nil && len(list) >= 2 {
				m.ErrorCode, _ = AsInt(list[0])
				m.ErrorMessage, _ = AsText(list[1])
			}
		}
	default:
		return m, fmt.Errorf("unknown KRPC y=%q", y)
	}
	return m, nil
}
