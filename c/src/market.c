// SPDX-License-Identifier: MIT
// aether-market in-memory marketplace + single-node PoV trust service — see
// aethernet/market.h.

#include "aethernet/market.h"

#include "aethernet/constants.h" /* AETHERNET_ED25519_*_KEY_SIZE */
#include "aethernet/security.h"   /* aethernet_ed25519_generate_keypair */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

// .NET DateTime.Ticks at the Unix epoch (ticks between 0001-01-01 and 1970-01-01).
#define UNIX_EPOCH_TICKS 621355968000000000LL
#define THIRTY_DAYS_MS (30LL * 24 * 60 * 60 * 1000)

static int64_t now_ms_market(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static int64_t now_ticks_market(void) {
    return now_ms_market() * 10000LL + UNIX_EPOCH_TICKS;
}

static char *str_dup_market(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

// Case-insensitive ASCII prefix test: does `s` start with `prefix` (already lowercased)?
static bool starts_with_ci(const char *s, const char *prefix) {
    for (size_t i = 0; prefix[i]; i++) {
        char c = s[i];
        if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
        if (c != prefix[i]) return false;
    }
    return true;
}

// Case-insensitive ASCII substring test (needle already lowercased).
static bool contains_ci(const char *haystack, const char *needle) {
    if (!*needle) return true;
    for (size_t i = 0; haystack[i]; i++) {
        size_t j = 0;
        while (needle[j]) {
            char c = haystack[i + j];
            if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
            if (c != needle[j]) break;
            j++;
        }
        if (!needle[j]) return true;
    }
    return false;
}

static char *to_lower_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s);
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    for (size_t i = 0; i < n; i++) {
        char c = s[i];
        out[i] = (c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c;
    }
    out[n] = '\0';
    return out;
}

// ── model free helpers ──────────────────────────────────────────────────────

void aethernet_pov_score_free_fields(aethernet_pov_score_t *score) {
    if (!score) return;
    free(score->uhid);
    memset(score, 0, sizeof(*score));
}

void aethernet_market_listing_free_fields(aethernet_market_listing_t *l) {
    if (!l) return;
    free(l->listing_id);
    free(l->seller_uhid);
    free(l->title);
    free(l->description);
    free(l->geohash);
    free(l->escrow_content_hash);
    memset(l, 0, sizeof(*l));
}

bool aethernet_market_listing_is_expired(const aethernet_market_listing_t *l) {
    return l ? now_ms_market() >= l->expires_at_ms : true;
}

void aethernet_trade_escrow_free_fields(aethernet_trade_escrow_t *e) {
    if (!e) return;
    free(e->escrow_id);
    free(e->listing_id);
    free(e->buyer_uhid);
    free(e->seller_uhid);
    free(e->escrow_content_hash);
    memset(e, 0, sizeof(*e));
}

// ── a small UUID-ish unique id (process-local, monotonic + ticks) ───────────
// The marketplace ids only need to be locally unique; a counter mixed with the
// tick clock suffices (no cross-node collision requirement for an in-memory store).
static char *make_local_id(const char *prefix, unsigned long seq) {
    char buf[64];
    int64_t t = now_ticks_market();
    snprintf(buf, sizeof(buf), "%s-%lx-%lx", prefix, (unsigned long)(t & 0xffffffffUL), seq);
    return str_dup_market(buf);
}

// ── InMemoryMarketService ───────────────────────────────────────────────────

typedef struct market_listing_node {
    aethernet_market_listing_t listing;
    struct market_listing_node *next;
} market_listing_node_t;

typedef struct market_escrow_node {
    aethernet_trade_escrow_t escrow;
    struct market_escrow_node *next;
} market_escrow_node_t;

struct aethernet_market_service {
    market_listing_node_t *listings;
    market_escrow_node_t  *escrows;
    aethernet_market_on_listing_fn on_listing;
    void *on_listing_user_data;
    unsigned long seq;
};

aethernet_market_service_t *aethernet_market_service_new(void) {
    return (aethernet_market_service_t *)calloc(1, sizeof(aethernet_market_service_t));
}

void aethernet_market_service_free(aethernet_market_service_t *service) {
    if (!service) return;
    market_listing_node_t *ln = service->listings;
    while (ln) {
        market_listing_node_t *next = ln->next;
        aethernet_market_listing_free_fields(&ln->listing);
        free(ln);
        ln = next;
    }
    market_escrow_node_t *en = service->escrows;
    while (en) {
        market_escrow_node_t *next = en->next;
        aethernet_trade_escrow_free_fields(&en->escrow);
        free(en);
        en = next;
    }
    free(service);
}

void aethernet_market_set_on_listing_received(aethernet_market_service_t *service,
                                              aethernet_market_on_listing_fn cb, void *user_data) {
    if (!service) return;
    service->on_listing = cb;
    service->on_listing_user_data = user_data;
}

const aethernet_market_listing_t *aethernet_market_create_listing(
    aethernet_market_service_t *service, const char *seller_uhid, const char *title,
    const char *description, double price_zar, const char *geohash,
    aethernet_market_category_t category, const char *escrow_content_hash) {
    if (!service) return NULL;

    market_listing_node_t *n = (market_listing_node_t *)calloc(1, sizeof(market_listing_node_t));
    if (!n) return NULL;

    int64_t now = now_ms_market();
    n->listing.listing_id = make_local_id("lst", service->seq++);
    n->listing.seller_uhid = str_dup_market(seller_uhid ? seller_uhid : "");
    n->listing.title = str_dup_market(title ? title : "");
    n->listing.description = str_dup_market(description ? description : "");
    n->listing.price_zar = price_zar;
    n->listing.geohash = str_dup_market(geohash ? geohash : "");
    n->listing.category = category;
    n->listing.escrow_content_hash = str_dup_market(escrow_content_hash); // NULL stays NULL
    n->listing.created_at_ms = now;
    n->listing.expires_at_ms = now + THIRTY_DAYS_MS;

    if (!n->listing.listing_id) {
        aethernet_market_listing_free_fields(&n->listing);
        free(n);
        return NULL;
    }

    n->next = service->listings;
    service->listings = n;

    if (service->on_listing) {
        service->on_listing(&n->listing, service->on_listing_user_data);
    }
    return &n->listing;
}

int aethernet_market_browse_nearby(aethernet_market_service_t *service, const char *center_geohash,
                                   int radius_cells, const aethernet_market_listing_t **out, int max) {
    if (!service || !center_geohash || !out || max <= 0) return 0;

    int center_len = (int)strlen(center_geohash);
    int prefix_len = center_len - radius_cells + 1;
    if (prefix_len < 1) prefix_len = 1;
    if (prefix_len > center_len) prefix_len = center_len;

    char *prefix = (char *)malloc((size_t)prefix_len + 1);
    if (!prefix) return 0;
    for (int i = 0; i < prefix_len; i++) {
        char c = center_geohash[i];
        prefix[i] = (c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c;
    }
    prefix[prefix_len] = '\0';

    int count = 0;
    for (market_listing_node_t *n = service->listings; n && count < max; n = n->next) {
        if (!aethernet_market_listing_is_expired(&n->listing) &&
            n->listing.geohash && starts_with_ci(n->listing.geohash, prefix)) {
            out[count++] = &n->listing;
        }
    }
    free(prefix);
    return count;
}

int aethernet_market_search(aethernet_market_service_t *service, const char *query,
                            const aethernet_market_category_t *category,
                            const aethernet_market_listing_t **out, int max) {
    if (!service || !query || !out || max <= 0) return 0;

    char *q = to_lower_dup(query);
    if (!q) return 0;

    int count = 0;
    for (market_listing_node_t *n = service->listings; n && count < max; n = n->next) {
        if (aethernet_market_listing_is_expired(&n->listing)) continue;
        if (category && n->listing.category != *category) continue;
        if ((n->listing.title && contains_ci(n->listing.title, q)) ||
            (n->listing.description && contains_ci(n->listing.description, q))) {
            out[count++] = &n->listing;
        }
    }
    free(q);
    return count;
}

const aethernet_trade_escrow_t *aethernet_market_initiate_trade(
    aethernet_market_service_t *service, const aethernet_market_listing_t *listing, const char *buyer_uhid) {
    if (!service || !listing) return NULL;

    market_escrow_node_t *n = (market_escrow_node_t *)calloc(1, sizeof(market_escrow_node_t));
    if (!n) return NULL;

    n->escrow.escrow_id = make_local_id("esc", service->seq++);
    n->escrow.listing_id = str_dup_market(listing->listing_id);
    n->escrow.buyer_uhid = str_dup_market(buyer_uhid ? buyer_uhid : "");
    n->escrow.seller_uhid = str_dup_market(listing->seller_uhid);
    n->escrow.state = AETHERNET_TRADE_STATE_INITIATED;
    n->escrow.escrow_content_hash = str_dup_market(listing->escrow_content_hash);
    n->escrow.created_at_ms = now_ms_market();

    if (!n->escrow.escrow_id) {
        aethernet_trade_escrow_free_fields(&n->escrow);
        free(n);
        return NULL;
    }

    n->next = service->escrows;
    service->escrows = n;
    return &n->escrow;
}

static market_escrow_node_t *find_escrow(aethernet_market_service_t *service, const char *escrow_id) {
    for (market_escrow_node_t *n = service->escrows; n; n = n->next) {
        if (n->escrow.escrow_id && escrow_id && strcmp(n->escrow.escrow_id, escrow_id) == 0) return n;
    }
    return NULL;
}

const aethernet_trade_escrow_t *aethernet_market_confirm_trade(
    aethernet_market_service_t *service, const aethernet_trade_escrow_t *escrow, aethernet_trade_role_t role) {
    if (!service || !escrow) return NULL;
    market_escrow_node_t *n = find_escrow(service, escrow->escrow_id);
    if (!n) return NULL;

    if (role == AETHERNET_TRADE_ROLE_BUYER) {
        n->escrow.state = AETHERNET_TRADE_STATE_BUYER_CONFIRMED;
    } else if (n->escrow.state == AETHERNET_TRADE_STATE_BUYER_CONFIRMED) {
        n->escrow.state = AETHERNET_TRADE_STATE_COMPLETE;
    } else {
        n->escrow.state = AETHERNET_TRADE_STATE_SELLER_CONFIRMED;
    }
    return &n->escrow;
}

const aethernet_trade_escrow_t *aethernet_market_dispute(
    aethernet_market_service_t *service, const aethernet_trade_escrow_t *escrow, const char *reason) {
    (void)reason;
    if (!service || !escrow) return NULL;
    market_escrow_node_t *n = find_escrow(service, escrow->escrow_id);
    if (!n) return NULL;
    n->escrow.state = AETHERNET_TRADE_STATE_DISPUTED;
    return &n->escrow;
}

// ── InMemoryPoVService (single-node) ────────────────────────────────────────

typedef struct pov_token_node {
    aethernet_pov_token_t token; // deep copy
    struct pov_token_node *next;
} pov_token_node_t;

typedef struct pov_override_node {
    char  *witness_uhid; // owned
    double score;
    struct pov_override_node *next;
} pov_override_node_t;

struct aethernet_pov_service {
    pov_token_node_t    *tokens; // all accepted tokens (keyed by subject in get_score)
    pov_override_node_t *overrides;
    uint8_t private_key[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    uint8_t public_key[AETHERNET_ED25519_PUBLIC_KEY_SIZE];
};

aethernet_pov_service_t *aethernet_pov_service_new(void) {
    aethernet_pov_service_t *svc = (aethernet_pov_service_t *)calloc(1, sizeof(aethernet_pov_service_t));
    if (!svc) return NULL;
    if (!aethernet_ed25519_generate_keypair(svc->private_key, svc->public_key)) {
        free(svc);
        return NULL;
    }
    return svc;
}

void aethernet_pov_service_free(aethernet_pov_service_t *service) {
    if (!service) return;
    pov_token_node_t *tn = service->tokens;
    while (tn) {
        pov_token_node_t *next = tn->next;
        aethernet_pov_token_free_fields(&tn->token);
        free(tn);
        tn = next;
    }
    pov_override_node_t *on = service->overrides;
    while (on) {
        pov_override_node_t *next = on->next;
        free(on->witness_uhid);
        free(on);
        on = next;
    }
    free(service);
}

bool aethernet_pov_service_issue_token(aethernet_pov_service_t *service, const char *witness_uhid,
                                       const char *subject_uhid, aethernet_pov_transport_t transport,
                                       aethernet_pov_token_t *out_token) {
    if (!service || !out_token) return false;
    aethernet_pov_token_init(out_token);
    if (!aethernet_pov_token_set_witness(out_token, witness_uhid ? witness_uhid : "") ||
        !aethernet_pov_token_set_subject(out_token, subject_uhid ? subject_uhid : "")) {
        aethernet_pov_token_free_fields(out_token);
        return false;
    }
    out_token->timestamp_ticks = now_ticks_market();
    out_token->transport_used = transport;
    // Single-node model: both witness and subject signatures from this node's one key.
    if (!aethernet_pov_token_sign_witness(out_token, service->private_key) ||
        !aethernet_pov_token_sign_subject(out_token, service->private_key)) {
        aethernet_pov_token_free_fields(out_token);
        return false;
    }
    return true;
}

bool aethernet_pov_service_verify_token(aethernet_pov_service_t *service, const aethernet_pov_token_t *token) {
    if (!service || !token) return false;
    // Structural: both parties signed, both UHIDs present, and distinct.
    if (token->witness_signature_len != AETHERNET_POV_SIGNATURE_SIZE ||
        token->subject_signature_len != AETHERNET_POV_SIGNATURE_SIZE ||
        !token->witness_uhid || !token->subject_uhid ||
        token->witness_uhid[0] == '\0' || token->subject_uhid[0] == '\0' ||
        strcmp(token->witness_uhid, token->subject_uhid) == 0) {
        return false;
    }
    // Cryptographic: BOTH signatures valid over the canonical body.
    return aethernet_pov_token_verify_witness(token, service->public_key) &&
           aethernet_pov_token_verify_subject(token, service->public_key);
}

static bool token_deep_copy(aethernet_pov_token_t *dst, const aethernet_pov_token_t *src) {
    aethernet_pov_token_init(dst);
    if (!aethernet_pov_token_set_witness(dst, src->witness_uhid ? src->witness_uhid : "") ||
        !aethernet_pov_token_set_subject(dst, src->subject_uhid ? src->subject_uhid : "")) {
        aethernet_pov_token_free_fields(dst);
        return false;
    }
    dst->timestamp_ticks = src->timestamp_ticks;
    dst->transport_used = src->transport_used;
    dst->witness_signature_len = src->witness_signature_len;
    if (src->witness_signature_len) {
        memcpy(dst->witness_signature, src->witness_signature, src->witness_signature_len);
    }
    dst->subject_signature_len = src->subject_signature_len;
    if (src->subject_signature_len) {
        memcpy(dst->subject_signature, src->subject_signature, src->subject_signature_len);
    }
    return true;
}

bool aethernet_pov_service_accept_token(aethernet_pov_service_t *service, const aethernet_pov_token_t *token) {
    if (!aethernet_pov_service_verify_token(service, token)) return false;
    pov_token_node_t *n = (pov_token_node_t *)calloc(1, sizeof(pov_token_node_t));
    if (!n) return false;
    if (!token_deep_copy(&n->token, token)) {
        free(n);
        return false;
    }
    n->next = service->tokens;
    service->tokens = n;
    return true;
}

static double override_for(aethernet_pov_service_t *service, const char *uhid, bool *found) {
    for (pov_override_node_t *o = service->overrides; o; o = o->next) {
        if (o->witness_uhid && strcmp(o->witness_uhid, uhid) == 0) {
            if (found) *found = true;
            return o->score;
        }
    }
    if (found) *found = false;
    return 0.0;
}

void aethernet_pov_service_get_score(aethernet_pov_service_t *service, const char *uhid,
                                     aethernet_pov_score_t *out_score) {
    if (!out_score) return;
    memset(out_score, 0, sizeof(*out_score));
    if (!service || !uhid) return;

    out_score->uhid = str_dup_market(uhid);
    out_score->last_updated_ms = now_ms_market();

    // Count distinct witnesses among tokens vouching for `uhid`.
    const char *seen[256];
    int seen_count = 0;
    for (pov_token_node_t *n = service->tokens; n; n = n->next) {
        if (!n->token.subject_uhid || strcmp(n->token.subject_uhid, uhid) != 0) continue;
        const char *w = n->token.witness_uhid ? n->token.witness_uhid : "";
        bool dup = false;
        for (int i = 0; i < seen_count; i++) {
            if (strcmp(seen[i], w) == 0) { dup = true; break; }
        }
        if (!dup && seen_count < (int)(sizeof(seen) / sizeof(seen[0]))) {
            seen[seen_count++] = w;
        }
    }
    int unique = seen_count;

    bool has_override = false;
    double override = override_for(service, uhid, &has_override);

    out_score->unique_witnesses = unique;
    if (unique == 0) {
        out_score->weighted_score = has_override ? override : 0.0;
    } else {
        double score = (double)unique / ((double)unique + 1.0); // sigmoid-ish w/(w+1)
        out_score->weighted_score = has_override ? override : score;
    }
}

void aethernet_pov_service_report_defection(aethernet_pov_service_t *service,
                                            const char *witness_uhid, const char *defector_uhid) {
    (void)defector_uhid;
    if (!service || !witness_uhid) return;

    aethernet_pov_score_t score;
    aethernet_pov_service_get_score(service, witness_uhid, &score);
    double penalised = score.weighted_score * 0.8;
    aethernet_pov_score_free_fields(&score);

    // Upsert the override.
    for (pov_override_node_t *o = service->overrides; o; o = o->next) {
        if (o->witness_uhid && strcmp(o->witness_uhid, witness_uhid) == 0) {
            o->score = penalised;
            return;
        }
    }
    pov_override_node_t *n = (pov_override_node_t *)calloc(1, sizeof(pov_override_node_t));
    if (!n) return;
    n->witness_uhid = str_dup_market(witness_uhid);
    n->score = penalised;
    n->next = service->overrides;
    service->overrides = n;
}
