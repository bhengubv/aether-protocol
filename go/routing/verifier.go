// SPDX-License-Identifier: MIT

package routing

import (
	"context"

	"github.com/bhengubv/aether-protocol/go/protocol"
)

// RouteReplyVerifier verifies that a received RREP was actually signed by the
// node it claims to come from. Without this an intermediate forwarder could
// forge an RREP and hijack traffic for the destination. Hosts that ship a real
// implementation typically back it with the security package's signature
// service; the default AcceptAll is permissive (fine for tests, not for prod).
type RouteReplyVerifier interface {
	Verify(ctx context.Context, rrep *protocol.MeshPacket) (bool, error)
}

// AcceptAllRouteReplyVerifier accepts every RREP without verification.
type AcceptAllRouteReplyVerifier struct{}

func (AcceptAllRouteReplyVerifier) Verify(ctx context.Context, rrep *protocol.MeshPacket) (bool, error) {
	return true, nil
}
