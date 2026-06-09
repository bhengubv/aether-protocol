// SPDX-License-Identifier: MIT
// Aether Transport Layer - Abstract Interface and In-Process Transport

#ifndef AETHERNET_TRANSPORT_H
#define AETHERNET_TRANSPORT_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "protocol.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Transport callback type: invoked when data is received from a peer.
 */
typedef void (*aethernet_transport_on_data_received)(
    const char *sender_uhid,
    const uint8_t *data,
    size_t data_len,
    void *user_data
);

/* Forward declarations for metrics types — full definitions below the vtable. */
typedef struct aethernet_transport_metrics       aethernet_transport_metrics_t;
typedef struct aethernet_transport_rank_entry    aethernet_transport_rank_entry_t;

/**
 * Abstract transport vtable.
 * Each transport implementation (BLE, Wi-Fi Direct, NearLink, in-process) provides these methods.
 */
typedef struct {
    // Human-readable name (e.g., "BLE", "WiFi-Direct")
    const char *name;

    // Send a byte array to a specific peer
    // Returns: true on success
    bool (*send)(void *transport_handle,
                 const char *peer_uhid,
                 const uint8_t *data,
                 size_t data_len);

    // Check if a connection is active to a peer
    bool (*is_connected)(void *transport_handle,
                         const char *peer_uhid);

    // Register a data received callback
    void (*set_on_data_received)(void *transport_handle,
                                 aethernet_transport_on_data_received callback,
                                 void *user_data);

    // Clean up transport resources
    void (*destroy)(void *transport_handle);


    /* ───────── Predictive transport selection (v1.2.x+) ───────── */

    /* Static performance characteristics (set at vtable-init time). */
    /* Conservative defaults if a transport doesn't fill them: 0 = unknown. */
    int64_t  max_bandwidth_bps;        /* Headline link capacity in bits/sec. */
    int32_t  power_cost_relative;      /* Relative battery cost; 1=cheap, 100=expensive. */
    int32_t  max_range_meters;         /* Max link range in metres; 0 = unknown / unbounded. */

    /* Live EWMA metrics accessor. NULL when the transport doesn't expose metrics. */
    aethernet_transport_metrics_t *(*get_metrics)(void *transport_handle);
} aethernet_transport_vtable_t;

/**
 * Transport instance wrapper.
 */
typedef struct {
    aethernet_transport_vtable_t *vtable;
    void *handle;  // Opaque pointer to transport-specific state
} aethernet_transport_t;

/**
 * In-process transport: allows communication between multiple nodes within
 * the same process, useful for testing and embedded scenarios.
 * Internally maintains a static array of registered nodes and uses
 * mutex-protected delivery.
 */
typedef struct aethernet_inprocess_transport aethernet_inprocess_transport_t;

/**
 * Create an in-process transport.
 * This creates a shared transport that can connect multiple nodes
 * running in the same process.
 *
 * Returns: allocated transport, or NULL on error.
 * Caller must free with aethernet_transport_destroy().
 */
aethernet_transport_t *aethernet_inprocess_transport_new(void);

/**
 * Register a node with the in-process transport.
 * This allows the node to send and receive messages via the transport.
 *
 * Returns: true on success.
 */
bool aethernet_inprocess_transport_register_node(aethernet_transport_t *transport,
                                               const char *uhid);

/**
 * Unregister a node from the in-process transport.
 *
 * Returns: true on success.
 */
bool aethernet_inprocess_transport_unregister_node(aethernet_transport_t *transport,
                                                 const char *uhid);

/**
 * Generic transport functions.
 */

/**
 * Send data via a transport.
 *
 * Returns: true on success.
 */
bool aethernet_transport_send(aethernet_transport_t *transport,
                          const char *peer_uhid,
                          const uint8_t *data,
                          size_t data_len);

/**
 * Check if connected to a peer via a transport.
 *
 * Returns: true if connected.
 */
bool aethernet_transport_is_connected(aethernet_transport_t *transport,
                                   const char *peer_uhid);

/**
 * Register a callback for incoming data.
 */
void aethernet_transport_set_on_data_received(aethernet_transport_t *transport,
                                          aethernet_transport_on_data_received callback,
                                          void *user_data);

/**
 * Destroy a transport and free resources.
 */
void aethernet_transport_destroy(aethernet_transport_t *transport);


/* ───────────────────────────────────────────────────────────────────────────
 * Per-transport EWMA metrics (v1.2.x+)
 *
 * Wire-equivalent to C# `PerTransportMetrics` in
 * src/AetherNet.Transport/Models/TransportModels.cs. EWMA smoothing factor
 * AETHERNET_METRICS_ALPHA = 0.10 matches the C# `Alpha` constant exactly.
 *
 * Used by aethernet_transport_rank() to score transports for the predictive
 * selector. A transport that doesn't expose metrics (vtable->get_metrics == NULL)
 * still ranks via static max_bandwidth_bps + power_cost_relative with conservative
 * fallbacks for live RTT / loss / throughput.
 * ───────────────────────────────────────────────────────────────────────── */

#define AETHERNET_METRICS_ALPHA 0.10

struct aethernet_transport_metrics {
    double   ewma_rtt_ms;        /* EWMA round-trip time in milliseconds. */
    double   ewma_loss_rate;     /* EWMA loss rate in [0, 1]. */
    double   ewma_tput_bps;      /* EWMA throughput in bits per second. */
    uint64_t sample_count;       /* Total observations recorded. */
    volatile int lock;           /* CAS spin-lock (see transport_metrics.c). */
};

struct aethernet_transport_rank_entry {
    aethernet_transport_t *transport;
    double                 score;   /* Composite ranking score; higher = preferred. */
};

/* Initialise metrics struct in place with conservative priors. */
void aethernet_transport_metrics_init(aethernet_transport_metrics_t *m);

/* Record one link observation. rtt_ms is ignored when success==false. */
void aethernet_transport_metrics_record_sample(
    aethernet_transport_metrics_t *m,
    uint64_t rtt_ms,
    bool     success,
    uint64_t bytes_transferred);

/* Composite score = (effective_bps / power_cost) * (1 - loss) / rtt_clamped.
 * NULL metrics: uses fallback_bps = 0.1 * max_bandwidth_bps and rtt=200ms. */
double aethernet_transport_metrics_composite_score(
    const aethernet_transport_metrics_t *m,
    int64_t max_bandwidth_bps,
    int32_t power_cost);

/* Rank N transports into out_ranked (descending by composite score).
 * out_ranked must hold at least n entries; out_count receives the actual
 * number of rankable transports (skips entries with no send function). */
void aethernet_transport_rank(
    aethernet_transport_t **transports,
    size_t n,
    aethernet_transport_rank_entry_t *out_ranked,
    size_t *out_count);


/* ───────────────────────────────────────────────────────────────────────────
 * Forward-error-correction codec base interface (v1.2.x+)
 *
 * Concrete codecs (RLNC, future Reed-Solomon, etc.) embed an instance of
 * aethernet_fec_codec_t as their FIRST struct member, so an
 * aethernet_rlnc_codec_t* can be freely cast to aethernet_fec_codec_t* and
 * passed to the generic transport layer. Standard C-style inheritance.
 *
 * Used by c/src/rlnc.c (and any future FEC codec). Mirrors the shape that
 * code already references — this header declaration was missing prior to
 * the v1.2.1 fix.
 * ───────────────────────────────────────────────────────────────────────── */

typedef struct aethernet_fec_codec aethernet_fec_codec_t;

struct aethernet_fec_codec {
    const char *codec_name;                  /* e.g. "RLNC-GF256" */
    int         device_tier_required;        /* 0 = any device, 1+ = tier gate */
    double      overhead_fraction;           /* expected coding overhead, [0,1) */
    int         fixed_symbol_size_bytes;     /* 0 = variable-symbol codec */

    /* Encode source bytes into `target_symbol_count` independently-decodable
     * symbols. Caller owns the returned buffer (malloc'd, *out_len bytes). */
    uint8_t *(*encode)(const aethernet_fec_codec_t *codec,
                       const uint8_t              *source,
                       size_t                      source_len,
                       int                         target_symbol_count,
                       size_t                     *out_len);

    /* Attempt to decode source bytes from received_count symbols.
     * Returns NULL if rank deficient (need more symbols). Caller owns buffer. */
    uint8_t *(*try_decode)(const aethernet_fec_codec_t *codec,
                           const uint8_t              **received_symbols,
                           const size_t                *symbol_lengths,
                           int                          received_count,
                           int                          source_symbol_count,
                           size_t                      *out_len);
};

#ifdef __cplusplus
}
#endif

#endif // AETHERNET_TRANSPORT_H
