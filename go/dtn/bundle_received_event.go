// SPDX-License-Identifier: MIT

package dtn

import (
	"time"

	"github.com/bhengubv/aether-protocol/go/models"
)

// DtnBundleReceivedEvent is the payload delivered to OnBundleReceived the
// moment a DTN bundle arrives whose final recipient is the local node — i.e.,
// a bundle addressed TO us has just been delivered locally by a peer or by
// the receive pump itself.
//
// Distinct from DtnDeliveryReceipt (delivered via OnBundleDelivered) which
// fires on the original sender side once a delivery confirmation flows back.
// Consumers that want to know "did a bundle arrive for me?" should set
// OnBundleReceived; consumers that want to know "did my outbound bundle
// reach the recipient?" should set OnBundleDelivered.
//
// Added in v1.2.0 — closes the Wave-16 gap that previously forced receive-side
// consumers to inspect Handle() indirectly via the host shell.
type DtnBundleReceivedEvent struct {
	// BundleID is the globally-unique bundle identifier.
	BundleID string

	// SenderUhid is the UHID of the original sender of the bundle.
	SenderUhid string

	// RecipientUhid is the UHID of the recipient — always the local node when
	// this event fires.
	RecipientUhid string

	// EncryptedPayload is the encrypted payload bytes as delivered. The DTN
	// layer does not decrypt — consumers route this through their security
	// layer.
	EncryptedPayload []byte

	// Priority is the replication-aggressiveness class of the bundle.
	Priority models.DtnPriority

	// HopCount is the number of custody transfers the bundle underwent before
	// arriving here.
	HopCount int32

	// ReceivedAtUtc is the UTC timestamp at which the bundle was received
	// locally.
	ReceivedAtUtc time.Time
}
