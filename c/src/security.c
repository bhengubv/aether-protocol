// SPDX-License-Identifier: MIT
// Aether Security - Ed25519, AES-GCM, HMAC-SHA256, HKDF using libsodium

// clock_gettime()/CLOCK_REALTIME are POSIX.1-2008; on strict libc builds
// (Linux, -std=c11) they are hidden unless this feature-test macro is defined
// before any system header is included. No-op on macOS/Windows.
#if !defined(_WIN32) && !defined(_POSIX_C_SOURCE)
#  define _POSIX_C_SOURCE 200809L
#endif

#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "aethernet/security.h"
#include "aethernet/protocol.h"

#include <sodium.h>
#include "uECC.h"
#include <blake3.h>

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
bool aethernet_ed25519_generate_keypair(uint8_t *out_private,
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
bool aethernet_ed25519_sign(const uint8_t *private_key,
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
bool aethernet_ed25519_verify(const uint8_t *public_key,
                          const uint8_t *data,
                          size_t data_len,
                          const uint8_t *signature) {
    if (!public_key || !signature) return false;

    ensure_libsodium_initialized();

    return crypto_sign_ed25519_verify_detached(signature, data, data_len, public_key) == 0;
}

/* --- Legacy P-256 (secp256r1) ECDSA verify - Ed25519 migration fallback (spec 7.5) ---
 * libsodium has no NIST P-256, so the raw curve verify is micro-ecc (uECC); SHA-256 is
 * libsodium's. The public key arrives as an X.509 SubjectPublicKeyInfo DER blob and the
 * signature as an ASN.1 DER ECDSA SEQUENCE - both fixed-shape for P-256, parsed below
 * into the raw 64-byte point / 64-byte r||s that uECC expects. */

/* Pull the uncompressed EC point (0x04 || X || Y) out of a P-256 SPKI DER blob into a
 * raw 64-byte X||Y key. For prime256v1 the point is always the trailing 65 bytes. */
static int p256_point_from_spki(const uint8_t *spki, size_t len, uint8_t out_pub[64]) {
    if (spki == NULL || len < 65 || len > 256) return 0;
    const uint8_t *point = spki + (len - 65);
    if (point[0] != 0x04) return 0;            /* uncompressed point marker */
    memcpy(out_pub, point + 1, 64);
    return 1;
}

/* Read one DER INTEGER (tag 0x02) into a fixed 32-byte big-endian field, stripping the
 * sign-padding 0x00 and left-padding short values. Advances *pp past the integer. */
static int der_int_to_32(const uint8_t **pp, const uint8_t *end, uint8_t out[32]) {
    if (*pp >= end || **pp != 0x02) return 0;
    (*pp)++;
    if (*pp >= end) return 0;
    size_t l = *(*pp)++;
    if (l == 0 || (size_t)(end - *pp) < l) return 0;
    const uint8_t *v = *pp;
    *pp += l;
    while (l > 0 && v[0] == 0x00) { v++; l--; }   /* strip leading sign pad */
    if (l > 32) return 0;
    memset(out, 0, 32);
    memcpy(out + (32 - l), v, l);
    return 1;
}

/* Parse a DER ECDSA signature (SEQUENCE { INTEGER r, INTEGER s }) into raw 64-byte
 * r||s for uECC. P-256 signatures use the short-form SEQUENCE length. */
static int p256_rawsig_from_der(const uint8_t *der, size_t len, uint8_t out_sig[64]) {
    if (der == NULL || len < 8) return 0;
    const uint8_t *p = der;
    const uint8_t *end = der + len;
    if (*p++ != 0x30) return 0;                   /* SEQUENCE */
    size_t seq_len = *p++;
    if (seq_len & 0x80) return 0;                 /* expect short form for P-256 */
    if ((size_t)(end - p) < seq_len) return 0;
    end = p + seq_len;                            /* bound to the declared sequence */
    if (!der_int_to_32(&p, end, out_sig)) return 0;
    if (!der_int_to_32(&p, end, out_sig + 32)) return 0;
    return 1;
}

bool aethernet_ed25519_verify_with_fallback(const uint8_t *public_key,
                                            size_t public_key_len,
                                            const uint8_t *data,
                                            size_t data_len,
                                            const uint8_t *signature,
                                            size_t signature_len) {
    if (!public_key || !signature) return false;

    if (public_key_len == 32) {
        if (signature_len != 64) return false;
        return aethernet_ed25519_verify(public_key, data, data_len, signature);
    }

    /* Legacy P-256 path. */
    ensure_libsodium_initialized();

    uint8_t pub[64];
    uint8_t sig[64];
    if (!p256_point_from_spki(public_key, public_key_len, pub)) return false;
    if (!p256_rawsig_from_der(signature, signature_len, sig)) return false;

    uint8_t digest[crypto_hash_sha256_BYTES];     /* 32 */
    crypto_hash_sha256(digest, data, data_len);

    return uECC_verify(pub, digest, sizeof(digest), sig, uECC_secp256r1()) == 1;
}
/**
 * AES-256-GCM encrypt.
 */
bool aethernet_aes256_gcm_encrypt(const uint8_t *plaintext,
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
    uint8_t actual_nonce[AETHERNET_AES_GCM_NONCE_SIZE];
    if (nonce == NULL) {
        randombytes_buf(actual_nonce, AETHERNET_AES_GCM_NONCE_SIZE);
    } else {
        memcpy(actual_nonce, nonce, AETHERNET_AES_GCM_NONCE_SIZE);
    }
    memcpy(out_nonce, actual_nonce, AETHERNET_AES_GCM_NONCE_SIZE);

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
bool aethernet_aes256_gcm_decrypt(const uint8_t *ciphertext,
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
bool aethernet_hmac_sha256(const uint8_t *key,
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
bool aethernet_sha256(const uint8_t *data,
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
 * BLAKE3 hash (32-byte output, portable C reference).
 *
 * Wraps the upstream blake3_hasher one-shot pattern. Wire-compatible
 * with the BLAKE3 implementations in every other language in this
 * repo (Rust, Go, C#, Swift, Python, JS).
 */
bool aethernet_blake3(const uint8_t *data,
                  size_t data_len,
                  uint8_t *out_hash) {
    if (!out_hash) return false;

    blake3_hasher hasher;
    blake3_hasher_init(&hasher);
    if (data != NULL && data_len > 0) {
        blake3_hasher_update(&hasher, data, data_len);
    }
    blake3_hasher_finalize(&hasher, out_hash, BLAKE3_OUT_LEN);
    return true;
}

/**
 * HKDF-SHA256 (extract-and-expand).
 * RFC 5869.
 */
bool aethernet_hkdf_sha256(const uint8_t *salt,
                       size_t salt_len,
                       const uint8_t *ikm,
                       size_t ikm_len,
                       const uint8_t *info,
                       size_t info_len,
                       size_t output_len,
                       uint8_t *out_okm) {
    if (!ikm || !out_okm) return false;
    if (output_len == 0 || output_len > 255 * AETHERNET_SHA256_SIZE) return false;

    ensure_libsodium_initialized();

    // HKDF-Extract: PRK = HMAC-Hash(salt, IKM)
    uint8_t salt_default[AETHERNET_SHA256_SIZE];
    if (salt == NULL) {
        memset(salt_default, 0, AETHERNET_SHA256_SIZE);
        salt = salt_default;
        salt_len = AETHERNET_SHA256_SIZE;
    }

    uint8_t prk[AETHERNET_SHA256_SIZE];
    if (!aethernet_hmac_sha256(salt, salt_len, ikm, ikm_len, prk)) {
        return false;
    }

    // HKDF-Expand: T = T(1) | T(2) | T(3) | ... | T(N)
    // T(1) = HMAC-Hash(PRK, info | 0x01)
    // T(2) = HMAC-Hash(PRK, T(1) | info | 0x02)
    // ...
    uint8_t t[AETHERNET_SHA256_SIZE];
    size_t okm_offset = 0;
    uint8_t counter = 1;

    while (okm_offset < output_len && counter <= 255) {
        size_t t_len = okm_offset > 0 ? AETHERNET_SHA256_SIZE : 0;

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

        uint8_t t_new[AETHERNET_SHA256_SIZE];
        bool ok = aethernet_hmac_sha256(prk, AETHERNET_SHA256_SIZE, input, input_len, t_new);
        free(input);

        if (!ok) {
            sodium_memzero(prk, sizeof(prk));
            sodium_memzero(t, sizeof(t));
            return false;
        }

        size_t copy_len = output_len - okm_offset;
        if (copy_len > AETHERNET_SHA256_SIZE) {
            copy_len = AETHERNET_SHA256_SIZE;
        }

        memcpy(&out_okm[okm_offset], t_new, copy_len);
        okm_offset += copy_len;

        memcpy(t, t_new, AETHERNET_SHA256_SIZE);
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
bool aethernet_signal_kdf_rk(const uint8_t *root_key,
                          const uint8_t *dh_output,
                          uint8_t *out_new_root_key,
                          uint8_t *out_new_chain_key) {
    if (!root_key || !dh_output || !out_new_root_key || !out_new_chain_key) {
        return false;
    }

    static const char info[] = "aether-ratchet-rk-v1";
    static const size_t info_len = sizeof(info) - 1;

    uint8_t derived[64];
    bool ok = aethernet_hkdf_sha256(
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
void aethernet_zeroize(void *mem, size_t len) {
    if (!mem) return;
    ensure_libsodium_initialized();
    sodium_memzero(mem, len);
}

/**
 * Generate random bytes.
 */
bool aethernet_random_bytes(uint8_t *out, size_t len) {
    if (!out) return false;

    ensure_libsodium_initialized();

    randombytes_buf(out, len);
    return true;
}

/**
 * Generate a fresh X25519 keypair.
 */
bool aethernet_x25519_generate_keypair(uint8_t *out_private,
                                    uint8_t *out_public) {
    if (!out_private || !out_public) return false;

    ensure_libsodium_initialized();

    // Random 32-byte private key. libsodium clamps internally on use.
    randombytes_buf(out_private, AETHERNET_X25519_PRIVATE_KEY_SIZE);

    // Public key = private * Basepoint.
    if (crypto_scalarmult_curve25519_base(out_public, out_private) != 0) {
        sodium_memzero(out_private, AETHERNET_X25519_PRIVATE_KEY_SIZE);
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
bool aethernet_x25519_agree(const uint8_t *local_private,
                         const uint8_t *remote_public,
                         uint8_t *out_shared) {
    if (!local_private || !remote_public || !out_shared) return false;

    ensure_libsodium_initialized();

    if (crypto_scalarmult_curve25519(out_shared, local_private, remote_public) != 0) {
        sodium_memzero(out_shared, AETHERNET_X25519_SHARED_SECRET_SIZE);
        return false;
    }
    return true;
}

/**
 * X25519 base-point scalar multiplication: pub = priv * Basepoint.
 */
bool aethernet_x25519_derive_public(const uint8_t *private_key,
                                 uint8_t *out_public) {
    if (!private_key || !out_public) return false;

    ensure_libsodium_initialized();

    if (crypto_scalarmult_curve25519_base(out_public, private_key) != 0) {
        sodium_memzero(out_public, AETHERNET_X25519_PUBLIC_KEY_SIZE);
        return false;
    }
    return true;
}

/* ─────────────────────────────────────────────────────────────────────────
 * Nonce deduplication store
 * ──────────────────────────────────────────────────────────────────────── */

/* Maximum number of (source, nonce) pairs tracked simultaneously. */
#define AETHERNET_NONCE_STORE_MAX_ENTRIES 4096

/* Maximum nonce size accepted (bytes). */
#define AETHERNET_NONCE_STORE_MAX_NONCE_LEN 64

/* Maximum source UHID length (bytes). */
#define AETHERNET_NONCE_STORE_MAX_SOURCE_LEN 64

/* Composite key: "source:hex(nonce)" — max length:
 *   source (63) + ':' (1) + hex nonce (128) + NUL (1) = 193 bytes */
#define AETHERNET_NONCE_STORE_KEY_LEN 193

typedef struct {
    char    key[AETHERNET_NONCE_STORE_KEY_LEN]; /* "source:hexnonce\0"     */
    int64_t expires_at;                       /* wall-clock seconds      */
} aethernet_nonce_entry_t;

struct aethernet_nonce_store {
    aethernet_nonce_entry_t entries[AETHERNET_NONCE_STORE_MAX_ENTRIES];
    int                  count;
};

aethernet_nonce_store_t *aethernet_nonce_store_new(void) {
    aethernet_nonce_store_t *s =
        (aethernet_nonce_store_t *)calloc(1, sizeof(aethernet_nonce_store_t));
    return s; /* NULL on allocation failure */
}

void aethernet_nonce_store_free(aethernet_nonce_store_t *store) {
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
static void nonce_store_prune(aethernet_nonce_store_t *store, int64_t now) {
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
        memset(&store->entries[i], 0, sizeof(aethernet_nonce_entry_t));
    }
    store->count = new_count;
}

bool aethernet_nonce_store_check_and_record(aethernet_nonce_store_t *store,
                                          const char *source_uhid,
                                          const uint8_t *nonce,
                                          size_t nonce_len,
                                          int ttl_seconds) {
    if (!store || !source_uhid || !nonce || nonce_len == 0) return false;
    if (nonce_len > AETHERNET_NONCE_STORE_MAX_NONCE_LEN) return false;
    if (strlen(source_uhid) >= AETHERNET_NONCE_STORE_MAX_SOURCE_LEN) return false;

    char key[AETHERNET_NONCE_STORE_KEY_LEN];
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
    if (store->count >= AETHERNET_NONCE_STORE_MAX_ENTRIES) {
        /* Evict the first (oldest) slot by shifting left. */
        memmove(&store->entries[0], &store->entries[1],
                (AETHERNET_NONCE_STORE_MAX_ENTRIES - 1) * sizeof(aethernet_nonce_entry_t));
        store->count = AETHERNET_NONCE_STORE_MAX_ENTRIES - 1;
    }

    aethernet_nonce_entry_t *e = &store->entries[store->count];
    memcpy(e->key, key, strlen(key) + 1);
    e->expires_at = now + (int64_t)ttl_seconds;
    store->count++;

    return true;
}

/* ─────────────────────────────────────────────────────────────────────────
 * PacketSigning service
 * ──────────────────────────────────────────────────────────────────────── */

void aethernet_packet_signing_init(AetherNetPacketSigningService *svc,
                                aethernet_nonce_store_t *nonce_store) {
    if (!svc) return;
    svc->nonce_store = nonce_store;
    svc->reputation  = NULL;
}

void aethernet_packet_signing_set_reputation(AetherNetPacketSigningService *svc,
                                           AetherNetNodeReputationService *rep) {
    if (!svc) return;
    svc->reputation = rep;
}

bool aethernet_packet_signing_verify(AetherNetPacketSigningService *svc,
                                   const aethernet_mesh_packet_t *packet,
                                   const uint8_t *sender_public_key,
                                   int ttl_seconds) {
    if (!svc || !packet || !sender_public_key) return false;

    const char *source_uhid = packet->source_uhid ? packet->source_uhid : "";

    /* 1. Nonce replay check ─────────────────────────────────────────────── */
    if (svc->nonce_store != NULL) {
        bool fresh = aethernet_nonce_store_check_and_record(
            svc->nonce_store,
            source_uhid,
            packet->packet_nonce,
            AETHERNET_PACKET_NONCE_SIZE,
            ttl_seconds);

        if (!fresh) {
            /* Replay detected — fire reputation hook if wired. */
            if (svc->reputation != NULL) {
                aethernet_reputation_record_replay(svc->reputation, source_uhid);
            }
            return false;
        }
    }

    /* 2. Build signable data ────────────────────────────────────────────── */
    size_t sig_data_len = 0;
    uint8_t *sig_data = aethernet_packet_get_signable_data(packet, &sig_data_len);
    if (!sig_data) return false;

    /* 3. Verify Ed25519 signature ────────────────────────────────────────── */
    bool valid = aethernet_ed25519_verify(
        sender_public_key,
        sig_data,
        sig_data_len,
        packet->signature);

    free(sig_data);

    if (!valid) {
        /* Signature failure — fire reputation hook if wired. */
        if (svc->reputation != NULL) {
            aethernet_reputation_record_sig_failure(svc->reputation, source_uhid);
        }
        return false;
    }

    return true;
}
