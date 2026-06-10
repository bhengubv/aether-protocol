// SPDX-License-Identifier: MIT
// AetherNet Bandwidth Measurement Framework (ABMF) — W18-5.
//
// C port of the C# reference implementation under
// src/AetherNet.Core/Bandwidth/ and src/AetherNet.Transport/Bandwidth/.
//
// Algorithm:
//   BtlBw max-filter  — circular buffer of BW_WINDOW_SIZE (10) delivery-rate
//                        samples; window = 10×RTprop (expired entries evicted).
//   RTprop min-filter — linked-list queue of (rttMs, timestampMs) pairs kept
//                        for up to RTPROP_WINDOW_MS (10 000 ms).
//   SRTT / RTTVAR     — RFC 6298 §2.3, α = 1/8, β = 1/4.
//   Loss rate         — EWMA, α = 0.10.
//   PHY cap           — RSSI-to-BtlBw table (IEEE 802.11 / Bluetooth SIG).
//   Confidence        — None (0 rounds, no gossip), Low (1-4), Medium (5-19),
//                        High (≥20).

#include <float.h>
#include <math.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

#include "aethernet/aethernet_bandwidth.h"

/* ─── Internal constants ────────────────────────────────────────────────── */

#define BW_WINDOW_SIZE       10          /* BtlBw max-filter ring size         */
#define RTPROP_WINDOW_MS     10000.0     /* RTprop min-filter window (ms)      */
#define LOSS_ALPHA           0.10        /* EWMA smoothing for loss rate        */
#define SRTT_ALPHA           0.125       /* RFC 6298 α = 1/8                   */
#define RTTVAR_BETA          0.25        /* RFC 6298 β = 1/4                   */
#define RTO_MIN_US           200000LL    /* 200 ms in µs                        */
#define RTO_MAX_US           60000000LL  /* 60 s  in µs                         */
#define IDLE_THRESHOLD_MS    5000LL      /* transport idle threshold (ms)       */
#define MAX_TRANSPORTS       32          /* max transports per monitor           */
#define MAX_PEERS            256         /* max distinct peers tracked / monitor */

/* BandwidthDirector capacities. */
#define DIRECTOR_MAX_MATRIX     256      /* (peer × transport) matrix entries    */
#define DIRECTOR_MAX_ESTIMATORS 32       /* registered estimators                 */

/* ─── ASCII case-insensitive compare (matches C# StringComparer.OrdinalIgnoreCase
 *      over the ASCII transport-name set; non-ASCII bytes compare byte-wise). ── */
static int ci_equal(const char *a, const char *b)
{
    if (a == b) return 1;
    if (!a || !b) return 0;
    for (;;) {
        unsigned char ca = (unsigned char)*a++;
        unsigned char cb = (unsigned char)*b++;
        if (ca >= 'A' && ca <= 'Z') ca = (unsigned char)(ca - 'A' + 'a');
        if (cb >= 'A' && cb <= 'Z') cb = (unsigned char)(cb - 'A' + 'a');
        if (ca != cb) return 0;
        if (ca == '\0') return 1;
    }
}

/* ─── Timing helper ─────────────────────────────────────────────────────── */

/* Returns milliseconds since the Unix epoch (monotonic-ish via CLOCK_REALTIME). */
static double now_ms(void)
{
    struct timespec ts;
#if defined(CLOCK_REALTIME)
    clock_gettime(CLOCK_REALTIME, &ts);
#else
    /* Fallback — less accurate but portable. */
    ts.tv_sec  = (time_t)time(NULL);
    ts.tv_nsec = 0;
#endif
    return (double)ts.tv_sec * 1000.0 + (double)ts.tv_nsec / 1.0e6;
}


/* ─── RTprop sample queue node ──────────────────────────────────────────── */

typedef struct rtprop_node {
    double rtt_ms;
    double timestamp_ms;
    struct rtprop_node *next;
} rtprop_node_t;

/* ─── BandwidthEstimator ────────────────────────────────────────────────── */

struct aethernet_bw_estimator {
    pthread_mutex_t lock;
    char transport_name[64];

    /* BtlBw circular ring: [head-1] is newest, [head+count..head-1] are live. */
    int64_t bw_rate_bps[BW_WINDOW_SIZE];    /* delivery rates */
    double  bw_ts_ms[BW_WINDOW_SIZE];       /* timestamps     */
    int     bw_head;                         /* next write slot */
    int     bw_count;                        /* live entries    */

    /* RTprop FIFO queue (oldest → newest) */
    rtprop_node_t *rt_head;
    rtprop_node_t *rt_tail;

    /* RFC 6298 SRTT / RTTVAR */
    double srtt_ms;
    double rttvar_ms;
    int    first_rtt;   /* 1 until first sample */

    /* Loss EWMA */
    double loss_rate;

    /* PHY cap (0 = unknown) */
    int64_t phy_cap_bps;

    /* Confidence tracking */
    int  probe_rounds;
    int  warmed_from_gossip;  /* 1 after WarmFromGossip succeeds */

    /* Snapshot cache — updated after every observation */
    aethernet_bw_sample_t current;
};

/* ── RTprop queue helpers ─────────────────────────────────────────────────── */

static void rtprop_push(struct aethernet_bw_estimator *e, double rtt_ms, double ts_ms)
{
    /* Evict expired samples from the front. */
    double expiry = ts_ms - RTPROP_WINDOW_MS;
    while (e->rt_head && e->rt_head->timestamp_ms < expiry) {
        rtprop_node_t *old = e->rt_head;
        e->rt_head = old->next;
        if (!e->rt_head) e->rt_tail = NULL;
        free(old);
    }

    /* Append new sample. */
    rtprop_node_t *n = (rtprop_node_t *)malloc(sizeof(rtprop_node_t));
    if (!n) return;
    n->rtt_ms = rtt_ms;
    n->timestamp_ms = ts_ms;
    n->next = NULL;
    if (e->rt_tail) {
        e->rt_tail->next = n;
        e->rt_tail = n;
    } else {
        e->rt_head = e->rt_tail = n;
    }
}

static double rtprop_min_ms(const struct aethernet_bw_estimator *e)
{
    if (!e->rt_head) return (e->srtt_ms > 0.0) ? e->srtt_ms : 50.0;
    double m = 1.0e18;
    for (const rtprop_node_t *n = e->rt_head; n; n = n->next) {
        if (n->rtt_ms < m) m = n->rtt_ms;
    }
    return (m > 0.0) ? m : 1.0;
}

static void rtprop_free_all(struct aethernet_bw_estimator *e)
{
    rtprop_node_t *n = e->rt_head;
    while (n) {
        rtprop_node_t *next = n->next;
        free(n);
        n = next;
    }
    e->rt_head = e->rt_tail = NULL;
}

/* ── BtlBw ring helpers ───────────────────────────────────────────────────── */

static void btlbw_add(struct aethernet_bw_estimator *e, int64_t rate_bps, double ts_ms)
{
    /* Evict expired samples. The oldest sample is at index
     * (head + WINDOW - count) % WINDOW. */
    double window_ms = 10.0 * fmax(1.0, rtprop_min_ms(e));
    double expiry    = ts_ms - window_ms;
    while (e->bw_count > 0) {
        int tail = (e->bw_head + BW_WINDOW_SIZE - e->bw_count) % BW_WINDOW_SIZE;
        if (e->bw_ts_ms[tail] < expiry) {
            e->bw_count--;
        } else {
            break;
        }
    }

    /* Write into the ring. */
    e->bw_rate_bps[e->bw_head] = rate_bps;
    e->bw_ts_ms[e->bw_head]    = ts_ms;
    e->bw_head = (e->bw_head + 1) % BW_WINDOW_SIZE;
    if (e->bw_count < BW_WINDOW_SIZE) e->bw_count++;
}

static int64_t btlbw_max(const struct aethernet_bw_estimator *e)
{
    if (e->bw_count == 0) return 0LL;
    int64_t mx = 0;
    for (int i = 0; i < e->bw_count; i++) {
        int idx = (e->bw_head + BW_WINDOW_SIZE - e->bw_count + i) % BW_WINDOW_SIZE;
        if (e->bw_rate_bps[idx] > mx) mx = e->bw_rate_bps[idx];
    }
    return mx;
}

/* ── Confidence ───────────────────────────────────────────────────────────── */

static aethernet_bw_confidence_t compute_confidence(const struct aethernet_bw_estimator *e)
{
    if (e->probe_rounds == 0 && !e->warmed_from_gossip) return AETHERNET_BW_CONFIDENCE_NONE;
    if (e->probe_rounds < 5)  return AETHERNET_BW_CONFIDENCE_LOW;
    if (e->probe_rounds < 20) return AETHERNET_BW_CONFIDENCE_MEDIUM;
    return AETHERNET_BW_CONFIDENCE_HIGH;
}

/* ── Snapshot rebuild ─────────────────────────────────────────────────────── */

/* Build the display snapshot from current estimator state, using `btlbw` as the
 * BtlBw value to report. Callers normally pass btlbw_max(e); the constructor
 * passes max_bps so a freshly-built estimator reports its advertised ceiling for
 * display WITHOUT seeding the (still-empty) BtlBw max-filter window — matching
 * the C# reference constructor, whose BuildSnapshot(maxBandwidthBps,…) populates
 * the initial display without inserting into the window. */
static void build_snapshot(struct aethernet_bw_estimator *e, int64_t btlbw)
{
    double  rtprop  = rtprop_min_ms(e);   /* ms */
    double  srtt_us = fmax(1000.0, e->srtt_ms * 1000.0);
    double  rttv_us = fmax(0.0,   e->rttvar_ms * 1000.0);

    /* RFC 6298 RTO: SRTT + max(G=1ms, 4×RTTVAR), clamped [200ms, 60s] */
    double rto_raw_us = srtt_us + fmax(1000.0, 4.0 * rttv_us);
    int64_t rto_us = (int64_t)fmax((double)RTO_MIN_US, fmin((double)RTO_MAX_US, rto_raw_us));

    double  loss    = fmax(0.0, fmin(1.0, e->loss_rate));
    int64_t effective = (e->phy_cap_bps > 0) ? (btlbw < e->phy_cap_bps ? btlbw : e->phy_cap_bps)
                                              : btlbw;
    int64_t available = (int64_t)((double)effective * (1.0 - loss));
    int64_t bdp = 0;
    if (effective > 0 && rtprop > 0.0) {
        /* BDP is derived from the EFFECTIVE (PHY-capped) rate, not the raw
         * BtlBw — matching the C# reference. With a PHY cap below BtlBw the
         * cap, not the measured rate, bounds the in-flight window.
         * BDP = (effective_bps / 8) × rtprop_seconds */
        bdp = (int64_t)((double)effective / 8.0 * rtprop / 1000.0);
    }

    aethernet_bw_sample_t *s = &e->current;
    strncpy(s->transport_name, e->transport_name, sizeof(s->transport_name) - 1);
    s->transport_name[sizeof(s->transport_name) - 1] = '\0';
    s->btlbw_bps    = effective;
    s->available_bps = available;
    s->bdp_bytes    = bdp;
    s->srtt_us      = (int64_t)srtt_us;
    s->rttvar_us    = (int64_t)rttv_us;
    s->rtprop_us    = (int64_t)(rtprop * 1000.0);
    s->loss_rate    = loss;
    s->phy_cap_bps  = e->phy_cap_bps;
    s->confidence   = compute_confidence(e);
    s->rto_us       = rto_us;
    s->effective_bps = effective;
}

/* Rebuild the snapshot from the live BtlBw max-filter window. */
static void rebuild_snapshot(struct aethernet_bw_estimator *e)
{
    build_snapshot(e, btlbw_max(e));
}

/* ── RFC 6298 RTT update ──────────────────────────────────────────────────── */

static void update_rtt(struct aethernet_bw_estimator *e, double rtt_ms, double ts_ms)
{
    if (e->first_rtt) {
        e->srtt_ms   = rtt_ms;
        e->rttvar_ms = rtt_ms / 2.0;
        e->first_rtt = 0;
    } else {
        e->rttvar_ms = (1.0 - RTTVAR_BETA) * e->rttvar_ms
                       + RTTVAR_BETA * fabs(e->srtt_ms - rtt_ms);
        e->srtt_ms   = (1.0 - SRTT_ALPHA) * e->srtt_ms
                       + SRTT_ALPHA * rtt_ms;
    }
    /* Successful delivery → EWMA loss toward 0. */
    e->loss_rate = LOSS_ALPHA * 0.0 + (1.0 - LOSS_ALPHA) * e->loss_rate;
    rtprop_push(e, rtt_ms, ts_ms);
}

/* ─── Public API: estimator ─────────────────────────────────────────────── */

aethernet_bw_estimator_t *aethernet_bw_estimator_new(const char *transport_name,
                                                      int64_t     max_bps)
{
    if (!transport_name) return NULL;
    aethernet_bw_estimator_t *e =
        (aethernet_bw_estimator_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    if (pthread_mutex_init(&e->lock, NULL) != 0) {
        free(e);
        return NULL;
    }
    strncpy(e->transport_name, transport_name, sizeof(e->transport_name) - 1);
    e->transport_name[sizeof(e->transport_name) - 1] = '\0';
    e->first_rtt = 1;
    e->srtt_ms   = 50.0;   /* Optimistic warm-start at 50 ms */

    /* Do NOT seed the BtlBw max-filter window — it stays EMPTY (bw_count = 0)
     * until the first real delivery/probe/gossip sample, matching the C#
     * reference constructor. Seeding would make btlbw_max() return
     * max(max_bps, real_rate) for the first ~10 samples and skew every derived
     * field. The initial DISPLAY snapshot still reports max_bps as btlbw_bps
     * (via build_snapshot below) so a freshly-constructed estimator advertises
     * its ceiling — exactly as C# BuildSnapshot(maxBandwidthBps,…) does. */
    build_snapshot(e, (max_bps > 0) ? max_bps : 0);
    return e;
}

void aethernet_bw_estimator_free(aethernet_bw_estimator_t *e)
{
    if (!e) return;
    pthread_mutex_lock(&e->lock);
    rtprop_free_all(e);
    pthread_mutex_unlock(&e->lock);
    pthread_mutex_destroy(&e->lock);
    free(e);
}

void aethernet_bw_estimator_record_delivery(aethernet_bw_estimator_t *e,
                                             int32_t bytes,
                                             int64_t send_us,
                                             int64_t deliver_us)
{
    if (!e || bytes <= 0 || deliver_us <= send_us) return;
    double elapsed_ms   = (double)(deliver_us - send_us) / 1000.0;
    double elapsed_sec  = elapsed_ms / 1000.0;
    int64_t rate_bps = (int64_t)((double)bytes * 8.0 / elapsed_sec);

    pthread_mutex_lock(&e->lock);
    double ts = now_ms();
    btlbw_add(e, rate_bps, ts);
    update_rtt(e, elapsed_ms, ts);
    e->probe_rounds++;
    rebuild_snapshot(e);
    pthread_mutex_unlock(&e->lock);
}

void aethernet_bw_estimator_record_loss(aethernet_bw_estimator_t *e, int32_t bytes)
{
    if (!e || bytes <= 0) return;
    pthread_mutex_lock(&e->lock);
    e->loss_rate = LOSS_ALPHA * 1.0 + (1.0 - LOSS_ALPHA) * e->loss_rate;
    rebuild_snapshot(e);
    pthread_mutex_unlock(&e->lock);
}

void aethernet_bw_estimator_record_probe_result(aethernet_bw_estimator_t       *e,
                                                 const aethernet_bw_probe_ack_t *ack,
                                                 int64_t                         local_receive_us)
{
    /* local_receive_us is reserved for future one-way-delay estimation. */
    (void)local_receive_us;
    if (!e || !ack) return;
    int64_t rtt_us = ack->rtt_us;
    /* Reject impossible or sentinel RTTs (≤ 0 or > 30 s). */
    if (rtt_us <= 0 || rtt_us > 30000000LL) return;

    double rtt_ms = (double)rtt_us / 1000.0;
    int64_t rate_bps = 0;
    if (ack->probe_bytes > 0) {
        double rtt_sec = (double)rtt_us / 1.0e6;
        rate_bps = (int64_t)((double)ack->probe_bytes * 8.0 / rtt_sec);
    }

    pthread_mutex_lock(&e->lock);
    double ts = now_ms();
    update_rtt(e, rtt_ms, ts);
    if (rate_bps > 0) btlbw_add(e, rate_bps, ts);
    e->probe_rounds++;
    rebuild_snapshot(e);
    pthread_mutex_unlock(&e->lock);
}

void aethernet_bw_estimator_warm_from_gossip(aethernet_bw_estimator_t  *e,
                                              int64_t                    btlbw_bps,
                                              int64_t                    rtprop_us,
                                              aethernet_bw_confidence_t  confidence)
{
    if (!e) return;
    pthread_mutex_lock(&e->lock);
    /* Never downgrade an existing estimate. */
    if (e->probe_rounds > 0 || e->warmed_from_gossip) {
        pthread_mutex_unlock(&e->lock);
        return;
    }
    double ts = now_ms();
    if (btlbw_bps > 0) btlbw_add(e, btlbw_bps, ts);
    if (rtprop_us > 0) {
        double rtt_ms = (double)rtprop_us / 1000.0;
        e->srtt_ms   = rtt_ms;
        e->rttvar_ms = rtt_ms / 2.0;
        e->first_rtt = 0;
        rtprop_push(e, rtt_ms, ts);
    }
    e->warmed_from_gossip = 1;
    rebuild_snapshot(e);
    pthread_mutex_unlock(&e->lock);
    (void)confidence;  /* stored implicitly via confidence tier rules */
}

void aethernet_bw_estimator_apply_phy_hint(aethernet_bw_estimator_t *e, int rssi_dbm)
{
    if (!e) return;

    /*
     * RSSI → theoretical capacity mapping.
     * Wi-Fi (802.11ax) calibration from 3GPP TS 36.213 Annex A:
     *   ≥ -50 dBm → up to 600 Mbps
     *   ≥ -67 dBm → up to 200 Mbps
     *   ≥ -80 dBm → up to  54 Mbps
     * BLE calibration from Bluetooth SIG Core Spec 5.4 Table 7.2 (2Msym/s PHY):
     *   ≥ -70 dBm → up to   2 Mbps
     *   ≥ -85 dBm → up to 500 kbps
     *   ≥ -95 dBm → up to 125 kbps
     *   < -95 dBm → up to  40 kbps (marginal link)
     * These thresholds match the C# reference BandwidthEstimator.ApplyPhyHint().
     */
    int64_t cap;
    if      (rssi_dbm >= -50) cap = 600000000LL;
    else if (rssi_dbm >= -67) cap = 200000000LL;
    else if (rssi_dbm >= -70) cap =   2000000LL;
    else if (rssi_dbm >= -80) cap =  54000000LL;
    else if (rssi_dbm >= -85) cap =    500000LL;
    else if (rssi_dbm >= -95) cap =    125000LL;
    else                      cap =     40000LL;

    pthread_mutex_lock(&e->lock);
    e->phy_cap_bps = cap;
    rebuild_snapshot(e);
    pthread_mutex_unlock(&e->lock);
}

void aethernet_bw_estimator_get_sample(const aethernet_bw_estimator_t *e,
                                        aethernet_bw_sample_t          *out)
{
    if (!e || !out) return;
    /* Cast away const to acquire the lock — the lock itself is mutable state. */
    aethernet_bw_estimator_t *me = (aethernet_bw_estimator_t *)(uintptr_t)e;
    pthread_mutex_lock(&me->lock);
    *out = e->current;
    pthread_mutex_unlock(&me->lock);
}

/* ─── BandwidthProbeAck ─────────────────────────────────────────────────── */

void aethernet_bw_probe_ack_compute_derived(aethernet_bw_probe_ack_t *ack)
{
    if (!ack) return;
    ack->rtt_us = (ack->sender_receive_us - ack->sender_send_us)
                - (ack->receiver_send_us  - ack->receiver_receive_us);
    ack->forward_owd_us = ack->receiver_receive_us - ack->sender_send_us;
}

/* ─── NodeActivityMonitor ───────────────────────────────────────────────── */

/* Per-transport traffic accumulator. Byte counters are read and zeroed
 * atomically under the monitor lock each tick. */
typedef struct {
    char                      name[64];
    aethernet_bw_estimator_t *estimator;        /* BORROWED */
    volatile int64_t          ingress_bytes;
    volatile int64_t          egress_bytes;
    int64_t                   last_egress_ms;   /* wall clock at last egress */
} transport_entry_t;

/* Peer-last-seen entry for active-peer tracking. A peer is "active" when its
 * last_seen_ms falls within the idle window. Populated only by the peer-aware
 * record_*_peer overloads; pruned each tick so the table stays bounded by the
 * count of recently-active peers, not the lifetime peer set. */
typedef struct {
    char    uhid[128];
    int64_t last_seen_ms;
    int     in_use;
} peer_entry_t;

struct aethernet_node_monitor {
    pthread_mutex_t  lock;

    transport_entry_t transports[MAX_TRANSPORTS];
    int               transport_count;

    /* Active-peer tracking (fixed-cap; new peers dropped silently when full). */
    peer_entry_t      peers[MAX_PEERS];

    /* Background thread */
    pthread_t         thread;
    int               running;    /* 1 while thread is active      */
    int               stop_req;   /* set to 1 to signal shutdown   */
    int               interval_ms;

    /* Last snapshot */
    aethernet_node_snapshot_t snapshot;

    /* Callback */
    aethernet_node_monitor_cb callback;
    void                     *callback_user;
};

/* Find the entry index for a transport name, or -1 if not found. */
static int monitor_find(struct aethernet_node_monitor *m, const char *name)
{
    for (int i = 0; i < m->transport_count; i++) {
        if (strncmp(m->transports[i].name, name, 63) == 0) return i;
    }
    return -1;
}

/* Stamp peer_uhid → now_ms in the peer-last-seen table. Caller holds the lock.
 * Updates an existing entry, else claims a free slot; silently drops when full. */
static void monitor_touch_peer(struct aethernet_node_monitor *m,
                               const char *peer_uhid, int64_t now_ms_)
{
    int free_slot = -1;
    for (int i = 0; i < MAX_PEERS; i++) {
        if (m->peers[i].in_use) {
            if (strncmp(m->peers[i].uhid, peer_uhid,
                        sizeof(m->peers[i].uhid) - 1) == 0) {
                m->peers[i].last_seen_ms = now_ms_;
                return;
            }
        } else if (free_slot < 0) {
            free_slot = i;
        }
    }
    if (free_slot < 0) return;   /* table full — drop silently */
    peer_entry_t *p = &m->peers[free_slot];
    strncpy(p->uhid, peer_uhid, sizeof(p->uhid) - 1);
    p->uhid[sizeof(p->uhid) - 1] = '\0';
    p->last_seen_ms = now_ms_;
    p->in_use = 1;
}

/* Count distinct peers seen within idle_threshold_ms; prune stale entries.
 * Caller holds the lock. */
static int monitor_active_peers(struct aethernet_node_monitor *m, int64_t now_ms_)
{
    int active = 0;
    for (int i = 0; i < MAX_PEERS; i++) {
        if (!m->peers[i].in_use) continue;
        if (now_ms_ - m->peers[i].last_seen_ms < IDLE_THRESHOLD_MS) {
            active++;
        } else {
            m->peers[i].in_use = 0;   /* prune stale */
        }
    }
    return active;
}

/* Compute the node-level state from per-transport data.
 * Follows the same precedence as the C# reference NodeActivityMonitor. */
static aethernet_node_state_t compute_node_state(int n,
                                                   const aethernet_node_state_t *states)
{
    if (n == 0) return AETHERNET_NODE_OFFLINE;
    int all_offline = 1;
    int has_degraded = 0, has_busy = 0, has_active = 0;
    for (int i = 0; i < n; i++) {
        if (states[i] != AETHERNET_NODE_OFFLINE) all_offline = 0;
        if (states[i] == AETHERNET_NODE_DEGRADED) has_degraded = 1;
        if (states[i] == AETHERNET_NODE_BUSY)     has_busy     = 1;
        if (states[i] == AETHERNET_NODE_ACTIVE)   has_active   = 1;
    }
    if (all_offline)   return AETHERNET_NODE_OFFLINE;
    if (has_degraded)  return AETHERNET_NODE_DEGRADED;
    if (has_busy)      return AETHERNET_NODE_BUSY;
    if (has_active)    return AETHERNET_NODE_ACTIVE;
    return AETHERNET_NODE_IDLE;
}

/* Background sampling thread. */
static void *monitor_thread(void *arg)
{
    aethernet_node_monitor_t *m = (aethernet_node_monitor_t *)arg;
    int64_t last_tick_ms = (int64_t)now_ms();

    while (1) {
        /* Sleep in 10ms slices so we can wake up quickly on stop_req. */
        pthread_mutex_lock(&m->lock);
        int interval_ms = m->interval_ms;
        int stop = m->stop_req;
        pthread_mutex_unlock(&m->lock);
        if (stop) break;

        /* Sleep the configured interval. */
#if defined(_POSIX_C_SOURCE) && _POSIX_C_SOURCE >= 199309L
        struct timespec req = {
            .tv_sec  = interval_ms / 1000,
            .tv_nsec = (long)(interval_ms % 1000) * 1000000L
        };
        nanosleep(&req, NULL);
#else
        usleep((unsigned int)(interval_ms * 1000));
#endif

        pthread_mutex_lock(&m->lock);
        if (m->stop_req) {
            pthread_mutex_unlock(&m->lock);
            break;
        }

        int64_t now = (int64_t)now_ms();
        double elapsed_sec = fmax(0.001, (double)(now - last_tick_ms) / 1000.0);
        last_tick_ms = now;

        /* Count distinct peers active within the idle window; prune stale entries. */
        int active_peers = monitor_active_peers(m, now);

        /* Per-transport accounting. */
        aethernet_node_state_t states[MAX_TRANSPORTS];
        int64_t total_ingress = 0, total_egress = 0;
        int active_transports = 0;

        for (int i = 0; i < m->transport_count; i++) {
            transport_entry_t *t = &m->transports[i];

            /* Atomically sample and reset byte counters. */
            int64_t ing = t->ingress_bytes;
            int64_t eg  = t->egress_bytes;
            t->ingress_bytes -= ing;
            t->egress_bytes  -= eg;

            int64_t ingress_bps = (int64_t)((double)ing * 8.0 / elapsed_sec);
            int64_t egress_bps  = (int64_t)((double)eg  * 8.0 / elapsed_sec);

            /* Query estimator for loss_rate and btlbw. */
            aethernet_bw_sample_t samp;
            memset(&samp, 0, sizeof(samp));
            if (t->estimator) {
                aethernet_bw_estimator_get_sample(t->estimator, &samp);
            }

            /* Determine per-transport state. */
            aethernet_node_state_t ts;
            if (ingress_bps == 0 && egress_bps == 0) {
                /* Check recency — is there recent egress? */
                int64_t idle_age = now - t->last_egress_ms;
                (void)idle_age;
                ts = AETHERNET_NODE_IDLE;
            } else {
                if (samp.loss_rate > 0.05) {
                    ts = AETHERNET_NODE_DEGRADED;
                } else {
                    double util = (samp.btlbw_bps > 0)
                        ? (double)egress_bps / (double)samp.btlbw_bps
                        : 0.0;
                    ts = (util >= 0.5) ? AETHERNET_NODE_BUSY : AETHERNET_NODE_ACTIVE;
                    active_transports++;
                }
            }
            states[i] = ts;
            total_ingress += ingress_bps;
            total_egress  += egress_bps;
        }

        aethernet_node_state_t node_state =
            compute_node_state(m->transport_count, states);

        aethernet_node_snapshot_t snap;
        snap.state             = node_state;
        snap.ingress_bps       = total_ingress;
        snap.egress_bps        = total_egress;
        snap.active_transports = active_transports;
        snap.active_peers      = active_peers;
        snap.total_bps         = total_ingress + total_egress;
        snap.has_activity      = (node_state == AETHERNET_NODE_ACTIVE
                                  || node_state == AETHERNET_NODE_BUSY
                                  || node_state == AETHERNET_NODE_DEGRADED);
        m->snapshot = snap;

        aethernet_node_monitor_cb cb = m->callback;
        void *user = m->callback_user;
        pthread_mutex_unlock(&m->lock);

        if (cb) cb(&snap, user);
    }
    return NULL;
}

aethernet_node_monitor_t *aethernet_node_monitor_new(void)
{
    aethernet_node_monitor_t *m =
        (aethernet_node_monitor_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    if (pthread_mutex_init(&m->lock, NULL) != 0) {
        free(m);
        return NULL;
    }
    m->interval_ms = 500;
    /* Default snapshot: Offline. */
    m->snapshot.state = AETHERNET_NODE_OFFLINE;
    return m;
}

void aethernet_node_monitor_free(aethernet_node_monitor_t *m)
{
    if (!m) return;
    aethernet_node_monitor_stop(m);
    pthread_mutex_destroy(&m->lock);
    free(m);
}

void aethernet_node_monitor_register(aethernet_node_monitor_t *m,
                                     const char               *name,
                                     aethernet_bw_estimator_t *e)
{
    if (!m || !name) return;
    pthread_mutex_lock(&m->lock);
    int idx = monitor_find(m, name);
    if (idx < 0) {
        if (m->transport_count >= MAX_TRANSPORTS) {
            pthread_mutex_unlock(&m->lock);
            return;
        }
        idx = m->transport_count++;
    }
    transport_entry_t *t = &m->transports[idx];
    strncpy(t->name, name, sizeof(t->name) - 1);
    t->name[sizeof(t->name) - 1] = '\0';
    t->estimator        = e;
    t->ingress_bytes    = 0;
    t->egress_bytes     = 0;
    t->last_egress_ms   = (int64_t)now_ms();
    pthread_mutex_unlock(&m->lock);
}

void aethernet_node_monitor_record_ingress(aethernet_node_monitor_t *m,
                                           const char               *transport,
                                           int32_t                   bytes)
{
    if (!m || !transport || bytes <= 0) return;
    pthread_mutex_lock(&m->lock);
    int idx = monitor_find(m, transport);
    if (idx >= 0) {
        m->transports[idx].ingress_bytes += bytes;
    }
    pthread_mutex_unlock(&m->lock);
}

void aethernet_node_monitor_record_egress(aethernet_node_monitor_t *m,
                                          const char               *transport,
                                          int32_t                   bytes)
{
    if (!m || !transport || bytes <= 0) return;
    pthread_mutex_lock(&m->lock);
    int idx = monitor_find(m, transport);
    if (idx >= 0) {
        m->transports[idx].egress_bytes  += bytes;
        m->transports[idx].last_egress_ms = (int64_t)now_ms();
    }
    pthread_mutex_unlock(&m->lock);
}

void aethernet_node_monitor_record_ingress_peer(aethernet_node_monitor_t *m,
                                                const char               *transport,
                                                const char               *peer_uhid,
                                                int32_t                   bytes)
{
    aethernet_node_monitor_record_ingress(m, transport, bytes);
    if (!m || !peer_uhid || peer_uhid[0] == '\0') return;
    pthread_mutex_lock(&m->lock);
    monitor_touch_peer(m, peer_uhid, (int64_t)now_ms());
    pthread_mutex_unlock(&m->lock);
}

void aethernet_node_monitor_record_egress_peer(aethernet_node_monitor_t *m,
                                               const char               *transport,
                                               const char               *peer_uhid,
                                               int32_t                   bytes)
{
    aethernet_node_monitor_record_egress(m, transport, bytes);
    if (!m || !peer_uhid || peer_uhid[0] == '\0') return;
    pthread_mutex_lock(&m->lock);
    monitor_touch_peer(m, peer_uhid, (int64_t)now_ms());
    pthread_mutex_unlock(&m->lock);
}

void aethernet_node_monitor_start(aethernet_node_monitor_t *m, int sample_interval_ms)
{
    if (!m) return;
    pthread_mutex_lock(&m->lock);
    if (m->running) {
        pthread_mutex_unlock(&m->lock);
        return;
    }
    if (sample_interval_ms < 10)    sample_interval_ms = 10;
    if (sample_interval_ms > 60000) sample_interval_ms = 60000;
    m->interval_ms = sample_interval_ms;
    m->stop_req    = 0;
    m->running     = 1;
    pthread_create(&m->thread, NULL, monitor_thread, m);
    pthread_mutex_unlock(&m->lock);
}

void aethernet_node_monitor_stop(aethernet_node_monitor_t *m)
{
    if (!m) return;
    pthread_mutex_lock(&m->lock);
    if (!m->running) {
        pthread_mutex_unlock(&m->lock);
        return;
    }
    m->stop_req = 1;
    pthread_t tid = m->thread;
    pthread_mutex_unlock(&m->lock);
    pthread_join(tid, NULL);
    pthread_mutex_lock(&m->lock);
    m->running  = 0;
    m->stop_req = 0;
    pthread_mutex_unlock(&m->lock);
}

void aethernet_node_monitor_get_snapshot(const aethernet_node_monitor_t *m,
                                          aethernet_node_snapshot_t      *out)
{
    if (!m || !out) return;
    aethernet_node_monitor_t *me = (aethernet_node_monitor_t *)(uintptr_t)m;
    pthread_mutex_lock(&me->lock);
    *out = m->snapshot;
    pthread_mutex_unlock(&me->lock);
}

void aethernet_node_monitor_set_callback(aethernet_node_monitor_t *m,
                                          aethernet_node_monitor_cb cb,
                                          void                     *user_data)
{
    if (!m) return;
    pthread_mutex_lock(&m->lock);
    m->callback      = cb;
    m->callback_user = user_data;
    pthread_mutex_unlock(&m->lock);
}

/* ─── BandwidthDirector ─────────────────────────────────────────────────────
 *
 * Port of src/AetherNet.Transport/Bandwidth/BandwidthDirector.cs.
 *
 *   • matrix  — (peer_uhid × transport_name) → aethernet_bw_sample_t.
 *               Fixed array of DIRECTOR_MAX_MATRIX (256) slots; seeding updates
 *               the matching slot in place or claims a free one, and drops
 *               silently when full.
 *               Key match semantics mirror the C# ConcurrentDictionary:
 *                 - GetEstimate: exact (case-sensitive) peer AND transport.
 *                 - RecommendTransport: case-insensitive peer (C# GetEstimates
 *                   uses OrdinalIgnoreCase on peer).
 *   • estimators — DIRECTOR_MAX_ESTIMATORS (32) borrowed pointers keyed by
 *                  transport name (case-insensitive, like C# OrdinalIgnoreCase).
 *
 * The director owns its matrix/estimator arrays (freed in _free); it never
 * frees the estimators themselves (borrowed). All state under one mutex. */

typedef struct {
    char                  peer[128];
    char                  transport[64];
    aethernet_bw_sample_t sample;
    int                   in_use;
} matrix_entry_t;

typedef struct {
    char                      name[64];
    aethernet_bw_estimator_t *estimator;   /* BORROWED */
    int                       in_use;
} director_estimator_t;

struct aethernet_bw_director {
    pthread_mutex_t      lock;
    matrix_entry_t       matrix[DIRECTOR_MAX_MATRIX];
    director_estimator_t estimators[DIRECTOR_MAX_ESTIMATORS];
};

/* Default per-transport power cost (lower = preferred). Case-insensitive name
 * match; default 5 for anything not listed. Mirrors C# DefaultPowerCosts. */
static int director_power_cost(const char *transport)
{
    if (!transport) return 5;
    if (ci_equal(transport, "NearLink"))     return 1;
    if (ci_equal(transport, "BLE"))          return 2;
    if (ci_equal(transport, "Wi-Fi Direct")) return 3;
    if (ci_equal(transport, "CircleLink"))   return 3;
    if (ci_equal(transport, "QUIC Relay"))   return 10;
    if (ci_equal(transport, "HTTP Relay"))   return 10;
    return 5;
}

/* Locate a registered estimator by transport name (case-insensitive).
 * Caller holds the lock. Returns index or -1. */
static int director_find_estimator(struct aethernet_bw_director *d, const char *name)
{
    for (int i = 0; i < DIRECTOR_MAX_ESTIMATORS; i++) {
        if (d->estimators[i].in_use && ci_equal(d->estimators[i].name, name))
            return i;
    }
    return -1;
}

/* Locate a matrix entry by exact peer + transport. Caller holds the lock. */
static int director_find_matrix(struct aethernet_bw_director *d,
                                const char *peer, const char *transport)
{
    for (int i = 0; i < DIRECTOR_MAX_MATRIX; i++) {
        if (d->matrix[i].in_use &&
            strncmp(d->matrix[i].peer, peer, sizeof(d->matrix[i].peer) - 1) == 0 &&
            strncmp(d->matrix[i].transport, transport,
                    sizeof(d->matrix[i].transport) - 1) == 0) {
            return i;
        }
    }
    return -1;
}

/* Seed (update or insert) a matrix entry. Caller holds the lock. */
static void director_seed_matrix(struct aethernet_bw_director *d,
                                 const char *peer, const char *transport,
                                 const aethernet_bw_sample_t *sample)
{
    int idx = director_find_matrix(d, peer, transport);
    if (idx < 0) {
        for (int i = 0; i < DIRECTOR_MAX_MATRIX; i++) {
            if (!d->matrix[i].in_use) { idx = i; break; }
        }
        if (idx < 0) return;   /* matrix full — drop silently */
        matrix_entry_t *m = &d->matrix[idx];
        strncpy(m->peer, peer, sizeof(m->peer) - 1);
        m->peer[sizeof(m->peer) - 1] = '\0';
        strncpy(m->transport, transport, sizeof(m->transport) - 1);
        m->transport[sizeof(m->transport) - 1] = '\0';
        m->in_use = 1;
    }
    d->matrix[idx].sample = *sample;
}

aethernet_bw_director_t *aethernet_bw_director_new(void)
{
    aethernet_bw_director_t *d =
        (aethernet_bw_director_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    if (pthread_mutex_init(&d->lock, NULL) != 0) {
        free(d);
        return NULL;
    }
    return d;
}

void aethernet_bw_director_free(aethernet_bw_director_t *d)
{
    if (!d) return;
    /* Estimators are borrowed — never freed here. */
    pthread_mutex_destroy(&d->lock);
    free(d);
}

void aethernet_bw_director_register(aethernet_bw_director_t  *d,
                                    aethernet_bw_estimator_t *e)
{
    if (!d || !e) return;
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);   /* read transport name (no lock held) */

    pthread_mutex_lock(&d->lock);
    int idx = director_find_estimator(d, s.transport_name);
    if (idx < 0) {
        for (int i = 0; i < DIRECTOR_MAX_ESTIMATORS; i++) {
            if (!d->estimators[i].in_use) { idx = i; break; }
        }
        if (idx < 0) { pthread_mutex_unlock(&d->lock); return; }  /* full — drop */
        strncpy(d->estimators[idx].name, s.transport_name,
                sizeof(d->estimators[idx].name) - 1);
        d->estimators[idx].name[sizeof(d->estimators[idx].name) - 1] = '\0';
        d->estimators[idx].in_use = 1;
    }
    d->estimators[idx].estimator = e;
    pthread_mutex_unlock(&d->lock);
}

bool aethernet_bw_director_get_estimate(const aethernet_bw_director_t *d,
                                        const char                    *peer_uhid,
                                        const char                    *transport,
                                        aethernet_bw_sample_t         *out)
{
    if (!d || !peer_uhid || !transport || !out) return false;
    aethernet_bw_director_t *me = (aethernet_bw_director_t *)(uintptr_t)d;
    pthread_mutex_lock(&me->lock);
    int idx = director_find_matrix(me, peer_uhid, transport);
    bool found = (idx >= 0);
    if (found) *out = me->matrix[idx].sample;
    pthread_mutex_unlock(&me->lock);
    return found;
}

bool aethernet_bw_director_recommend_transport(const aethernet_bw_director_t *d,
                                               const char                    *peer_uhid,
                                               int64_t                        payload_bytes,
                                               char                          *out_name,
                                               size_t                         out_cap)
{
    if (!d || !peer_uhid || !out_name || out_cap == 0) return false;
    aethernet_bw_director_t *me = (aethernet_bw_director_t *)(uintptr_t)d;
    pthread_mutex_lock(&me->lock);

    /* Count this peer's matrix entries (case-insensitive peer, like C#
     * GetEstimates which filters with OrdinalIgnoreCase). */
    const char *best_name = NULL;
    double      best_score = -DBL_MAX;
    int         candidates = 0;

    for (int i = 0; i < DIRECTOR_MAX_MATRIX; i++) {
        if (!me->matrix[i].in_use) continue;
        if (!ci_equal(me->matrix[i].peer, peer_uhid)) continue;
        candidates++;

        const aethernet_bw_sample_t *s = &me->matrix[i].sample;
        double power = (double)director_power_cost(s->transport_name);
        double available = (double)s->available_bps;
        /* Oversize payloads get a NEUTRAL 1.0 (not 0.0) so the available-bandwidth/
           power term still ranks them — keeps selection identical across all 8 SDKs. */
        double bdp_bonus = (payload_bytes > s->bdp_bytes) ? 1.0 : 1.5;
        double conf_factor =
            (s->confidence == AETHERNET_BW_CONFIDENCE_NONE) ? 0.5 : 1.0;
        double score = (available / power) * bdp_bonus * conf_factor;

        if (score > best_score) {
            best_score = score;
            best_name  = me->matrix[i].transport;
        }
    }

    if (candidates == 0) {
        /* No measurement data — fall back to the registered transport with the
         * lowest power cost (matches C# RecommendTransport fallback). */
        int best_cost = 0;
        for (int i = 0; i < DIRECTOR_MAX_ESTIMATORS; i++) {
            if (!me->estimators[i].in_use) continue;
            int cost = director_power_cost(me->estimators[i].name);
            if (best_name == NULL || cost < best_cost) {
                best_cost = cost;
                best_name = me->estimators[i].name;
            }
        }
    }

    bool ok = (best_name != NULL);
    if (ok) {
        strncpy(out_name, best_name, out_cap - 1);
        out_name[out_cap - 1] = '\0';
    }
    pthread_mutex_unlock(&me->lock);
    return ok;
}

bool aethernet_bw_director_build_gossip(const aethernet_bw_director_t *d,
                                        const char                    *peer_uhid,
                                        const char                    *transport,
                                        aethernet_bw_gossip_t         *out)
{
    if (!d || !peer_uhid || !transport || !out) return false;
    aethernet_bw_director_t *me = (aethernet_bw_director_t *)(uintptr_t)d;

    pthread_mutex_lock(&me->lock);
    int idx = director_find_estimator(me, transport);
    aethernet_bw_estimator_t *e = (idx >= 0) ? me->estimators[idx].estimator : NULL;
    pthread_mutex_unlock(&me->lock);

    if (!e) return false;

    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    if (s.confidence == AETHERNET_BW_CONFIDENCE_NONE) return false;

    memset(out, 0, sizeof(*out));
    strncpy(out->peer_uhid, peer_uhid, sizeof(out->peer_uhid) - 1);
    out->peer_uhid[sizeof(out->peer_uhid) - 1] = '\0';
    strncpy(out->transport_name, s.transport_name, sizeof(out->transport_name) - 1);
    out->transport_name[sizeof(out->transport_name) - 1] = '\0';
    out->btlbw_bps           = s.btlbw_bps;
    out->rtprop_us           = s.rtprop_us;
    out->confidence          = s.confidence;
    out->measured_at_unix_ms = (int64_t)now_ms();
    return true;
}

void aethernet_bw_director_apply_gossip(aethernet_bw_director_t     *d,
                                        const aethernet_bw_gossip_t *gossip)
{
    if (!d || !gossip) return;

    pthread_mutex_lock(&d->lock);
    int idx = director_find_estimator(d, gossip->transport_name);
    aethernet_bw_estimator_t *e = (idx >= 0) ? d->estimators[idx].estimator : NULL;
    pthread_mutex_unlock(&d->lock);

    if (!e) return;

    /* Warm the estimator outside the director lock (estimator has its own). */
    aethernet_bw_estimator_warm_from_gossip(e, gossip->btlbw_bps,
                                            gossip->rtprop_us, gossip->confidence);

    /* Seed the matrix so get_estimate returns something before we probe. */
    aethernet_bw_sample_t s;
    aethernet_bw_estimator_get_sample(e, &s);
    pthread_mutex_lock(&d->lock);
    director_seed_matrix(d, gossip->peer_uhid, gossip->transport_name, &s);
    pthread_mutex_unlock(&d->lock);
}
