// SPDX-License-Identifier: MIT
//
// Panic-wipe parity gate. Loads fixtures/panicwipe/vectors.json and asserts this
// C port reproduces the reference (src/AetherNet.Security/Privacy/PanicWipe.cs)
// byte-for-byte:
//
//   duress_pin_hashes: for each {pin, sha256}
//                        hex(duress_pin_hash(pin))       == sha256
//                        verify_duress_pin(pin,      hash) == true
//                        verify_duress_pin(pin+"x",  hash) == false
//   identity_key_names:  AETHERNET_IDENTITY_KEY_NAMES == fixture list (order too)
//   max_prekeys:         AETHERNET_MAX_PREKEYS == 200 (== fixture)
//   prekey_name(index):        == expected
//   signed_prekey_name(index): == expected
//
// Plus behavioural checks that need no fixture:
//   secure_erase zeroes a non-empty buffer
//   verify_duress_pin with a 16-byte stored hash -> false (wrong length)
//
// JSON parsing is the same tiny hand-rolled extractor style used by
// test_bip39.c / test_ble_privacy.c — the schema is flat, no JSON library
// needed. Run from the repo root so the relative fixture path resolves.

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/panic_wipe.h"

static int tests_run = 0;

#define FAILF(...) do { \
    fprintf(stderr, "FAIL: "); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); \
    exit(1); \
} while (0)

/* ─── hex helper (encode only; the fixture stores hashes as lowercase hex) ─── */

// Lowercase-hex encode bytes into a caller-provided buffer (>= 2*len+1).
static void bytes_to_hex(const uint8_t *b, size_t len, char *out) {
    static const char HEX[] = "0123456789abcdef";
    for (size_t i = 0; i < len; i++) {
        out[i * 2] = HEX[(b[i] >> 4) & 0xF];
        out[i * 2 + 1] = HEX[b[i] & 0xF];
    }
    out[len * 2] = '\0';
}

/* ─── tiny JSON extractor (mirrors test_bip39.c / test_fixtures.c) ────────── */

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

// Returns the value of a string field (newly allocated). Caller frees. Searches
// from `from` (or the whole object if `from` is NULL) for the FIRST match of
// "key": — used both for top-level fields and for fields inside a sub-object
// located by a prior strstr.
static char *json_str_field_from(const char *from, const char *key) {
    char needle[64];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = strstr(from, needle);
    if (!p) return NULL;
    p += strlen(needle);
    while (*p && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')) p++;
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

// Returns the integer value of a numeric field, or `dflt` if not found.
static long json_int_field(const char *json, const char *key, long dflt) {
    char needle[64];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = strstr(json, needle);
    if (!p) return dflt;
    p += strlen(needle);
    while (*p && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r')) p++;
    return strtol(p, NULL, 10);
}

typedef struct {
    char *pin;
    char *sha256; // hex
} pin_vector_t;

// Split the "duress_pin_hashes" array into flat {pin, sha256} objects.
// Returns count; writes a malloc'd array via *out.
static int parse_pin_vectors(const char *json, pin_vector_t **out) {
    const char *arr = strstr(json, "\"duress_pin_hashes\"");
    if (!arr) return 0;
    const char *p = strchr(arr, '[');
    if (!p) return 0;
    p++;

    int count = 0, cap = 16;
    pin_vector_t *items = (pin_vector_t *)calloc((size_t)cap, sizeof *items);

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
            items = (pin_vector_t *)realloc(items, (size_t)cap * sizeof *items);
        }
        pin_vector_t *v = &items[count++];
        v->pin = json_str_field_from(obj, "pin");
        v->sha256 = json_str_field_from(obj, "sha256");
        free(obj);
    }
    *out = items;
    return count;
}

// Parse the "identity_key_names" string array into a malloc'd char* array.
// Returns count; writes the array via *out.
static int parse_identity_names(const char *json, char ***out) {
    const char *arr = strstr(json, "\"identity_key_names\"");
    if (!arr) return 0;
    const char *p = strchr(arr, '[');
    if (!p) return 0;
    p++;
    const char *end = strchr(p, ']');
    if (!end) return 0;

    int count = 0, cap = 16;
    char **items = (char **)calloc((size_t)cap, sizeof *items);
    while (p < end) {
        // advance to the next opening quote (or the array end)
        while (p < end && *p != '"') p++;
        if (p >= end) break;
        p++;
        const char *start = p;
        while (p < end && !(*p == '"' && *(p - 1) != '\\')) p++;
        size_t n = (size_t)(p - start);
        if (p < end) p++; // skip closing quote
        char *s = (char *)malloc(n + 1);
        memcpy(s, start, n);
        s[n] = '\0';
        if (count == cap) {
            cap *= 2;
            items = (char **)realloc(items, (size_t)cap * sizeof *items);
        }
        items[count++] = s;
    }
    *out = items;
    return count;
}

/* ─── checks ────────────────────────────────────────────────────────────── */

static void check_pin_vectors(const pin_vector_t *v, int n) {
    for (int i = 0; i < n; i++) {
        if (!v[i].pin || !v[i].sha256)
            FAILF("pin vector %d missing a field", i);

        // 1. hex(duress_pin_hash(pin)) == sha256
        uint8_t hash[AETHERNET_DURESS_PIN_HASH_SIZE];
        if (!aethernet_duress_pin_hash(v[i].pin, hash))
            FAILF("pin vector %d: duress_pin_hash failed", i);
        char hex[AETHERNET_DURESS_PIN_HASH_SIZE * 2 + 1];
        bytes_to_hex(hash, sizeof hash, hex);
        if (strcmp(hex, v[i].sha256) != 0)
            FAILF("pin vector %d (pin=\"%s\"): hash mismatch\n  got:  %s\n  want: %s",
                  i, v[i].pin, hex, v[i].sha256);

        // 2. verify_duress_pin(pin, hash) == true
        if (!aethernet_verify_duress_pin(v[i].pin, hash, sizeof hash))
            FAILF("pin vector %d (pin=\"%s\"): verify true-case failed", i, v[i].pin);

        // 3. verify_duress_pin(pin+"x", hash) == false
        size_t plen = strlen(v[i].pin);
        char *wrong = (char *)malloc(plen + 2);
        memcpy(wrong, v[i].pin, plen);
        wrong[plen] = 'x';
        wrong[plen + 1] = '\0';
        if (aethernet_verify_duress_pin(wrong, hash, sizeof hash))
            FAILF("pin vector %d: verify accepted a wrong pin (\"%s\")", i, wrong);
        free(wrong);

        tests_run++;
    }
    printf("  all %d duress-PIN vectors OK (hash + verify true/false)\n", n);
}

static void check_identity_names(char **names, int n) {
    if ((size_t)n != AETHERNET_IDENTITY_KEY_NAME_COUNT)
        FAILF("identity_key_names count %d != %u", n, AETHERNET_IDENTITY_KEY_NAME_COUNT);
    for (int i = 0; i < n; i++) {
        if (strcmp(names[i], AETHERNET_IDENTITY_KEY_NAMES[i]) != 0)
            FAILF("identity_key_names[%d] mismatch\n  got:  %s\n  want: %s",
                  i, AETHERNET_IDENTITY_KEY_NAMES[i], names[i]);
    }
    printf("  identity_key_names match (%d, in order)\n", n);
    tests_run++;
}

static void check_prekey_names(const char *json) {
    // prekey_name.{index,expected}
    const char *pk = strstr(json, "\"prekey_name\"");
    if (!pk) FAILF("fixture missing \"prekey_name\"");
    long pk_index = json_int_field(pk, "index", -1);
    char *pk_expected = json_str_field_from(pk, "expected");
    if (pk_index < 0 || !pk_expected) FAILF("prekey_name index/expected missing");

    char buf[64];
    if (!aethernet_prekey_name((int)pk_index, buf, sizeof buf))
        FAILF("prekey_name(%ld) failed", pk_index);
    if (strcmp(buf, pk_expected) != 0)
        FAILF("prekey_name(%ld) mismatch\n  got:  %s\n  want: %s", pk_index, buf, pk_expected);

    // signed_prekey_name.{index,expected}
    const char *sp = strstr(json, "\"signed_prekey_name\"");
    if (!sp) FAILF("fixture missing \"signed_prekey_name\"");
    long sp_index = json_int_field(sp, "index", -1);
    char *sp_expected = json_str_field_from(sp, "expected");
    if (sp_index < 0 || !sp_expected) FAILF("signed_prekey_name index/expected missing");

    if (!aethernet_signed_prekey_name((int)sp_index, buf, sizeof buf))
        FAILF("signed_prekey_name(%ld) failed", sp_index);
    if (strcmp(buf, sp_expected) != 0)
        FAILF("signed_prekey_name(%ld) mismatch\n  got:  %s\n  want: %s", sp_index, buf, sp_expected);

    printf("  prekey_name(%ld)==%s, signed_prekey_name(%ld)==%s OK\n",
           pk_index, pk_expected, sp_index, sp_expected);
    free(pk_expected);
    free(sp_expected);
    tests_run++;
}

static void check_max_prekeys(const char *json) {
    long fx = json_int_field(json, "max_prekeys", -1);
    if (fx != 200) FAILF("fixture max_prekeys != 200 (got %ld)", fx);
    if (AETHERNET_MAX_PREKEYS != 200) FAILF("AETHERNET_MAX_PREKEYS != 200");
    if ((long)AETHERNET_MAX_PREKEYS != fx)
        FAILF("AETHERNET_MAX_PREKEYS (%d) != fixture max_prekeys (%ld)",
              AETHERNET_MAX_PREKEYS, fx);
    printf("  max_prekeys == 200 OK\n");
    tests_run++;
}

static void check_behavioural(void) {
    // secure_erase zeroes a non-empty buffer.
    uint8_t buf[64];
    for (size_t i = 0; i < sizeof buf; i++) buf[i] = (uint8_t)(i + 1);
    aethernet_secure_erase(buf, sizeof buf);
    for (size_t i = 0; i < sizeof buf; i++)
        if (buf[i] != 0) FAILF("secure_erase left a non-zero byte at %zu", i);

    // secure_erase on NULL / len 0 is a safe no-op (must not crash).
    aethernet_secure_erase(NULL, 0);
    aethernet_secure_erase(buf, 0);

    // verify_duress_pin with a wrong-length (16-byte) stored hash -> false.
    uint8_t short_hash[16];
    memset(short_hash, 0xAB, sizeof short_hash);
    if (aethernet_verify_duress_pin("0000", short_hash, sizeof short_hash))
        FAILF("verify_duress_pin accepted a 16-byte hash");

    // NULL stored hash -> false.
    if (aethernet_verify_duress_pin("0000", NULL, 32))
        FAILF("verify_duress_pin accepted a NULL hash");

    printf("  secure_erase zeroes; verify rejects wrong-length/NULL hash OK\n");
    tests_run++;
}

int main(void) {
    printf("AetherNet Panic-Wipe Parity (C)\n");
    printf("===============================\n");

    const char *candidates[] = {
        "fixtures/panicwipe/vectors.json",
        "../fixtures/panicwipe/vectors.json",
        "../../fixtures/panicwipe/vectors.json",
        "../../../fixtures/panicwipe/vectors.json",
        NULL,
    };
    char *json = NULL;
    for (int i = 0; candidates[i]; i++) {
        json = read_file(candidates[i], NULL);
        if (json) break;
    }
    if (!json) FAILF("could not locate fixtures/panicwipe/vectors.json (run from repo root)");

    pin_vector_t *pins = NULL;
    int np = parse_pin_vectors(json, &pins);
    if (np == 0) FAILF("parsed zero duress-PIN vectors");

    char **names = NULL;
    int nn = parse_identity_names(json, &names);
    if (nn == 0) FAILF("parsed zero identity_key_names");

    printf("Loaded %d PIN vectors, %d identity names.\n", np, nn);

    check_pin_vectors(pins, np);
    check_identity_names(names, nn);
    check_max_prekeys(json);
    check_prekey_names(json);
    check_behavioural();

    for (int i = 0; i < np; i++) { free(pins[i].pin); free(pins[i].sha256); }
    free(pins);
    for (int i = 0; i < nn; i++) free(names[i]);
    free(names);
    free(json);

    printf("\n%d panic-wipe test groups passed.\n", tests_run);
    return 0;
}
