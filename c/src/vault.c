// SPDX-License-Identifier: MIT
// aether-vault in-memory erasure-coded backup service — see aethernet/vault.h.

#include "aethernet/vault.h"

#include "aethernet/reed_solomon.h"
#include "aethernet/security.h"

#include <stdlib.h>
#include <string.h>
#include <time.h>

static int64_t now_ms_vault(void) {
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static char *str_dup_vault(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

// SHA-256 hex (64 lowercase chars + NUL) of `data` into out (>= 65 bytes).
static bool sha256_hex_vault(const uint8_t *data, size_t len, char *out) {
    uint8_t digest[AETHERNET_SHA256_SIZE];
    if (!aethernet_sha256(data, len, digest)) return false;
    static const char hexd[] = "0123456789abcdef";
    for (int i = 0; i < AETHERNET_SHA256_SIZE; i++) {
        out[i * 2] = hexd[digest[i] >> 4];
        out[i * 2 + 1] = hexd[digest[i] & 0x0F];
    }
    out[AETHERNET_SHA256_SIZE * 2] = '\0';
    return true;
}

// ---- shard store: a linked list keyed by shard content hash ----------------

typedef struct vault_shard {
    char                hash[65]; // 64 hex + NUL
    uint8_t            *bytes;    // owned
    size_t              len;
    struct vault_shard *next;
} vault_shard_t;

struct aethernet_vault_service {
    vault_shard_t *head;
};

static vault_shard_t *store_find(aethernet_vault_service_t *svc, const char *hash) {
    for (vault_shard_t *n = svc->head; n; n = n->next) {
        if (strcmp(n->hash, hash) == 0) return n;
    }
    return NULL;
}

// Insert a copy of `bytes` keyed by `hash`. Idempotent: an existing hash is kept
// (content-addressed — identical bytes hash identically). Returns false on OOM.
static bool store_put(aethernet_vault_service_t *svc, const char *hash, const uint8_t *bytes, size_t len) {
    if (store_find(svc, hash)) return true; // already stored
    vault_shard_t *n = (vault_shard_t *)calloc(1, sizeof(vault_shard_t));
    if (!n) return false;
    n->bytes = (uint8_t *)malloc(len ? len : 1);
    if (!n->bytes) { free(n); return false; }
    if (len) memcpy(n->bytes, bytes, len);
    n->len = len;
    memcpy(n->hash, hash, strlen(hash) + 1);
    n->next = svc->head;
    svc->head = n;
    return true;
}

aethernet_vault_service_t *aethernet_vault_service_new(void) {
    return (aethernet_vault_service_t *)calloc(1, sizeof(aethernet_vault_service_t));
}

void aethernet_vault_service_free(aethernet_vault_service_t *service) {
    if (!service) return;
    vault_shard_t *n = service->head;
    while (n) {
        vault_shard_t *next = n->next;
        free(n->bytes);
        free(n);
        n = next;
    }
    free(service);
}

void aethernet_vault_manifest_free(aethernet_vault_manifest_t *manifest) {
    if (!manifest) return;
    free(manifest->content_hash);
    if (manifest->shard_hashes) {
        for (int i = 0; i < manifest->total_shards; i++) free(manifest->shard_hashes[i]);
        free(manifest->shard_hashes);
    }
    free(manifest->label);
    memset(manifest, 0, sizeof(*manifest));
}

bool aethernet_vault_store(aethernet_vault_service_t *service,
                           const uint8_t *data, size_t data_len,
                           const char *label,
                           aethernet_vault_manifest_t *out_manifest) {
    if (!service || !out_manifest || (data_len > 0 && !data)) return false;
    memset(out_manifest, 0, sizeof(*out_manifest));

    const int k = AETHERNET_VAULT_K;
    const int m = AETHERNET_VAULT_M;
    const int n = k + m;

    char content_hex[65];
    if (!sha256_hex_vault(data, data_len, content_hex)) return false;

    aethernet_reed_solomon_t *codec = aethernet_reed_solomon_new(k, m);
    if (!codec) return false;

    uint8_t **out_shards = (uint8_t **)calloc((size_t)n, sizeof(uint8_t *));
    if (!out_shards) { aethernet_reed_solomon_free(codec); return false; }

    size_t shard_size = 0;
    bool encoded;
    if (data_len == 0) {
        // Empty file: K zero-padded 1-byte data shards (mirrors the C# shardSize = 1 case).
        const uint8_t zero = 0;
        const uint8_t *ds[AETHERNET_VAULT_K];
        for (int i = 0; i < k; i++) ds[i] = &zero;
        encoded = aethernet_reed_solomon_encode(codec, ds, 1, out_shards);
        shard_size = 1;
    } else {
        encoded = aethernet_reed_solomon_encode_data(codec, data, data_len, out_shards, &shard_size);
    }
    aethernet_reed_solomon_free(codec);
    if (!encoded) { free(out_shards); return false; }

    char **shard_hashes = (char **)calloc((size_t)n, sizeof(char *));
    if (!shard_hashes) {
        for (int i = 0; i < n; i++) free(out_shards[i]);
        free(out_shards);
        return false;
    }

    bool ok = true;
    for (int i = 0; i < n; i++) {
        char shard_hex[65];
        if (!sha256_hex_vault(out_shards[i], shard_size, shard_hex) ||
            !store_put(service, shard_hex, out_shards[i], shard_size) ||
            !(shard_hashes[i] = str_dup_vault(shard_hex))) {
            ok = false;
            break;
        }
    }
    for (int i = 0; i < n; i++) free(out_shards[i]);
    free(out_shards);

    if (!ok) {
        for (int i = 0; i < n; i++) free(shard_hashes[i]);
        free(shard_hashes);
        return false;
    }

    out_manifest->content_hash = str_dup_vault(content_hex);
    out_manifest->shard_hashes = shard_hashes;
    out_manifest->total_shards = n;
    out_manifest->k = k;
    out_manifest->m = m;
    out_manifest->size_bytes = (int64_t)data_len;
    out_manifest->label = str_dup_vault(label ? label : "");
    out_manifest->created_at_ms = now_ms_vault();
    return true;
}

bool aethernet_vault_recover(aethernet_vault_service_t *service,
                             const aethernet_vault_manifest_t *manifest,
                             uint8_t **out_data, size_t *out_len) {
    if (!service || !manifest || !out_data || !out_len) return false;
    const int total = manifest->total_shards;
    const int k = manifest->k;
    const int m = total - k;
    if (k <= 0 || total <= 0) return false;

    aethernet_reed_solomon_t *codec = aethernet_reed_solomon_new(k, m);
    if (!codec) return false;

    int *indices = (int *)calloc((size_t)total, sizeof(int));
    const uint8_t **shards = (const uint8_t **)calloc((size_t)total, sizeof(uint8_t *));
    if (!indices || !shards) {
        free(indices);
        free((void *)shards);
        aethernet_reed_solomon_free(codec);
        return false;
    }

    size_t count = 0;
    size_t shard_size = 0;
    for (int i = 0; i < total; i++) {
        vault_shard_t *node = store_find(service, manifest->shard_hashes[i]);
        if (node) {
            indices[count] = i;
            shards[count] = node->bytes;
            if (shard_size == 0) shard_size = node->len;
            count++;
        }
    }

    bool ok = false;
    if ((int)count >= k && shard_size > 0) {
        ok = aethernet_reed_solomon_reconstruct_data(codec, indices, shards, count, shard_size,
                                                     (size_t)manifest->size_bytes, out_data, out_len);
    }

    free(indices);
    free((void *)shards);
    aethernet_reed_solomon_free(codec);
    return ok;
}

void aethernet_vault_check_health(aethernet_vault_service_t *service,
                                  const aethernet_vault_manifest_t *manifest,
                                  aethernet_vault_health_t *out_health) {
    if (!out_health) return;
    memset(out_health, 0, sizeof(*out_health));
    if (!service || !manifest) return;

    int reachable = 0;
    for (int i = 0; i < manifest->total_shards; i++) {
        if (store_find(service, manifest->shard_hashes[i])) reachable++;
    }
    int total = manifest->total_shards;
    out_health->total_shards = total;
    out_health->reachable_shards = reachable;
    out_health->is_recoverable = reachable >= manifest->k;
    out_health->redundancy_score = total > 0 ? (double)reachable / (double)total : 0.0;
}

bool aethernet_vault_replicate(aethernet_vault_service_t *service,
                               const aethernet_vault_manifest_t *manifest,
                               int target_redundancy) {
    (void)service;
    (void)manifest;
    (void)target_redundancy;
    return true; // no-op in the in-memory implementation
}

bool aethernet_vault_forget_shard(aethernet_vault_service_t *service, const char *shard_hash) {
    if (!service || !shard_hash) return false;
    vault_shard_t *prev = NULL;
    for (vault_shard_t *n = service->head; n; prev = n, n = n->next) {
        if (strcmp(n->hash, shard_hash) == 0) {
            if (prev) prev->next = n->next;
            else service->head = n->next;
            free(n->bytes);
            free(n);
            return true;
        }
    }
    return false;
}
