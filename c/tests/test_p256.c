// SPDX-License-Identifier: MIT
// Cross-language P-256 ECDSA verify fixture driver (C).
//
// Drives aethernet_ed25519_verify_with_fallback (src/security.c) through the SAME
// corpus as every other AetherNet SDK: tests/cross-language/p256-fixtures.json — a
// DER SubjectPublicKeyInfo public key + an ASN.1 DER ECDSA signature over SHA-256
// (PROTOCOL_SPEC.md 7.5). cJSON is a PRIVATE dependency of the library and is not on
// the test include path (see the rationale in test_bandwidth_fixtures.c), so the 3
// vectors are transcribed verbatim below — every literal copied from
// p256-fixtures.json. An Ed25519-only regression rejects the valid vector and this
// binary exits non-zero, so the legacy fallback can never silently drop to a stub.

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "aethernet/security.h"

typedef struct {
    const char *name;
    const char *public_key_der_hex;   /* DER SubjectPublicKeyInfo P-256 public key */
    const char *message_hex;
    const char *signature_der_hex;    /* ASN.1 DER ECDSA signature                 */
    int         expect_valid;         /* 1 = must verify, 0 = must reject           */
} p256_vector_t;

/* Transcribed verbatim from tests/cross-language/p256-fixtures.json. */
static const p256_vector_t VECTORS[] = {
    { "valid_p256_ecdsa_sha256",
      "3059301306072a8648ce3d020106082a8648ce3d030107034200045807521e3fbcf9991d5906a68516b814310fe83a8c530793594429e409185b78874c68defa466983dd45ee3df091e5be9ea14672a761b9b09dbb98f7ac728256",
      "6165746865726e65743a703235362d6d6967726174696f6e2d7665726966793a7631",
      "3045022100a09828511f955fcf73c4ef415360b4830c890e08f99b8b4c6774bd1fe7769e160220353e694642e52eb78c42c77f3ac75edce401b4085017e9ca9c71c77437c5dfdc",
      1 },
    { "tampered_signature",
      "3059301306072a8648ce3d020106082a8648ce3d030107034200045807521e3fbcf9991d5906a68516b814310fe83a8c530793594429e409185b78874c68defa466983dd45ee3df091e5be9ea14672a761b9b09dbb98f7ac728256",
      "6165746865726e65743a703235362d6d6967726174696f6e2d7665726966793a7631",
      "3045022100a09828511f955fcf73c4ef415360b4830c890e08f99b8b4c6774bd1fe7769e160220353e694642e52eb78c42c77f3ac75edce401b4085017e9ca9c71c77437c5dfdd",
      0 },
    { "wrong_message",
      "3059301306072a8648ce3d020106082a8648ce3d030107034200045807521e3fbcf9991d5906a68516b814310fe83a8c530793594429e409185b78874c68defa466983dd45ee3df091e5be9ea14672a761b9b09dbb98f7ac728256",
      "6165746865726e65743a703235362d6d6967726174696f6e2d7665726966793a54414d5045524544",
      "3045022100a09828511f955fcf73c4ef415360b4830c890e08f99b8b4c6774bd1fe7769e160220353e694642e52eb78c42c77f3ac75edce401b4085017e9ca9c71c77437c5dfdc",
      0 },
};
#define VECTOR_COUNT (sizeof(VECTORS) / sizeof(VECTORS[0]))

/* Decode a hex string into out (caller-allocated). Returns the byte length, or
 * (size_t)-1 on malformed input. */
static size_t hex_decode(const char *hex, uint8_t *out, size_t out_cap) {
    size_t hlen = strlen(hex);
    if (hlen % 2 != 0 || hlen / 2 > out_cap) return (size_t)-1;
    for (size_t i = 0; i < hlen / 2; i++) {
        unsigned int byte;
        if (sscanf(hex + i * 2, "%2x", &byte) != 1) return (size_t)-1;
        out[i] = (uint8_t)byte;
    }
    return hlen / 2;
}

int main(void) {
    printf("=== P-256 ECDSA verify fixture driver (C) ===\n");
    printf("corpus: tests/cross-language/p256-fixtures.json (transcribed)\n\n");

    int failed = 0;
    for (size_t i = 0; i < VECTOR_COUNT; i++) {
        const p256_vector_t *v = &VECTORS[i];
        uint8_t pub[256], msg[256], sig[128];
        size_t publen = hex_decode(v->public_key_der_hex, pub, sizeof(pub));
        size_t msglen = hex_decode(v->message_hex, msg, sizeof(msg));
        size_t siglen = hex_decode(v->signature_der_hex, sig, sizeof(sig));
        if (publen == (size_t)-1 || msglen == (size_t)-1 || siglen == (size_t)-1) {
            fprintf(stderr, "FAIL [%s]: malformed hex in fixture\n", v->name);
            failed++;
            continue;
        }
        /* A >32-byte key forces the P-256 branch; the Ed25519 path only takes 32. */
        if (publen <= 32) {
            fprintf(stderr, "FAIL [%s]: P-256 key must be > 32 bytes (got %zu)\n",
                    v->name, publen);
            failed++;
            continue;
        }

        bool got  = aethernet_ed25519_verify_with_fallback(pub, publen, msg, msglen, sig, siglen);
        bool want = v->expect_valid != 0;
        if (got != want) {
            fprintf(stderr, "FAIL [%s]: expected %s, got %s\n",
                    v->name, want ? "valid" : "reject", got ? "valid" : "reject");
            failed++;
        } else {
            printf("ok [%s]: %s\n", v->name, want ? "valid" : "rejected");
        }
    }

    if (failed) {
        fprintf(stderr, "\n%d/%zu P-256 vector(s) FAILED.\n", failed, VECTOR_COUNT);
        return 1;
    }
    printf("\nAll %zu P-256 vectors passed.\n", VECTOR_COUNT);
    return 0;
}
