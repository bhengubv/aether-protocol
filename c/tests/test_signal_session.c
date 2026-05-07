// SPDX-License-Identifier: MIT
//
// E2E tests for the Signal Protocol session service (signal_protocol.c).
//
// Two-node (Alice / Bob) round-trip tests covering X3DH session
// establishment, Double-Ratchet encrypt/decrypt, multi-message chains,
// and basic error paths.

#define _POSIX_C_SOURCE 200809L

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aether/signal_protocol.h"

// ─── Test harness macros (same style as test_routing.c) ──────────────────

#define PASS() do { return; } while (0)
#define FAIL(msg) do { \
    fprintf(stderr, "FAIL: %s:%d: %s\n", __FILE__, __LINE__, (msg)); \
    abort(); \
} while (0)
#define ASSERT(cond) do { \
    if (!(cond)) FAIL(#cond); \
} while (0)

static int tests_run = 0;
#define RUN(fn) do { printf("TEST: " #fn "..."); fn(); printf(" OK\n"); tests_run++; } while (0)

// ─── Helpers ─────────────────────────────────────────────────────────────

/** Encrypt plaintext from sender → receiver and decrypt it, asserting the
 *  round-trip matches. Frees the message and plaintext internally. */
static void assert_encrypt_decrypt(aether_signal_service_t *sender,
                                   const char *receiver_uhid,
                                   aether_signal_service_t *receiver,
                                   const char *sender_uhid,
                                   const char *text)
{
    const uint8_t *pt_in = (const uint8_t *)text;
    size_t         plen  = strlen(text);

    aether_signal_message_t msg;
    memset(&msg, 0, sizeof(msg));

    ASSERT(aether_signal_encrypt(sender, receiver_uhid, pt_in, plen, &msg));

    uint8_t *pt_out = NULL;
    size_t   pt_len = 0;
    ASSERT(aether_signal_decrypt(receiver, sender_uhid, &msg, &pt_out, &pt_len));

    ASSERT(pt_len == plen);
    ASSERT(memcmp(pt_out, pt_in, plen) == 0);

    free(pt_out);
    aether_signal_message_free(&msg);
}

// ─── Test cases ───────────────────────────────────────────────────────────

/**
 * test_basic_session
 * Alice processes Bob's bundle, Alice encrypts a message, Bob decrypts it.
 */
static void test_basic_session(void)
{
    aether_signal_service_t alice, bob;
    ASSERT(aether_signal_service_init(&alice, "alice"));
    ASSERT(aether_signal_service_init(&bob,   "bob"));

    aether_pre_key_bundle_t bob_bundle;
    ASSERT(aether_signal_generate_pre_key_bundle(&bob, &bob_bundle));

    /* Alice processes Bob's bundle → initiator session. */
    ASSERT(aether_signal_process_pre_key_bundle(&alice, &bob_bundle));

    /* Alice encrypts → Bob decrypts. */
    assert_encrypt_decrypt(&alice, "bob", &bob, "alice", "hello from alice");

    aether_signal_service_destroy(&alice);
    aether_signal_service_destroy(&bob);
}

/**
 * test_bidirectional
 * Full bidirectional exchange: Alice→Bob then Bob→Alice.
 */
static void test_bidirectional(void)
{
    aether_signal_service_t alice, bob;
    ASSERT(aether_signal_service_init(&alice, "alice"));
    ASSERT(aether_signal_service_init(&bob,   "bob"));

    aether_pre_key_bundle_t bob_bundle;
    ASSERT(aether_signal_generate_pre_key_bundle(&bob, &bob_bundle));
    ASSERT(aether_signal_process_pre_key_bundle(&alice, &bob_bundle));

    /* Alice → Bob (first message; PreKey). */
    assert_encrypt_decrypt(&alice, "bob", &bob, "alice", "msg1: alice to bob");

    /* Bob → Alice (Bob now has a session; Alice decrypts and ratchets). */
    assert_encrypt_decrypt(&bob, "alice", &alice, "bob", "msg2: bob to alice");

    aether_signal_service_destroy(&alice);
    aether_signal_service_destroy(&bob);
}

/**
 * test_ratchet_steps
 * 5 alternating exchanges, each step advancing both ratchets.
 */
static void test_ratchet_steps(void)
{
    aether_signal_service_t alice, bob;
    ASSERT(aether_signal_service_init(&alice, "alice"));
    ASSERT(aether_signal_service_init(&bob,   "bob"));

    aether_pre_key_bundle_t bob_bundle;
    ASSERT(aether_signal_generate_pre_key_bundle(&bob, &bob_bundle));
    ASSERT(aether_signal_process_pre_key_bundle(&alice, &bob_bundle));

    char msg[64];
    for (int i = 0; i < 5; i++) {
        snprintf(msg, sizeof(msg), "alice->bob round %d", i);
        assert_encrypt_decrypt(&alice, "bob",   &bob,   "alice", msg);

        snprintf(msg, sizeof(msg), "bob->alice round %d", i);
        assert_encrypt_decrypt(&bob,   "alice", &alice, "bob",   msg);
    }

    aether_signal_service_destroy(&alice);
    aether_signal_service_destroy(&bob);
}

/**
 * test_has_session
 * aether_signal_has_session returns false before bundle processing, true after.
 */
static void test_has_session(void)
{
    aether_signal_service_t alice, bob;
    ASSERT(aether_signal_service_init(&alice, "alice"));
    ASSERT(aether_signal_service_init(&bob,   "bob"));

    /* No session yet. */
    ASSERT(!aether_signal_has_session(&alice, "bob"));
    ASSERT(!aether_signal_has_session(&bob,   "alice"));

    aether_pre_key_bundle_t bob_bundle;
    ASSERT(aether_signal_generate_pre_key_bundle(&bob, &bob_bundle));
    ASSERT(aether_signal_process_pre_key_bundle(&alice, &bob_bundle));

    /* Alice now has a session with bob. */
    ASSERT(aether_signal_has_session(&alice, "bob"));
    /* Bob does not yet (session created on first decrypt). */
    ASSERT(!aether_signal_has_session(&bob, "alice"));

    aether_signal_service_destroy(&alice);
    aether_signal_service_destroy(&bob);
}

/**
 * test_spk_sig_invalid
 * Tamper with the SPK signature → process_pre_key_bundle returns false.
 */
static void test_spk_sig_invalid(void)
{
    aether_signal_service_t alice, bob;
    ASSERT(aether_signal_service_init(&alice, "alice"));
    ASSERT(aether_signal_service_init(&bob,   "bob"));

    aether_pre_key_bundle_t bob_bundle;
    ASSERT(aether_signal_generate_pre_key_bundle(&bob, &bob_bundle));

    /* Corrupt the last byte of the signature. */
    bob_bundle.signed_pre_key_signature[63] ^= 0xFF;

    ASSERT(!aether_signal_process_pre_key_bundle(&alice, &bob_bundle));

    aether_signal_service_destroy(&alice);
    aether_signal_service_destroy(&bob);
}

/**
 * test_multi_message_same_chain
 * Alice sends 3 messages before Bob replies; all 3 must decrypt correctly
 * (tests in-order same-chain delivery without ratchet steps between them).
 */
static void test_multi_message_same_chain(void)
{
    aether_signal_service_t alice, bob;
    ASSERT(aether_signal_service_init(&alice, "alice"));
    ASSERT(aether_signal_service_init(&bob,   "bob"));

    aether_pre_key_bundle_t bob_bundle;
    ASSERT(aether_signal_generate_pre_key_bundle(&bob, &bob_bundle));
    ASSERT(aether_signal_process_pre_key_bundle(&alice, &bob_bundle));

    const char *texts[3] = { "msg A", "msg B", "msg C" };
    aether_signal_message_t msgs[3];
    memset(msgs, 0, sizeof(msgs));

    /* Alice encrypts 3 messages before Bob decrypts any. */
    for (int i = 0; i < 3; i++) {
        ASSERT(aether_signal_encrypt(&alice, "bob",
                                     (const uint8_t *)texts[i],
                                     strlen(texts[i]),
                                     &msgs[i]));
    }

    /* Bob decrypts all 3 in order. */
    for (int i = 0; i < 3; i++) {
        uint8_t *pt  = NULL;
        size_t   len = 0;
        ASSERT(aether_signal_decrypt(&bob, "alice", &msgs[i], &pt, &len));
        ASSERT(len == strlen(texts[i]));
        ASSERT(memcmp(pt, texts[i], len) == 0);
        free(pt);
        aether_signal_message_free(&msgs[i]);
    }

    aether_signal_service_destroy(&alice);
    aether_signal_service_destroy(&bob);
}

// ─── main ─────────────────────────────────────────────────────────────────

int main(void)
{
    printf("Aether Signal Session — E2E Tests\n");
    printf("==================================\n");

    RUN(test_basic_session);
    RUN(test_bidirectional);
    RUN(test_ratchet_steps);
    RUN(test_has_session);
    RUN(test_spk_sig_invalid);
    RUN(test_multi_message_same_chain);

    printf("\n%d tests passed.\n", tests_run);
    return 0;
}
