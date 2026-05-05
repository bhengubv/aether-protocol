// SPDX-License-Identifier: MIT

use async_trait::async_trait;

use crate::protocol::MeshPacket;

/// Verifies that a received RREP was actually signed by the node it claims to come from.
/// Without this, an intermediate forwarder can forge an RREP and hijack traffic for the
/// destination. The default `AcceptAllRouteReplyVerifier` is permissive — fine for tests,
/// not production.
#[async_trait]
pub trait RouteReplyVerifier: Send + Sync {
    async fn verify(&self, _route_reply: &MeshPacket) -> bool {
        true
    }
}

/// Permissive default — accepts every RREP.
pub struct AcceptAllRouteReplyVerifier;

#[async_trait]
impl RouteReplyVerifier for AcceptAllRouteReplyVerifier {}
