// SPDX-License-Identifier: MIT
// Cross-language circuit-relay-v2 parity: the C port must reproduce the Go
// oracle's byte vectors (fixtures/circuit-relay/expected/<name>.bin) byte-for-byte
// for every case in fixtures/circuit-relay/inputs.json, then deserialize each back
// to matching fields. Cases are transcribed in C (no JSON parser on the test
// surface, mirroring test_dtn_fixture.c). Run from the repo root so the relative
// fixture paths resolve.

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/circuit_relay.h"

#define NIL_UUID "00000000-0000-0000-0000-000000000000"

static int tests_run = 0;

#define FAILF(name, ...) do { \
    fprintf(stderr, "FAIL [%s]: ", (name)); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); \
    exit(1); \
} while (0)

static int hexv(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

// Parse a dashed/undashed UUID string into 16 RFC-4122 big-endian bytes.
static bool parse_uuid_str(const char *s, uint8_t out[16]) {
    int n = 0;
    while (*s && n < 16) {
        if (*s == '-') { s++; continue; }
        int hi = hexv(s[0]);
        if (hi < 0 || s[1] == '\0') return false;
        int lo = hexv(s[1]);
        if (lo < 0) return false;
        out[n++] = (uint8_t)((hi << 4) | lo);
        s += 2;
    }
    return n == 16;
}

static uint8_t *hex_to_bytes(const char *hex, uint32_t *out_len) {
    size_t hl = strlen(hex);
    uint32_t n = (uint32_t)(hl / 2);
    *out_len = n;
    if (n == 0) return NULL;
    uint8_t *b = (uint8_t *)malloc(n);
    for (uint32_t i = 0; i < n; i++) {
        b[i] = (uint8_t)((hexv(hex[i * 2]) << 4) | hexv(hex[i * 2 + 1]));
    }
    return b;
}

static bool streq_or_empty(const char *a, const char *b) {
    return strcmp(a ? a : "", b ? b : "") == 0;
}

static uint8_t *read_expected(const char *name, long *out_len) {
    char path[256];
    snprintf(path, sizeof path, "fixtures/circuit-relay/expected/%s.bin", name);
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long len = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (len < 0) { fclose(f); return NULL; }
    uint8_t *buf = (uint8_t *)malloc((size_t)(len > 0 ? len : 1));
    if (!buf) { fclose(f); return NULL; }
    size_t got = fread(buf, 1, (size_t)len, f);
    fclose(f);
    if ((long)got != len) { free(buf); return NULL; }
    *out_len = len;
    return buf;
}

// Serialize a frame, assert byte-identity with the oracle vector, then decode the
// vector back and assert every field round-trips. `payload` is borrowed.
static void check(const char *name, uint8_t type, uint8_t status,
                  const char *src, const char *dst, const char *relay, const char *uuid,
                  int64_t reservation_expires_at_ms, int32_t limit_duration_seconds,
                  int64_t limit_data_bytes, const uint8_t *payload, uint32_t payload_len) {
    aethernet_relay_frame_t f;
    memset(&f, 0, sizeof f);
    f.type = type;
    f.status = status;
    f.source_uhid = (char *)src;
    f.destination_uhid = (char *)dst;
    f.relay_uhid = (char *)relay;
    if (!parse_uuid_str(uuid, f.connection_id)) FAILF(name, "bad uuid");
    f.reservation_expires_at_ms = reservation_expires_at_ms;
    f.limit_duration_seconds = limit_duration_seconds;
    f.limit_data_bytes = limit_data_bytes;
    f.payload = (uint8_t *)payload;
    f.payload_len = payload_len;

    uint8_t *enc = NULL;
    uint32_t enc_len = 0;
    if (!aethernet_relay_frame_encode(&f, &enc, &enc_len)) FAILF(name, "encode failed");

    long exp_len = 0;
    uint8_t *exp = read_expected(name, &exp_len);
    if (!exp) FAILF(name, "missing fixtures/circuit-relay/expected/%s.bin (run from repo root)", name);
    if ((long)enc_len != exp_len || memcmp(enc, exp, enc_len) != 0) {
        FAILF(name, "serialize byte mismatch (got %u bytes, expected %ld)", enc_len, exp_len);
    }
    free(enc);

    aethernet_relay_frame_t *d = aethernet_relay_frame_decode(exp, (uint32_t)exp_len);
    if (!d) FAILF(name, "decode returned NULL");
    if (d->type != type) FAILF(name, "type");
    if (d->status != status) FAILF(name, "status");
    if (!streq_or_empty(d->source_uhid, src)) FAILF(name, "source_uhid");
    if (!streq_or_empty(d->destination_uhid, dst)) FAILF(name, "destination_uhid");
    if (!streq_or_empty(d->relay_uhid, relay)) FAILF(name, "relay_uhid");
    if (memcmp(d->connection_id, f.connection_id, AETHERNET_RELAY_CONN_ID_SIZE) != 0) FAILF(name, "connection_id");
    if (d->reservation_expires_at_ms != reservation_expires_at_ms) FAILF(name, "reservation_expires_at_ms");
    if (d->limit_duration_seconds != limit_duration_seconds) FAILF(name, "limit_duration_seconds");
    if (d->limit_data_bytes != limit_data_bytes) FAILF(name, "limit_data_bytes");
    if (d->payload_len != payload_len) FAILF(name, "payload_len");
    if (payload_len && memcmp(d->payload, payload, payload_len) != 0) FAILF(name, "payload bytes");
    aethernet_relay_frame_free(d);
    free(exp);

    printf("  %s OK\n", name);
    tests_run++;
}

int main(void) {
    printf("Aether Circuit-Relay-v2 Frame — Cross-Language Fixture Parity\n");
    printf("============================================================\n");

    const char *CID = "01020304-0506-0708-090a-0b0c0d0e0f10";
    uint32_t plen = 0;
    uint8_t *p;

    check("relay_reserve", 1, 0, "alice-uhid", "", "relay-1-uhid", NIL_UUID, 0, 0, 0, NULL, 0);
    check("relay_reserve_response_ok", 2, 0, "alice-uhid", "", "relay-1-uhid", NIL_UUID, 1735689600000LL, 0, 0, NULL, 0);
    check("relay_reserve_response_refused", 2, 1, "alice-uhid", "", "relay-1-uhid", NIL_UUID, 0, 0, 0, NULL, 0);
    check("relay_connect", 3, 0, "alice-uhid", "bob-uhid", "relay-1-uhid", CID, 0, 0, 0, NULL, 0);
    check("relay_stop", 4, 0, "alice-uhid", "bob-uhid", "relay-1-uhid", CID, 0, 120, 1048576LL, NULL, 0);
    check("relay_stop_response_ok", 5, 0, "alice-uhid", "bob-uhid", "relay-1-uhid", CID, 0, 0, 0, NULL, 0);
    check("relay_connect_response_ok", 6, 0, "alice-uhid", "bob-uhid", "relay-1-uhid", CID, 0, 0, 1048576LL, NULL, 0);
    check("relay_connect_response_failed", 6, 5, "alice-uhid", "bob-uhid", "relay-1-uhid", CID, 0, 0, 0, NULL, 0);

    p = hex_to_bytes("deadbeef", &plen);
    check("relay_data_small", 7, 0, "alice-uhid", "bob-uhid", "relay-1-uhid", CID, 0, 0, 0, p, plen);
    free(p);

    check("relay_data_empty_payload", 7, 0, "alice-uhid", "bob-uhid", "relay-1-uhid", CID, 0, 0, 0, NULL, 0);

    // 65537-byte payload, byte[i] = i % 256 — proves the int32 length prefix.
    uint8_t *large = (uint8_t *)malloc(65537);
    for (uint32_t i = 0; i < 65537; i++) large[i] = (uint8_t)(i % 256);
    check("relay_data_large_payload", 7, 0, "alice-uhid", "bob-uhid", "relay-1-uhid", CID, 0, 0, 0, large, 65537);
    free(large);

    // Multibyte UTF-8 UHIDs — the same bytes the Go oracle emitted.
    check("relay_unicode_uhids", 3, 0, "нода-α", "節點-β", "relay-δ", CID, 0, 0, 0, NULL, 0);

    p = hex_to_bytes("0102030405", &plen);
    check("relay_full_all_fields", 6, 3, "alice-uhid", "bob-uhid", "relay-1-uhid",
          "ffeeddcc-bbaa-9988-7766-554433221100", 1735689600000LL, 120, 5000000000LL, p, plen);
    free(p);

    check("relay_max_status", 2, 6, "alice-uhid", "", "relay-1-uhid", NIL_UUID, 0, 0, 0, NULL, 0);

    printf("\n%d fixture cases passed.\n", tests_run);
    return 0;
}
