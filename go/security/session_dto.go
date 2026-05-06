// SPDX-License-Identifier: MIT

package security

import (
	"encoding/json"
	"fmt"
)

// signalSessionDto is the JSON-serialisable snapshot of a SignalSession.
//
// On-disk format mirrors the C# SignalSessionDto byte-for-byte (same field
// names, same JSON layout) so that future cross-language session migration
// is possible. New fields must be added to the END of the struct so old
// snapshots keep deserialising — the on-disk format is part of the
// persistence contract.
//
// Field tags use the same short keys as the C# DTO (`rk`, `cks`, `ckr`,
// `ns`, `nr`, `pn`, `dhs_priv`, `dhs_pub`, `dhr`, `mkskipped`,
// `pending_pkmsg`, `init_ik`, `used_spk_id`, `used_opk_id`).
type signalSessionDto struct {
	RootKey                    []byte            `json:"rk"`
	SendChainKey               []byte            `json:"cks"`
	RecvChainKey               []byte            `json:"ckr"`
	SendCounter                int32             `json:"ns"`
	RecvCounter                int32             `json:"nr"`
	PreviousChainCount         int32             `json:"pn"`
	MyEphemeralPriv            []byte            `json:"dhs_priv"`
	MyEphemeralPub             []byte            `json:"dhs_pub"`
	RemoteEphemeralPub         []byte            `json:"dhr"`
	SkippedMessageKeys         map[string][]byte `json:"mkskipped"`
	PendingPreKeyMessage       bool              `json:"pending_pkmsg"`
	InitiatorIdentityKeyX25519 []byte            `json:"init_ik"`
	UsedSignedPreKeyID         int32             `json:"used_spk_id"`
	UsedOneTimePreKeyID        int32             `json:"used_opk_id"`
}

// serializeSignalSession serialises a SignalSession to JSON bytes.
// Returns nil + error if session is nil.
func serializeSignalSession(s *SignalSession) ([]byte, error) {
	if s == nil {
		return nil, fmt.Errorf("serializeSignalSession: session is nil")
	}
	// Defensive copy of the skipped-keys map: the session continues to
	// mutate after serialise.
	skipped := make(map[string][]byte, len(s.SkippedMessageKeys))
	for k, v := range s.SkippedMessageKeys {
		cp := make([]byte, len(v))
		copy(cp, v)
		skipped[k] = cp
	}
	dto := signalSessionDto{
		RootKey:                    s.RootKey,
		SendChainKey:               s.SendChainKey,
		RecvChainKey:               s.RecvChainKey,
		SendCounter:                s.SendCounter,
		RecvCounter:                s.RecvCounter,
		PreviousChainCount:         s.PreviousChainCount,
		MyEphemeralPriv:            s.MyEphemeralPriv,
		MyEphemeralPub:             s.MyEphemeralPub,
		RemoteEphemeralPub:         s.RemoteEphemeralPub,
		SkippedMessageKeys:         skipped,
		PendingPreKeyMessage:       s.PendingPreKeyMessage,
		InitiatorIdentityKeyX25519: s.InitiatorIdentityKeyX25519,
		UsedSignedPreKeyID:         s.UsedSignedPreKeyID,
		UsedOneTimePreKeyID:        s.UsedOneTimePreKeyID,
	}
	return json.Marshal(dto)
}

// deserializeSignalSession reconstructs a SignalSession from JSON bytes.
// Returns (nil, nil) for empty input and (nil, err) on malformed input.
func deserializeSignalSession(bytes []byte) (*SignalSession, error) {
	if len(bytes) == 0 {
		return nil, nil
	}
	var dto signalSessionDto
	if err := json.Unmarshal(bytes, &dto); err != nil {
		return nil, fmt.Errorf("deserializeSignalSession: %w", err)
	}
	skipped := make(map[string][]byte, len(dto.SkippedMessageKeys))
	for k, v := range dto.SkippedMessageKeys {
		skipped[k] = v
	}
	if dto.SkippedMessageKeys == nil {
		skipped = make(map[string][]byte)
	}
	return &SignalSession{
		RootKey:                    dto.RootKey,
		SendChainKey:               dto.SendChainKey,
		RecvChainKey:               dto.RecvChainKey,
		SendCounter:                dto.SendCounter,
		RecvCounter:                dto.RecvCounter,
		PreviousChainCount:         dto.PreviousChainCount,
		MyEphemeralPriv:            dto.MyEphemeralPriv,
		MyEphemeralPub:             dto.MyEphemeralPub,
		RemoteEphemeralPub:         dto.RemoteEphemeralPub,
		SkippedMessageKeys:         skipped,
		PendingPreKeyMessage:       dto.PendingPreKeyMessage,
		InitiatorIdentityKeyX25519: dto.InitiatorIdentityKeyX25519,
		UsedSignedPreKeyID:         dto.UsedSignedPreKeyID,
		UsedOneTimePreKeyID:        dto.UsedOneTimePreKeyID,
	}, nil
}
