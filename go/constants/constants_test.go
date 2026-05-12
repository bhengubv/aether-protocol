// SPDX-License-Identifier: MIT

package constants_test

import (
	"testing"

	"github.com/bhengubv/aether-protocol/go/constants"
)

// ── Routing constants ─────────────────────────────────────────────────────────

func TestRouting_DefaultTtl(t *testing.T) {
	if constants.DefaultTtl != 7 {
		t.Errorf("DefaultTtl: got %d, want 7", constants.DefaultTtl)
	}
}

func TestRouting_SosTtlGreaterThanDefaultTtl(t *testing.T) {
	if constants.SosTtl <= constants.DefaultTtl {
		t.Errorf("SosTtl (%d) should be > DefaultTtl (%d)", constants.SosTtl, constants.DefaultTtl)
	}
}

func TestRouting_DtnTtlGreaterThanSosTtl(t *testing.T) {
	if constants.DtnTtl <= constants.SosTtl {
		t.Errorf("DtnTtl (%d) should be > SosTtl (%d)", constants.DtnTtl, constants.SosTtl)
	}
}

func TestRouting_RouteTimeoutMs(t *testing.T) {
	if constants.RouteTimeoutMs != 5000 {
		t.Errorf("RouteTimeoutMs: got %d, want 5000", constants.RouteTimeoutMs)
	}
}

func TestRouting_RouteExpirySeconds(t *testing.T) {
	if constants.RouteExpirySeconds != 300 {
		t.Errorf("RouteExpirySeconds: got %d, want 300", constants.RouteExpirySeconds)
	}
}

// ── Security constants ────────────────────────────────────────────────────────

func TestSecurity_PacketNonceSize(t *testing.T) {
	if constants.PacketNonceSize != 8 {
		t.Errorf("PacketNonceSize: got %d, want 8", constants.PacketNonceSize)
	}
}

func TestSecurity_MaxPacketAgeSeconds(t *testing.T) {
	if constants.MaxPacketAgeSeconds != 300 {
		t.Errorf("MaxPacketAgeSeconds: got %d, want 300", constants.MaxPacketAgeSeconds)
	}
}

func TestSecurity_CurrentProtocolVersion(t *testing.T) {
	if constants.CurrentProtocolVersion != 2 {
		t.Errorf("CurrentProtocolVersion: got %d, want 2", constants.CurrentProtocolVersion)
	}
}

func TestSecurity_ProtocolVersionSignedIsCurrentVersion(t *testing.T) {
	if constants.ProtocolVersionSigned != constants.CurrentProtocolVersion {
		t.Errorf("ProtocolVersionSigned (%d) should equal CurrentProtocolVersion (%d)",
			constants.ProtocolVersionSigned, constants.CurrentProtocolVersion)
	}
}

func TestSecurity_AesGcmNonceSize(t *testing.T) {
	if constants.AesGcmNonceSize != 12 {
		t.Errorf("AesGcmNonceSize: got %d, want 12", constants.AesGcmNonceSize)
	}
}

func TestSecurity_AesGcmTagSize(t *testing.T) {
	if constants.AesGcmTagSize != 16 {
		t.Errorf("AesGcmTagSize: got %d, want 16", constants.AesGcmTagSize)
	}
}

// ── SOS constants ─────────────────────────────────────────────────────────────

// REGRESSION: SosPriority was incorrectly declared as 999, which overflows byte.
// The corrected value is 255 (max byte). Verify this never regresses.
func TestSos_SosPriorityIs255(t *testing.T) {
	if constants.SosPriority != 255 {
		t.Errorf("SosPriority: got %d, want 255 (must fit in byte; 999 is a regression)", constants.SosPriority)
	}
}

func TestSos_SosPriorityFitsInByte(t *testing.T) {
	// Compile-time guarantee: the constant is declared as byte.
	var _ byte = constants.SosPriority
}

func TestSos_MaxSosBroadcastsPerHour(t *testing.T) {
	if constants.MaxSosBroadcastsPerHour <= 0 {
		t.Errorf("MaxSosBroadcastsPerHour: got %d, want > 0", constants.MaxSosBroadcastsPerHour)
	}
}

// ── BLE constants ─────────────────────────────────────────────────────────────

func TestBle_ServiceUUIDNonEmpty(t *testing.T) {
	if constants.AetherBleServiceUUID == "" {
		t.Error("AetherBleServiceUUID should not be empty")
	}
}

func TestBle_DiscoveryIntervalPositive(t *testing.T) {
	if constants.BleDiscoveryIntervalMs <= 0 {
		t.Errorf("BleDiscoveryIntervalMs: got %d, want > 0", constants.BleDiscoveryIntervalMs)
	}
}

func TestBle_ScanOnPlusScanOffEqualsDiscoveryInterval(t *testing.T) {
	if constants.BleScanOnMs+constants.BleScanOffMs != constants.BleDiscoveryIntervalMs {
		t.Errorf("BleScanOnMs(%d) + BleScanOffMs(%d) should equal BleDiscoveryIntervalMs(%d)",
			constants.BleScanOnMs, constants.BleScanOffMs, constants.BleDiscoveryIntervalMs)
	}
}

// ── Transport constants ───────────────────────────────────────────────────────

func TestTransport_BleMaxPayloadBytesPositive(t *testing.T) {
	if constants.BleMaxPayloadBytes <= 0 {
		t.Errorf("BleMaxPayloadBytes: got %d, want > 0", constants.BleMaxPayloadBytes)
	}
}

func TestTransport_ChunkSizeOrdering(t *testing.T) {
	if constants.DefaultChunkSizeBytes > constants.MaxChunkSizeBytes {
		t.Errorf("DefaultChunkSizeBytes (%d) should be <= MaxChunkSizeBytes (%d)",
			constants.DefaultChunkSizeBytes, constants.MaxChunkSizeBytes)
	}
}

// ── DTN constants ─────────────────────────────────────────────────────────────

func TestDtn_BundleTtlHoursPositive(t *testing.T) {
	if constants.DtnBundleTtlHours <= 0 {
		t.Errorf("DtnBundleTtlHours: got %d, want > 0", constants.DtnBundleTtlHours)
	}
}

func TestDtn_MaxCopiesPositive(t *testing.T) {
	if constants.DtnMaxCopies <= 0 {
		t.Errorf("DtnMaxCopies: got %d, want > 0", constants.DtnMaxCopies)
	}
}

func TestDtn_MaxBundlesPerNodePositive(t *testing.T) {
	if constants.DtnMaxBundlesPerNode <= 0 {
		t.Errorf("DtnMaxBundlesPerNode: got %d, want > 0", constants.DtnMaxBundlesPerNode)
	}
}

// ── Voice constants ───────────────────────────────────────────────────────────

func TestVoice_FrameDurationMs(t *testing.T) {
	if constants.VoiceFrameDurationMs != 20 {
		t.Errorf("VoiceFrameDurationMs: got %d, want 20", constants.VoiceFrameDurationMs)
	}
}

func TestVoice_JitterBufferOrdering(t *testing.T) {
	if constants.JitterBufferMinMs >= constants.JitterBufferMaxMs {
		t.Errorf("JitterBufferMinMs(%d) should be < JitterBufferMaxMs(%d)",
			constants.JitterBufferMinMs, constants.JitterBufferMaxMs)
	}
}

// ── Presence constants ────────────────────────────────────────────────────────

func TestPresence_TimeoutPositive(t *testing.T) {
	if constants.PresenceTimeoutSeconds <= 0 {
		t.Errorf("PresenceTimeoutSeconds: got %d, want > 0", constants.PresenceTimeoutSeconds)
	}
}

func TestPresence_NodeOfflineThresholdGreaterThanPresenceTimeout(t *testing.T) {
	if constants.NodeOfflineThresholdSeconds <= constants.PresenceTimeoutSeconds {
		t.Errorf("NodeOfflineThresholdSeconds (%d) should be > PresenceTimeoutSeconds (%d)",
			constants.NodeOfflineThresholdSeconds, constants.PresenceTimeoutSeconds)
	}
}

// ── HKDF ─────────────────────────────────────────────────────────────────────

func TestHkdf_SaltNonEmpty(t *testing.T) {
	if constants.HkdfSalt == "" {
		t.Error("HkdfSalt should not be empty")
	}
}
