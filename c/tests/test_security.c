// SPDX-License-Identifier: MIT
// Unit tests for security.c — Ed25519, AES-256-GCM, HMAC-SHA256, SHA-256,
// BLAKE3, HKDF-SHA256, Signal KDF_RK, X25519, zeroize, random_bytes.

#define _POSIX_C_SOURCE 200809L

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "aether/constants.h"
#include "aether/protocol.h"
#include "aether/security.h"
#include "aether_reputation.h"

// ── Test runner ───────────────────────────────────────────────

#define RUN(name) do { \
    printf("TEST: " #name "..."); \
    name(); \
    printf(" OK\n"); \
    tests_run++; \
} while (0)
static int tests_run = 0;

// ── Helpers ───────────────────────────────────────────────────

static int all_zero(const uint8_t *buf, size_t len) {
    for (size_t i = 0; i < len; i++) if (buf[i]) return 0;
    return 1;
}

// ── Ed25519 ───────────────────────────────────────────────────

static void ed25519_generate_keypair_produces_nonzero_keys(void) {
    uint8_t priv[AETHER_ED25519_PRIVATE_KEY_SIZE];
    uint8_t pub[AETHER_ED25519_PUBLIC_KEY_SIZE];
    bool ok = aether_ed25519_generate_keypair(priv, pub);
    assert(ok);
    assert(!all_zero(priv, sizeof(priv)));
    assert(!all_zero(pub, sizeof(pub)));
}

static void ed25519_sign_and_verify_roundtrip(void) {
    uint8_t priv[AETHER_ED25519_PRIVATE_KEY_SIZE];
    uint8_t pub[AETHER_ED25519_PUBLIC_KEY_SIZE];
    aether_ed25519_generate_keypair(priv, pub);

    const uint8_t msg[] = { 'h', 'e', 'l', 'l', 'o' };
    uint8_t sig[AETHER_ED25519_SIGNATURE_SIZE];

    bool signed_ok = aether_ed25519_sign(priv, msg, sizeof(msg), sig);
    assert(signed_ok);
    assert(!all_zero(sig, sizeof(sig)));

    bool verified = aether_ed25519_verify(pub, msg, sizeof(msg), sig);
    assert(verified);
}

static void ed25519_verify_tampered_signature_returns_false(void) {
    uint8_t priv[AETHER_ED25519_PRIVATE_KEY_SIZE];
    uint8_t pub[AETHER_ED25519_PUBLIC_KEY_SIZE];
    aether_ed25519_generate_keypair(priv, pub);

    const uint8_t msg[] = { 't', 'e', 's', 't' };
    uint8_t sig[AETHER_ED25519_SIGNATURE_SIZE];
    aether_ed25519_sign(priv, msg, sizeof(msg), sig);

    // Flip a byte in the signature
    sig[0] ^= 0xFF;
    bool bad = aether_ed25519_verify(pub, msg, sizeof(msg), sig);
    assert(!bad);
}

static void ed25519_verify_wrong_key_returns_false(void) {
    uint8_t priv[AETHER_ED25519_PRIVATE_KEY_SIZE];
    uint8_t pub[AETHER_ED25519_PUBLIC_KEY_SIZE];
    aether_ed25519_generate_keypair(priv, pub);

    uint8_t priv2[AETHER_ED25519_PRIVATE_KEY_SIZE];
    uint8_t pub2[AETHER_ED25519_PUBLIC_KEY_SIZE];
    aether_ed25519_generate_keypair(priv2, pub2);

    const uint8_t msg[] = { 'a', 'b', 'c' };
    uint8_t sig[AETHER_ED25519_SIGNATURE_SIZE];
    aether_ed25519_sign(priv, msg, sizeof(msg), sig);

    // Verify with the wrong public key
    bool bad = aether_ed25519_verify(pub2, msg, sizeof(msg), sig);
    assert(!bad);
}

// ── AES-256-GCM ───────────────────────────────────────────────

static void aes_gcm_encrypt_decrypt_roundtrip(void) {
    uint8_t key[32];
    aether_random_bytes(key, sizeof(key));

    const uint8_t plain[] = { 'A', 'e', 't', 'h', 'e', 'r', ' ', 'P', 'r', 'o', 't', 'o' };
    uint8_t cipher[sizeof(plain)];
    uint8_t tag[AETHER_AES_GCM_TAG_SIZE];
    uint8_t nonce[AETHER_AES_GCM_NONCE_SIZE];

    bool enc_ok = aether_aes256_gcm_encrypt(
        plain, sizeof(plain), key, NULL, NULL, 0, cipher, tag, nonce);
    assert(enc_ok);
    // Ciphertext should differ from plaintext
    assert(memcmp(plain, cipher, sizeof(plain)) != 0);

    uint8_t recovered[sizeof(plain)];
    bool dec_ok = aether_aes256_gcm_decrypt(
        cipher, sizeof(cipher), key, nonce, tag, NULL, 0, recovered);
    assert(dec_ok);
    assert(memcmp(plain, recovered, sizeof(plain)) == 0);
}

static void aes_gcm_decrypt_tampered_tag_returns_false(void) {
    uint8_t key[32];
    aether_random_bytes(key, sizeof(key));

    const uint8_t plain[] = { 1, 2, 3, 4 };
    uint8_t cipher[sizeof(plain)];
    uint8_t tag[AETHER_AES_GCM_TAG_SIZE];
    uint8_t nonce[AETHER_AES_GCM_NONCE_SIZE];

    aether_aes256_gcm_encrypt(plain, sizeof(plain), key, NULL, NULL, 0, cipher, tag, nonce);

    // Flip a tag byte
    tag[0] ^= 0x01;
    uint8_t out[sizeof(plain)];
    bool bad = aether_aes256_gcm_decrypt(
        cipher, sizeof(cipher), key, nonce, tag, NULL, 0, out);
    assert(!bad);
}

static void aes_gcm_with_aad_roundtrip(void) {
    uint8_t key[32];
    aether_random_bytes(key, sizeof(key));

    const uint8_t plain[] = { 'p', 'a', 'y', 'l', 'o', 'a', 'd' };
    const uint8_t aad[]   = { 'h', 'e', 'a', 'd', 'e', 'r' };
    uint8_t cipher[sizeof(plain)];
    uint8_t tag[AETHER_AES_GCM_TAG_SIZE];
    uint8_t nonce[AETHER_AES_GCM_NONCE_SIZE];

    bool enc_ok = aether_aes256_gcm_encrypt(
        plain, sizeof(plain), key, NULL, aad, sizeof(aad), cipher, tag, nonce);
    assert(enc_ok);

    uint8_t recovered[sizeof(plain)];
    bool dec_ok = aether_aes256_gcm_decrypt(
        cipher, sizeof(cipher), key, nonce, tag, aad, sizeof(aad), recovered);
    assert(dec_ok);
    assert(memcmp(plain, recovered, sizeof(plain)) == 0);
}

// ── HMAC-SHA256 ───────────────────────────────────────────────

static void hmac_sha256_is_deterministic(void) {
    const uint8_t key[]  = { 'k', 'e', 'y' };
    const uint8_t data[] = { 'd', 'a', 't', 'a' };
    uint8_t h1[AETHER_HMAC_SHA256_SIZE];
    uint8_t h2[AETHER_HMAC_SHA256_SIZE];

    bool ok1 = aether_hmac_sha256(key, sizeof(key), data, sizeof(data), h1);
    bool ok2 = aether_hmac_sha256(key, sizeof(key), data, sizeof(data), h2);
    assert(ok1 && ok2);
    assert(memcmp(h1, h2, sizeof(h1)) == 0);
    assert(!all_zero(h1, sizeof(h1)));
}

static void hmac_sha256_different_keys_produce_different_hashes(void) {
    const uint8_t key1[] = { 0x01 };
    const uint8_t key2[] = { 0x02 };
    const uint8_t data[] = { 'm', 's', 'g' };
    uint8_t h1[AETHER_HMAC_SHA256_SIZE];
    uint8_t h2[AETHER_HMAC_SHA256_SIZE];

    aether_hmac_sha256(key1, sizeof(key1), data, sizeof(data), h1);
    aether_hmac_sha256(key2, sizeof(key2), data, sizeof(data), h2);
    assert(memcmp(h1, h2, sizeof(h1)) != 0);
}

// ── SHA-256 ───────────────────────────────────────────────────

static void sha256_is_deterministic(void) {
    const uint8_t data[] = { 'a', 'b', 'c' };
    uint8_t h1[AETHER_SHA256_SIZE];
    uint8_t h2[AETHER_SHA256_SIZE];

    bool ok1 = aether_sha256(data, sizeof(data), h1);
    bool ok2 = aether_sha256(data, sizeof(data), h2);
    assert(ok1 && ok2);
    assert(memcmp(h1, h2, sizeof(h1)) == 0);
    assert(!all_zero(h1, sizeof(h1)));
}

static void sha256_different_inputs_produce_different_hashes(void) {
    const uint8_t a[] = { 'a' };
    const uint8_t b[] = { 'b' };
    uint8_t ha[AETHER_SHA256_SIZE], hb[AETHER_SHA256_SIZE];
    aether_sha256(a, sizeof(a), ha);
    aether_sha256(b, sizeof(b), hb);
    assert(memcmp(ha, hb, sizeof(ha)) != 0);
}

// ── BLAKE3 ────────────────────────────────────────────────────

static void blake3_is_deterministic(void) {
    const uint8_t data[] = { 'a', 'e', 't', 'h', 'e', 'r' };
    uint8_t h1[AETHER_BLAKE3_SIZE];
    uint8_t h2[AETHER_BLAKE3_SIZE];

    bool ok1 = aether_blake3(data, sizeof(data), h1);
    bool ok2 = aether_blake3(data, sizeof(data), h2);
    assert(ok1 && ok2);
    assert(memcmp(h1, h2, sizeof(h1)) == 0);
    assert(!all_zero(h1, sizeof(h1)));
}

static void blake3_and_sha256_produce_different_outputs(void) {
    // BLAKE3 and SHA-256 are different algorithms — same input must differ.
    const uint8_t data[] = { 1, 2, 3, 4, 5 };
    uint8_t hb3[AETHER_BLAKE3_SIZE];
    uint8_t hsha[AETHER_SHA256_SIZE];
    aether_blake3(data, sizeof(data), hb3);
    aether_sha256(data, sizeof(data), hsha);
    assert(memcmp(hb3, hsha, AETHER_BLAKE3_SIZE) != 0);
}

// ── HKDF-SHA256 ───────────────────────────────────────────────

static void hkdf_produces_nonzero_output(void) {
    const uint8_t ikm[]  = { 'i', 'k', 'm' };
    const uint8_t salt[] = { 's', 'a', 'l', 't' };
    const uint8_t info[] = { 'i', 'n', 'f', 'o' };
    uint8_t okm[32];

    bool ok = aether_hkdf_sha256(
        salt, sizeof(salt), ikm, sizeof(ikm), info, sizeof(info), sizeof(okm), okm);
    assert(ok);
    assert(!all_zero(okm, sizeof(okm)));
}

static void hkdf_is_deterministic(void) {
    const uint8_t ikm[]  = { 0x01, 0x02, 0x03 };
    const uint8_t salt[] = { 0xAA };
    const uint8_t info[] = { 'v', '1' };
    uint8_t okm1[32], okm2[32];

    aether_hkdf_sha256(salt, sizeof(salt), ikm, sizeof(ikm), info, sizeof(info), sizeof(okm1), okm1);
    aether_hkdf_sha256(salt, sizeof(salt), ikm, sizeof(ikm), info, sizeof(info), sizeof(okm2), okm2);
    assert(memcmp(okm1, okm2, sizeof(okm1)) == 0);
}

// ── Signal KDF_RK ─────────────────────────────────────────────

static void signal_kdf_rk_produces_nonzero_keys(void) {
    uint8_t root_key[32];
    uint8_t dh_output[32];
    aether_random_bytes(root_key, sizeof(root_key));
    aether_random_bytes(dh_output, sizeof(dh_output));

    uint8_t new_rk[32];
    uint8_t new_ck[32];
    bool ok = aether_signal_kdf_rk(root_key, dh_output, new_rk, new_ck);
    assert(ok);
    assert(!all_zero(new_rk, 32));
    assert(!all_zero(new_ck, 32));
    // New root key and chain key must differ
    assert(memcmp(new_rk, new_ck, 32) != 0);
}

static void signal_kdf_rk_is_deterministic(void) {
    uint8_t rk[32] = {0}, dh[32] = {0};
    rk[0] = 0x11; dh[0] = 0x22;

    uint8_t new_rk1[32], new_ck1[32];
    uint8_t new_rk2[32], new_ck2[32];
    aether_signal_kdf_rk(rk, dh, new_rk1, new_ck1);
    aether_signal_kdf_rk(rk, dh, new_rk2, new_ck2);
    assert(memcmp(new_rk1, new_rk2, 32) == 0);
    assert(memcmp(new_ck1, new_ck2, 32) == 0);
}

// ── X25519 ────────────────────────────────────────────────────

static void x25519_generate_keypair_produces_nonzero_keys(void) {
    uint8_t priv[AETHER_X25519_PRIVATE_KEY_SIZE];
    uint8_t pub[AETHER_X25519_PUBLIC_KEY_SIZE];
    bool ok = aether_x25519_generate_keypair(priv, pub);
    assert(ok);
    assert(!all_zero(priv, sizeof(priv)));
    assert(!all_zero(pub, sizeof(pub)));
}

static void x25519_derive_public_matches_generated(void) {
    uint8_t priv[AETHER_X25519_PRIVATE_KEY_SIZE];
    uint8_t pub_generated[AETHER_X25519_PUBLIC_KEY_SIZE];
    uint8_t pub_derived[AETHER_X25519_PUBLIC_KEY_SIZE];

    aether_x25519_generate_keypair(priv, pub_generated);
    bool ok = aether_x25519_derive_public(priv, pub_derived);
    assert(ok);
    assert(memcmp(pub_generated, pub_derived, AETHER_X25519_PUBLIC_KEY_SIZE) == 0);
}

static void x25519_ecdh_is_commutative(void) {
    // Alice and Bob should arrive at the same shared secret.
    uint8_t alice_priv[AETHER_X25519_PRIVATE_KEY_SIZE];
    uint8_t alice_pub[AETHER_X25519_PUBLIC_KEY_SIZE];
    uint8_t bob_priv[AETHER_X25519_PRIVATE_KEY_SIZE];
    uint8_t bob_pub[AETHER_X25519_PUBLIC_KEY_SIZE];

    aether_x25519_generate_keypair(alice_priv, alice_pub);
    aether_x25519_generate_keypair(bob_priv, bob_pub);

    uint8_t alice_shared[AETHER_X25519_SHARED_SECRET_SIZE];
    uint8_t bob_shared[AETHER_X25519_SHARED_SECRET_SIZE];

    bool ok_a = aether_x25519_agree(alice_priv, bob_pub, alice_shared);
    bool ok_b = aether_x25519_agree(bob_priv, alice_pub, bob_shared);
    assert(ok_a && ok_b);
    assert(memcmp(alice_shared, bob_shared, AETHER_X25519_SHARED_SECRET_SIZE) == 0);
    assert(!all_zero(alice_shared, AETHER_X25519_SHARED_SECRET_SIZE));
}

static void x25519_agree_rejects_low_order_point(void) {
    // RFC 7748 low-order point triggers the all-zero output check.
    // The all-zero 32-byte string is a well-known low-order X25519 public key
    // that always produces an all-zero shared secret regardless of the private key.
    uint8_t priv[AETHER_X25519_PRIVATE_KEY_SIZE];
    uint8_t pub_discard[AETHER_X25519_PUBLIC_KEY_SIZE];
    aether_x25519_generate_keypair(priv, pub_discard);  // need a valid private key

    uint8_t pub_lo[AETHER_X25519_PUBLIC_KEY_SIZE];
    memset(pub_lo, 0, sizeof(pub_lo));  // all-zero = low-order point

    uint8_t shared[AETHER_X25519_SHARED_SECRET_SIZE];
    bool ok = aether_x25519_agree(priv, pub_lo, shared);
    // Must return false (all-zero result = small-subgroup attack indicator)
    assert(!ok);
}

// ── Zeroize / random ─────────────────────────────────────────

static void zeroize_clears_buffer(void) {
    uint8_t buf[64];
    aether_random_bytes(buf, sizeof(buf));
    // Likely non-zero after random fill
    aether_zeroize(buf, sizeof(buf));
    assert(all_zero(buf, sizeof(buf)));
}

static void random_bytes_produces_nonzero_output(void) {
    // Extremely unlikely to be all-zero for 32 random bytes
    uint8_t buf[32];
    bool ok = aether_random_bytes(buf, sizeof(buf));
    assert(ok);
    assert(!all_zero(buf, sizeof(buf)));
}

static void random_bytes_two_calls_differ(void) {
    // Two independent calls should produce different outputs
    uint8_t a[32], b[32];
    aether_random_bytes(a, sizeof(a));
    aether_random_bytes(b, sizeof(b));
    assert(memcmp(a, b, sizeof(a)) != 0);
}

// ── Nonce store ───────────────────────────────────────────────

static void nonce_store_new_returns_non_null(void) {
    aether_nonce_store_t *s = aether_nonce_store_new();
    assert(s != NULL);
    aether_nonce_store_free(s);
}

static void nonce_store_fresh_nonce_returns_true(void) {
    aether_nonce_store_t *s = aether_nonce_store_new();
    uint8_t nonce[8] = {1, 2, 3, 4, 5, 6, 7, 8};
    bool ok = aether_nonce_store_check_and_record(s, "alice", nonce, 8, 300);
    assert(ok);
    aether_nonce_store_free(s);
}

static void nonce_store_same_source_same_nonce_is_replay(void) {
    aether_nonce_store_t *s = aether_nonce_store_new();
    uint8_t nonce[8] = {0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44};
    bool first  = aether_nonce_store_check_and_record(s, "bob", nonce, 8, 300);
    bool second = aether_nonce_store_check_and_record(s, "bob", nonce, 8, 300);
    assert(first  == true);   /* fresh */
    assert(second == false);  /* replay */
    aether_nonce_store_free(s);
}

static void nonce_store_different_source_same_nonce_bytes_not_replay(void) {
    /* Two senders may use the same nonce bytes — the key is (source, nonce). */
    aether_nonce_store_t *s = aether_nonce_store_new();
    uint8_t nonce[8] = {0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03};
    bool alice = aether_nonce_store_check_and_record(s, "alice", nonce, 8, 300);
    bool bob   = aether_nonce_store_check_and_record(s, "bob",   nonce, 8, 300);
    assert(alice == true);
    assert(bob   == true); /* different source — not a replay */
    aether_nonce_store_free(s);
}

static void nonce_store_same_source_different_nonce_not_replay(void) {
    aether_nonce_store_t *s = aether_nonce_store_new();
    uint8_t n1[8] = {1, 1, 1, 1, 1, 1, 1, 1};
    uint8_t n2[8] = {2, 2, 2, 2, 2, 2, 2, 2};
    bool first  = aether_nonce_store_check_and_record(s, "dave", n1, 8, 300);
    bool second = aether_nonce_store_check_and_record(s, "dave", n2, 8, 300);
    assert(first  == true);
    assert(second == true); /* different nonce — not a replay */
    aether_nonce_store_free(s);
}

static void nonce_store_null_params_return_false(void) {
    aether_nonce_store_t *s = aether_nonce_store_new();
    uint8_t nonce[8] = {0};
    assert(aether_nonce_store_check_and_record(NULL, "alice", nonce, 8, 300) == false);
    assert(aether_nonce_store_check_and_record(s, NULL, nonce, 8, 300) == false);
    assert(aether_nonce_store_check_and_record(s, "alice", NULL, 8, 300) == false);
    assert(aether_nonce_store_check_and_record(s, "alice", nonce, 0, 300) == false);
    aether_nonce_store_free(s);
}

static void nonce_store_expired_entry_treated_as_fresh(void) {
    /* A (source, nonce) pair recorded with a TTL of 0 should already be
     * pruned on the next call, making a second use of the same bytes fresh. */
    aether_nonce_store_t *s = aether_nonce_store_new();
    uint8_t nonce[8] = {0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x01};
    /* Record with TTL = 0 (expires immediately). */
    aether_nonce_store_check_and_record(s, "carol", nonce, 8, 0);
    /* With TTL = 0 the entry is in the past on the very next call.
     * The second check should treat it as fresh (pruned). */
    bool second = aether_nonce_store_check_and_record(s, "carol", nonce, 8, 300);
    assert(second == true);
    aether_nonce_store_free(s);
}

// ── PacketSigning reputation hooks ───────────────────────────

/* Build a minimal signed packet using real Ed25519 keys. */
static aether_mesh_packet_t *make_signed_packet(const uint8_t *priv_key,
                                                 const char *source_uhid,
                                                 const uint8_t *nonce_override) {
    aether_mesh_packet_t *pkt = aether_packet_new();
    assert(pkt != NULL);

    aether_packet_set_source_uhid(pkt, source_uhid);
    aether_packet_set_destination_uhid(pkt, "dst-node");

    /* Use the provided nonce or fill with a fixed pattern. */
    if (nonce_override) {
        memcpy(pkt->packet_nonce, nonce_override, AETHER_PACKET_NONCE_SIZE);
    } else {
        for (int i = 0; i < AETHER_PACKET_NONCE_SIZE; i++) {
            pkt->packet_nonce[i] = (uint8_t)(0xA0 + i);
        }
    }

    pkt->timestamp_ms = 1700000000000LL; /* fixed — not freshness-checked by verify */
    pkt->type         = AETHER_PACKET_TYPE_DATA;
    pkt->ttl          = 5;
    pkt->priority     = 1;
    pkt->protocol_version = 2;

    /* Sign */
    size_t sig_data_len = 0;
    uint8_t *sig_data = aether_packet_get_signable_data(pkt, &sig_data_len);
    assert(sig_data != NULL);

    uint8_t sig[AETHER_ED25519_SIGNATURE_SIZE];
    bool ok = aether_ed25519_sign(priv_key, sig_data, sig_data_len, sig);
    free(sig_data);
    assert(ok);

    aether_packet_set_signature(pkt, sig, AETHER_ED25519_SIGNATURE_SIZE);
    return pkt;
}

/*
 * Test 1: Replay attempt fires the reputation hook.
 *
 * Send the same (source, nonce) twice through aether_packet_signing_verify().
 * The second call should return false AND decrement the reputation score.
 */
static void packet_signing_replay_fires_reputation_hook(void) {
    uint8_t priv[AETHER_ED25519_PRIVATE_KEY_SIZE];
    uint8_t pub[AETHER_ED25519_PUBLIC_KEY_SIZE];
    aether_ed25519_generate_keypair(priv, pub);

    aether_nonce_store_t *ns = aether_nonce_store_new();
    assert(ns != NULL);

    AetherPacketSigningService svc;
    aether_packet_signing_init(&svc, ns);

    AetherNodeReputationService rep;
    aether_reputation_init(&rep);
    aether_packet_signing_set_reputation(&svc, &rep);

    /* Initial score for this UHID is 1.0 (unknown → benefit of the doubt). */
    const char *uhid = "replay-sender";
    double score_before = aether_reputation_get_score(&rep, uhid);
    assert(score_before == 1.0);

    aether_mesh_packet_t *pkt = make_signed_packet(priv, uhid, NULL);

    /* First verify: fresh nonce — should succeed. */
    bool first = aether_packet_signing_verify(&svc, pkt, pub, 300);
    assert(first == true);

    /* Second verify: same packet (same nonce) — replay, must fail. */
    bool second = aether_packet_signing_verify(&svc, pkt, pub, 300);
    assert(second == false);

    /* Score must have dropped (replay signal = −0.15). */
    double score_after = aether_reputation_get_score(&rep, uhid);
    assert(score_after < score_before);

    aether_packet_free(pkt);
    aether_nonce_store_free(ns);
}

/*
 * Test 2: A fresh (non-replay) nonce does NOT fire the reputation hook.
 *
 * Two different nonces from the same sender — neither should trigger the
 * replay penalty.
 */
static void packet_signing_fresh_nonce_does_not_fire_hook(void) {
    uint8_t priv[AETHER_ED25519_PRIVATE_KEY_SIZE];
    uint8_t pub[AETHER_ED25519_PUBLIC_KEY_SIZE];
    aether_ed25519_generate_keypair(priv, pub);

    aether_nonce_store_t *ns = aether_nonce_store_new();
    AetherPacketSigningService svc;
    aether_packet_signing_init(&svc, ns);

    AetherNodeReputationService rep;
    aether_reputation_init(&rep);
    aether_packet_signing_set_reputation(&svc, &rep);

    const char *uhid = "fresh-sender";
    double score_before = aether_reputation_get_score(&rep, uhid);

    /* Packet A — nonce 0x01…08 */
    uint8_t nonce_a[AETHER_PACKET_NONCE_SIZE] = {1, 2, 3, 4, 5, 6, 7, 8};
    aether_mesh_packet_t *pkt_a = make_signed_packet(priv, uhid, nonce_a);
    bool ok_a = aether_packet_signing_verify(&svc, pkt_a, pub, 300);
    assert(ok_a == true);

    /* Packet B — nonce 0x11…18 (different) */
    uint8_t nonce_b[AETHER_PACKET_NONCE_SIZE] = {0x11, 0x12, 0x13, 0x14,
                                                   0x15, 0x16, 0x17, 0x18};
    aether_mesh_packet_t *pkt_b = make_signed_packet(priv, uhid, nonce_b);
    bool ok_b = aether_packet_signing_verify(&svc, pkt_b, pub, 300);
    assert(ok_b == true);

    /* Score must be unchanged — no replay, no sig failure. */
    double score_after = aether_reputation_get_score(&rep, uhid);
    assert(score_after == score_before);

    aether_packet_free(pkt_a);
    aether_packet_free(pkt_b);
    aether_nonce_store_free(ns);
}

/*
 * Test 3: Signature failure fires the reputation hook.
 *
 * Sign with one key, verify with a different public key — must fail and
 * decrement the sender's reputation score.
 */
static void packet_signing_sig_failure_fires_reputation_hook(void) {
    /* Signer key pair */
    uint8_t priv[AETHER_ED25519_PRIVATE_KEY_SIZE];
    uint8_t pub[AETHER_ED25519_PUBLIC_KEY_SIZE];
    aether_ed25519_generate_keypair(priv, pub);

    /* Unrelated verifier key (wrong public key) */
    uint8_t priv2[AETHER_ED25519_PRIVATE_KEY_SIZE];
    uint8_t pub2[AETHER_ED25519_PUBLIC_KEY_SIZE];
    aether_ed25519_generate_keypair(priv2, pub2);

    /* No nonce store — we want to reach the signature check unconditionally. */
    AetherPacketSigningService svc;
    aether_packet_signing_init(&svc, NULL);

    AetherNodeReputationService rep;
    aether_reputation_init(&rep);
    aether_packet_signing_set_reputation(&svc, &rep);

    const char *uhid = "bad-sig-sender";
    double score_before = aether_reputation_get_score(&rep, uhid);
    assert(score_before == 1.0);

    aether_mesh_packet_t *pkt = make_signed_packet(priv, uhid, NULL);

    /* Verify with the WRONG public key — signature check must fail. */
    bool ok = aether_packet_signing_verify(&svc, pkt, pub2, 300);
    assert(ok == false);

    /* Score must have dropped (sig-failure signal = −0.20). */
    double score_after = aether_reputation_get_score(&rep, uhid);
    assert(score_after < score_before);

    aether_packet_free(pkt);
}

// ── main ─────────────────────────────────────────────────────

int main(void) {
    printf("Aether Security — Unit Tests\n");
    printf("============================\n");

    RUN(ed25519_generate_keypair_produces_nonzero_keys);
    RUN(ed25519_sign_and_verify_roundtrip);
    RUN(ed25519_verify_tampered_signature_returns_false);
    RUN(ed25519_verify_wrong_key_returns_false);

    RUN(aes_gcm_encrypt_decrypt_roundtrip);
    RUN(aes_gcm_decrypt_tampered_tag_returns_false);
    RUN(aes_gcm_with_aad_roundtrip);

    RUN(hmac_sha256_is_deterministic);
    RUN(hmac_sha256_different_keys_produce_different_hashes);

    RUN(sha256_is_deterministic);
    RUN(sha256_different_inputs_produce_different_hashes);

    RUN(blake3_is_deterministic);
    RUN(blake3_and_sha256_produce_different_outputs);

    RUN(hkdf_produces_nonzero_output);
    RUN(hkdf_is_deterministic);

    RUN(signal_kdf_rk_produces_nonzero_keys);
    RUN(signal_kdf_rk_is_deterministic);

    RUN(x25519_generate_keypair_produces_nonzero_keys);
    RUN(x25519_derive_public_matches_generated);
    RUN(x25519_ecdh_is_commutative);
    RUN(x25519_agree_rejects_low_order_point);

    RUN(zeroize_clears_buffer);
    RUN(random_bytes_produces_nonzero_output);
    RUN(random_bytes_two_calls_differ);

    RUN(nonce_store_new_returns_non_null);
    RUN(nonce_store_fresh_nonce_returns_true);
    RUN(nonce_store_same_source_same_nonce_is_replay);
    RUN(nonce_store_different_source_same_nonce_bytes_not_replay);
    RUN(nonce_store_same_source_different_nonce_not_replay);
    RUN(nonce_store_null_params_return_false);
    RUN(nonce_store_expired_entry_treated_as_fresh);

    RUN(packet_signing_replay_fires_reputation_hook);
    RUN(packet_signing_fresh_nonce_does_not_fire_hook);
    RUN(packet_signing_sig_failure_fires_reputation_hook);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
