// SPDX-License-Identifier: MIT
//
// Behavioural test for the aether-vault in-memory service (Phase-2):
//   - store -> recover round-trips a multi-shard blob,
//   - recovery still succeeds after M shards are lost (any K of N suffice),
//   - dropping one more (K-1 survivors) is unrecoverable, and check_health agrees,
//   - an empty blob round-trips (the K 1-byte-shard edge case).
//
// Mirrors the Go/Rust/Python/TS/Kotlin/Swift InMemoryVaultService tests.

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/vault.h"

static int g_failures = 0;

#define CHECK(cond, msg)                                              \
    do {                                                              \
        if (!(cond)) {                                               \
            fprintf(stderr, "FAIL: %s (%s:%d)\n", (msg), __FILE__, __LINE__); \
            g_failures++;                                            \
        }                                                            \
    } while (0)

int main(void) {
    // ── store -> recover round-trip ────────────────────────────────────────
    aethernet_vault_service_t *svc = aethernet_vault_service_new();
    CHECK(svc != NULL, "service_new");

    const size_t n = 3333;
    uint8_t *data = (uint8_t *)malloc(n);
    CHECK(data != NULL, "alloc data");
    for (size_t i = 0; i < n; i++) data[i] = (uint8_t)((i * 7) % 256);

    aethernet_vault_manifest_t manifest;
    bool stored = aethernet_vault_store(svc, data, n, "doc.bin", &manifest);
    CHECK(stored, "store");
    CHECK(manifest.total_shards == AETHERNET_VAULT_K + AETHERNET_VAULT_M, "total_shards == 14");
    CHECK(manifest.k == AETHERNET_VAULT_K && manifest.m == AETHERNET_VAULT_M, "k/m");
    CHECK(manifest.size_bytes == (int64_t)n, "size_bytes");
    CHECK(manifest.content_hash != NULL && strlen(manifest.content_hash) == 64, "content_hash hex64");
    CHECK(manifest.label != NULL && strcmp(manifest.label, "doc.bin") == 0, "label");

    uint8_t *out = NULL;
    size_t out_len = 0;
    bool recovered = aethernet_vault_recover(svc, &manifest, &out, &out_len);
    CHECK(recovered, "recover");
    CHECK(out_len == n, "recovered length");
    CHECK(out != NULL && memcmp(out, data, n) == 0, "recovered bytes match");
    free(out);

    // health: all shards reachable
    aethernet_vault_health_t health;
    aethernet_vault_check_health(svc, &manifest, &health);
    CHECK(health.total_shards == AETHERNET_VAULT_K + AETHERNET_VAULT_M, "health total");
    CHECK(health.reachable_shards == manifest.total_shards, "all reachable");
    CHECK(health.is_recoverable, "recoverable at full");
    CHECK(health.redundancy_score > 0.99, "redundancy 1.0");

    // ── lose M shards: still recoverable from the surviving K ───────────────
    for (int i = 0; i < AETHERNET_VAULT_M; i++) {
        CHECK(aethernet_vault_forget_shard(svc, manifest.shard_hashes[i]), "forget shard");
    }
    aethernet_vault_check_health(svc, &manifest, &health);
    CHECK(health.reachable_shards == AETHERNET_VAULT_K, "K reachable after losing M");
    CHECK(health.is_recoverable, "still recoverable with K");

    out = NULL;
    out_len = 0;
    recovered = aethernet_vault_recover(svc, &manifest, &out, &out_len);
    CHECK(recovered, "recover from K");
    CHECK(out_len == n && out != NULL && memcmp(out, data, n) == 0, "K-recovered bytes match");
    free(out);

    // ── lose one more (K-1 survivors): unrecoverable ────────────────────────
    CHECK(aethernet_vault_forget_shard(svc, manifest.shard_hashes[AETHERNET_VAULT_M]), "forget one more");
    aethernet_vault_check_health(svc, &manifest, &health);
    CHECK(health.reachable_shards == AETHERNET_VAULT_K - 1, "K-1 reachable");
    CHECK(!health.is_recoverable, "unrecoverable below K");
    out = NULL;
    recovered = aethernet_vault_recover(svc, &manifest, &out, &out_len);
    CHECK(!recovered, "recover fails below K");

    // replicate is a no-op but must succeed
    CHECK(aethernet_vault_replicate(svc, &manifest, 14), "replicate no-op");

    aethernet_vault_manifest_free(&manifest);
    free(data);

    // ── empty blob round-trips ──────────────────────────────────────────────
    aethernet_vault_manifest_t empty_manifest;
    bool empty_stored = aethernet_vault_store(svc, NULL, 0, "empty", &empty_manifest);
    CHECK(empty_stored, "store empty");
    CHECK(empty_manifest.size_bytes == 0, "empty size 0");
    CHECK(empty_manifest.total_shards == AETHERNET_VAULT_K + AETHERNET_VAULT_M, "empty total shards");

    uint8_t *empty_out = NULL;
    size_t empty_len = 123;
    bool empty_rec = aethernet_vault_recover(svc, &empty_manifest, &empty_out, &empty_len);
    CHECK(empty_rec, "recover empty");
    CHECK(empty_len == 0, "empty recovered length 0");
    free(empty_out);
    aethernet_vault_manifest_free(&empty_manifest);

    aethernet_vault_service_free(svc);

    if (g_failures == 0) {
        printf("test_vault: all checks passed\n");
        return 0;
    }
    fprintf(stderr, "test_vault: %d check(s) failed\n", g_failures);
    return 1;
}
