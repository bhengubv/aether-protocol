// SPDX-License-Identifier: MIT
// Aether Protocol Demo

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "aethernet/protocol.h"
#include "aethernet/security.h"
#include "aethernet/transport.h"

/**
 * Print hex dump of data.
 */
static void hex_dump(const char *label, const uint8_t *data, size_t len) {
    printf("%s: ", label);
    for (size_t i = 0; i < len && i < 32; i++) {
        printf("%02x", data[i]);
    }
    if (len > 32) printf("...");
    printf(" (%zu bytes)\n", len);
}

/**
 * Main demo.
 */
int main(void) {
    printf("=== Aether Mesh Protocol C Implementation Demo ===\n\n");

    // Demo 1: Key Generation
    printf("--- Demo 1: Ed25519 Key Generation ---\n");
    uint8_t private_key[AETHERNET_ED25519_PRIVATE_KEY_SIZE];
    uint8_t public_key[AETHERNET_ED25519_PUBLIC_KEY_SIZE];

    if (!aethernet_ed25519_generate_keypair(private_key, public_key)) {
        printf("ERROR: Failed to generate keypair\n");
        return 1;
    }

    hex_dump("Private Key", private_key, AETHERNET_ED25519_PRIVATE_KEY_SIZE);
    hex_dump("Public Key", public_key, AETHERNET_ED25519_PUBLIC_KEY_SIZE);

    // Demo 2: Packet Creation and Signing
    printf("\n--- Demo 2: Packet Creation and Signing ---\n");
    aethernet_mesh_packet_t *packet = aethernet_packet_new();
    if (!packet) {
        printf("ERROR: Failed to create packet\n");
        return 1;
    }

    packet->type = AETHERNET_PACKET_TYPE_DATA;
    packet->ttl = 7;
    packet->priority = 0;

    if (!aethernet_packet_set_source_uhid(packet, "node-alice-001")) {
        printf("ERROR: Failed to set source UHID\n");
        aethernet_packet_free(packet);
        return 1;
    }

    if (!aethernet_packet_set_destination_uhid(packet, "node-bob-002")) {
        printf("ERROR: Failed to set destination UHID\n");
        aethernet_packet_free(packet);
        return 1;
    }

    const char *payload_str = "Hello from Aether mesh!";
    if (!aethernet_packet_set_payload(packet, (const uint8_t *)payload_str, strlen(payload_str))) {
        printf("ERROR: Failed to set payload\n");
        aethernet_packet_free(packet);
        return 1;
    }

    printf("Packet created:\n");
    printf("  Type: %d (Data)\n", packet->type);
    printf("  Source: %s\n", packet->source_uhid);
    printf("  Destination: %s\n", packet->destination_uhid);
    printf("  Payload: %.*s\n", (int)packet->payload_len, (char *)packet->payload);
    printf("  TTL: %d\n", packet->ttl);

    // Get signable data and sign
    size_t signable_len = 0;
    uint8_t *signable_data = aethernet_packet_get_signable_data(packet, &signable_len);
    if (!signable_data) {
        printf("ERROR: Failed to get signable data\n");
        aethernet_packet_free(packet);
        return 1;
    }

    hex_dump("Signable Data", signable_data, signable_len);

    uint8_t signature[AETHERNET_ED25519_SIGNATURE_SIZE];
    if (!aethernet_ed25519_sign(private_key, signable_data, signable_len, signature)) {
        printf("ERROR: Failed to sign packet\n");
        free(signable_data);
        aethernet_packet_free(packet);
        return 1;
    }

    hex_dump("Signature", signature, AETHERNET_ED25519_SIGNATURE_SIZE);

    if (!aethernet_packet_set_signature(packet, signature, AETHERNET_ED25519_SIGNATURE_SIZE)) {
        printf("ERROR: Failed to set signature\n");
        free(signable_data);
        aethernet_packet_free(packet);
        return 1;
    }

    // Verify signature
    if (aethernet_ed25519_verify(public_key, signable_data, signable_len, signature)) {
        printf("✓ Signature verification PASSED\n");
    } else {
        printf("✗ Signature verification FAILED\n");
        free(signable_data);
        aethernet_packet_free(packet);
        return 1;
    }

    free(signable_data);

    // Demo 3: Packet Serialization
    printf("\n--- Demo 3: Packet Serialization ---\n");
    size_t estimated_size = aethernet_packet_estimate_size(packet);
    printf("Estimated packet size: %zu bytes\n", estimated_size);

    uint8_t *buffer = (uint8_t *)malloc(estimated_size + 256);  // Add some margin
    if (!buffer) {
        printf("ERROR: Failed to allocate buffer\n");
        aethernet_packet_free(packet);
        return 1;
    }

    int serialized_size = aethernet_packet_serialize(packet, buffer, estimated_size + 256);
    if (serialized_size < 0) {
        printf("ERROR: Failed to serialize packet\n");
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    printf("Serialized packet size: %d bytes\n", serialized_size);
    hex_dump("Serialized Packet", buffer, serialized_size);

    // Demo 4: Packet Deserialization
    printf("\n--- Demo 4: Packet Deserialization ---\n");
    aethernet_mesh_packet_t *deserialized = aethernet_packet_deserialize(buffer, serialized_size);
    if (!deserialized) {
        printf("ERROR: Failed to deserialize packet\n");
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    printf("Deserialized packet:\n");
    printf("  Type: %d\n", deserialized->type);
    printf("  Source: %s\n", deserialized->source_uhid);
    printf("  Destination: %s\n", deserialized->destination_uhid);
    printf("  Payload: %.*s\n", (int)deserialized->payload_len, (char *)deserialized->payload);
    printf("  Signature length: %d bytes\n", deserialized->signature_len);

    // Verify the deserialized packet's signature
    signable_data = aethernet_packet_get_signable_data(deserialized, &signable_len);
    if (!signable_data) {
        printf("ERROR: Failed to get signable data for deserialized packet\n");
        aethernet_packet_free(deserialized);
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    if (aethernet_ed25519_verify(public_key, signable_data, signable_len, deserialized->signature)) {
        printf("✓ Deserialized packet signature verification PASSED\n");
    } else {
        printf("✗ Deserialized packet signature verification FAILED\n");
        free(signable_data);
        aethernet_packet_free(deserialized);
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    free(signable_data);

    // Demo 5: AES-GCM Encryption/Decryption
    printf("\n--- Demo 5: AES-256-GCM Encryption/Decryption ---\n");
    uint8_t aes_key[32];
    if (!aethernet_random_bytes(aes_key, 32)) {
        printf("ERROR: Failed to generate AES key\n");
        aethernet_packet_free(deserialized);
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    const char *plaintext = "Secret message from Alice to Bob";
    size_t plaintext_len = strlen(plaintext);

    uint8_t ciphertext[256];
    uint8_t tag[AETHERNET_AES_GCM_TAG_SIZE];
    uint8_t nonce[AETHERNET_AES_GCM_NONCE_SIZE];

    if (!aethernet_aes256_gcm_encrypt((const uint8_t *)plaintext,
                                  plaintext_len,
                                  aes_key,
                                  NULL,  // Generate random nonce
                                  NULL,  // No AAD
                                  0,
                                  ciphertext,
                                  tag,
                                  nonce)) {
        printf("ERROR: AES-GCM encryption failed\n");
        aethernet_packet_free(deserialized);
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    printf("Plaintext: %s (%zu bytes)\n", plaintext, plaintext_len);
    hex_dump("Ciphertext", ciphertext, plaintext_len);
    hex_dump("Tag", tag, AETHERNET_AES_GCM_TAG_SIZE);
    hex_dump("Nonce", nonce, AETHERNET_AES_GCM_NONCE_SIZE);

    // Decrypt
    uint8_t decrypted[256];
    if (!aethernet_aes256_gcm_decrypt(ciphertext,
                                  plaintext_len,
                                  aes_key,
                                  nonce,
                                  tag,
                                  NULL,
                                  0,
                                  decrypted)) {
        printf("ERROR: AES-GCM decryption failed\n");
        aethernet_packet_free(deserialized);
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    printf("Decrypted: %.*s\n", (int)plaintext_len, (char *)decrypted);

    if (memcmp(plaintext, decrypted, plaintext_len) == 0) {
        printf("✓ Encryption/decryption round-trip PASSED\n");
    } else {
        printf("✗ Encryption/decryption round-trip FAILED\n");
        aethernet_packet_free(deserialized);
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    // Demo 6: HMAC-SHA256
    printf("\n--- Demo 6: HMAC-SHA256 ---\n");
    const char *hmac_msg = "Test message for HMAC";
    uint8_t hmac_key[32];
    if (!aethernet_random_bytes(hmac_key, 32)) {
        printf("ERROR: Failed to generate HMAC key\n");
        aethernet_packet_free(deserialized);
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    uint8_t hmac_result[AETHERNET_HMAC_SHA256_SIZE];
    if (!aethernet_hmac_sha256(hmac_key, 32, (const uint8_t *)hmac_msg, strlen(hmac_msg), hmac_result)) {
        printf("ERROR: HMAC-SHA256 failed\n");
        aethernet_packet_free(deserialized);
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    hex_dump("HMAC-SHA256", hmac_result, AETHERNET_HMAC_SHA256_SIZE);
    printf("✓ HMAC-SHA256 computed successfully\n");

    // Demo 7: HKDF-SHA256
    printf("\n--- Demo 7: HKDF-SHA256 Key Derivation ---\n");
    uint8_t ikm[32];
    if (!aethernet_random_bytes(ikm, 32)) {
        printf("ERROR: Failed to generate IKM\n");
        aethernet_packet_free(deserialized);
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    uint8_t derived_key[32];
    const char *info_str = "aether-root-v1";
    if (!aethernet_hkdf_sha256(NULL, 0, ikm, 32, (const uint8_t *)info_str, strlen(info_str), 32, derived_key)) {
        printf("ERROR: HKDF-SHA256 failed\n");
        aethernet_packet_free(deserialized);
        free(buffer);
        aethernet_packet_free(packet);
        return 1;
    }

    hex_dump("Derived Root Key", derived_key, 32);
    printf("✓ HKDF-SHA256 key derivation successful\n");

    // Cleanup
    printf("\n--- Cleanup ---\n");
    aethernet_packet_free(packet);
    aethernet_packet_free(deserialized);
    free(buffer);

    printf("\n=== All demos completed successfully! ===\n");
    return 0;
}
