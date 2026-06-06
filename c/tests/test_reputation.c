// SPDX-License-Identifier: MIT
// Unit tests for aethernet_reputation.c (NodeReputationService).

#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

#include "aethernet_reputation.h"

// ─── Test runner ─────────────────────────────────────────────────────────────

#define RUN(name) do { printf("TEST: " #name "..."); name(); printf(" OK\n"); tests_run++; } while (0)
static int tests_run = 0;

// ─── Tests ───────────────────────────────────────────────────────────────────

static void unknown_peer_returns_1_0(void)
{
    AetherNetNodeReputationService svc;
    aethernet_reputation_init(&svc);

    double score = aethernet_reputation_get_score(&svc, "alice");
    assert(fabs(score - 1.0) < 1e-9);
}

static void rreq_flood_gives_0_95(void)
{
    AetherNetNodeReputationService svc;
    aethernet_reputation_init(&svc);

    aethernet_reputation_record_rreq_flood(&svc, "alice");
    double score = aethernet_reputation_get_score(&svc, "alice");
    assert(fabs(score - 0.95) < 1e-9);
}

static void replay_gives_0_85(void)
{
    AetherNetNodeReputationService svc;
    aethernet_reputation_init(&svc);

    aethernet_reputation_record_replay(&svc, "alice");
    double score = aethernet_reputation_get_score(&svc, "alice");
    assert(fabs(score - 0.85) < 1e-9);
}

static void sig_failure_gives_0_80(void)
{
    AetherNetNodeReputationService svc;
    aethernet_reputation_init(&svc);

    aethernet_reputation_record_sig_failure(&svc, "alice");
    double score = aethernet_reputation_get_score(&svc, "alice");
    assert(fabs(score - 0.80) < 1e-9);
}

static void custody_refusal_gives_0_95(void)
{
    AetherNetNodeReputationService svc;
    aethernet_reputation_init(&svc);

    aethernet_reputation_record_custody_refusal(&svc, "alice");
    double score = aethernet_reputation_get_score(&svc, "alice");
    assert(fabs(score - 0.95) < 1e-9);
}

static void delivery_failure_gives_0_98(void)
{
    AetherNetNodeReputationService svc;
    aethernet_reputation_init(&svc);

    aethernet_reputation_record_delivery_failure(&svc, "alice");
    double score = aethernet_reputation_get_score(&svc, "alice");
    assert(fabs(score - 0.98) < 1e-9);
}

static void five_sig_failures_floors_to_zero(void)
{
    AetherNetNodeReputationService svc;
    aethernet_reputation_init(&svc);

    // 5 × -0.20 = -1.00 from 1.0 → clamped to 0.0
    for (int i = 0; i < 5; i++) {
        aethernet_reputation_record_sig_failure(&svc, "attacker");
    }
    double score = aethernet_reputation_get_score(&svc, "attacker");
    assert(fabs(score) < 1e-9);
}

static void ten_delivery_successes_ceil_to_one(void)
{
    AetherNetNodeReputationService svc;
    aethernet_reputation_init(&svc);

    // Start from 1.0; 10 × +0.01 would exceed 1.0 → clamped to 1.0
    for (int i = 0; i < 10; i++) {
        aethernet_reputation_record_delivery_success(&svc, "good-node", 50);
    }
    double score = aethernet_reputation_get_score(&svc, "good-node");
    assert(fabs(score - 1.0) < 1e-9);
}

static void no_cross_contamination(void)
{
    AetherNetNodeReputationService svc;
    aethernet_reputation_init(&svc);

    // Penalise alice heavily.
    for (int i = 0; i < 5; i++) {
        aethernet_reputation_record_sig_failure(&svc, "alice");
    }

    // bob is untouched — must still be 1.0.
    double bob_score = aethernet_reputation_get_score(&svc, "bob");
    assert(fabs(bob_score - 1.0) < 1e-9);

    // alice is at 0.0.
    double alice_score = aethernet_reputation_get_score(&svc, "alice");
    assert(fabs(alice_score) < 1e-9);
}

static void compound_signals_give_0_60(void)
{
    AetherNetNodeReputationService svc;
    aethernet_reputation_init(&svc);

    // Start: 1.0
    // rreq flood:       1.0 - 0.05 = 0.95
    // replay:           0.95 - 0.15 = 0.80
    // sig failure:      0.80 - 0.20 = 0.60
    aethernet_reputation_record_rreq_flood(&svc, "node");
    aethernet_reputation_record_replay(&svc, "node");
    aethernet_reputation_record_sig_failure(&svc, "node");

    double score = aethernet_reputation_get_score(&svc, "node");
    assert(fabs(score - 0.60) < 1e-9);
}

// ─── main ─────────────────────────────────────────────────────────────────────

int main(void)
{
    printf("Aether NodeReputationService — Unit Tests\n");
    printf("==========================================\n");

    RUN(unknown_peer_returns_1_0);
    RUN(rreq_flood_gives_0_95);
    RUN(replay_gives_0_85);
    RUN(sig_failure_gives_0_80);
    RUN(custody_refusal_gives_0_95);
    RUN(delivery_failure_gives_0_98);
    RUN(five_sig_failures_floors_to_zero);
    RUN(ten_delivery_successes_ceil_to_one);
    RUN(no_cross_contamination);
    RUN(compound_signals_give_0_60);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
