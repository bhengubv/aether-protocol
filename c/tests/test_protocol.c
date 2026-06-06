// SPDX-License-Identifier: MIT
// Unit Tests for Aether Protocol

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

#include "aethernet/protocol.h"
#include "aethernet/security.h"

/**
 * Test: Packet Creation
 */
static void test_packet_creation(void) {
    printf("TEST: Packet creation...");

    aethernet_mesh_packet_t *packet = aethernet_packet_new();
    assert(packet != NULL);
    assert(packet->protocol_version == AETHERNET_PROTOCOL_VERSION_CURRENT);
    assert(packet->ttl == AETHERNET_DEFAULT_TTL);
    assert(packet->priority == 0);
    assert(packet->payload == NULL);
    assert(packet->signature == NULL);

    aethernet_packet_free(packet);
    printf(" OK\n");
}

/**
 * Test: Packet Cloning
 */
static void test_packet_clone(void) {
    printf("TEST: Packet cloning...");

    aethernet_mesh_packet_t *packet = aethernet_packet_new();
    assert(aethernet_packet_set_source_uhid(packet, "test-node"));
    assert(aethernet_packet_set_destination_uhid(packet, "dest-node"));
    assert(aethernet_packet_set_payload(packet, (const uint8_t *)"test", 4));

    aethernet_mesh_packet_t *clone = aethernet_packet_clone(packet);
    assert(clone != NULL);
    assert(strcmp(clone->source_uhid, "test-node") == 0);
    assert(strcmp(clone->destination_uhid, "dest-node") == 0);
    assert(clone->payload_len == 4);
    assert(memcmp(clone->payload, "test", 4) == 0);

    aethernet_packet_free(packet);
    aethernet_packet_free(clone);
    printf(" OK\n");
}

/**
 * Test: Serialization and Deserialization
 */
static void test_serialize_deserialize(void) {
    printf("TEST: Serialize/deserialize round-trip...");

    aethernet_mesh_packet_t *packet = aethernet_packet_new();
    assert(aethernet_packet_set_source_uhid(packet, "alice"));
    assert(aethernet_packet_set_destination_uhid(packet, "bob"));
    assert(aethernet_packet_set_payload(packet, (const uint8_t *)"Hello mesh", 10));
    packet->priority = 42;

    // Serialize
    size_t est_size = aethernet_packet_estimate_size(packet);
    uint8_t *buffer = (uint8_t *)malloc(est_size + 256);
    int serialized_len = aethernet_packet_serialize(packet, buffer, est_size + 256);
    assert(serialized_len > 0);

    // Deserialize
    aethernet_mesh_packet_t *deserialized = aethernet_packet_deserialize(buffer, serialized_len);
    assert(deserialized != NULL);
    assert(strcmp(deserialized->source_uhid, "alice") == 0);
    assert(strcmp(deserialized->destination_uhid, "bob") == 0);
    assert(deserialized->payload_len == 10);
    assert(memcmp(deserialized->payload, "Hello mesh", 10) == 0);
    assert(deserialized->priority == 42);

    free(buffer);
    aethernet_packet_free(packet);
    aethernet_packet_free(deserialized);
    printf(" OK\n");
}

/**
 * Test: Ed25519 Key Generation and Signing
 */
static void test_ed25519_sign_verify(void) {
    printf("TEST: Ed25519 sign/verify...");

    uint8_t private_key[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    uint8_t public_key[AETHERNET_ED25519_PUBLIC_KEY_SIZE];

    assert(aethernet_ed25519_generate_keypair(private_key, public_key));

    const char *message = "Test message for signing";
    uint8_t signature[AETHERNET_ED25519_SIGNATURE_SIZE];

    assert(aethernet_ed25519_sign(private_key, (const uint8_t *)message, strlen(message), signature));
    assert(aethernet_ed25519_verify(public_key, (const uint8_t *)message, strlen(message), signature));

    // Tamper with signature should fail
    signature[0] ^= 0xFF;
    assert(!aethernet_ed25519_verify(public_key, (const uint8_t *)message, strlen(message), signature));

    printf(" OK\n");
}

/**
 * Test: AES-256-GCM Encryption/Decryption
 */
static void test_aes_gcm(void) {
    printf("TEST: AES-256-GCM encrypt/decrypt...");

    uint8_t key[32];
    assert(aethernet_random_bytes(key, 32));

    const char *plaintext = "Secret message";
    size_t plaintext_len = strlen(plaintext);

    uint8_t ciphertext[128];
    uint8_t tag[AETHERNET_AES_GCM_TAG_SIZE];
    uint8_t nonce[AETHERNET_AES_GCM_NONCE_SIZE];

    assert(aethernet_aes256_gcm_encrypt((const uint8_t *)plaintext,
                                    plaintext_len,
                                    key,
                                    NULL,  // Generate nonce
                                    NULL,
                                    0,
                                    ciphertext,
                                    tag,
                                    nonce));

    uint8_t decrypted[128];
    assert(aethernet_aes256_gcm_decrypt(ciphertext,
                                    plaintext_len,
                                    key,
                                    nonce,
                                    tag,
                                    NULL,
                                    0,
                                    decrypted));

    assert(memcmp(plaintext, decrypted, plaintext_len) == 0);

    // Tamper with ciphertext should fail
    ciphertext[0] ^= 0xFF;
    assert(!aethernet_aes256_gcm_decrypt(ciphertext,
                                     plaintext_len,
                                     key,
                                     nonce,
                                     tag,
                                     NULL,
                                     0,
                                     decrypted));

    printf(" OK\n");
}

/**
 * Test: HMAC-SHA256
 */
static void test_hmac_sha256(void) {
    printf("TEST: HMAC-SHA256...");

    uint8_t key[32];
    assert(aethernet_random_bytes(key, 32));

    const char *message = "Message to authenticate";
    uint8_t hash[AETHERNET_HMAC_SHA256_SIZE];

    assert(aethernet_hmac_sha256(key, 32, (const uint8_t *)message, strlen(message), hash));

    // Computing again should give same result
    uint8_t hash2[AETHERNET_HMAC_SHA256_SIZE];
    assert(aethernet_hmac_sha256(key, 32, (const uint8_t *)message, strlen(message), hash2));
    assert(memcmp(hash, hash2, AETHERNET_HMAC_SHA256_SIZE) == 0);

    printf(" OK\n");
}

/**
 * Test: HKDF-SHA256
 */
static void test_hkdf_sha256(void) {
    printf("TEST: HKDF-SHA256...");

    uint8_t ikm[32];
    assert(aethernet_random_bytes(ikm, 32));

    uint8_t key1[32];
    uint8_t key2[32];
    const char *info = "test-info";

    assert(aethernet_hkdf_sha256(NULL, 0, ikm, 32, (const uint8_t *)info, strlen(info), 32, key1));
    assert(aethernet_hkdf_sha256(NULL, 0, ikm, 32, (const uint8_t *)info, strlen(info), 32, key2));

    // Same inputs should give same output
    assert(memcmp(key1, key2, 32) == 0);

    printf(" OK\n");
}

/**
 * Test: Packet Expiry
 */
static void test_packet_expiry(void) {
    printf("TEST: Packet expiry...");

    aethernet_mesh_packet_t *packet = aethernet_packet_new();
    assert(packet != NULL);

    // Fresh packet should not be expired
    assert(!aethernet_packet_is_expired(packet, 300));

    // Set timestamp to far past
    packet->timestamp_ms = 0;
    assert(aethernet_packet_is_expired(packet, 1));

    aethernet_packet_free(packet);
    printf(" OK\n");
}

/**
 * Test: TTL and Forwarding
 */
static void test_packet_ttl(void) {
    printf("TEST: Packet TTL and forwarding...");

    aethernet_mesh_packet_t *packet = aethernet_packet_new();
    assert(packet->ttl == AETHERNET_DEFAULT_TTL);
    assert(aethernet_packet_can_forward(packet));

    // Set TTL to 0
    packet->ttl = 0;
    assert(!aethernet_packet_can_forward(packet));

    // Set TTL to 1
    packet->ttl = 1;
    assert(aethernet_packet_can_forward(packet));

    aethernet_packet_free(packet);
    printf(" OK\n");
}

/**
 * Test: Signable Data Construction
 */
static void test_signable_data(void) {
    printf("TEST: Signable data construction...");

    aethernet_mesh_packet_t *packet = aethernet_packet_new();
    assert(aethernet_packet_set_source_uhid(packet, "alice"));
    assert(aethernet_packet_set_destination_uhid(packet, "bob"));
    assert(aethernet_packet_set_payload(packet, (const uint8_t *)"test", 4));

    size_t signable_len = 0;
    uint8_t *signable = aethernet_packet_get_signable_data(packet, &signable_len);
    assert(signable != NULL);
    assert(signable_len > 0);

    // Signable data should be deterministic
    size_t signable_len2 = 0;
    uint8_t *signable2 = aethernet_packet_get_signable_data(packet, &signable_len2);
    assert(signable_len == signable_len2);
    assert(memcmp(signable, signable2, signable_len) == 0);

    free(signable);
    free(signable2);
    aethernet_packet_free(packet);
    printf(" OK\n");
}

/**
 * Main test runner
 */
int main(void) {
    printf("=== Aether Protocol C Implementation Tests ===\n\n");

    test_packet_creation();
    test_packet_clone();
    test_serialize_deserialize();
    test_ed25519_sign_verify();
    test_aes_gcm();
    test_hmac_sha256();
    test_hkdf_sha256();
    test_packet_expiry();
    test_packet_ttl();
    test_signable_data();

    printf("\n=== All tests passed! ===\n");
    return 0;
}
