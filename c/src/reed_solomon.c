// SPDX-License-Identifier: MIT
// AetherNet Vault — systematic Cauchy-Reed-Solomon (K, N) erasure codec over GF(2⁸).
//
// C port of AetherNet.Vault.ReedSolomonCodec. See include/aethernet/reed_solomon.h
// for the contract. Byte-identical to the C# reference and every other language
// implementation, proven against fixtures/vault/reed_solomon_basic.json.
//
// The GF(2⁸) field arithmetic is reused from the RLNC engine (aethernet_gf256_*,
// same primitive polynomial 0x11D / α=2), guaranteeing identical parity bytes.

#include "aethernet/reed_solomon.h"

#include <stdlib.h>
#include <string.h>

#include "aethernet/rlnc.h"   /* aethernet_gf256_init / _mul / _inv */

// ── allocation helpers ──────────────────────────────────────────────────────

// Free the first `count` non-NULL slots of an array of pointers and NULL them.
static void free_shard_array(uint8_t **shards, size_t count) {
    if (!shards) return;
    for (size_t i = 0; i < count; i++) {
        free(shards[i]);
        shards[i] = NULL;
    }
}

// ── Cauchy parity matrix ────────────────────────────────────────────────────
//
// C[i][j] = 1 / (x_i ⊕ y_j) with disjoint distinct element sets y_j = j (0…K-1) and
// x_i = K + i (K…K+M-1). Cauchy ⇒ every square submatrix invertible ⇒ MDS when
// stacked on the systematic identity.
static uint8_t **build_cauchy_parity_matrix(int k, int m) {
    if (m == 0) {
        // Allocate a zero-length-but-valid array so free() logic is uniform.
        uint8_t **matrix = (uint8_t **)calloc(1, sizeof(uint8_t *));
        return matrix;
    }
    uint8_t **matrix = (uint8_t **)calloc((size_t)m, sizeof(uint8_t *));
    if (!matrix) return NULL;
    for (int i = 0; i < m; i++) {
        uint8_t *row = (uint8_t *)malloc((size_t)k);
        if (!row) {
            for (int r = 0; r < i; r++) free(matrix[r]);
            free(matrix);
            return NULL;
        }
        uint8_t xi = (uint8_t)(k + i);
        for (int j = 0; j < k; j++) {
            uint8_t yj = (uint8_t)j;
            // x_i and y_j are drawn from disjoint ranges, so x_i ⊕ y_j is never 0.
            row[j] = aethernet_gf256_inv((uint8_t)(xi ^ yj));
        }
        matrix[i] = row;
    }
    return matrix;
}

// ── lifecycle ───────────────────────────────────────────────────────────────

aethernet_reed_solomon_t *aethernet_reed_solomon_new(int k, int m) {
    if (k < 1 || m < 0 || k + m > 256) return NULL;

    aethernet_gf256_init();

    aethernet_reed_solomon_t *codec = (aethernet_reed_solomon_t *)malloc(sizeof(*codec));
    if (!codec) return NULL;

    codec->k = k;
    codec->m = m;
    codec->n = k + m;
    codec->parity = build_cauchy_parity_matrix(k, m);
    if (!codec->parity) {
        free(codec);
        return NULL;
    }
    return codec;
}

void aethernet_reed_solomon_free(aethernet_reed_solomon_t *codec) {
    if (!codec) return;
    if (codec->parity) {
        for (int i = 0; i < codec->m; i++) free(codec->parity[i]);
        free(codec->parity);
    }
    free(codec);
}

// ── generator matrix ────────────────────────────────────────────────────────

// Write the K-byte generator row for shard `index` into row (caller-allocated, K
// bytes): identity basis vector for a data shard, Cauchy coefficients for a parity
// shard.
static void generator_row(const aethernet_reed_solomon_t *codec, int index, uint8_t *row) {
    memset(row, 0, (size_t)codec->k);
    if (index < codec->k) {
        row[index] = 1; // systematic data row = standard basis vector e_index
    } else {
        memcpy(row, codec->parity[index - codec->k], (size_t)codec->k);
    }
}

// ── GF(256) matrix inversion (Gauss-Jordan) ─────────────────────────────────
//
// Invert a K×K GF(256) matrix `m` (K rows of K bytes) into `inv` (K rows of K
// bytes, caller-allocated). The Cauchy/identity stack guarantees the picked
// submatrix is non-singular. Returns true on success, false if singular / OOM.
static bool invert_matrix(int k, uint8_t **m, uint8_t **inv) {
    int n = k;

    // Augmented matrix [m | I], n rows of 2n bytes.
    uint8_t **aug = (uint8_t **)calloc((size_t)n, sizeof(uint8_t *));
    if (!aug) return false;
    bool ok = true;
    for (int r = 0; r < n && ok; r++) {
        aug[r] = (uint8_t *)calloc((size_t)(2 * n), 1);
        if (!aug[r]) { ok = false; break; }
        memcpy(aug[r], m[r], (size_t)n);
        aug[r][n + r] = 1;
    }

    for (int col = 0; col < n && ok; col++) {
        // Find a pivot row at or below `col` with a non-zero entry in this column.
        int pivot = -1;
        for (int r = col; r < n; r++) {
            if (aug[r][col] != 0) { pivot = r; break; }
        }
        if (pivot < 0) { ok = false; break; } // singular

        if (pivot != col) {
            uint8_t *tmp = aug[col]; aug[col] = aug[pivot]; aug[pivot] = tmp;
        }

        // Normalise the pivot row so the pivot element becomes 1.
        uint8_t pinv = aethernet_gf256_inv(aug[col][col]);
        for (int c = 0; c < 2 * n; c++) {
            aug[col][c] = aethernet_gf256_mul(aug[col][c], pinv);
        }

        // Eliminate this column from every other row.
        for (int r = 0; r < n; r++) {
            if (r == col) continue;
            uint8_t factor = aug[r][col];
            if (factor == 0) continue;
            for (int c = 0; c < 2 * n; c++) {
                aug[r][c] = (uint8_t)(aug[r][c] ^ aethernet_gf256_mul(factor, aug[col][c]));
            }
        }
    }

    if (ok) {
        // Right half is the inverse.
        for (int r = 0; r < n; r++) {
            memcpy(inv[r], aug[r] + n, (size_t)n);
        }
    }

    if (aug) {
        for (int r = 0; r < n; r++) free(aug[r]);
        free(aug);
    }
    return ok;
}

// ── encode ──────────────────────────────────────────────────────────────────

bool aethernet_reed_solomon_encode(const aethernet_reed_solomon_t *codec,
                                   const uint8_t *const *data_shards,
                                   size_t shard_size,
                                   uint8_t **out_shards) {
    if (!codec || !data_shards || !out_shards || shard_size == 0) return false;

    for (int j = 0; j < codec->k; j++) {
        if (!data_shards[j]) return false;
    }
    for (int i = 0; i < codec->n; i++) out_shards[i] = NULL;

    // Systematic: the first K shards ARE the data shards (defensive copies).
    for (int j = 0; j < codec->k; j++) {
        uint8_t *clone = (uint8_t *)malloc(shard_size);
        if (!clone) { free_shard_array(out_shards, (size_t)codec->n); return false; }
        memcpy(clone, data_shards[j], shard_size);
        out_shards[j] = clone;
    }

    // Parity: shard K+i = Σ_j parity[i][j] · dataShards[j] over GF(256).
    for (int i = 0; i < codec->m; i++) {
        uint8_t *parity_shard = (uint8_t *)calloc(shard_size, 1);
        if (!parity_shard) { free_shard_array(out_shards, (size_t)codec->n); return false; }
        const uint8_t *coeffs = codec->parity[i];
        for (int j = 0; j < codec->k; j++) {
            uint8_t coeff = coeffs[j];
            if (coeff == 0) continue;
            const uint8_t *src = data_shards[j];
            for (size_t b = 0; b < shard_size; b++) {
                parity_shard[b] = (uint8_t)(parity_shard[b] ^ aethernet_gf256_mul(coeff, src[b]));
            }
        }
        out_shards[codec->k + i] = parity_shard;
    }

    return true;
}

bool aethernet_reed_solomon_encode_data(const aethernet_reed_solomon_t *codec,
                                        const uint8_t *data,
                                        size_t data_len,
                                        uint8_t **out_shards,
                                        size_t *out_shard_size) {
    if (!codec || !data || data_len == 0 || !out_shards || !out_shard_size) return false;

    int k = codec->k;
    size_t shard_size = (data_len + (size_t)k - 1) / (size_t)k; // ceil(len/K)
    if (shard_size == 0) return false;

    // Split into K equal zero-padded data shards.
    uint8_t **data_shards = (uint8_t **)calloc((size_t)k, sizeof(uint8_t *));
    if (!data_shards) return false;

    bool ok = true;
    for (int i = 0; i < k && ok; i++) {
        uint8_t *shard = (uint8_t *)calloc(shard_size, 1);
        if (!shard) { ok = false; break; }
        size_t offset = (size_t)i * shard_size;
        if (offset < data_len) {
            size_t length = shard_size;
            if (offset + length > data_len) length = data_len - offset;
            memcpy(shard, data + offset, length);
        }
        data_shards[i] = shard;
    }

    if (ok) {
        ok = aethernet_reed_solomon_encode(codec, (const uint8_t *const *)data_shards, shard_size, out_shards);
    }

    free_shard_array(data_shards, (size_t)k);
    free(data_shards);

    if (ok) *out_shard_size = shard_size;
    return ok;
}

// ── decode ──────────────────────────────────────────────────────────────────

bool aethernet_reed_solomon_decode_data_shards(const aethernet_reed_solomon_t *codec,
                                               const int *available_indices,
                                               const uint8_t *const *available_shards,
                                               size_t available_count,
                                               size_t shard_size,
                                               uint8_t **out_data_shards) {
    if (!codec || !available_indices || !available_shards || !out_data_shards || shard_size == 0) {
        return false;
    }

    int k = codec->k;
    int n = codec->n;

    // Collect valid (index in range, shard non-NULL) entries, deduping by index and
    // keeping the first occurrence's position. picked_idx[r] is the shard index of
    // the r-th picked survivor; picked_pos_arr[r] is its position in the input arrays.
    if (available_count == 0) return false;
    int *picked_idx = (int *)malloc(available_count * sizeof(int));
    size_t *picked_pos_arr = (size_t *)malloc(available_count * sizeof(size_t));
    if (!picked_idx || !picked_pos_arr) { free(picked_idx); free(picked_pos_arr); return false; }

    size_t picked_n = 0;
    for (size_t j = 0; j < available_count; j++) {
        int idx = available_indices[j];
        if (idx < 0 || idx >= n || available_shards[j] == NULL) continue;
        // Skip duplicate indices (keep the first occurrence).
        bool dup = false;
        for (size_t p = 0; p < picked_n; p++) {
            if (picked_idx[p] == idx) { dup = true; break; }
        }
        if (dup) continue;
        picked_idx[picked_n] = idx;
        picked_pos_arr[picked_n] = j;
        picked_n++;
    }

    if (picked_n < (size_t)k) { free(picked_idx); free(picked_pos_arr); return false; }

    // Sort indices ascending, carrying their source positions alongside.
    // Simple insertion sort on (index, pos) pairs — picked_n is small (<= N <= 256).
    for (size_t a = 1; a < picked_n; a++) {
        int ki = picked_idx[a];
        size_t kp = picked_pos_arr[a];
        size_t b = a;
        while (b > 0 && picked_idx[b - 1] > ki) {
            picked_idx[b] = picked_idx[b - 1];
            picked_pos_arr[b] = picked_pos_arr[b - 1];
            b--;
        }
        picked_idx[b] = ki;
        picked_pos_arr[b] = kp;
    }

    // The K lowest-indexed survivors (picked_idx[0..k-1]) are used; any K suffice for
    // an MDS code. Equal shard length is the caller's responsibility.
    for (int i = 0; i < k; i++) out_data_shards[i] = NULL;

    // Fast path: if all K picked indices are data shards (0…K-1), the data is the
    // systematic prefix verbatim — no inversion needed.
    bool all_data = true;
    for (int r = 0; r < k; r++) {
        if (picked_idx[r] >= k) { all_data = false; break; }
    }

    bool ok = true;
    if (all_data) {
        for (int r = 0; r < k; r++) {
            int idx = picked_idx[r];               // 0…K-1
            size_t pos = picked_pos_arr[r];
            uint8_t *clone = (uint8_t *)malloc(shard_size);
            if (!clone) { ok = false; break; }
            memcpy(clone, available_shards[pos], shard_size);
            out_data_shards[idx] = clone;          // place in its natural data slot
        }
        if (!ok) { free_shard_array(out_data_shards, (size_t)k); }
        free(picked_idx); free(picked_pos_arr);
        return ok;
    }

    // General path: build the K×K generator submatrix A for the picked indices,
    // invert it, and apply A⁻¹ to the picked symbol-vectors to recover the K source
    // (data) symbols.
    uint8_t **a   = (uint8_t **)calloc((size_t)k, sizeof(uint8_t *));
    uint8_t **inv = (uint8_t **)calloc((size_t)k, sizeof(uint8_t *));
    const uint8_t **rhs = (const uint8_t **)calloc((size_t)k, sizeof(uint8_t *));
    if (!a || !inv || !rhs) {
        free(a); free(inv); free(rhs); free(picked_idx); free(picked_pos_arr);
        return false;
    }

    for (int r = 0; r < k && ok; r++) {
        a[r]   = (uint8_t *)malloc((size_t)k);
        inv[r] = (uint8_t *)malloc((size_t)k);
        if (!a[r] || !inv[r]) { ok = false; break; }
        generator_row(codec, picked_idx[r], a[r]);
        rhs[r] = available_shards[picked_pos_arr[r]];
    }

    if (ok) ok = invert_matrix(k, a, inv);

    if (ok) {
        for (int r = 0; r < k && ok; r++) {
            uint8_t *symbol = (uint8_t *)calloc(shard_size, 1);
            if (!symbol) { ok = false; break; }
            for (int c = 0; c < k; c++) {
                uint8_t coeff = inv[r][c];
                if (coeff == 0) continue;
                const uint8_t *src = rhs[c];
                for (size_t b = 0; b < shard_size; b++) {
                    symbol[b] = (uint8_t)(symbol[b] ^ aethernet_gf256_mul(coeff, src[b]));
                }
            }
            out_data_shards[r] = symbol;
        }
    }

    if (!ok) free_shard_array(out_data_shards, (size_t)k);

    for (int r = 0; r < k; r++) { if (a) free(a[r]); if (inv) free(inv[r]); }
    free(a); free(inv); free(rhs);
    free(picked_idx); free(picked_pos_arr);
    return ok;
}

bool aethernet_reed_solomon_reconstruct_data(const aethernet_reed_solomon_t *codec,
                                             const int *available_indices,
                                             const uint8_t *const *available_shards,
                                             size_t available_count,
                                             size_t shard_size,
                                             size_t original_size,
                                             uint8_t **out_data,
                                             size_t *out_len) {
    if (!codec || !out_data || !out_len || shard_size == 0) return false;

    int k = codec->k;

    uint8_t **data_shards = (uint8_t **)calloc((size_t)k, sizeof(uint8_t *));
    if (!data_shards) return false;

    bool ok = aethernet_reed_solomon_decode_data_shards(codec, available_indices, available_shards,
                                                        available_count, shard_size, data_shards);
    if (!ok) { free(data_shards); return false; }

    size_t total = (size_t)k * shard_size;
    if (original_size > total) {
        free_shard_array(data_shards, (size_t)k);
        free(data_shards);
        return false;
    }

    uint8_t *out = (uint8_t *)malloc(total);
    if (!out) {
        free_shard_array(data_shards, (size_t)k);
        free(data_shards);
        return false;
    }
    for (int j = 0; j < k; j++) {
        memcpy(out + (size_t)j * shard_size, data_shards[j], shard_size);
    }

    free_shard_array(data_shards, (size_t)k);
    free(data_shards);

    // Trim to the original size (the buffer is malloc'd at full length; we hand back
    // a view of original_size bytes — realloc to shrink so the caller frees exactly).
    if (original_size < total) {
        uint8_t *shrunk = (uint8_t *)realloc(out, original_size ? original_size : 1);
        if (shrunk) out = shrunk;
    }

    *out_data = out;
    *out_len = original_size;
    return true;
}
