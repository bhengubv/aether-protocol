// SPDX-License-Identifier: MIT
// Unit tests for aethermesh_tag.c

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "aethermesh/aethermesh_tag.h"
#include "aethermesh/constants.h"

/* ── Test runner ─────────────────────────────────────────────────────────── */

#define RUN(name) do { \
    printf("TEST: " #name "..."); \
    name(); \
    printf(" OK\n"); \
    tests_run++; \
} while (0)

static int tests_run = 0;

/* ── Helpers ─────────────────────────────────────────────────────────────── */

/* A fixed 32-byte key used for the known-vector tests. */
static const uint8_t FIXED_KEY[32] = {
    0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
    0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10,
    0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
    0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20
};

/* A second distinct 32-byte key used to confirm different keys produce
 * different tags. */
static const uint8_t OTHER_KEY[32] = {
    0xff, 0xfe, 0xfd, 0xfc, 0xfb, 0xfa, 0xf9, 0xf8,
    0xf7, 0xf6, 0xf5, 0xf4, 0xf3, 0xf2, 0xf1, 0xf0,
    0xef, 0xee, 0xed, 0xec, 0xeb, 0xea, 0xe9, 0xe8,
    0xe7, 0xe6, 0xe5, 0xe4, 0xe3, 0xe2, 0xe1, 0xe0
};

/* ── 1. Known-vector: format is "XXXXX-XXXXX" ────────────────────────────── */

static void known_vector_format_is_xxxxx_dash_xxxxx(void)
{
    aethermesh_tag_t tag;
    int rc = aethermesh_tag_from_public_key(FIXED_KEY, 32, &tag);
    assert(rc == 0);

    /* Length must be exactly 11 chars (+ NUL) */
    assert(strlen(tag.value) == 11);

    /* Position 5 must be the separator */
    assert(tag.value[5] == '-');

    /* Every other character must be in the Crockford alphabet:
     * "0123456789ABCDEFGHJKMNPQRSTVWXYZ" */
    const char *alpha = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    for (int i = 0; i < 11; i++) {
        if (i == 5) continue; /* skip separator */
        int found = 0;
        for (int j = 0; j < 32; j++) {
            if (tag.value[i] == alpha[j]) { found = 1; break; }
        }
        assert(found);
    }

    /* Print the derived tag so it is visible in CI output */
    printf(" [tag=%s]", tag.value);
}

/* ── 2. Round-trip: from_public_key → parse → values match ──────────────── */

static void roundtrip_from_public_key_then_parse(void)
{
    aethermesh_tag_t tag1, tag2;

    int rc1 = aethermesh_tag_from_public_key(FIXED_KEY, 32, &tag1);
    assert(rc1 == 0);

    int rc2 = aethermesh_tag_parse(tag1.value, &tag2);
    assert(rc2 == 0);

    assert(memcmp(tag1.value, tag2.value, AETHERMESH_TAG_LENGTH) == 0);
}

/* ── 3. verify() correct key returns 1, wrong key returns 0 ─────────────── */

static void verify_correct_key_returns_1(void)
{
    aethermesh_tag_t tag;
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &tag);

    int result = aethermesh_tag_verify(tag.value, FIXED_KEY, 32);
    assert(result == 1);
}

static void verify_wrong_key_returns_0(void)
{
    aethermesh_tag_t tag;
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &tag);

    int result = aethermesh_tag_verify(tag.value, OTHER_KEY, 32);
    assert(result == 0);
}

/* ── 4. parse() accepts: with separator, without, lowercase, mixed ────────── */

static void parse_accepts_canonical_with_separator(void)
{
    aethermesh_tag_t ref, parsed;
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &ref);

    int rc = aethermesh_tag_parse(ref.value, &parsed);
    assert(rc == 0);
    assert(memcmp(ref.value, parsed.value, AETHERMESH_TAG_LENGTH) == 0);
}

static void parse_accepts_no_separator(void)
{
    aethermesh_tag_t ref, parsed;
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &ref);

    /* Build "XXXXXXXXXX" (10 chars, no '-') */
    char no_sep[11];
    memcpy(no_sep,     ref.value,     5);
    memcpy(no_sep + 5, ref.value + 6, 5);
    no_sep[10] = '\0';

    int rc = aethermesh_tag_parse(no_sep, &parsed);
    assert(rc == 0);
    assert(memcmp(ref.value, parsed.value, AETHERMESH_TAG_LENGTH) == 0);
}

static void parse_accepts_lowercase_with_separator(void)
{
    aethermesh_tag_t ref, parsed;
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &ref);

    /* Lower-case the canonical tag */
    char lower[AETHERMESH_TAG_LENGTH];
    for (int i = 0; i < 11; i++) {
        char c = ref.value[i];
        if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
        lower[i] = c;
    }
    lower[11] = '\0';

    int rc = aethermesh_tag_parse(lower, &parsed);
    assert(rc == 0);
    assert(memcmp(ref.value, parsed.value, AETHERMESH_TAG_LENGTH) == 0);
}

static void parse_accepts_lowercase_no_separator(void)
{
    aethermesh_tag_t ref, parsed;
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &ref);

    char no_sep[11];
    memcpy(no_sep,     ref.value,     5);
    memcpy(no_sep + 5, ref.value + 6, 5);
    no_sep[10] = '\0';

    /* Lower-case */
    for (int i = 0; i < 10; i++) {
        char c = no_sep[i];
        if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
        no_sep[i] = c;
    }

    int rc = aethermesh_tag_parse(no_sep, &parsed);
    assert(rc == 0);
    assert(memcmp(ref.value, parsed.value, AETHERMESH_TAG_LENGTH) == 0);
}

static void parse_accepts_mixed_case(void)
{
    aethermesh_tag_t ref, parsed;
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &ref);

    /* Alternate upper/lower per character */
    char mixed[AETHERMESH_TAG_LENGTH];
    for (int i = 0; i < 11; i++) {
        char c = ref.value[i];
        if (i % 2 == 0 && c >= 'A' && c <= 'Z')
            c = (char)(c - 'A' + 'a');
        mixed[i] = c;
    }
    mixed[11] = '\0';

    int rc = aethermesh_tag_parse(mixed, &parsed);
    assert(rc == 0);
    assert(memcmp(ref.value, parsed.value, AETHERMESH_TAG_LENGTH) == 0);
}

/* ── 5. parse() rejects invalid inputs ───────────────────────────────────── */

static void parse_rejects_null_input(void)
{
    aethermesh_tag_t out;
    int rc = aethermesh_tag_parse(NULL, &out);
    assert(rc == -1);
}

static void parse_rejects_null_out(void)
{
    int rc = aethermesh_tag_parse("ABCDE-FGHJK", NULL);
    assert(rc == -1);
}

static void parse_rejects_wrong_length_short(void)
{
    aethermesh_tag_t out;
    int rc = aethermesh_tag_parse("ABCD", &out);
    assert(rc == -1);
}

static void parse_rejects_wrong_length_long(void)
{
    aethermesh_tag_t out;
    int rc = aethermesh_tag_parse("ABCDE-FGHJK0", &out); /* 12 chars */
    assert(rc == -1);
}

static void parse_rejects_invalid_chars(void)
{
    aethermesh_tag_t out;
    /* 'I' is not in the Crockford alphabet */
    int rc = aethermesh_tag_parse("IABCD-EFGHJ", &out);
    assert(rc == -1);
}

static void parse_rejects_invalid_chars_no_sep(void)
{
    aethermesh_tag_t out;
    /* 'O' is not in the Crockford alphabet */
    int rc = aethermesh_tag_parse("OABCDEFGHJ", &out);
    assert(rc == -1);
}

static void parse_rejects_wrong_separator_position(void)
{
    aethermesh_tag_t out;
    /* Separator in wrong position */
    int rc = aethermesh_tag_parse("ABCD-EFGHJ0", &out);
    assert(rc == -1);
}

/* ── 6. Different keys produce different tags ────────────────────────────── */

static void different_keys_produce_different_tags(void)
{
    aethermesh_tag_t tag1, tag2;
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &tag1);
    aethermesh_tag_from_public_key(OTHER_KEY, 32, &tag2);

    assert(memcmp(tag1.value, tag2.value, AETHERMESH_TAG_LENGTH) != 0);

    printf(" [tag1=%s tag2=%s]", tag1.value, tag2.value);
}

/* ── 7. aethermesh_tag_is_valid() ────────────────────────────────────────────── */

static void is_valid_returns_1_for_derived_tag(void)
{
    aethermesh_tag_t tag;
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &tag);
    assert(aethermesh_tag_is_valid(&tag) == 1);
}

static void is_valid_returns_0_for_empty(void)
{
    aethermesh_tag_t tag;
    memset(&tag, 0, sizeof(tag));
    assert(aethermesh_tag_is_valid(&tag) == 0);
}

static void is_valid_returns_0_for_null(void)
{
    assert(aethermesh_tag_is_valid(NULL) == 0);
}

/* ── 8. from_public_key() error handling ─────────────────────────────────── */

static void from_public_key_rejects_null_key(void)
{
    aethermesh_tag_t out;
    int rc = aethermesh_tag_from_public_key(NULL, 32, &out);
    assert(rc == -1);
}

static void from_public_key_rejects_wrong_length(void)
{
    aethermesh_tag_t out;
    int rc = aethermesh_tag_from_public_key(FIXED_KEY, 16, &out);
    assert(rc == -1);
}

static void from_public_key_rejects_null_out(void)
{
    int rc = aethermesh_tag_from_public_key(FIXED_KEY, 32, NULL);
    assert(rc == -1);
}

/* ── 9. Determinism: same key always yields same tag ────────────────────── */

static void same_key_always_yields_same_tag(void)
{
    aethermesh_tag_t a, b, c;
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &a);
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &b);
    aethermesh_tag_from_public_key(FIXED_KEY, 32, &c);

    assert(memcmp(a.value, b.value, AETHERMESH_TAG_LENGTH) == 0);
    assert(memcmp(b.value, c.value, AETHERMESH_TAG_LENGTH) == 0);
}

/* ── main ────────────────────────────────────────────────────────────────── */

int main(void)
{
    printf("=== AetherMeshTag tests ===\n");

    /* Format */
    RUN(known_vector_format_is_xxxxx_dash_xxxxx);

    /* Round-trip */
    RUN(roundtrip_from_public_key_then_parse);

    /* verify() */
    RUN(verify_correct_key_returns_1);
    RUN(verify_wrong_key_returns_0);

    /* parse() accepts */
    RUN(parse_accepts_canonical_with_separator);
    RUN(parse_accepts_no_separator);
    RUN(parse_accepts_lowercase_with_separator);
    RUN(parse_accepts_lowercase_no_separator);
    RUN(parse_accepts_mixed_case);

    /* parse() rejects */
    RUN(parse_rejects_null_input);
    RUN(parse_rejects_null_out);
    RUN(parse_rejects_wrong_length_short);
    RUN(parse_rejects_wrong_length_long);
    RUN(parse_rejects_invalid_chars);
    RUN(parse_rejects_invalid_chars_no_sep);
    RUN(parse_rejects_wrong_separator_position);

    /* Different keys → different tags */
    RUN(different_keys_produce_different_tags);

    /* is_valid() */
    RUN(is_valid_returns_1_for_derived_tag);
    RUN(is_valid_returns_0_for_empty);
    RUN(is_valid_returns_0_for_null);

    /* from_public_key() error handling */
    RUN(from_public_key_rejects_null_key);
    RUN(from_public_key_rejects_wrong_length);
    RUN(from_public_key_rejects_null_out);

    /* Determinism */
    RUN(same_key_always_yields_same_tag);

    printf("\nAll %d tests passed.\n", tests_run);
    return 0;
}
