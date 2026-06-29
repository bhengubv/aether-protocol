// SPDX-License-Identifier: MIT
// Cross-language PeerID parity: the C port must reproduce the exact libp2p
// PeerID string that js-libp2p / the C# / Go references produce for each Ed25519
// public key in fixtures/peerid/inputs.json. The 5 cases are transcribed here in
// C (no JSON parser on the test surface, mirroring test_dtn_fixture.c /
// test_p256.c). Any drift between C and the other ports surfaces as a string
// mismatch. See fixtures/peerid/.

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "aethernet/peer_id.h"

static int tests_run = 0;

#define FAILF(name, ...) do { \
    fprintf(stderr, "FAIL [%s]: ", (name)); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); \
    return 1; \
} while (0)

static int hexv(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

// Parse exactly 32 bytes (64 hex chars) into out[32]. Returns false on a bad
// length or a non-hex character.
static bool hex32(const char *hex, uint8_t out[32]) {
    if (strlen(hex) != 64) return false;
    for (int i = 0; i < 32; i++) {
        int hi = hexv(hex[i * 2]);
        int lo = hexv(hex[i * 2 + 1]);
        if (hi < 0 || lo < 0) return false;
        out[i] = (uint8_t)((hi << 4) | lo);
    }
    return true;
}

// Derive the PeerID for pubkey_hex and assert it equals expected, starts with
// "12D3Koo", and is non-empty.
static int check_case(const char *name, const char *pubkey_hex, const char *expected) {
    uint8_t pubkey[32];
    if (!hex32(pubkey_hex, pubkey)) FAILF(name, "bad pubkey hex");

    char peer_id[64];
    if (!aethernet_peer_id_from_ed25519(pubkey, peer_id)) {
        FAILF(name, "derivation returned false for valid 32-byte key");
    }
    if (strncmp(peer_id, "12D3Koo", 7) != 0) {
        FAILF(name, "expected 12D3Koo prefix, got '%.7s'", peer_id);
    }
    if (strcmp(peer_id, expected) != 0) {
        FAILF(name, "PeerID mismatch\n  got:      %s\n  expected: %s", peer_id, expected);
    }

    printf("  %s OK  %s\n", name, peer_id);
    tests_run++;
    return 0;
}

int main(void) {
    printf("Aether PeerID — Cross-Language Fixture Parity\n");
    printf("=============================================\n");

    // 5 cases transcribed from fixtures/peerid/inputs.json +
    // fixtures/peerid/expected/<name>.txt.
    if (check_case("ed25519_1",
                   "3b6a27bcceb6a42d62a3a8d02a6f0d73653215771de243a63ac048a18b59da29",
                   "12D3KooWDpJ7As7BWAwRMfu1VU2WCqNjvq387JEYKDBj4kx6nXTN")) return 1;
    if (check_case("ed25519_2",
                   "8a88e3dd7409f195fd52db2d3cba5d72ca6709bf1d94121bf3748801b40f6f5c",
                   "12D3KooWK99VoVxNE7XzyBwXEzW7xhK7Gpv85r9F3V3fyKSUKPH5")) return 1;
    if (check_case("ed25519_3",
                   "ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c",
                   "12D3KooWRawPbxPtP1eZaJpumGnyWX2DcUyd3RQnydr3eAto4Az7")) return 1;
    if (check_case("ed25519_4",
                   "197f6b23e16c8532c6abc838facd5ea789be0c76b2920334039bfa8b3d368d61",
                   "12D3KooWBXu3uGPMkjjxViK6autSnFH5QaKJgTwW8CaSxYSD6yYL")) return 1;
    if (check_case("ed25519_5",
                   "76a1592044a6e4f511265bca73a604d90b0529d1df602be30a19a9257660d1f5",
                   "12D3KooWHoSyTgntm77sXShoeX9uNkqKNMhHxKtskaHqnA54SrSG")) return 1;

    // 32-byte length guard: AETHERNET_ED25519_PUBLIC_KEY_LENGTH must be 32, and a
    // NULL key must be rejected (the length contract lives in the array param).
    if (AETHERNET_ED25519_PUBLIC_KEY_LENGTH != 32) {
        FAILF("length_guard", "AETHERNET_ED25519_PUBLIC_KEY_LENGTH != 32 (got %d)",
              AETHERNET_ED25519_PUBLIC_KEY_LENGTH);
    }
    char tmp[64];
    if (aethernet_peer_id_from_ed25519(NULL, tmp)) {
        FAILF("length_guard", "NULL pubkey should return false");
    }
    tests_run++;
    printf("  length_guard OK  (32-byte key length enforced, NULL rejected)\n");

    printf("\n%d cases passed.\n", tests_run);
    return 0;
}
