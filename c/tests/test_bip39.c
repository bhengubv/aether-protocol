// SPDX-License-Identifier: MIT
//
// BIP-39 recovery-phrase parity gate. Loads the official Trezor test vectors
// from fixtures/bip39/vectors.json and asserts, for all 24 cases, that this C
// port reproduces the reference (src/AetherNet.Security/Backup/*.cs, 80/80
// green) byte-for-byte:
//
//   entropy  -> mnemonic          == mnemonic
//   mnemonic -> entropy   (hex)   == entropy
//   mnemonic -> seed(...,TREZOR)  == seed   (PBKDF2-HMAC-SHA512, 2048, 64)
//
// Plus an identity round-trip (32-byte Ed25519 seed <-> 24 words <-> seed +
// derived public key) and the reject-paths (bad checksum, unknown word, wrong
// word count) that make a mistyped phrase fail instead of silently yielding a
// different secret.
//
// JSON parsing is the same tiny hand-rolled extractor used by test_fixtures.c —
// the schema is a flat array of flat string objects, no JSON library needed.
// Run from the repo root so the relative fixture path resolves.

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/bip39.h"

static int tests_run = 0;

#define FAILF(...) do { \
    fprintf(stderr, "FAIL: "); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); \
    exit(1); \
} while (0)

/* ─── hex helpers ───────────────────────────────────────────────────────── */

static int hexv(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

// Hex string -> freshly allocated byte buffer. Caller frees. *out_len set.
static uint8_t *hex_to_bytes(const char *hex, size_t *out_len) {
    size_t hl = strlen(hex);
    size_t n = hl / 2;
    *out_len = n;
    uint8_t *b = (uint8_t *)malloc(n ? n : 1);
    for (size_t i = 0; i < n; i++)
        b[i] = (uint8_t)((hexv(hex[i * 2]) << 4) | hexv(hex[i * 2 + 1]));
    return b;
}

// Lowercase-hex encode bytes into a caller-provided buffer (>= 2*len+1).
static void bytes_to_hex(const uint8_t *b, size_t len, char *out) {
    static const char HEX[] = "0123456789abcdef";
    for (size_t i = 0; i < len; i++) {
        out[i * 2] = HEX[(b[i] >> 4) & 0xF];
        out[i * 2 + 1] = HEX[b[i] & 0xF];
    }
    out[len * 2] = '\0';
}

/* ─── tiny JSON extractor (mirrors test_fixtures.c) ─────────────────────── */

static char *read_file(const char *path, size_t *out_len) {
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz < 0) { fclose(f); return NULL; }
    char *buf = (char *)malloc((size_t)sz + 1);
    if (!buf) { fclose(f); return NULL; }
    size_t got = fread(buf, 1, (size_t)sz, f);
    buf[got] = '\0';
    fclose(f);
    if (out_len) *out_len = got;
    return buf;
}

// Returns the value of a string field (newly allocated). Caller frees.
static char *json_str_field(const char *obj, const char *key) {
    char needle[64];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = strstr(obj, needle);
    if (!p) return NULL;
    p += strlen(needle);
    while (*p && (*p == ' ' || *p == '\t')) p++;
    if (*p != '"') return NULL;
    p++;
    const char *start = p;
    while (*p && !(*p == '"' && *(p - 1) != '\\')) p++;
    size_t n = (size_t)(p - start);
    char *out = (char *)malloc(n + 1);
    memcpy(out, start, n);
    out[n] = '\0';
    return out;
}

typedef struct {
    char *entropy;   // hex
    char *mnemonic;
    char *seed;      // hex
} bip39_vector_t;

// Splits the "vectors" array into flat objects and extracts the three fields
// from each. Returns count; writes a malloc'd array via *out.
static int parse_vectors(const char *json, bip39_vector_t **out) {
    // Locate the "vectors" array specifically (skip the top-level string
    // fields "passphrase"/"source"/"note").
    const char *arr = strstr(json, "\"vectors\"");
    if (!arr) return 0;
    const char *p = strchr(arr, '[');
    if (!p) return 0;
    p++;

    int count = 0, cap = 32;
    bip39_vector_t *items = (bip39_vector_t *)calloc((size_t)cap, sizeof *items);

    while (*p) {
        while (*p && (*p == ' ' || *p == '\n' || *p == '\r' || *p == '\t' || *p == ',')) p++;
        if (!*p || *p == ']') break;
        if (*p != '{') break;
        const char *start = p;
        int depth = 1;
        p++;
        while (*p && depth > 0) {
            if (*p == '{') depth++;
            else if (*p == '}') depth--;
            else if (*p == '"') {
                p++;
                while (*p && *p != '"') {
                    if (*p == '\\' && *(p + 1)) p++;
                    p++;
                }
            }
            p++;
        }
        size_t obj_len = (size_t)(p - start);
        char *obj = (char *)malloc(obj_len + 1);
        memcpy(obj, start, obj_len);
        obj[obj_len] = '\0';

        if (count == cap) {
            cap *= 2;
            items = (bip39_vector_t *)realloc(items, (size_t)cap * sizeof *items);
        }
        bip39_vector_t *v = &items[count++];
        v->entropy = json_str_field(obj, "entropy");
        v->mnemonic = json_str_field(obj, "mnemonic");
        v->seed = json_str_field(obj, "seed");
        free(obj);
    }
    *out = items;
    return count;
}

/* ─── vector checks ─────────────────────────────────────────────────────── */

static void check_vector(int idx, const bip39_vector_t *v, const char *passphrase) {
    if (!v->entropy || !v->mnemonic || !v->seed)
        FAILF("vector %d missing a field", idx);

    size_t ent_len = 0;
    uint8_t *entropy = hex_to_bytes(v->entropy, &ent_len);

    // 1. entropy -> mnemonic == mnemonic
    char phrase[AETHERNET_BIP39_MAX_PHRASE_LEN];
    if (!aethernet_bip39_entropy_to_mnemonic(entropy, ent_len, phrase, sizeof phrase))
        FAILF("vector %d: entropy_to_mnemonic failed", idx);
    if (strcmp(phrase, v->mnemonic) != 0)
        FAILF("vector %d: mnemonic mismatch\n  got:  %s\n  want: %s", idx, phrase, v->mnemonic);

    // 2. hex(mnemonic -> entropy) == entropy
    uint8_t back[32];
    size_t back_len = 0;
    if (!aethernet_bip39_mnemonic_to_entropy(v->mnemonic, back, sizeof back, &back_len))
        FAILF("vector %d: mnemonic_to_entropy failed (checksum?)", idx);
    if (back_len != ent_len || memcmp(back, entropy, ent_len) != 0) {
        char got[65];
        bytes_to_hex(back, back_len, got);
        FAILF("vector %d: entropy round-trip mismatch\n  got:  %s\n  want: %s",
              idx, got, v->entropy);
    }

    // 3. hex(mnemonic -> seed(..., "TREZOR")) == seed
    uint8_t seed[AETHERNET_BIP39_SEED_SIZE];
    if (!aethernet_bip39_mnemonic_to_seed(v->mnemonic, passphrase, seed))
        FAILF("vector %d: mnemonic_to_seed failed", idx);
    char seed_hex[AETHERNET_BIP39_SEED_SIZE * 2 + 1];
    bytes_to_hex(seed, sizeof seed, seed_hex);
    if (strcmp(seed_hex, v->seed) != 0)
        FAILF("vector %d: seed mismatch\n  got:  %s\n  want: %s", idx, seed_hex, v->seed);

    // Bonus: the phrase must validate.
    if (!aethernet_bip39_is_valid(v->mnemonic))
        FAILF("vector %d: is_valid returned false for a valid phrase", idx);

    free(entropy);
    tests_run++;
}

/* ─── identity round-trip ───────────────────────────────────────────────── */

static void check_identity_round_trip(void) {
    // The known identity vector from the task: a 32-byte seed <-> 24-word phrase.
    const char *seed_hex_in =
        "f585c11aec520db57dd353c69554b21a89b20fb0650966fa0a9d6f74fd989d8f";
    const char *expected_phrase =
        "void come effort suffer camp survey warrior heavy shoot primary clutch "
        "crush open amazing screen patrol group space point ten exist slush "
        "involve unfold";

    size_t seed_len = 0;
    uint8_t *priv_in = hex_to_bytes(seed_hex_in, &seed_len);
    if (seed_len != 32) FAILF("identity: seed hex is not 32 bytes");

    // seed -> phrase == expected
    char phrase[AETHERNET_BIP39_MAX_PHRASE_LEN];
    if (!aethernet_identity_to_recovery_phrase(priv_in, phrase, sizeof phrase))
        FAILF("identity: to_recovery_phrase failed");
    if (strcmp(phrase, expected_phrase) != 0)
        FAILF("identity: phrase mismatch\n  got:  %s\n  want: %s", phrase, expected_phrase);

    // phrase -> (priv, pub); priv must equal the original seed.
    uint8_t priv_out[32], pub_out[32];
    if (!aethernet_identity_from_recovery_phrase(phrase, priv_out, pub_out))
        FAILF("identity: from_recovery_phrase failed");
    if (memcmp(priv_out, priv_in, 32) != 0)
        FAILF("identity: recovered private key differs from original seed");

    // Deriving the public key independently (via to->from again) must be stable,
    // and the public key must be non-zero (a real point).
    uint8_t priv2[32], pub2[32];
    if (!aethernet_identity_from_recovery_phrase(expected_phrase, priv2, pub2))
        FAILF("identity: second restore failed");
    if (memcmp(pub_out, pub2, 32) != 0)
        FAILF("identity: public-key derivation is not deterministic");
    int all_zero = 1;
    for (int i = 0; i < 32; i++) if (pub_out[i]) { all_zero = 0; break; }
    if (all_zero) FAILF("identity: derived public key is all zeros");

    free(priv_in);
    printf("  identity round-trip OK\n");
    tests_run++;
}

/* ─── reject paths ──────────────────────────────────────────────────────── */

static void check_reject_paths(void) {
    uint8_t entropy[32];
    size_t len = 0;

    // 24 x "abandon" — structurally 24 words but a deliberately wrong checksum
    // (the valid all-zero-entropy phrase ends in "art", not "abandon").
    const char *bad_checksum =
        "abandon abandon abandon abandon abandon abandon abandon abandon "
        "abandon abandon abandon abandon abandon abandon abandon abandon "
        "abandon abandon abandon abandon abandon abandon abandon abandon";
    if (aethernet_bip39_mnemonic_to_entropy(bad_checksum, entropy, sizeof entropy, &len))
        FAILF("reject: 24x'abandon' bad checksum was accepted");
    if (aethernet_bip39_is_valid(bad_checksum))
        FAILF("reject: is_valid true for bad-checksum phrase");

    // Unknown word ("abandonx" is not in the wordlist) in an otherwise 12-word
    // phrase. The valid all-zero 12-word phrase is 11x"abandon" + "about".
    const char *unknown_word =
        "abandon abandon abandon abandon abandon abandon "
        "abandon abandon abandon abandon abandon abandonx";
    if (aethernet_bip39_mnemonic_to_entropy(unknown_word, entropy, sizeof entropy, &len))
        FAILF("reject: unknown word was accepted");

    // Wrong word count (13 words — not in {12,15,18,21,24}).
    const char *wrong_count =
        "abandon abandon abandon abandon abandon abandon abandon "
        "abandon abandon abandon abandon abandon abandon";
    if (aethernet_bip39_mnemonic_to_entropy(wrong_count, entropy, sizeof entropy, &len))
        FAILF("reject: 13-word phrase was accepted");

    // Empty phrase -> zero words -> wrong count.
    if (aethernet_bip39_mnemonic_to_entropy("", entropy, sizeof entropy, &len))
        FAILF("reject: empty phrase was accepted");

    // A valid 12-word phrase (11x"abandon" + "about") must still be accepted —
    // proves the reject paths above aren't just failing everything.
    const char *valid12 =
        "abandon abandon abandon abandon abandon abandon "
        "abandon abandon abandon abandon abandon about";
    if (!aethernet_bip39_mnemonic_to_entropy(valid12, entropy, sizeof entropy, &len))
        FAILF("reject: valid 12-word phrase was wrongly rejected");
    if (len != 16) FAILF("reject: valid 12-word entropy len != 16 (got %zu)", len);

    // A 24-word identity phrase must NOT restore as an identity if it has a
    // bad checksum.
    uint8_t p[32], q[32];
    if (aethernet_identity_from_recovery_phrase(bad_checksum, p, q))
        FAILF("reject: identity restore accepted a bad-checksum phrase");

    // A 12-word (valid) phrase is not a 24-word identity seed -> identity
    // restore must reject it.
    if (aethernet_identity_from_recovery_phrase(valid12, p, q))
        FAILF("reject: identity restore accepted a 12-word (non-256-bit) phrase");

    printf("  reject-paths OK (bad checksum, unknown word, wrong count)\n");
    tests_run++;
}

int main(void) {
    printf("AetherNet BIP-39 Recovery-Phrase Parity (C)\n");
    printf("===========================================\n");

    const char *candidates[] = {
        "fixtures/bip39/vectors.json",
        "../fixtures/bip39/vectors.json",
        "../../fixtures/bip39/vectors.json",
        "../../../fixtures/bip39/vectors.json",
        NULL,
    };
    char *json = NULL;
    for (int i = 0; candidates[i]; i++) {
        json = read_file(candidates[i], NULL);
        if (json) break;
    }
    if (!json) FAILF("could not locate fixtures/bip39/vectors.json (run from repo root)");

    char *passphrase = json_str_field(json, "passphrase");
    if (!passphrase) FAILF("vectors.json missing top-level \"passphrase\"");

    bip39_vector_t *vectors = NULL;
    int n = parse_vectors(json, &vectors);
    if (n == 0) FAILF("parsed zero vectors");
    if (n != 24) FAILF("expected 24 vectors, parsed %d", n);

    printf("Loaded %d vectors (passphrase=\"%s\").\n", n, passphrase);
    for (int i = 0; i < n; i++) {
        check_vector(i, &vectors[i], passphrase);
    }
    printf("  all %d Trezor vectors OK (entropy<->mnemonic<->seed)\n", n);

    check_identity_round_trip();
    check_reject_paths();

    for (int i = 0; i < n; i++) {
        free(vectors[i].entropy);
        free(vectors[i].mnemonic);
        free(vectors[i].seed);
    }
    free(vectors);
    free(passphrase);
    free(json);

    printf("\n%d BIP-39 test groups passed.\n", tests_run);
    return 0;
}
