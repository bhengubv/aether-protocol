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

#include "aethermesh_reputation.h"

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
static AetherMeshReputationEntry *find_entry(AetherMeshNodeReputationService *svc, const char *uhid)
{
    for (int i = 0; i < svc->count; i++) {
        if (strncmp(svc->entries[i].uhid, uhid, AETHERMESH_UHID_MAX_LEN - 1) == 0) {
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
static AetherMeshReputationEntry *get_or_create(AetherMeshNodeReputationService *svc, const char *uhid)
{
    AetherMeshReputationEntry *e = find_entry(svc, uhid);
    if (e != NULL) {
        return e;
    }

    if (svc->count < AETHERMESH_REPUTATION_MAX_ENTRIES) {
        e = &svc->entries[svc->count++];
    } else {
        /* Evict oldest (slot 0), shift everyone down. */
        memmove(&svc->entries[0], &svc->entries[1],
                sizeof(AetherMeshReputationEntry) * (AETHERMESH_REPUTATION_MAX_ENTRIES - 1));
        e = &svc->entries[AETHERMESH_REPUTATION_MAX_ENTRIES - 1];
    }

    strncpy(e->uhid, uhid, AETHERMESH_UHID_MAX_LEN - 1);
    e->uhid[AETHERMESH_UHID_MAX_LEN - 1] = '\0';
    e->score = 1.0;
    return e;
}

/* Apply a delta to `uhid`'s score, creating the entry if needed. */
static void apply_delta(AetherMeshNodeReputationService *svc, const char *uhid, double delta)
{
    AetherMeshReputationEntry *e = get_or_create(svc, uhid);
    e->score = clamp_score(e->score + delta);
}

// ─── Public API ───────────────────────────────────────────────────────────────

void aethermesh_reputation_init(AetherMeshNodeReputationService *svc)
{
    memset(svc, 0, sizeof(*svc));
}

void aethermesh_reputation_record_rreq_flood(AetherMeshNodeReputationService *svc, const char *uhid)
{
    apply_delta(svc, uhid, DELTA_RREQ_FLOOD);
}

void aethermesh_reputation_record_replay(AetherMeshNodeReputationService *svc, const char *uhid)
{
    apply_delta(svc, uhid, DELTA_REPLAY);
}

void aethermesh_reputation_record_sig_failure(AetherMeshNodeReputationService *svc, const char *uhid)
{
    apply_delta(svc, uhid, DELTA_SIG_FAILURE);
}

void aethermesh_reputation_record_custody_refusal(AetherMeshNodeReputationService *svc, const char *uhid)
{
    apply_delta(svc, uhid, DELTA_CUSTODY_REFUSAL);
}

void aethermesh_reputation_record_delivery_success(AetherMeshNodeReputationService *svc,
                                               const char *uhid,
                                               int round_trip_ms)
{
    (void)round_trip_ms; /* reserved for future latency weighting */
    apply_delta(svc, uhid, DELTA_DELIVERY_OK);
}

void aethermesh_reputation_record_delivery_failure(AetherMeshNodeReputationService *svc, const char *uhid)
{
    apply_delta(svc, uhid, DELTA_DELIVERY_FAIL);
}

double aethermesh_reputation_get_score(const AetherMeshNodeReputationService *svc, const char *uhid)
{
    for (int i = 0; i < svc->count; i++) {
        if (strncmp(svc->entries[i].uhid, uhid, AETHERMESH_UHID_MAX_LEN - 1) == 0) {
            return svc->entries[i].score;
        }
    }
    return 1.0; /* unknown peer — benefit of the doubt */
}

void aethermesh_reputation_apply_weighted_delta(
    AetherMeshNodeReputationService *svc,
    const char *uhid,
    double weighted_delta)
{
    if (!svc || !uhid) return;
    double clamped = weighted_delta < -1.0 ? -1.0
                   : weighted_delta >  1.0 ?  1.0 : weighted_delta;
    apply_delta(svc, uhid, clamped);
}
