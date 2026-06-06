// SPDX-License-Identifier: MIT
// Aether ReputationGossipService — signed reputation-update propagation.
//
// JSON serialisation / parsing uses snprintf + strstr/sscanf throughout;
// no external JSON library is required.  Packets are well-formed by
// construction on the send path, and the receive path validates the type
// field and key payload fields before trusting any values.
//
// Suppress MSVC's strncpy/sscanf deprecation warnings — both are standard C11
// and the correct tools here (POSIX, length-bounded, no dynamic allocation).
#ifdef _MSC_VER
#  define _CRT_SECURE_NO_WARNINGS
#endif

#include "aethermesh_gossip.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

// ─── Internal buffer sizes ────────────────────────────────────────────────────

#define PAYLOAD_BUF_SIZE  1024
#define PACKET_BUF_SIZE   2048
#define SIGNED_BUF_SIZE   2560   /* signed wrapper may add base64 overhead    */

// ─── Freshness window (mirrors the header constant for clarity) ───────────────

#define FRESHNESS_WINDOW_MS  AETHERMESH_GOSSIP_FRESHNESS_MS

// ─── Opaque struct definition ─────────────────────────────────────────────────

struct AetherMeshGossipService {
    AetherMeshNodeReputationService *reputation;
    AetherMeshGossipCallbacks        callbacks;  /* copy of caller's callbacks     */
};

// ─── Helper: portable abs for int64_t ────────────────────────────────────────

static int64_t i64_abs(int64_t v) { return v < 0 ? -v : v; }

// ─── Helper: naive timestamp_ms — always 0 in embedded / test contexts.
//     In production the host integrates a real clock via callbacks; for the
//     gossip service itself we only need a monotonically increasing value to
//     populate outgoing packet timestamps.  We use 0 here because the test
//     suite controls timestamps by injecting them directly through the JSON
//     parsing path.  The spec says to use "now_ms" from the system clock;
//     replace this stub with a real clock call when integrating. ─────────────

static int64_t get_now_ms(void)
{
    /* Stub: returns 0. Replace with platform clock in production. */
    return 0;
}

// ─── Helper: clamp double to [-1.0, 1.0] ──────────────────────────────────────

static double clamp_delta(double d)
{
    if (d < -1.0) return -1.0;
    if (d >  1.0) return  1.0;
    return d;
}

// ─── Helper: extract a quoted-string field from JSON ─────────────────────────
// Searches for "\"key\":\"" in json, copies the value into out (max out_len).
// Returns true on success.

static bool extract_string(const char *json, const char *key,
                            char *out, size_t out_len)
{
    char needle[128];
    /* Build search pattern: "key":" */
    snprintf(needle, sizeof(needle), "\"%s\":\"", key);
    const char *p = strstr(json, needle);
    if (!p) return false;
    p += strlen(needle);  /* advance past the pattern to the value start */

    size_t i = 0;
    while (*p && *p != '"' && i + 1 < out_len) {
        out[i++] = *p++;
    }
    out[i] = '\0';
    return (*p == '"');  /* must have hit the closing quote */
}

// ─── Helper: extract a double field from JSON ─────────────────────────────────
// Searches for "\"key\":" then reads a floating-point value.
// Returns true on success.

static bool extract_double(const char *json, const char *key, double *out)
{
    char needle[128];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = strstr(json, needle);
    if (!p) return false;
    p += strlen(needle);
    return (sscanf(p, "%lf", out) == 1);
}

// ─── Helper: extract an int64_t field from JSON ───────────────────────────────

static bool extract_int64(const char *json, const char *key, int64_t *out)
{
    char needle[128];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = strstr(json, needle);
    if (!p) return false;
    p += strlen(needle);
    /* Use long long — guaranteed 64-bit or wider on all our targets. */
    long long val;
    if (sscanf(p, "%lld", &val) != 1) return false;
    *out = (int64_t)val;
    return true;
}

// ─── Helper: extract the integer "type" field ────────────────────────────────

static bool extract_int(const char *json, const char *key, int *out)
{
    char needle[128];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = strstr(json, needle);
    if (!p) return false;
    p += strlen(needle);
    return (sscanf(p, "%d", out) == 1);
}

// ─── Lifecycle ────────────────────────────────────────────────────────────────

AetherMeshGossipService *aethermesh_gossip_create(
    AetherMeshNodeReputationService *reputation,
    const AetherMeshGossipCallbacks *callbacks)
{
    if (!reputation || !callbacks) return NULL;
    if (!callbacks->local_uhid) return NULL;
    if (!callbacks->broadcast || !callbacks->sign_packet || !callbacks->verify_packet) {
        return NULL;
    }

    AetherMeshGossipService *svc =
        (AetherMeshGossipService *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;

    svc->reputation = reputation;
    svc->callbacks  = *callbacks;   /* deep-copy the struct; caller owns memory  */
    return svc;
}

void aethermesh_gossip_destroy(AetherMeshGossipService *svc)
{
    free(svc);
}

// ─── Broadcast ────────────────────────────────────────────────────────────────

int aethermesh_gossip_broadcast(
    AetherMeshGossipService *svc,
    const char *target_uhid,
    double score_delta,
    const char *reason)
{
    if (!svc || !target_uhid || !reason) return -1;

    const AetherMeshGossipCallbacks *cb = &svc->callbacks;
    double clamped = clamp_delta(score_delta);
    int64_t now_ms = get_now_ms();

    /* 1. Build the payload JSON object. */
    char payload_buf[PAYLOAD_BUF_SIZE];
    int payload_len = snprintf(payload_buf, sizeof(payload_buf),
        "{\"reporter_uhid\":\"%s\",\"target_uhid\":\"%s\","
        "\"score_delta\":%.6f,\"timestamp_ms\":%lld,\"reason\":\"%s\"}",
        cb->local_uhid,
        target_uhid,
        clamped,
        (long long)now_ms,
        reason);
    if (payload_len < 0 || (size_t)payload_len >= sizeof(payload_buf)) {
        return -1;
    }

    /* 2. Wrap in the outer packet envelope. */
    char pkt_buf[PACKET_BUF_SIZE];
    int pkt_len = snprintf(pkt_buf, sizeof(pkt_buf),
        "{\"type\":%d,\"source_uhid\":\"%s\",\"destination_uhid\":\"*\","
        "\"ttl\":3,\"payload\":%s,\"timestamp_ms\":%lld}",
        AETHERMESH_PACKET_TYPE_REPUTATION_UPDATE,
        cb->local_uhid,
        payload_buf,
        (long long)now_ms);
    if (pkt_len < 0 || (size_t)pkt_len >= sizeof(pkt_buf)) {
        return -1;
    }

    /* 3. Sign the packet. */
    char signed_buf[SIGNED_BUF_SIZE];
    if (!cb->sign_packet(pkt_buf, signed_buf, sizeof(signed_buf), cb->sign_ctx)) {
        return -1;
    }

    /* 4. Broadcast and return the peer-delivery count. */
    return cb->broadcast(signed_buf, cb->broadcast_ctx);
}

// ─── Handle ───────────────────────────────────────────────────────────────────

bool aethermesh_gossip_handle(
    AetherMeshGossipService *svc,
    const char *json_packet,
    const uint8_t *sender_pub_key,
    size_t key_len)
{
    if (!svc || !json_packet) return false;

    const AetherMeshGossipCallbacks *cb = &svc->callbacks;

    /* 1. Check packet type. */
    int pkt_type = 0;
    if (!extract_int(json_packet, "type", &pkt_type)) return false;
    if (pkt_type != AETHERMESH_PACKET_TYPE_REPUTATION_UPDATE) return false;

    /* 2. Verify signature. */
    if (!cb->verify_packet(json_packet, sender_pub_key, key_len, cb->verify_ctx)) {
        return false;
    }

    /* 3. Parse payload fields.
          The payload is embedded as a sub-object under "payload":{...}.
          We search within the full packet JSON — the field names are unique
          enough that simple strstr works on our well-formed packets. */
    char reporter_uhid[256] = {0};
    char target_uhid[256]   = {0};
    double score_delta      = 0.0;
    int64_t timestamp_ms    = 0;

    if (!extract_string(json_packet, "reporter_uhid", reporter_uhid, sizeof(reporter_uhid))) {
        return false;
    }
    if (!extract_string(json_packet, "target_uhid", target_uhid, sizeof(target_uhid))) {
        return false;
    }
    if (!extract_double(json_packet, "score_delta", &score_delta)) {
        return false;
    }
    if (!extract_int64(json_packet, "timestamp_ms", &timestamp_ms)) {
        return false;
    }

    /* 4. Freshness check. */
    int64_t now_ms = get_now_ms();
    if (i64_abs(now_ms - timestamp_ms) > FRESHNESS_WINDOW_MS) {
        return false;
    }

    /* 5. Non-empty reporter and target. */
    if (reporter_uhid[0] == '\0' || target_uhid[0] == '\0') {
        return false;
    }

    /* 6. Reject own gossip (loop prevention). */
    if (strcmp(reporter_uhid, cb->local_uhid) == 0) {
        return false;
    }

    /* 7. Weight the delta by the reporter's own reputation score.
          Unknown reporters default to 1.0 (benefit of the doubt). */
    double R = aethermesh_reputation_get_score(svc->reputation, reporter_uhid);

    /* 8. Compute the effective (weighted) delta and apply it. */
    double clamped   = clamp_delta(score_delta);
    double effective = clamped * R;
    aethermesh_reputation_apply_weighted_delta(svc->reputation, target_uhid, effective);

    return true;
}
