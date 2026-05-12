// SPDX-License-Identifier: MIT
// Aether NodeReputationService — per-UHID behavioural score aggregation.
// Suppress MSVC's strncpy deprecation warning — strncpy is standard C11
// and the correct tool here (POSIX, length-bounded, no dynamic allocation).
#ifdef _MSC_VER
#  define _CRT_SECURE_NO_WARNINGS
#endif
//
// Scores are clamped to [0.0, 1.0] after every mutation.
// Epsilon-snap: result < 1e-12 → 0.0; result > 1.0 - 1e-12 → 1.0.
// Unknown peers default to 1.0 (benefit of the doubt).
// Storage is fully static; no heap allocation.

#include "aether_reputation.h"

#include <string.h>

// ─── Score delta constants (mirror C# exactly) ───────────────────────────────

#define DELTA_RREQ_FLOOD      (-0.05)
#define DELTA_REPLAY          (-0.15)
#define DELTA_SIG_FAILURE     (-0.20)
#define DELTA_CUSTODY_REFUSAL (-0.05)
#define DELTA_DELIVERY_FAIL   (-0.02)
#define DELTA_DELIVERY_OK     (+0.01)

#define EPSILON               (1e-12)

// ─── Internal helpers ─────────────────────────────────────────────────────────

/* Clamp and epsilon-snap a raw score into [0.0, 1.0]. */
static double clamp_score(double s)
{
    if (s < EPSILON)              return 0.0;
    if (s > 1.0 - EPSILON)       return 1.0;
    return s;
}

/*
 * Look up the entry for `uhid`.  Returns a pointer into entries[] on hit,
 * or NULL if the UHID is not yet tracked.
 */
static AetherReputationEntry *find_entry(AetherNodeReputationService *svc, const char *uhid)
{
    for (int i = 0; i < svc->count; i++) {
        if (strncmp(svc->entries[i].uhid, uhid, AETHER_UHID_MAX_LEN - 1) == 0) {
            return &svc->entries[i];
        }
    }
    return NULL;
}

/*
 * Return the existing entry for `uhid`, or create a new one initialised to
 * 1.0.  If the table is full the oldest entry (index 0) is evicted and all
 * subsequent entries are shifted down by one slot.
 */
static AetherReputationEntry *get_or_create(AetherNodeReputationService *svc, const char *uhid)
{
    AetherReputationEntry *e = find_entry(svc, uhid);
    if (e != NULL) {
        return e;
    }

    if (svc->count < AETHER_REPUTATION_MAX_ENTRIES) {
        e = &svc->entries[svc->count++];
    } else {
        /* Evict oldest (slot 0), shift everyone down. */
        memmove(&svc->entries[0], &svc->entries[1],
                sizeof(AetherReputationEntry) * (AETHER_REPUTATION_MAX_ENTRIES - 1));
        e = &svc->entries[AETHER_REPUTATION_MAX_ENTRIES - 1];
    }

    strncpy(e->uhid, uhid, AETHER_UHID_MAX_LEN - 1);
    e->uhid[AETHER_UHID_MAX_LEN - 1] = '\0';
    e->score = 1.0;
    return e;
}

/* Apply a delta to `uhid`'s score, creating the entry if needed. */
static void apply_delta(AetherNodeReputationService *svc, const char *uhid, double delta)
{
    AetherReputationEntry *e = get_or_create(svc, uhid);
    e->score = clamp_score(e->score + delta);
}

// ─── Public API ───────────────────────────────────────────────────────────────

void aether_reputation_init(AetherNodeReputationService *svc)
{
    memset(svc, 0, sizeof(*svc));
}

void aether_reputation_record_rreq_flood(AetherNodeReputationService *svc, const char *uhid)
{
    apply_delta(svc, uhid, DELTA_RREQ_FLOOD);
}

void aether_reputation_record_replay(AetherNodeReputationService *svc, const char *uhid)
{
    apply_delta(svc, uhid, DELTA_REPLAY);
}

void aether_reputation_record_sig_failure(AetherNodeReputationService *svc, const char *uhid)
{
    apply_delta(svc, uhid, DELTA_SIG_FAILURE);
}

void aether_reputation_record_custody_refusal(AetherNodeReputationService *svc, const char *uhid)
{
    apply_delta(svc, uhid, DELTA_CUSTODY_REFUSAL);
}

void aether_reputation_record_delivery_success(AetherNodeReputationService *svc,
                                               const char *uhid,
                                               int round_trip_ms)
{
    (void)round_trip_ms; /* reserved for future latency weighting */
    apply_delta(svc, uhid, DELTA_DELIVERY_OK);
}

void aether_reputation_record_delivery_failure(AetherNodeReputationService *svc, const char *uhid)
{
    apply_delta(svc, uhid, DELTA_DELIVERY_FAIL);
}

double aether_reputation_get_score(const AetherNodeReputationService *svc, const char *uhid)
{
    for (int i = 0; i < svc->count; i++) {
        if (strncmp(svc->entries[i].uhid, uhid, AETHER_UHID_MAX_LEN - 1) == 0) {
            return svc->entries[i].score;
        }
    }
    return 1.0; /* unknown peer — benefit of the doubt */
}

void aether_reputation_apply_weighted_delta(
    AetherNodeReputationService *svc,
    const char *uhid,
    double weighted_delta)
{
    if (!svc || !uhid) return;
    double clamped = weighted_delta < -1.0 ? -1.0
                   : weighted_delta >  1.0 ?  1.0 : weighted_delta;
    apply_delta(svc, uhid, clamped);
}
