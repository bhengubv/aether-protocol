// SPDX-License-Identifier: MIT
// aether-market: offline-capable P2P marketplace + single-node Proof-of-Vicinity
// trust service (Phase-2 extension). C port of AetherNet.Market.IMarketService /
// InMemoryMarketService and IPoVService / InMemoryPoVService (+ the listing/escrow
// and PoVScore models). Listings are geo-pinned and may reference a vaulted
// document escrow by content hash; trades run a two-party confirm state machine.
// The PoV service issues/accepts real-Ed25519 co-presence tokens and derives a
// purely-local anti-Sybil trust score (NO value semantics).

#ifndef AETHERNET_MARKET_H
#define AETHERNET_MARKET_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "aethernet/pov_token.h" /* aethernet_pov_token_t, aethernet_pov_transport_t */

#ifdef __cplusplus
extern "C" {
#endif

/** Category of a market listing. */
typedef enum {
    AETHERNET_MARKET_GOODS     = 0,
    AETHERNET_MARKET_SERVICES  = 1,
    AETHERNET_MARKET_LABOUR    = 2,
    AETHERNET_MARKET_LAND      = 3,
    AETHERNET_MARKET_DOCUMENTS = 4
} aethernet_market_category_t;

/** Role of the node confirming a trade step. */
typedef enum {
    AETHERNET_TRADE_ROLE_BUYER  = 0,
    AETHERNET_TRADE_ROLE_SELLER = 1
} aethernet_trade_role_t;

/** State machine for a trade escrow. */
typedef enum {
    AETHERNET_TRADE_STATE_INITIATED       = 0,
    AETHERNET_TRADE_STATE_BUYER_CONFIRMED = 1,
    AETHERNET_TRADE_STATE_SELLER_CONFIRMED = 2,
    AETHERNET_TRADE_STATE_COMPLETE        = 3,
    AETHERNET_TRADE_STATE_DISPUTED        = 4
} aethernet_trade_state_t;

/**
 * Proof-of-Vicinity trust score for a node — a purely local anti-Sybil
 * routing/identity signal that attaches NO value semantics. Owned `uhid` string;
 * release with aethernet_pov_score_free_fields().
 */
typedef struct {
    char   *uhid;             /**< owned UHID of the scored node. */
    int     unique_witnesses; /**< distinct witnesses who vouched for this node. */
    double  weighted_score;   /**< 0.0–1.0. */
    int64_t last_updated_ms;  /**< ms since the Unix epoch. */
} aethernet_pov_score_t;

/// Release the owned fields of a score and zero it. NULL is a no-op.
void aethernet_pov_score_free_fields(aethernet_pov_score_t *score);

/**
 * A geo-pinned market listing dropped by a verified seller. Owned string fields;
 * a listing returned by the service is BORROWED (valid until the next mutating
 * call). `escrow_content_hash` is the vaulted document's content hash for a
 * document-backed sale, or NULL.
 */
typedef struct {
    char   *listing_id;          /**< owned. */
    char   *seller_uhid;         /**< owned. */
    char   *title;               /**< owned. */
    char   *description;         /**< owned. */
    double  price_zar;           /**< South African Rand. */
    char   *geohash;             /**< owned; 6-char geohash of the location. */
    aethernet_market_category_t category;
    char   *escrow_content_hash; /**< owned; vaulted document hash, or NULL. */
    int64_t created_at_ms;
    int64_t expires_at_ms;
} aethernet_market_listing_t;

/// Release the owned fields of a listing and zero it. NULL is a no-op.
void aethernet_market_listing_free_fields(aethernet_market_listing_t *listing);

/// Whether the listing has reached its expiry.
bool aethernet_market_listing_is_expired(const aethernet_market_listing_t *listing);

/**
 * Tracks the lifecycle of a marketplace trade. A returned escrow is BORROWED
 * (valid until the next mutating call).
 */
typedef struct {
    char   *escrow_id;           /**< owned. */
    char   *listing_id;          /**< owned. */
    char   *buyer_uhid;          /**< owned. */
    char   *seller_uhid;         /**< owned. */
    aethernet_trade_state_t state;
    char   *escrow_content_hash; /**< owned; copied from the listing, or NULL. */
    int64_t created_at_ms;
} aethernet_trade_escrow_t;

/// Release the owned fields of an escrow and zero it. NULL is a no-op.
void aethernet_trade_escrow_free_fields(aethernet_trade_escrow_t *escrow);

/* ── InMemoryMarketService ─────────────────────────────────────────────── */

/// Opaque in-memory market service handle.
typedef struct aethernet_market_service aethernet_market_service_t;

aethernet_market_service_t *aethernet_market_service_new(void);
void aethernet_market_service_free(aethernet_market_service_t *service);

/// Fired when a new listing is created locally / received from the mesh. The
/// pointer is valid only for the duration of the callback.
typedef void (*aethernet_market_on_listing_fn)(const aethernet_market_listing_t *listing, void *user_data);
void aethernet_market_set_on_listing_received(aethernet_market_service_t *service,
                                              aethernet_market_on_listing_fn cb, void *user_data);

/**
 * Create and store a listing (expires in 30 days), fire the listing callback, and
 * return a borrowed pointer to the stored listing (NULL on bad args / OOM).
 * `escrow_content_hash` may be NULL (no document escrow).
 */
const aethernet_market_listing_t *aethernet_market_create_listing(
    aethernet_market_service_t *service, const char *seller_uhid, const char *title,
    const char *description, double price_zar, const char *geohash,
    aethernet_market_category_t category, const char *escrow_content_hash);

/**
 * Write up to `max` borrowed pointers to non-expired listings whose geohash shares
 * the center prefix (length = len(center) - radius_cells + 1, floored at 1) into
 * `out`. Returns the count written.
 */
int aethernet_market_browse_nearby(aethernet_market_service_t *service, const char *center_geohash,
                                   int radius_cells, const aethernet_market_listing_t **out, int max);

/**
 * Write up to `max` borrowed pointers to non-expired listings whose title or
 * description contains `query` (case-insensitive) into `out`, optionally filtered
 * by `category` (NULL = any). Returns the count written.
 */
int aethernet_market_search(aethernet_market_service_t *service, const char *query,
                            const aethernet_market_category_t *category,
                            const aethernet_market_listing_t **out, int max);

/// Open an escrow in the Initiated state for `listing`/`buyer_uhid`; returns a borrowed escrow.
const aethernet_trade_escrow_t *aethernet_market_initiate_trade(
    aethernet_market_service_t *service, const aethernet_market_listing_t *listing, const char *buyer_uhid);

/**
 * Advance the escrow state machine. Buyer → BuyerConfirmed; Seller → Complete if
 * the buyer already confirmed, else SellerConfirmed. Returns the borrowed updated escrow.
 */
const aethernet_trade_escrow_t *aethernet_market_confirm_trade(
    aethernet_market_service_t *service, const aethernet_trade_escrow_t *escrow, aethernet_trade_role_t role);

/// Mark the escrow Disputed; returns the borrowed updated escrow.
const aethernet_trade_escrow_t *aethernet_market_dispute(
    aethernet_market_service_t *service, const aethernet_trade_escrow_t *escrow, const char *reason);

/* ── InMemoryPoVService (single-node) ──────────────────────────────────── */

/// Opaque single-node PoV service handle (holds one self-contained Ed25519 identity).
typedef struct aethernet_pov_service aethernet_pov_service_t;

/// Construct a service with a fresh Ed25519 identity (NULL on OOM / keygen failure).
aethernet_pov_service_t *aethernet_pov_service_new(void);
void aethernet_pov_service_free(aethernet_pov_service_t *service);

/**
 * Issue a PoV token to `subject_uhid` (both witness and subject signatures from this
 * node's one key). Fills *out_token (caller frees with aethernet_pov_token_free_fields).
 * Returns true on success.
 */
bool aethernet_pov_service_issue_token(aethernet_pov_service_t *service, const char *witness_uhid,
                                       const char *subject_uhid, aethernet_pov_transport_t transport,
                                       aethernet_pov_token_t *out_token);

/// Record an incoming token iff it cryptographically verifies. Returns true if recorded.
bool aethernet_pov_service_accept_token(aethernet_pov_service_t *service, const aethernet_pov_token_t *token);

/// Fill the current PoV score for `uhid` (caller frees with aethernet_pov_score_free_fields).
void aethernet_pov_service_get_score(aethernet_pov_service_t *service, const char *uhid,
                                     aethernet_pov_score_t *out_score);

/// Whether the token is structurally and cryptographically valid (both sigs, distinct parties).
bool aethernet_pov_service_verify_token(aethernet_pov_service_t *service, const aethernet_pov_token_t *token);

/// Reduce the witness's weighted score by 20%.
void aethernet_pov_service_report_defection(aethernet_pov_service_t *service,
                                            const char *witness_uhid, const char *defector_uhid);

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_MARKET_H
