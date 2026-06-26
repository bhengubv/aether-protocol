// SPDX-License-Identifier: MIT
//
// Behavioural test for the aether-market in-memory services (Phase-2):
//   - marketplace: create -> browse (geohash prefix) -> search -> trade state machine -> dispute,
//   - PoV: issue -> verify -> accept -> score (w/(w+1)), tamper + self-vouch rejected, defection penalty.
//
// Mirrors the Go/Rust/Python/TS/Kotlin/Swift market + PoV tests.

#include <math.h>
#include <stdio.h>
#include <string.h>

#include "aethernet/market.h"

static int g_failures = 0;

#define CHECK(cond, msg)                                                        \
    do {                                                                        \
        if (!(cond)) {                                                          \
            fprintf(stderr, "FAIL: %s (%s:%d)\n", (msg), __FILE__, __LINE__);   \
            g_failures++;                                                       \
        }                                                                       \
    } while (0)

static int g_listing_events = 0;
static void on_listing(const aethernet_market_listing_t *l, void *ud) {
    (void)l;
    (void)ud;
    g_listing_events++;
}

int main(void) {
    // ── marketplace ─────────────────────────────────────────────────────────
    aethernet_market_service_t *m = aethernet_market_service_new();
    CHECK(m != NULL, "market_service_new");
    aethernet_market_set_on_listing_received(m, on_listing, NULL);

    const aethernet_market_listing_t *l = aethernet_market_create_listing(
        m, "seller1", "Bicycle", "Red mountain bike", 1500.0, "k3vf9z", AETHERNET_MARKET_GOODS, NULL);
    CHECK(l != NULL, "create_listing");
    CHECK(l->listing_id != NULL && l->listing_id[0] != '\0', "listing has id");
    CHECK(g_listing_events == 1, "on_listing fired once");
    CHECK(!aethernet_market_listing_is_expired(l), "not expired");

    const aethernet_market_listing_t *hits[8];
    int n = aethernet_market_browse_nearby(m, "k3vf9z", 2, hits, 8);
    CHECK(n == 1, "browse near returns 1");
    n = aethernet_market_browse_nearby(m, "xxxxxx", 2, hits, 8);
    CHECK(n == 0, "browse far returns 0");

    n = aethernet_market_search(m, "bike", NULL, hits, 8);
    CHECK(n == 1, "search 'bike' returns 1");
    aethernet_market_category_t services = AETHERNET_MARKET_SERVICES;
    n = aethernet_market_search(m, "bike", &services, hits, 8);
    CHECK(n == 0, "search 'bike' in Services returns 0");

    // Trade state machine: Initiated -> BuyerConfirmed -> Complete.
    const aethernet_trade_escrow_t *e = aethernet_market_initiate_trade(m, l, "buyer1");
    CHECK(e != NULL && e->state == AETHERNET_TRADE_STATE_INITIATED, "initiated");
    e = aethernet_market_confirm_trade(m, e, AETHERNET_TRADE_ROLE_BUYER);
    CHECK(e != NULL && e->state == AETHERNET_TRADE_STATE_BUYER_CONFIRMED, "buyer confirmed");
    e = aethernet_market_confirm_trade(m, e, AETHERNET_TRADE_ROLE_SELLER);
    CHECK(e != NULL && e->state == AETHERNET_TRADE_STATE_COMPLETE, "complete");

    const aethernet_trade_escrow_t *e2 = aethernet_market_initiate_trade(m, l, "buyer2");
    e2 = aethernet_market_dispute(m, e2, "item not as described");
    CHECK(e2 != NULL && e2->state == AETHERNET_TRADE_STATE_DISPUTED, "disputed");

    aethernet_market_service_free(m);

    // ── PoV trust service ────────────────────────────────────────────────────
    aethernet_pov_service_t *p = aethernet_pov_service_new();
    CHECK(p != NULL, "pov_service_new");

    aethernet_pov_token_t tok;
    bool issued = aethernet_pov_service_issue_token(p, "w1", "A", AETHERNET_POV_TRANSPORT_BLE, &tok);
    CHECK(issued, "issue token");
    CHECK(aethernet_pov_service_verify_token(p, &tok), "issued token verifies");
    CHECK(aethernet_pov_service_accept_token(p, &tok), "accept token");

    aethernet_pov_score_t score;
    aethernet_pov_service_get_score(p, "A", &score);
    CHECK(score.unique_witnesses == 1, "1 unique witness");
    CHECK(fabs(score.weighted_score - 0.5) < 1e-9, "weighted score 0.5");
    aethernet_pov_score_free_fields(&score);

    // Tampering invalidates the signatures.
    aethernet_pov_token_t bad;
    aethernet_pov_token_init(&bad);
    aethernet_pov_token_set_witness(&bad, tok.witness_uhid);
    aethernet_pov_token_set_subject(&bad, "C"); // changed subject -> body differs
    bad.timestamp_ticks = tok.timestamp_ticks;
    bad.transport_used = tok.transport_used;
    bad.witness_signature_len = tok.witness_signature_len;
    memcpy(bad.witness_signature, tok.witness_signature, tok.witness_signature_len);
    bad.subject_signature_len = tok.subject_signature_len;
    memcpy(bad.subject_signature, tok.subject_signature, tok.subject_signature_len);
    CHECK(!aethernet_pov_service_verify_token(p, &bad), "tampered token rejected");
    aethernet_pov_token_free_fields(&bad);

    // A node cannot vouch for itself.
    aethernet_pov_token_t self_tok;
    CHECK(aethernet_pov_service_issue_token(p, "x", "x", AETHERNET_POV_TRANSPORT_NFC, &self_tok), "issue self");
    CHECK(!aethernet_pov_service_verify_token(p, &self_tok), "self-vouch rejected");
    aethernet_pov_token_free_fields(&self_tok);

    // Defection penalty: A's score 0.5 -> 0.4.
    aethernet_pov_service_report_defection(p, "A", "victim");
    aethernet_pov_service_get_score(p, "A", &score);
    CHECK(fabs(score.weighted_score - 0.4) < 1e-9, "post-defection score 0.4");
    aethernet_pov_score_free_fields(&score);

    aethernet_pov_token_free_fields(&tok);
    aethernet_pov_service_free(p);

    if (g_failures == 0) {
        printf("test_market: all checks passed\n");
        return 0;
    }
    fprintf(stderr, "test_market: %d check(s) failed\n", g_failures);
    return 1;
}
