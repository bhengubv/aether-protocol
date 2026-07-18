// SPDX-License-Identifier: MIT

package bittorrent

import (
	"crypto/sha1"
	"encoding/hex"
	"fmt"
	"strings"
)

// TorrentFileEntry is one file within a torrent: its path components and length.
type TorrentFileEntry struct {
	Path   []string
	Length int64
}

// JoinedPath returns the path components joined with '/'.
func (e TorrentFileEntry) JoinedPath() string { return strings.Join(e.Path, "/") }

// TorrentMetainfo is a parsed BitTorrent v1 metainfo (.torrent). InfoHashV1 is the
// SHA-1 of the RAW bencoded info dictionary as it appears in the file (not a re-encode),
// so it matches real clients byte-for-byte.
type TorrentMetainfo struct {
	Root         *BDict
	Info         *BDict
	InfoHashV1   [20]byte
	Name         string
	PieceLength  int64
	PieceHashes  [][]byte // each 20-byte SHA-1
	Files        []TorrentFileEntry
	TotalLength  int64
	AnnounceURLs []string
	IsSingleFile bool
}

// InfoHashV1Hex is the lowercase hex of InfoHashV1 (40 chars).
func (m *TorrentMetainfo) InfoHashV1Hex() string { return hex.EncodeToString(m.InfoHashV1[:]) }

// ParseTorrent parses .torrent bytes.
func ParseTorrent(data []byte) (*TorrentMetainfo, error) {
	rootVal, err := Decode(data)
	if err != nil {
		return nil, err
	}
	root, err := AsDict(rootVal)
	if err != nil {
		return nil, err
	}
	infoVal, ok := root.Get("info")
	if !ok {
		return nil, fmt.Errorf("metainfo has no 'info' dictionary")
	}
	info, err := AsDict(infoVal)
	if err != nil {
		return nil, err
	}

	infoSpan, err := extractInfoSpan(data)
	if err != nil {
		return nil, err
	}
	infoHash := sha1.Sum(infoSpan)

	nameVal, ok := info.Get("name")
	if !ok {
		return nil, fmt.Errorf("info has no 'name'")
	}
	name, err := AsText(nameVal)
	if err != nil {
		return nil, err
	}

	plVal, ok := info.Get("piece length")
	if !ok {
		return nil, fmt.Errorf("info has no 'piece length'")
	}
	pieceLength, err := AsInt(plVal)
	if err != nil {
		return nil, err
	}
	if pieceLength <= 0 {
		return nil, fmt.Errorf("'piece length' must be positive")
	}

	piecesVal, ok := info.Get("pieces")
	if !ok {
		return nil, fmt.Errorf("info has no 'pieces'")
	}
	piecesBytes, err := AsBytes(piecesVal)
	if err != nil {
		return nil, err
	}
	if len(piecesBytes)%20 != 0 {
		return nil, fmt.Errorf("'pieces' length %d is not a multiple of 20", len(piecesBytes))
	}
	pieceHashes := make([][]byte, 0, len(piecesBytes)/20)
	for i := 0; i < len(piecesBytes); i += 20 {
		h := make([]byte, 20)
		copy(h, piecesBytes[i:i+20])
		pieceHashes = append(pieceHashes, h)
	}

	var files []TorrentFileEntry
	var total int64
	singleFile := false
	if filesVal, ok := info.Get("files"); ok {
		list, err := AsList(filesVal)
		if err != nil {
			return nil, err
		}
		for _, f := range list {
			fd, err := AsDict(f)
			if err != nil {
				return nil, err
			}
			lenVal, ok := fd.Get("length")
			if !ok {
				return nil, fmt.Errorf("file entry has no 'length'")
			}
			length, err := AsInt(lenVal)
			if err != nil {
				return nil, err
			}
			pathVal, ok := fd.Get("path")
			if !ok {
				return nil, fmt.Errorf("file entry has no 'path'")
			}
			pathList, err := AsList(pathVal)
			if err != nil {
				return nil, err
			}
			parts := make([]string, 0, len(pathList))
			for _, p := range pathList {
				s, err := AsText(p)
				if err != nil {
					return nil, err
				}
				parts = append(parts, s)
			}
			if len(parts) == 0 {
				return nil, fmt.Errorf("file entry has an empty 'path'")
			}
			files = append(files, TorrentFileEntry{Path: parts, Length: length})
			total += length
		}
	} else {
		singleFile = true
		lenVal, ok := info.Get("length")
		if !ok {
			return nil, fmt.Errorf("single-file info has neither 'length' nor 'files'")
		}
		length, err := AsInt(lenVal)
		if err != nil {
			return nil, err
		}
		files = append(files, TorrentFileEntry{Path: []string{name}, Length: length})
		total = length
	}

	// Trackers: announce + announce-list, de-duplicated, order preserved.
	var announce []string
	seen := map[string]bool{}
	add := func(u string) {
		if u != "" && !seen[u] {
			seen[u] = true
			announce = append(announce, u)
		}
	}
	if a, ok := root.Get("announce"); ok {
		if s, err := AsText(a); err == nil {
			add(s)
		}
	}
	if al, ok := root.Get("announce-list"); ok {
		if tiers, err := AsList(al); err == nil {
			for _, tier := range tiers {
				if ts, err := AsList(tier); err == nil {
					for _, t := range ts {
						if s, err := AsText(t); err == nil {
							add(s)
						}
					}
				}
			}
		}
	}

	m := &TorrentMetainfo{
		Root:         root,
		Info:         info,
		Name:         name,
		PieceLength:  pieceLength,
		PieceHashes:  pieceHashes,
		Files:        files,
		TotalLength:  total,
		AnnounceURLs: announce,
		IsSingleFile: singleFile,
	}
	copy(m.InfoHashV1[:], infoHash[:])
	return m, nil
}

// extractInfoSpan returns the raw bencoded bytes of the top-level "info" value by
// walking the dictionary with byte-offset tracking (structure already validated).
func extractInfoSpan(data []byte) ([]byte, error) {
	if len(data) == 0 || data[0] != 'd' {
		return nil, fmt.Errorf("metainfo is not a bencoded dictionary")
	}
	pos := 1
	for pos < len(data) && data[pos] != 'e' {
		keyVal, keyN, err := DecodeN(data[pos:])
		if err != nil {
			return nil, err
		}
		key, err := AsBytes(keyVal)
		if err != nil {
			return nil, fmt.Errorf("dictionary key is not a byte string")
		}
		pos += keyN
		valStart := pos
		_, valN, err := DecodeN(data[pos:])
		if err != nil {
			return nil, err
		}
		valEnd := pos + valN
		pos = valEnd
		if string(key) == "info" {
			return data[valStart:valEnd], nil
		}
	}
	return nil, fmt.Errorf("metainfo has no 'info' key")
}

// BuildSingleFileTorrent creates single-file .torrent bytes for data, splitting into
// pieceLength-byte pieces and SHA-1-hashing each. Byte-identical to the C# TorrentBuilder.
func BuildSingleFileTorrent(name string, data []byte, pieceLength int, announce string) ([]byte, error) {
	if name == "" {
		return nil, fmt.Errorf("name is required")
	}
	if pieceLength <= 0 {
		return nil, fmt.Errorf("piece length must be positive")
	}
	pieceCount := (len(data) + pieceLength - 1) / pieceLength
	pieces := make([]byte, pieceCount*20)
	for i := 0; i < pieceCount; i++ {
		start := i * pieceLength
		end := start + pieceLength
		if end > len(data) {
			end = len(data)
		}
		h := sha1.Sum(data[start:end])
		copy(pieces[i*20:], h[:])
	}

	info := NewBDict()
	_ = info.Add("length", BInt(int64(len(data))))
	_ = info.Add("name", BStr(name))
	_ = info.Add("piece length", BInt(int64(pieceLength)))
	_ = info.Add("pieces", BStr(pieces))

	root := NewBDict()
	if strings.TrimSpace(announce) != "" {
		_ = root.Add("announce", BStr(announce))
	}
	_ = root.Add("info", info)
	return Encode(root), nil
}
