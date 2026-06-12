// SPDX-License-Identifier: MIT
//
// Cross-language ERID parity verifier (C).
//
// Verifies the C ERID port reproduces the C# reference vectors
// (fixtures/erid/vectors.json) byte-for-byte. The secret, routing key, and the
// AERD announcement frame are loaded directly from the fixture file. The per-epoch
// and per-unixseconds ERID expectations are transcribed from the same file (parsing
// nested JSON arrays in C is not worth a dependency — the values ARE the canonical
// fixture values; keep them in sync with fixtures/erid/vectors.json).

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <direct.h>
#define getcwd _getcwd
#else
#include <unistd.h>
#endif

#include "aethernet/erid.h"

// ─── helpers (mirroring test_signal_fixtures.c) ──────────────────────────────

static void hex_encode(const uint8_t *bytes, size_t len, char *out)
{
    static const char digits[] = "0123456789abcdef";
    for (size_t i = 0; i < len; i++) {
        out[i * 2]     = digits[(bytes[i] >> 4) & 0xF];
        out[i * 2 + 1] = digits[bytes[i] & 0xF];
    }
    out[len * 2] = '\0';
}

static char *read_file(const char *path)
{
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    char *buf = (char *)malloc((size_t)sz + 1);
    if (!buf) { fclose(f); return NULL; }
    size_t got = fread(buf, 1, (size_t)sz, f);
    fclose(f);
    buf[got] = '\0';
    return buf;
}

// Locates "key": "value" and writes the value into out. Returns 1 on success.
static int json_get_str(const char *scope, const char *key, char *out, size_t out_size)
{
    char needle[128];
    int needle_n = snprintf(needle, sizeof(needle), "\"%s\"", key);
    const char *p = strstr(scope, needle);
    if (!p) return 0;
    p += needle_n;
    while (*p && *p != '\"') p++;
    if (*p != '\"') return 0;
    p++;
    size_t i = 0;
    while (*p && *p != '\"' && i + 1 < out_size) {
        if (*p == '\\' && p[1]) p++;
        out[i++] = *p++;
    }
    out[i] = '\0';
    return *p == '\"';
}

static char repo_root_path[1024];

static int find_repo_root(void)
{
    char cwd[1024];
    if (!getcwd(cwd, sizeof(cwd))) return 0;
    char path[1280];
    for (int depth = 0; depth < 10; depth++) {
        snprintf(path, sizeof(path), "%s/AetherNetProtocol.slnx", cwd);
        FILE *f = fopen(path, "rb");
        if (f) { fclose(f); strncpy(repo_root_path, cwd, sizeof(repo_root_path) - 1); return 1; }
        char *last_slash = strrchr(cwd, '/');
        if (!last_slash) last_slash = strrchr(cwd, '\\');
        if (!last_slash) return 0;
        *last_slash = '\0';
    }
    return 0;
}

// ─── ground truth transcribed from fixtures/erid/vectors.json ────────────────

static const struct { int64_t epoch; const char *erid; } ERIDS_BY_EPOCH[] = {
    { 0,    "3B38HPPFG9JXE37Q" },
    { 1,    "0Z5BD0HB1Q7W76MY" },
    { 100,  "SJZ1VWA7SK1XR2XV" },
    { 1371, "50ZJFR0TF1JVNT64" },
};

static const struct { int64_t unix_s; const char *erid; } DERIVE_BY_UNIX[] = {
    { 1000, "0Z5BD0HB1Q7W76MY" },
    { 2000, "8GX2DTFDKAGTQ96Z" },
};

static int failures = 0;

static void expect_str(const char *label, const char *got, const char *want)
{
    if (strcmp(got, want) != 0) {
        fprintf(stderr, "  FAIL %s\n    expected: %s\n    actual:   %s\n", label, want, got);
        failures++;
    }
}

int main(void)
{
    if (!find_repo_root()) {
        fprintf(stderr, "Cannot locate repo root (AetherNetProtocol.slnx).\n");
        return 1;
    }
    printf("ERID fixture verifier — repo root: %s\n", repo_root_path);

    char path[2048];
    snprintf(path, sizeof(path), "%s/fixtures/erid/vectors.json", repo_root_path);
    char *json = read_file(path);
    if (!json) { fprintf(stderr, "Cannot read %s\n", path); return 1; }

    char secret[64], routing_key_hex[128], announce_hex[256];
    if (!json_get_str(json, "secret_ascii", secret, sizeof(secret)) ||
        !json_get_str(json, "routing_key_hex", routing_key_hex, sizeof(routing_key_hex)) ||
        !json_get_str(json, "announcement_encode_hex", announce_hex, sizeof(announce_hex))) {
        fprintf(stderr, "Cannot parse fixture string fields.\n");
        free(json);
        return 1;
    }
    free(json);

    // Routing key — derived from the secret loaded from the fixture.
    uint8_t rk[AETHERNET_ERID_ROUTING_KEY_SIZE];
    if (!aethernet_erid_derive_routing_key((const uint8_t *)secret, strlen(secret), rk)) {
        fprintf(stderr, "derive_routing_key failed\n");
        return 1;
    }
    char rk_hex[AETHERNET_ERID_ROUTING_KEY_SIZE * 2 + 1];
    hex_encode(rk, sizeof(rk), rk_hex);
    expect_str("routing_key", rk_hex, routing_key_hex);

    // Per-epoch ERIDs.
    for (size_t i = 0; i < sizeof(ERIDS_BY_EPOCH) / sizeof(ERIDS_BY_EPOCH[0]); i++) {
        char out[AETHERNET_ERID_DEFAULT_LENGTH + 1];
        char label[64];
        snprintf(label, sizeof(label), "epoch %lld", (long long)ERIDS_BY_EPOCH[i].epoch);
        if (!aethernet_erid_derive_for_epoch(rk, sizeof(rk), ERIDS_BY_EPOCH[i].epoch,
                                             AETHERNET_ERID_DEFAULT_LENGTH, out, sizeof(out))) {
            fprintf(stderr, "  FAIL %s (derive returned false)\n", label);
            failures++;
            continue;
        }
        expect_str(label, out, ERIDS_BY_EPOCH[i].erid);
    }

    // Derive-by-unixseconds.
    for (size_t i = 0; i < sizeof(DERIVE_BY_UNIX) / sizeof(DERIVE_BY_UNIX[0]); i++) {
        char out[AETHERNET_ERID_DEFAULT_LENGTH + 1];
        char label[64];
        snprintf(label, sizeof(label), "unix %lld", (long long)DERIVE_BY_UNIX[i].unix_s);
        if (!aethernet_erid_derive(rk, sizeof(rk), DERIVE_BY_UNIX[i].unix_s,
                                   AETHERNET_ERID_DEFAULT_EPOCH_SECONDS,
                                   AETHERNET_ERID_DEFAULT_LENGTH, out, sizeof(out))) {
            fprintf(stderr, "  FAIL %s (derive returned false)\n", label);
            failures++;
            continue;
        }
        expect_str(label, out, DERIVE_BY_UNIX[i].erid);
    }

    // Announcement frame — compared against the fixture.
    uint8_t frame[AETHERNET_ERID_ANNOUNCE_HEADER_LEN + AETHERNET_ERID_ROUTING_KEY_SIZE];
    size_t frame_len = 0;
    if (!aethernet_erid_announcement_encode(rk, sizeof(rk),
                                            AETHERNET_ERID_DEFAULT_EPOCH_SECONDS,
                                            AETHERNET_ERID_DEFAULT_LENGTH,
                                            frame, sizeof(frame), &frame_len)) {
        fprintf(stderr, "announcement_encode failed\n");
        return 1;
    }
    char frame_hex[sizeof(frame) * 2 + 1];
    hex_encode(frame, frame_len, frame_hex);
    expect_str("announcement_frame", frame_hex, announce_hex);

    // Round-trip decode of the frame.
    uint8_t dec_key[AETHERNET_ERID_ROUTING_KEY_SIZE];
    size_t dec_key_len = 0;
    int32_t dec_epoch = 0, dec_len = 0;
    if (!aethernet_erid_announcement_try_decode(frame, frame_len, dec_key, sizeof(dec_key),
                                                &dec_key_len, &dec_epoch, &dec_len)) {
        fprintf(stderr, "  FAIL announcement_try_decode rejected its own frame\n");
        failures++;
    } else {
        char dec_hex[AETHERNET_ERID_ROUTING_KEY_SIZE * 2 + 1];
        hex_encode(dec_key, dec_key_len, dec_hex);
        expect_str("decoded_routing_key", dec_hex, routing_key_hex);
        if (dec_epoch != AETHERNET_ERID_DEFAULT_EPOCH_SECONDS ||
            dec_len != AETHERNET_ERID_DEFAULT_LENGTH) {
            fprintf(stderr, "  FAIL decoded header mismatch: epoch=%d len=%d\n", dec_epoch, dec_len);
            failures++;
        }
    }

    // Directory: an established peer resolves both ways; an outsider cannot.
    uint8_t a_key[32], b_key[32], x_key[32];
    aethernet_erid_derive_routing_key((const uint8_t *)"identity-A", 10, a_key);
    aethernet_erid_derive_routing_key((const uint8_t *)"identity-B", 10, b_key);
    aethernet_erid_derive_routing_key((const uint8_t *)"identity-X", 10, x_key);

    aethernet_erid_directory_t alice, bob, outsider;
    aethernet_erid_directory_init(&alice, a_key, 0, 0);
    aethernet_erid_directory_init(&bob, b_key, 0, 0);
    aethernet_erid_directory_init(&outsider, x_key, 0, 0);
    aethernet_erid_directory_remember_peer(&alice, "bob", b_key);
    aethernet_erid_directory_remember_peer(&bob, "alice", a_key);
    int64_t t = 1700000000;

    char alice_for_bob[AETHERNET_ERID_MAX_LENGTH + 1];
    char bob_self[AETHERNET_ERID_MAX_LENGTH + 1];
    char alice_self[AETHERNET_ERID_MAX_LENGTH + 1];
    char who[AETHERNET_ERID_MAX_UHID];

    aethernet_erid_directory_erid_for_peer(&alice, "bob", t, alice_for_bob, sizeof(alice_for_bob));
    aethernet_erid_directory_my_erid(&bob, t, bob_self, sizeof(bob_self));
    expect_str("directory_peer_resolves", alice_for_bob, bob_self);

    aethernet_erid_directory_my_erid(&alice, t, alice_self, sizeof(alice_self));
    if (!aethernet_erid_directory_resolve_peer(&bob, alice_self, t, who, sizeof(who)) ||
        strcmp(who, "alice") != 0) {
        fprintf(stderr, "  FAIL reverse-resolve: got '%s'\n", who);
        failures++;
    }
    if (aethernet_erid_directory_resolve_peer(&outsider, alice_self, t, who, sizeof(who))) {
        fprintf(stderr, "  FAIL an outsider resolved an ERID it should not\n");
        failures++;
    }
    if (aethernet_erid_directory_known_peer_count(&alice) != 1) {
        fprintf(stderr, "  FAIL known_peer_count != 1\n");
        failures++;
    }

    if (failures == 0)
        printf("\nAll ERID parity checks passed.\n");
    else
        printf("\n%d ERID parity check(s) FAILED.\n", failures);
    return failures == 0 ? 0 : 1;
}
