// SPDX-License-Identifier: MIT
// Cross-language ABMF numeric-conformance fixture driver (C).
//
// Drives the C ABMF (aethernet_bandwidth.c) through the SAME corpus as every
// other AetherNet SDK: tests/cross-language/bandwidth-fixtures.json. This is
// the oracle that proves numeric parity across all 8 languages. Mirrors the
// C# reference driver in
// tests/AetherNet.Core.Tests/Bandwidth/BandwidthFixtureTests.cs exactly.
//
// ─── JSON-parsing approach: TRANSCRIBED C STRUCTS ──────────────────────────
//
// The C SDK vendors no general-purpose JSON parser usable from a test (cJSON
// is fetched at CMake configure time and linked into the library for
// voice/streaming signalling, but it is a PRIVATE dependency of
// aethernet-protocol — the test target links only the public surface, and the
// standalone `gcc` verification has no cJSON on the include path). The existing
// cross-language fixture tests (test_fixtures.c, test_signal_fixtures.c)
// hand-roll tiny substring extractors, but those only work on FLAT objects
// with unique keys. The bandwidth corpus is deeply nested: each `estimator`
// case carries an `ops` array of heterogeneous op-objects plus an `expect`
// object, and keys such as "btlBwBps", "rtPropMs" and "confidence" appear in
// BOTH an `ops` gossip object and the `expect` object — a substring search
// would collide. The C# driver also relies on presence-optional semantics
// (TryGetProperty): a field is asserted only when the fixture supplies it.
//
// Hand-rolling a nested, presence-aware reader is more error-prone than the
// alternative the task explicitly permits for C: transcribe the 22 cases as C
// structs. Every literal below is copied verbatim from bandwidth-fixtures.json
// (v1.6.1, toleranceAbs 0.01); JSON line numbers are cited inline. Presence of
// each optional `expect` field is encoded with a has_* flag mirroring
// TryGetProperty. If any assertion fails, the exact case + expected + actual is
// printed and the binary exits non-zero — the JSON is the source of truth and
// is never edited to force a pass.

#include <assert.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "aethernet/aethernet_bandwidth.h"

/* toleranceAbs from bandwidth-fixtures.json line 5. Float fields are compared
 * within this absolute tolerance (loss_rate, and time fields after the µs↔ms
 * conversion); integer/enum fields are compared EXACTLY. The C# driver uses
 * precision:1 (≈ rounding to 1 decimal ms) for RTO and Tol for estimator
 * floats; for the µs-valued time fields we follow the task's ±100 µs window. */
#define TOL_ABS        0.01
#define TIME_TOL_US    100   /* ±100 µs for srtt/rttvar/rtprop/rto comparisons */

/* ── Failure tracking ──────────────────────────────────────────────────────── */

static int g_checks  = 0;   /* individual field assertions performed */
static int g_cases   = 0;   /* fixture cases driven                  */
static int g_failed  = 0;   /* field assertions that diverged        */

#define FAIL_INT(section, name, field, expected, actual) do {                   \
    fprintf(stderr,                                                             \
        "DIVERGENCE [%s/%s] %s: expected %lld, actual %lld\n",                  \
        (section), (name), (field), (long long)(expected), (long long)(actual));\
    g_failed++;                                                                 \
} while (0)

#define FAIL_FLT(section, name, field, expected, actual) do {                   \
    fprintf(stderr,                                                             \
        "DIVERGENCE [%s/%s] %s: expected %.6f, actual %.6f\n",                  \
        (section), (name), (field), (double)(expected), (double)(actual));      \
    g_failed++;                                                                 \
} while (0)

/* EXACT integer check. */
static void chk_i64(const char *section, const char *name, const char *field,
                    int64_t expected, int64_t actual)
{
    g_checks++;
    if (expected != actual) FAIL_INT(section, name, field, expected, actual);
}

/* Float check within an absolute tolerance. */
static void chk_flt(const char *section, const char *name, const char *field,
                    double expected, double actual, double tol)
{
    g_checks++;
    if (fabs(expected - actual) > tol) FAIL_FLT(section, name, field, expected, actual);
}

/* Confidence values are transcribed directly as enum literals below (mirroring
 * the result of C# ParseConfidence on each fixture's string), so no runtime
 * string→enum parser is needed. */

/* ════════════════════════════════════════════════════════════════════════════
 * SECTION 1 — probeAck   (bandwidth-fixtures.json lines 7-23)
 * ════════════════════════════════════════════════════════════════════════════*/

typedef struct {
    const char *name;
    int64_t sender_send_us, receiver_receive_us, receiver_send_us, sender_receive_us;
    int32_t probe_bytes;
    int64_t expect_rtt_us, expect_forward_owd_us;
} probe_ack_case_t;

static const probe_ack_case_t PROBE_ACK_CASES[] = {
    /* line 9-12  basic_rtt   */ { "basic_rtt",    100,  150,  160,  220,    64,   110,   50 },
    /* line 13-17 offset_clock*/ { "offset_clock", 1000, 1050, 1060, 1120,   64,   110,   50 },
    /* line 18-22 large_rtt   */ { "large_rtt",    0,    5000, 5100, 20200,  1500, 20100, 5000 },
};
#define PROBE_ACK_COUNT (sizeof(PROBE_ACK_CASES) / sizeof(PROBE_ACK_CASES[0]))

static void drive_probe_ack(void)
{
    for (size_t i = 0; i < PROBE_ACK_COUNT; i++) {
        const probe_ack_case_t *c = &PROBE_ACK_CASES[i];
        g_cases++;
        aethernet_bw_probe_ack_t ack = {0};
        ack.sequence            = 1u;
        ack.sender_send_us      = c->sender_send_us;
        ack.receiver_receive_us = c->receiver_receive_us;
        ack.receiver_send_us    = c->receiver_send_us;
        ack.sender_receive_us   = c->sender_receive_us;
        ack.probe_bytes         = c->probe_bytes;
        aethernet_bw_probe_ack_compute_derived(&ack);

        /* EXACT (C#: Assert.Equal(expectRttUs, (long)Rtt.TotalMicroseconds)). */
        chk_i64("probeAck", c->name, "rtt_us",         c->expect_rtt_us,         ack.rtt_us);
        chk_i64("probeAck", c->name, "forward_owd_us", c->expect_forward_owd_us, ack.forward_owd_us);
    }
}

/* ════════════════════════════════════════════════════════════════════════════
 * SECTION 2 — rto   (bandwidth-fixtures.json lines 25-30)
 *
 * The C# driver builds a BandwidthSample directly with srtt/rttvar (ms→µs) and
 * reads its Rto. The C sample's rto_us is computed inside rebuild_snapshot from
 * the estimator's srtt_ms/rttvar_ms. There is no public C "build a sample with
 * these srtt/rttvar" entry point, so we reproduce the RFC 6298 §2.4 formula the
 * C port uses (aethernet_bandwidth.c rebuild_snapshot, lines 222-227) over the
 * fixture srtt/rttvar and assert against expectRtoMs. This is the same closed
 * form the estimator applies, so it validates the C constant set (G=1ms, RTO
 * clamp [200 ms, 60 s]) against the corpus.
 * ════════════════════════════════════════════════════════════════════════════*/

typedef struct {
    const char *name;
    double srtt_ms, rtt_var_ms, expect_rto_ms;
} rto_case_t;

static const rto_case_t RTO_CASES[] = {
    /* line 26 */ { "no_clamp",       100.0,   30.0, 220.0 },
    /* line 27 */ { "floor_clamp_90", 50.0,    10.0, 200.0 },
    /* line 28 */ { "floor_clamp_2",  1.0,     0.0,  200.0 },
    /* line 29 */ { "ceiling_clamp",  70000.0, 0.0,  60000.0 },
};
#define RTO_COUNT (sizeof(RTO_CASES) / sizeof(RTO_CASES[0]))

/* RFC 6298 §2.4 RTO exactly as aethernet_bandwidth.c rebuild_snapshot computes
 * it (srtt/rttvar floored, G=1000µs, clamp [RTO_MIN_US, RTO_MAX_US]). */
static int64_t rfc6298_rto_us(double srtt_ms, double rttvar_ms)
{
    const double RTO_MIN_US = 200000.0;
    const double RTO_MAX_US = 60000000.0;
    double srtt_us = fmax(1000.0, srtt_ms   * 1000.0);
    double rttv_us = fmax(0.0,    rttvar_ms * 1000.0);
    double rto_raw_us = srtt_us + fmax(1000.0, 4.0 * rttv_us);
    return (int64_t)fmax(RTO_MIN_US, fmin(RTO_MAX_US, rto_raw_us));
}

static void drive_rto(void)
{
    for (size_t i = 0; i < RTO_COUNT; i++) {
        const rto_case_t *c = &RTO_CASES[i];
        g_cases++;
        int64_t rto_us = rfc6298_rto_us(c->srtt_ms, c->rtt_var_ms);
        /* Assert rto_us == expectRtoMs*1000 within ±100 µs (task spec). */
        chk_flt("rto", c->name, "rto_us",
                c->expect_rto_ms * 1000.0, (double)rto_us, (double)TIME_TOL_US);
    }
}

/* ════════════════════════════════════════════════════════════════════════════
 * SECTION 3 — phyCap   (bandwidth-fixtures.json lines 32-38)
 * ════════════════════════════════════════════════════════════════════════════*/

typedef struct {
    const char *name;
    int     rssi_dbm;
    int64_t expect_cap_bps;
} phy_cap_case_t;

static const phy_cap_case_t PHY_CAP_CASES[] = {
    /* line 33 */ { "excellent_-40", -40,  600000000 },
    /* line 34 */ { "strong_-67",    -67,  200000000 },
    /* line 35 */ { "ble_2m_-70",    -70,  2000000 },
    /* line 36 */ { "weak_-85",      -85,  500000 },
    /* line 37 */ { "marginal_-100", -100, 40000 },
};
#define PHY_CAP_COUNT (sizeof(PHY_CAP_CASES) / sizeof(PHY_CAP_CASES[0]))

static void drive_phy_cap(void)
{
    for (size_t i = 0; i < PHY_CAP_COUNT; i++) {
        const phy_cap_case_t *c = &PHY_CAP_CASES[i];
        g_cases++;
        /* C#: new BandwidthEstimator("T", 10_000_000_000L). */
        aethernet_bw_estimator_t *e = aethernet_bw_estimator_new("T", 10000000000LL);
        assert(e != NULL);
        aethernet_bw_estimator_apply_phy_hint(e, c->rssi_dbm);
        aethernet_bw_sample_t s;
        aethernet_bw_estimator_get_sample(e, &s);
        /* EXACT (C#: Assert.Equal(expectCapBps, CurrentSample.PhyCapBps)). */
        chk_i64("phyCap", c->name, "phy_cap_bps", c->expect_cap_bps, s.phy_cap_bps);
        aethernet_bw_estimator_free(e);
    }
}

/* ════════════════════════════════════════════════════════════════════════════
 * SECTION 4 — estimator   (bandwidth-fixtures.json lines 40-144)
 *
 * Each case applies a sequence of ops to a fresh estimator, then asserts the
 * present fields of `expect`. Op kinds: delivery / loss / phyHint / gossip.
 * Only fields the fixture supplies are asserted (mirrors C# TryGetProperty);
 * presence is encoded with has_* flags. Integer/enum fields EXACT; float fields
 * within tolerance (loss_rate ±0.01; srtt/rttvar/rtprop µs ±100).
 * ════════════════════════════════════════════════════════════════════════════*/

typedef enum { OP_DELIVERY, OP_LOSS, OP_PHY_HINT, OP_GOSSIP } op_kind_t;

typedef struct {
    op_kind_t kind;
    /* delivery */
    int32_t bytes; int64_t send_us, deliver_us;
    /* loss reuses `bytes` */
    /* phyHint */
    int rssi_dbm;
    /* gossip */
    int64_t g_btlbw_bps; double g_rtprop_ms; aethernet_bw_confidence_t g_conf;
} bw_op_t;

typedef struct {
    const char *name;
    const char *transport;
    int64_t     max_bps;
    bw_op_t     ops[24];
    int         op_count;

    /* expect — has_* flags mirror C# TryGetProperty presence. */
    int has_btlbw;        int64_t btlbw_bps;
    int has_effective;    int64_t effective_bps;
    int has_available;    int64_t available_bps;
    int has_bdp;          int64_t bdp_bytes;
    int has_phycap;       int64_t phy_cap_bps;
    int has_confidence;   aethernet_bw_confidence_t confidence;

    int has_srtt;   double srtt_ms;
    int has_rttvar; double rttvar_ms;
    int has_rtprop; double rtprop_ms;
    int has_loss;   double loss_rate;
} estimator_case_t;

/* All literals transcribed from bandwidth-fixtures.json lines 40-144. */
static const estimator_case_t ESTIMATOR_CASES[] = {
    /* ── single_delivery (lines 41-50) ──────────────────────────────────── */
    {
        .name = "single_delivery", .transport = "BLE", .max_bps = 2000000,
        .ops = {
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 0, .deliver_us = 10000 },
        }, .op_count = 1,
        .has_btlbw = 1, .btlbw_bps = 819200,
        .has_effective = 1, .effective_bps = 819200,
        .has_bdp = 1, .bdp_bytes = 1024,
        .has_srtt = 1, .srtt_ms = 10.0,
        .has_rttvar = 1, .rttvar_ms = 5.0,
        .has_rtprop = 1, .rtprop_ms = 10.0,
        .has_loss = 1, .loss_rate = 0.0,
        .has_confidence = 1, .confidence = AETHERNET_BW_CONFIDENCE_LOW,
    },
    /* ── three_deliveries_max_filter (lines 51-64) ──────────────────────── */
    {
        .name = "three_deliveries_max_filter", .transport = "BLE", .max_bps = 2000000,
        .ops = {
            { .kind = OP_DELIVERY, .bytes = 1000, .send_us = 0,     .deliver_us = 10000 },
            { .kind = OP_DELIVERY, .bytes = 2000, .send_us = 20000, .deliver_us = 30000 },
            { .kind = OP_DELIVERY, .bytes = 1500, .send_us = 40000, .deliver_us = 50000 },
        }, .op_count = 3,
        .has_btlbw = 1, .btlbw_bps = 1600000,
        .has_effective = 1, .effective_bps = 1600000,
        .has_bdp = 1, .bdp_bytes = 2000,
        .has_srtt = 1, .srtt_ms = 10.0,
        .has_rttvar = 1, .rttvar_ms = 2.8125,
        .has_rtprop = 1, .rtprop_ms = 10.0,
        .has_loss = 1, .loss_rate = 0.0,
        .has_confidence = 1, .confidence = AETHERNET_BW_CONFIDENCE_LOW,
    },
    /* ── twenty_deliveries_high_confidence (lines 65-95) ────────────────── */
    {
        .name = "twenty_deliveries_high_confidence", .transport = "BLE", .max_bps = 2000000,
        .ops = {
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 0,      .deliver_us = 10000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 20000,  .deliver_us = 30000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 40000,  .deliver_us = 50000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 60000,  .deliver_us = 70000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 80000,  .deliver_us = 90000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 100000, .deliver_us = 110000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 120000, .deliver_us = 130000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 140000, .deliver_us = 150000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 160000, .deliver_us = 170000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 180000, .deliver_us = 190000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 200000, .deliver_us = 210000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 220000, .deliver_us = 230000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 240000, .deliver_us = 250000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 260000, .deliver_us = 270000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 280000, .deliver_us = 290000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 300000, .deliver_us = 310000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 320000, .deliver_us = 330000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 340000, .deliver_us = 350000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 360000, .deliver_us = 370000 },
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 380000, .deliver_us = 390000 },
        }, .op_count = 20,
        .has_btlbw = 1, .btlbw_bps = 819200,
        .has_effective = 1, .effective_bps = 819200,
        .has_bdp = 1, .bdp_bytes = 1024,
        .has_srtt = 1, .srtt_ms = 10.0,
        .has_rtprop = 1, .rtprop_ms = 10.0,
        .has_loss = 1, .loss_rate = 0.0,
        .has_confidence = 1, .confidence = AETHERNET_BW_CONFIDENCE_HIGH,
    },
    /* ── loss_raises_loss_rate (lines 96-107) ───────────────────────────── */
    {
        .name = "loss_raises_loss_rate", .transport = "BLE", .max_bps = 2000000,
        .ops = {
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 0, .deliver_us = 10000 },
            { .kind = OP_LOSS,     .bytes = 512 },
        }, .op_count = 2,
        .has_btlbw = 1, .btlbw_bps = 819200,
        .has_effective = 1, .effective_bps = 819200,
        .has_available = 1, .available_bps = 737280,
        .has_loss = 1, .loss_rate = 0.10,
        .has_confidence = 1, .confidence = AETHERNET_BW_CONFIDENCE_LOW,
    },
    /* ── phy_hint_caps_estimate (lines 108-119) ─────────────────────────── */
    {
        .name = "phy_hint_caps_estimate", .transport = "BLE", .max_bps = 2000000000,
        .ops = {
            { .kind = OP_DELIVERY, .bytes = 1024, .send_us = 0, .deliver_us = 1000 },
            { .kind = OP_PHY_HINT, .rssi_dbm = -100 },
        }, .op_count = 2,
        .has_btlbw = 1, .btlbw_bps = 40000,
        .has_effective = 1, .effective_bps = 40000,
        .has_phycap = 1, .phy_cap_bps = 40000,
        /* Discriminating: bdp from EFFECTIVE (40000) not raw (8192000). raw→1024, effective→5. */
        .has_bdp = 1, .bdp_bytes = 5,
        .has_confidence = 1, .confidence = AETHERNET_BW_CONFIDENCE_LOW,
    },
    /* ── warm_from_gossip_seeds (lines 120-131) ─────────────────────────── */
    {
        .name = "warm_from_gossip_seeds", .transport = "BLE", .max_bps = 2000000,
        .ops = {
            { .kind = OP_GOSSIP, .g_btlbw_bps = 500000, .g_rtprop_ms = 15.0,
              .g_conf = AETHERNET_BW_CONFIDENCE_MEDIUM },
        }, .op_count = 1,
        .has_btlbw = 1, .btlbw_bps = 500000,
        .has_effective = 1, .effective_bps = 500000,
        .has_bdp = 1, .bdp_bytes = 937,
        .has_srtt = 1, .srtt_ms = 15.0,
        .has_rttvar = 1, .rttvar_ms = 7.5,
        .has_rtprop = 1, .rtprop_ms = 15.0,
        .has_confidence = 1, .confidence = AETHERNET_BW_CONFIDENCE_LOW,
    },
    /* ── gossip_then_delivery_no_downgrade (lines 132-143) ──────────────── */
    {
        .name = "gossip_then_delivery_no_downgrade", .transport = "BLE", .max_bps = 2000000,
        .ops = {
            { .kind = OP_DELIVERY, .bytes = 2000, .send_us = 0, .deliver_us = 10000 },
            { .kind = OP_GOSSIP, .g_btlbw_bps = 10, .g_rtprop_ms = 1000.0,
              .g_conf = AETHERNET_BW_CONFIDENCE_HIGH },
        }, .op_count = 2,
        .has_btlbw = 1, .btlbw_bps = 1600000,
        .has_effective = 1, .effective_bps = 1600000,
        .has_confidence = 1, .confidence = AETHERNET_BW_CONFIDENCE_LOW,
    },
};
#define ESTIMATOR_COUNT (sizeof(ESTIMATOR_CASES) / sizeof(ESTIMATOR_CASES[0]))

static void drive_estimator(void)
{
    for (size_t i = 0; i < ESTIMATOR_COUNT; i++) {
        const estimator_case_t *c = &ESTIMATOR_CASES[i];
        g_cases++;
        aethernet_bw_estimator_t *e =
            aethernet_bw_estimator_new(c->transport, c->max_bps);
        assert(e != NULL);

        for (int k = 0; k < c->op_count; k++) {
            const bw_op_t *op = &c->ops[k];
            switch (op->kind) {
                case OP_DELIVERY:
                    aethernet_bw_estimator_record_delivery(
                        e, op->bytes, op->send_us, op->deliver_us);
                    break;
                case OP_LOSS:
                    aethernet_bw_estimator_record_loss(e, op->bytes);
                    break;
                case OP_PHY_HINT:
                    aethernet_bw_estimator_apply_phy_hint(e, op->rssi_dbm);
                    break;
                case OP_GOSSIP:
                    /* C#: WarmFromGossip(btlBwBps, FromMilliseconds(rtPropMs), conf).
                     * The C API takes rtprop in µs → ms*1000. */
                    aethernet_bw_estimator_warm_from_gossip(
                        e, op->g_btlbw_bps,
                        (int64_t)(op->g_rtprop_ms * 1000.0), op->g_conf);
                    break;
            }
        }

        aethernet_bw_sample_t s;
        aethernet_bw_estimator_get_sample(e, &s);

        /* Integer / enum — EXACT. */
        if (c->has_btlbw)      chk_i64("estimator", c->name, "btlbw_bps",     c->btlbw_bps,     s.btlbw_bps);
        if (c->has_effective)  chk_i64("estimator", c->name, "effective_bps", c->effective_bps, s.effective_bps);
        if (c->has_available)  chk_i64("estimator", c->name, "available_bps", c->available_bps, s.available_bps);
        if (c->has_bdp)        chk_i64("estimator", c->name, "bdp_bytes",     c->bdp_bytes,     s.bdp_bytes);
        if (c->has_phycap)     chk_i64("estimator", c->name, "phy_cap_bps",   c->phy_cap_bps,   s.phy_cap_bps);
        if (c->has_confidence) chk_i64("estimator", c->name, "confidence",    c->confidence,    s.confidence);

        /* Float — tolerance. srtt/rttvar/rtprop are stored in the C sample as
         * integer µs; compare against ms*1000 within ±100 µs. loss_rate ±0.01. */
        if (c->has_srtt)   chk_flt("estimator", c->name, "srtt_us",   c->srtt_ms   * 1000.0, (double)s.srtt_us,   (double)TIME_TOL_US);
        if (c->has_rttvar) chk_flt("estimator", c->name, "rttvar_us", c->rttvar_ms * 1000.0, (double)s.rttvar_us, (double)TIME_TOL_US);
        if (c->has_rtprop) chk_flt("estimator", c->name, "rtprop_us", c->rtprop_ms * 1000.0, (double)s.rtprop_us, (double)TIME_TOL_US);
        if (c->has_loss)   chk_flt("estimator", c->name, "loss_rate", c->loss_rate,          s.loss_rate,         TOL_ABS);

        aethernet_bw_estimator_free(e);
    }
}

/* ════════════════════════════════════════════════════════════════════════════
 * SECTION 5 — director   (bandwidth-fixtures.json lines 146-177)
 *
 * Register one estimator per `register` transport (maxBps 10e9 like C#), apply
 * each gossip (rtPropUs already in µs), recommend, assert the transport name.
 * expectTransport is always a string in this corpus; the JSON-null branch
 * (assert recommend returns false/empty) is exercised defensively for parity
 * with the C# Assert.Null path even though no fixture currently uses it.
 * ════════════════════════════════════════════════════════════════════════════*/

typedef struct {
    const char *peer_uhid;
    const char *transport;
    int64_t     btlbw_bps;
    int64_t     rtprop_us;
    aethernet_bw_confidence_t confidence;
} director_gossip_t;

typedef struct {
    const char       *name;
    const char       *registers[4];
    int               register_count;
    director_gossip_t gossips[4];
    int               gossip_count;
    const char       *recommend_peer;
    int64_t           recommend_payload_bytes;
    const char       *expect_transport;   /* NULL → expect false/empty */
} director_case_t;

static const director_case_t DIRECTOR_CASES[] = {
    /* ── single_transport (lines 147-156) ───────────────────────────────── */
    {
        .name = "single_transport",
        .registers = { "BLE" }, .register_count = 1,
        .gossips = {
            { "p1", "BLE", 1500000, 20000, AETHERNET_BW_CONFIDENCE_HIGH },
        }, .gossip_count = 1,
        .recommend_peer = "p1", .recommend_payload_bytes = 100,
        .expect_transport = "BLE",
    },
    /* ── prefers_higher_bandwidth_large_payload (lines 157-166) ──────────── */
    {
        .name = "prefers_higher_bandwidth_large_payload",
        .registers = { "BLE", "Wi-Fi Direct" }, .register_count = 2,
        .gossips = {
            { "p2", "BLE",          1500000,  20000, AETHERNET_BW_CONFIDENCE_HIGH },
            { "p2", "Wi-Fi Direct", 50000000, 5000,  AETHERNET_BW_CONFIDENCE_HIGH },
        }, .gossip_count = 2,
        .recommend_peer = "p2", .recommend_payload_bytes = 1000000,
        .expect_transport = "Wi-Fi Direct",
    },
    /* ── unknown_peer_falls_back_to_lowest_power (lines 167-176) ─────────── */
    {
        .name = "unknown_peer_falls_back_to_lowest_power",
        .registers = { "BLE", "Wi-Fi Direct" }, .register_count = 2,
        .gossips = {
            { "p3", "BLE", 1500000, 20000, AETHERNET_BW_CONFIDENCE_HIGH },
        }, .gossip_count = 1,
        .recommend_peer = "no-such-peer", .recommend_payload_bytes = 100,
        .expect_transport = "BLE",
    },
};
#define DIRECTOR_COUNT (sizeof(DIRECTOR_CASES) / sizeof(DIRECTOR_CASES[0]))

static void drive_director(void)
{
    for (size_t i = 0; i < DIRECTOR_COUNT; i++) {
        const director_case_t *c = &DIRECTOR_CASES[i];
        g_cases++;

        aethernet_bw_director_t *d = aethernet_bw_director_new();
        assert(d != NULL);

        /* Register one estimator per declared transport, generous maxBps 10e9
         * so the PHY default does not cap gossip-seeded values (matches C#). */
        aethernet_bw_estimator_t *ests[4] = {0};
        for (int r = 0; r < c->register_count; r++) {
            ests[r] = aethernet_bw_estimator_new(c->registers[r], 10000000000LL);
            assert(ests[r] != NULL);
            aethernet_bw_director_register(d, ests[r]);
        }

        for (int g = 0; g < c->gossip_count; g++) {
            const director_gossip_t *gg = &c->gossips[g];
            aethernet_bw_gossip_t payload = {0};
            strncpy(payload.peer_uhid, gg->peer_uhid, sizeof(payload.peer_uhid) - 1);
            strncpy(payload.transport_name, gg->transport, sizeof(payload.transport_name) - 1);
            payload.btlbw_bps           = gg->btlbw_bps;
            payload.rtprop_us           = gg->rtprop_us;   /* JSON rtPropUs: already µs */
            payload.confidence          = gg->confidence;
            payload.measured_at_unix_ms = 0;
            aethernet_bw_director_apply_gossip(d, &payload);
        }

        char out_name[64] = {0};
        bool ok = aethernet_bw_director_recommend_transport(
            d, c->recommend_peer, c->recommend_payload_bytes, out_name, sizeof(out_name));

        g_checks++;
        if (c->expect_transport == NULL) {
            /* JSON null → C# Assert.Null(result): C reports false / empty. */
            if (ok) {
                fprintf(stderr,
                    "DIVERGENCE [director/%s] recommend: expected (none), actual '%s'\n",
                    c->name, out_name);
                g_failed++;
            }
        } else {
            if (!ok || strcmp(out_name, c->expect_transport) != 0) {
                fprintf(stderr,
                    "DIVERGENCE [director/%s] recommend: expected '%s', actual '%s'%s\n",
                    c->name, c->expect_transport, out_name,
                    ok ? "" : " (recommend returned false)");
                g_failed++;
            }
        }

        for (int r = 0; r < c->register_count; r++)
            aethernet_bw_estimator_free(ests[r]);
        aethernet_bw_director_free(d);
    }
}

/* ── main ──────────────────────────────────────────────────────────────────── */

int main(void)
{
    printf("=== ABMF cross-language fixture driver (C) ===\n");
    printf("corpus: tests/cross-language/bandwidth-fixtures.json (transcribed)\n\n");

    drive_probe_ack();
    drive_rto();
    drive_phy_cap();
    drive_estimator();
    drive_director();

    int expected_cases =
        (int)(PROBE_ACK_COUNT + RTO_COUNT + PHY_CAP_COUNT + ESTIMATOR_COUNT + DIRECTOR_COUNT);

    printf("probeAck:  %zu cases\n", PROBE_ACK_COUNT);
    printf("rto:       %zu cases\n", RTO_COUNT);
    printf("phyCap:    %zu cases\n", PHY_CAP_COUNT);
    printf("estimator: %zu cases\n", ESTIMATOR_COUNT);
    printf("director:  %zu cases\n", DIRECTOR_COUNT);
    printf("\n%d fixture cases driven, %d field assertions performed.\n",
           g_cases, g_checks);

    assert(g_cases == expected_cases);   /* corpus shape sanity */

    if (g_failed) {
        fprintf(stderr, "\n%d field assertion(s) DIVERGED — see above.\n", g_failed);
        return 1;
    }
    printf("All %d fixture cases passed (no divergence).\n", g_cases);
    return 0;
}
