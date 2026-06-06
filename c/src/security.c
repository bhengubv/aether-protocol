// SPDX-License-Identifier: MIT
// Aether Security - Ed25519, AES-GCM, HMAC-SHA256, HKDF using libsodium

#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "aethermesh/security.h"
#include "aethermesh/protocol.h"

#include <sodium.h>

/* ─── Cross-platform "wall-clock seconds since epoch" ─────────────────── */
static int64_t security_now_seconds(void) {
    struct timespec ts;
#if defined(_WIN32)
    timespec_get(&ts, TIME_UTC);
#else
    clock_gettime(CLOCK_REALTIME, &ts);
#endif
    return (int64_t)ts.tv_sec;
}

/**
 * Initialize libsodium (idempotent).
 */
static void ensure_libsodium_initialized(void) {
    static volatile int initialized = 0;
    if (!initialized) {
        if (sodium_init() >= 0) {
            initialized = 1;
        }
    }
}

/**
 * Generate Ed25519 key pair.
 */
bool aethermesh_ed25519_generate_keypair(uint8_t *out_private,
                                    uint8_t *out_public) {
    if (!out_private || !out_public) return false;

    ensure_libsodium_initialized();

    unsigned char pk[crypto_sign_ed25519_PUBLICKEYBYTES];
    unsigned char sk[crypto_sign_ed25519_SECRETKEYBYTES];

    if (crypto_sign_ed25519_keypair(pk, sk) != 0) {
        return false;
    }

    // libsodium's secret key is 64 bytes (32 bytes seed + 32 bytes public key)
    // We only store the 32-byte seed for our "private key"
    memcpy(out_private, sk, crypto_sign_SEEDBYTES);
    memcpy(out_public, pk, crypto_sign_ed25519_PUBLICKEYBYTES);

    // Zero the full sk
    sodium_memzero(sk, sizeof(sk));

    return true;
}

/**
 * Sign data with Ed25519.
 */
bool aethermesh_ed25519_sign(const uint8_t *private_key,
                        const uint8_t *data,
                        size_t data_len,
                        uint8_t *out_signature) {
    if (!private_key || !out_signature) return false;

    ensure_libsodium_initialized();

    // Reconstruct the full secret key from seed
    unsigned char sk[crypto_sign_ed25519_SECRETKEYBYTES];
    unsigned char pk[crypto_sign_ed25519_PUBLICKEYBYTES];

    if (crypto_sign_ed25519_seed_keypair(pk, sk, private_key) != 0) {
        return false;
    }

    unsigned char sig[crypto_sign_ed25519_BYTES];
    unsigned long long sig_len;

    int result = crypto_sign_ed25519_detached(sig, &sig_len, data, data_len, sk);

    sodium_memzero(sk, sizeof(sk));

    if (result != 0) {
        return false;
    }

    memcpy(out_signature, sig, crypto_sign_ed25519_BYTES);
    sodium_memzero(sig, sizeof(sig));

    return true;
}

/**
 * Verify Ed25519 signature.
 */
bool aethermesh_ed25519_verify(const uint8_t *public_key,
                          const uint8_t *data,
                          size_t data_len,
                          const uint8_t *signature) {
    if (!public_key || !signature) return false;

    ensure_libsodium_initialized();

    return crypto_sign_ed25519_verify_detached(signature, data, data_len, public_key) == 0;
}

/**
 * AES-256-GCM encrypt.
 */
bool aethermesh_aes256_gcm_encrypt(const uint8_t *plaintext,
                              size_t plaintext_len,
                              const uint8_t *key,
                              const uint8_t *nonce,
                              const uint8_t *aad,
                              size_t aad_len,
                              uint8_t *out_ciphertext,
                              uint8_t *out_tag,
                              uint8_t *out_nonce) {
    if (!key || !out_ciphertext || !out_tag || !out_nonce) return false;

    ensure_libsodium_initialized();

    // Generate nonce if not provided
    uint8_t actual_nonce[AETHERMESH_AES_GCM_NONCE_SIZE];
    if (nonce == NULL) {
        randombytes_buf(actual_nonce, AETHERMESH_AES_GCM_NONCE_SIZE);
    } else {
        memcpy(actual_nonce, nonce, AETHERMESH_AES_GCM_NONCE_SIZE);
    }
    memcpy(out_nonce, actual_nonce, AETHERMESH_AES_GCM_NONCE_SIZE);

    unsigned char tag[crypto_aead_aes256gcm_ABYTES];
    unsigned long long ciphertext_len_actual;

    // Handle NULL plaintext
    if (plaintext_len > 0 && !plaintext) return false;

    int result = crypto_aead_aes256gcm_encrypt_detached(
        out_ciphertext,
        tag,
        &ciphertext_len_actual,
        plaintext ? plaintext : (const unsigned char *)"",
        plaintext_len,
        aad,
        aad_len,
        NULL,  // secret nonce (unused)
        actual_nonce,
        (const unsigned char *)key
    );

    if (result != 0) {
        sodium_memzero(actual_nonce, sizeof(actual_nonce));
        return false;
    }

    memcpy(out_tag, tag, crypto_aead_aes256gcm_ABYTES);
    sodium_memzero(actual_nonce, sizeof(actual_nonce));
    sodium_memzero(tag, sizeof(tag));

    return true;
}

/**
 * AES-256-GCM decrypt.
 */
bool aethermesh_aes256_gcm_decrypt(const uint8_t *ciphertext,
                              size_t ciphertext_len,
                              const uint8_t *key,
                              const uint8_t *nonce,
                              const uint8_t *tag,
                              const uint8_t *aad,
                              size_t aad_len,
                              uint8_t *out_plaintext) {
    if (!key || !nonce || !tag) return false;

    ensure_libsodium_initialized();

    // Handle NULL ciphertext
    if (ciphertext_len > 0 && !ciphertext) return false;

    int result = crypto_aead_aes256gcm_decrypt_detached(
        out_plaintext,
        NULL,  // secret nonce (unused)
        ciphertext ? ciphertext : (const unsigned char *)"",
        ciphertext_len,
        (const unsigned char *)tag,
        aad,
        aad_len,
        nonce,
        (const unsigned char *)key
    );

    return result == 0;
}

/**
 * HMAC-SHA256.
 */
bool aethermesh_hmac_sha256(const uint8_t *key,
                       size_t key_len,
                       const uint8_t *data,
                       size_t data_len,
                       uint8_t *out_hash) {
    if (!key || !out_hash) return false;

    ensure_libsodium_initialized();

    unsigned char hash[crypto_auth_hmacsha256_BYTES];

    int result = crypto_auth_hmacsha256(hash, data ? data : (const unsigned char *)"", data_len, key);

    if (result != 0) {
        return false;
    }

    memcpy(out_hash, hash, crypto_auth_hmacsha256_BYTES);
    sodium_memzero(hash, sizeof(hash));

    return true;
}

/**
 * SHA-256.
 */
bool aethermesh_sha256(const uint8_t *data,
                  size_t data_len,
                  uint8_t *out_hash) {
    if (!out_hash) return false;

    ensure_libsodium_initialized();

    unsigned char hash[crypto_hash_sha256_BYTES];

    int result = crypto_hash_sha256(hash, data ? data : (const unsigned char *)"", data_len);

    if (result != 0) {
        return false;
    }

    memcpy(out_hash, hash, crypto_hash_sha256_BYTES);
    sodium_memzero(hash, sizeof(hash));

    return true;
}

/**
 * HKDF-SHA256 (extract-and-expand).
 * RFC 5869.
 */
bool aethermesh_hkdf_sha256(const uint8_t *salt,
                       size_t salt_len,
                       const uint8_t *ikm,
                       size_t ikm_len,
                       const uint8_t *info,
                       size_t info_len,
                       size_t output_len,
                       uint8_t *out_okm) {
    if (!ikm || !out_okm) return false;
    if (output_len == 0 || output_len > 255 * AETHERMESH_SHA256_SIZE) return false;

    ensure_libsodium_initialized();

    // HKDF-Extract: PRK = HMAC-Hash(salt, IKM)
    uint8_t salt_default[AETHERMESH_SHA256_SIZE];
    if (salt == NULL) {
        memset(salt_default, 0, AETHERMESH_SHA256_SIZE);
        salt = salt_default;
        salt_len = AETHERMESH_SHA256_SIZE;
    }

    uint8_t prk[AETHERMESH_SHA256_SIZE];
    if (!aethermesh_hmac_sha256(salt, salt_len, ikm, ikm_len, prk)) {
        return false;
    }

    // HKDF-Expand: T = T(1) | T(2) | T(3) | ... | T(N)
    // T(1) = HMAC-Hash(PRK, info | 0x01)
    // T(2) = HMAC-Hash(PRK, T(1) | info | 0x02)
    // ...
    uint8_t t[AETHERMESH_SHA256_SIZE];
    size_t okm_offset = 0;
    uint8_t counter = 1;

    while (okm_offset < output_len && counter <= 255) {
        size_t t_len = okm_offset > 0 ? AETHERMESH_SHA256_SIZE : 0;

        // Create input: [previous T] | info | counter
        size_t input_len = t_len + info_len + 1;
        uint8_t *input = (uint8_t *)malloc(input_len);
        if (!input) {
            sodium_memzero(prk, sizeof(prk));
            sodium_memzero(t, sizeof(t));
            return false;
        }

        if (t_len > 0) {
            memcpy(input, t, t_len);
        }
        if (info_len > 0) {
            memcpy(&input[t_len], info, info_len);
        }
        input[t_len + info_len] = counter;

        uint8_t t_new[AETHERMESH_SHA256_SIZE];
        bool ok = aethermesh_hmac_sha256(prk, AETHERMESH_SHA256_SIZE, input, input_len, t_new);
        free(input);

        if (!ok) {
            sodium_memzero(prk, sizeof(prk));
            sodium_memzero(t, sizeof(t));
            return false;
        }

        size_t copy_len = output_len - okm_offset;
        if (copy_len > AETHERMESH_SHA256_SIZE) {
            copy_len = AETHERMESH_SHA256_SIZE;
        }

        memcpy(&out_okm[okm_offset], t_new, copy_len);
        okm_offset += copy_len;

        memcpy(t, t_new, AETHERMESH_SHA256_SIZE);
        sodium_memzero(t_new, sizeof(t_new));

        counter++;
    }

    sodium_memzero(prk, sizeof(prk));
    sodium_memzero(t, sizeof(t));

    return true;
}

/**
 * Signal Double-Ratchet KDF_RK (Signal §5.2).
 *
 * HKDF-SHA256 over (salt=root_key, ikm=dh_output, info="aether-ratchet-rk-v1",
 * L=64) — split into new_root_key (first 32 bytes) and new_chain_key (next 32).
 * Mirrors C# SignalProtocolService.KdfRk byte-for-byte.
 */
bool aethermesh_signal_kdf_rk(const uint8_t *root_key,
                          const uint8_t *dh_output,
                          uint8_t *out_new_root_key,
                          uint8_t *out_new_chain_key) {
    if (!root_key || !dh_output || !out_new_root_key || !out_new_chain_key) {
        return false;
    }

    static const char info[] = "aether-ratchet-rk-v1";
    static const size_t info_len = sizeof(info) - 1;

    uint8_t derived[64];
    bool ok = aethermesh_hkdf_sha256(
        root_key, 32,
        dh_output, 32,
        (const uint8_t *)info, info_len,
        sizeof(derived), derived);

    if (!ok) {
        sodium_memzero(derived, sizeof(derived));
        return false;
    }

    memcpy(out_new_root_key, derived, 32);
    memcpy(out_new_chain_key, derived + 32, 32);
    sodium_memzero(derived, sizeof(derived));
    return true;
}

/**
 * Zeroize memory.
 */
void aethermesh_zeroize(void *mem, size_t len) {
    if (!mem) return;
    ensure_libsodium_initialized();
    sodium_memzero(mem, len);
}

/**
 * Generate random bytes.
 */
bool aethermesh_random_bytes(uint8_t *out, size_t len) {
    if (!out) return false;

    ensure_libsodium_initialized();

    randombytes_buf(out, len);
    return true;
}

/**
 * Generate a fresh X25519 keypair.
 */
bool aethermesh_x25519_generate_keypair(uint8_t *out_private,
                                    uint8_t *out_public) {
    if (!out_private || !out_public) return false;

    ensure_libsodium_initialized();

    // Random 32-byte private key. libsodium clamps internally on use.
    randombytes_buf(out_private, AETHERMESH_X25519_PRIVATE_KEY_SIZE);

    // Public key = private * Basepoint.
    if (crypto_scalarmult_curve25519_base(out_public, out_private) != 0) {
        sodium_memzero(out_private, AETHERMESH_X25519_PRIVATE_KEY_SIZE);
        return false;
    }
    return true;
}

/**
 * X25519 ECDH agreement. Returns 32 raw shared-secret bytes.
 *
 * RFC 7748 §6.1: detect the all-zero output (small-subgroup attack via a
 * low-order remote public key). libsodium's crypto_scalarmult returns -1
 * on the all-zero result by default — we surface that as `false`.
 */
bool aethermesh_x25519_agree(const uint8_t *local_private,
                         const uint8_t *remote_public,
                         uint8_t *out_shared) {
    if (!local_private || !remote_public || !out_shared) return false;

    ensure_libsodium_initialized();

    if (crypto_scalarmult_curve25519(out_shared, local_private, remote_public) != 0) {
        sodium_memzero(out_shared, AETHERMESH_X25519_SHARED_SECRET_SIZE);
        return false;
    }
    return true;
}

/**
 * X25519 base-point scalar multiplication: pub = priv * Basepoint.
 */
bool aethermesh_x25519_derive_public(const uint8_t *private_key,
                                 uint8_t *out_public) {
    if (!private_key || !out_public) return false;

    ensure_libsodium_initialized();

    if (crypto_scalarmult_curve25519_base(out_public, private_key) != 0) {
        sodium_memzero(out_public, AETHERMESH_X25519_PUBLIC_KEY_SIZE);
        return false;
    }
    return true;
}

/* ─────────────────────────────────────────────────────────────────────────
 * Nonce deduplication store
 * ──────────────────────────────────────────────────────────────────────── */

/* Maximum number of (source, nonce) pairs tracked simultaneously. */
#define AETHERMESH_NONCE_STORE_MAX_ENTRIES 4096

/* Maximum nonce size accepted (bytes). */
#define AETHERMESH_NONCE_STORE_MAX_NONCE_LEN 64

/* Maximum source UHID length (bytes). */
#define AETHERMESH_NONCE_STORE_MAX_SOURCE_LEN 64

/* Composite key: "source:hex(nonce)" — max length:
 *   source (63) + ':' (1) + hex nonce (128) + NUL (1) = 193 bytes */
#define AETHERMESH_NONCE_STORE_KEY_LEN 193

typedef struct {
    char    key[AETHERMESH_NONCE_STORE_KEY_LEN]; /* "source:hexnonce\0"     */
    int64_t expires_at;                       /* wall-clock seconds      */
} aethermesh_nonce_entry_t;

struct aethermesh_nonce_store {
    aethermesh_nonce_entry_t entries[AETHERMESH_NONCE_STORE_MAX_ENTRIES];
    int                  count;
};

aethermesh_nonce_store_t *aethermesh_nonce_store_new(void) {
    aethermesh_nonce_store_t *s =
        (aethermesh_nonce_store_t *)calloc(1, sizeof(aethermesh_nonce_store_t));
    return s; /* NULL on allocation failure */
}

void aethermesh_nonce_store_free(aethermesh_nonce_store_t *store) {
    if (!store) return;
    /* Zero any key material before freeing */
    memset(store, 0, sizeof(*store));
    free(store);
}

/**
 * Build the composite lookup key "source_uhid:HEX(nonce)" into `buf`.
 * Returns false if the source or nonce are too long to fit.
 */
static bool build_nonce_key(char *buf, size_t buf_len,
                             const char *source_uhid,
                             const uint8_t *nonce, size_t nonce_len) {
    size_t src_len = strlen(source_uhid);
    /* hex(nonce) needs 2 chars per byte */
    size_t needed = src_len + 1 + nonce_len * 2 + 1;
    if (needed > buf_len) return false;

    memcpy(buf, source_uhid, src_len);
    buf[src_len] = ':';
    char *p = buf + src_len + 1;
    for (size_t i = 0; i < nonce_len; i++) {
        static const char hex[] = "0123456789abcdef";
        *p++ = hex[(nonce[i] >> 4) & 0xF];
        *p++ = hex[nonce[i] & 0xF];
    }
    *p = '\0';
    return true;
}

/**
 * Prune expired entries (shift remaining entries down).
 * Called lazily before inserting a new entry.
 */
static void nonce_store_prune(aethermesh_nonce_store_t *store, int64_t now) {
    int new_count = 0;
    for (int i = 0; i < store->count; i++) {
        if (store->entries[i].expires_at > now) {
            if (new_count != i) {
                store->entries[new_count] = store->entries[i];
            }
            new_count++;
        }
    }
    /* Zero the vacated tail slots */
    for (int i = new_count; i < store->count; i++) {
        memset(&store->entries[i], 0, sizeof(aethermesh_nonce_entry_t));
    }
    store->count = new_count;
}

bool aethermesh_nonce_store_check_and_record(aethermesh_nonce_store_t *store,
                                          const char *source_uhid,
                                          const uint8_t *nonce,
                                          size_t nonce_len,
                                          int ttl_seconds) {
    if (!store || !source_uhid || !nonce || nonce_len == 0) return false;
    if (nonce_len > AETHERMESH_NONCE_STORE_MAX_NONCE_LEN) return false;
    if (strlen(source_uhid) >= AETHERMESH_NONCE_STORE_MAX_SOURCE_LEN) return false;

    char key[AETHERMESH_NONCE_STORE_KEY_LEN];
    if (!build_nonce_key(key, sizeof(key), source_uhid, nonce, nonce_len)) {
        return false;
    }

    int64_t now = security_now_seconds();

    /* Prune expired entries before the lookup — keeps the store bounded. */
    nonce_store_prune(store, now);

    /* Search for an existing non-expired entry with the same key. */
    for (int i = 0; i < store->count; i++) {
        if (strcmp(store->entries[i].key, key) == 0) {
            /* Found — this is a replay. */
            return false;
        }
    }

    /* Not a replay. Record it if there is space (oldest entry is evicted
     * if the store is full). */
    if (store->count >= AETHERMESH_NONCE_STORE_MAX_ENTRIES) {
        /* Evict the first (oldest) slot by shifting left. */
        memmove(&store->entries[0], &store->entries[1],
                (AETHERMESH_NONCE_STORE_MAX_ENTRIES - 1) * sizeof(aethermesh_nonce_entry_t));
        store->count = AETHERMESH_NONCE_STORE_MAX_ENTRIES - 1;
    }

    aethermesh_nonce_entry_t *e = &store->entries[store->count];
    memcpy(e->key, key, strlen(key) + 1);
    e->expires_at = now + (int64_t)ttl_seconds;
    store->count++;

    return true;
}

/* ─────────────────────────────────────────────────────────────────────────
 * PacketSigning service
 * ──────────────────────────────────────────────────────────────────────── */

void aethermesh_packet_signing_init(AetherMeshPacketSigningService *svc,
                                aethermesh_nonce_store_t *nonce_store) {
    if (!svc) return;
    svc->nonce_store = nonce_store;
    svc->reputation  = NULL;
}

void aethermesh_packet_signing_set_reputation(AetherMeshPacketSigningService *svc,
                                           AetherMeshNodeReputationService *rep) {
    if (!svc) return;
    svc->reputation = rep;
}

bool aethermesh_packet_signing_verify(AetherMeshPacketSigningService *svc,
                                   const aethermesh_mesh_packet_t *packet,
                                   const uint8_t *sender_public_key,
                                   int ttl_seconds) {
    if (!svc || !packet || !sender_public_key) return false;

    const char *source_uhid = packet->source_uhid ? packet->source_uhid : "";

    /* 1. Nonce replay check ─────────────────────────────────────────────── */
    if (svc->nonce_store != NULL) {
        bool fresh = aethermesh_nonce_store_check_and_record(
            svc->nonce_store,
            source_uhid,
            packet->packet_nonce,
            AETHERMESH_PACKET_NONCE_SIZE,
            ttl_seconds);

        if (!fresh) {
            /* Replay detected — fire reputation hook if wired. */
            if (svc->reputation != NULL) {
                aethermesh_reputation_record_replay(svc->reputation, source_uhid);
            }
            return false;
        }
    }

    /* 2. Build signable data ────────────────────────────────────────────── */
    size_t sig_data_len = 0;
    uint8_t *sig_data = aethermesh_packet_get_signable_data(packet, &sig_data_len);
    if (!sig_data) return false;

    /* 3. Verify Ed25519 signature ────────────────────────────────────────── */
    bool valid = aethermesh_ed25519_verify(
        sender_public_key,
        sig_data,
        sig_data_len,
        packet->signature);

    free(sig_data);

    if (!valid) {
        /* Signature failure — fire reputation hook if wired. */
        if (svc->reputation != NULL) {
            aethermesh_reputation_record_sig_failure(svc->reputation, source_uhid);
        }
        return false;
    }

    return true;
}
