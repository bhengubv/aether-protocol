// SPDX-License-Identifier: MIT
// Unit tests for aethernet_uri.c
//
// Covers parse/round-trip/equality/builder/manifest/router. The valid +
// invalid + manifest cases mirror the entries in
// tests/cross-language/uri-fixtures.json so this binary is part of the
// cross-language conformance contract.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/aethernet_uri.h"

/* ── Test runner ─────────────────────────────────────────────────────────── */

#define RUN(name) do {                              \
    printf("TEST: " #name "...");                   \
    name();                                         \
    printf(" OK\n");                                \
    tests_run++;                                    \
} while (0)

static int tests_run = 0;

/* ── Parse: happy paths ──────────────────────────────────────────────────── */

static void parse_authority_only(void)
{
    char err[128] = {0};
    aethernet_uri_t *u = aethernet_uri_parse("aether://KXJB7-MN2P4", err, sizeof(err));
    assert(u);
    assert(strcmp(aethernet_uri_authority(u), "KXJB7-MN2P4") == 0);
    assert(strcmp(aethernet_uri_path(u), "") == 0);
    assert(strcmp(aethernet_uri_fragment(u), "") == 0);
    assert(strcmp(aethernet_uri_handler_name(u), "") == 0);
    assert(aethernet_uri_path_segment_count(u) == 0);
    assert(aethernet_uri_query_count(u) == 0);
    aethernet_uri_free(u);
}

static void parse_authority_no_dash_canonicalises(void)
{
    aethernet_uri_t *u = aethernet_uri_parse("aether://KXJB7MN2P4", NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_authority(u), "KXJB7-MN2P4") == 0);
    aethernet_uri_free(u);
}

static void parse_authority_lowercase_canonicalises(void)
{
    aethernet_uri_t *u = aethernet_uri_parse("aether://kxjb7-mn2p4", NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_authority(u), "KXJB7-MN2P4") == 0);
    aethernet_uri_free(u);
}

static void parse_scheme_case_insensitive(void)
{
    aethernet_uri_t *u = aethernet_uri_parse("AETHER://KXJB7-MN2P4/profile", NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_authority(u), "KXJB7-MN2P4") == 0);
    assert(strcmp(aethernet_uri_handler_name(u), "profile") == 0);
    aethernet_uri_free(u);
}

static void parse_single_segment_path(void)
{
    aethernet_uri_t *u = aethernet_uri_parse("aether://KXJB7-MN2P4/profile", NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_path(u), "profile") == 0);
    assert(strcmp(aethernet_uri_handler_name(u), "profile") == 0);
    assert(aethernet_uri_path_segment_count(u) == 1);
    assert(strcmp(aethernet_uri_path_segment(u, 0), "profile") == 0);
    aethernet_uri_free(u);
}

static void parse_two_segment_path(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/content/sha256-abc123", NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_path(u), "content/sha256-abc123") == 0);
    assert(strcmp(aethernet_uri_handler_name(u), "content") == 0);
    assert(aethernet_uri_path_segment_count(u) == 2);
    assert(strcmp(aethernet_uri_path_segment(u, 0), "content") == 0);
    assert(strcmp(aethernet_uri_path_segment(u, 1), "sha256-abc123") == 0);
    aethernet_uri_free(u);
}

static void parse_with_query(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/content/abc?codec=opus&bitrate=128", NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_query_get(u, "codec"), "opus") == 0);
    assert(strcmp(aethernet_uri_query_get(u, "bitrate"), "128") == 0);
    /* Case-insensitive */
    assert(strcmp(aethernet_uri_query_get(u, "CODEC"), "opus") == 0);
    assert(strcmp(aethernet_uri_query_get(u, "Codec"), "opus") == 0);
    assert(aethernet_uri_query_count(u) == 2);
    aethernet_uri_free(u);
}

static void parse_with_fragment(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/stream/live#t=1m30s", NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_fragment(u), "t=1m30s") == 0);
    aethernet_uri_free(u);
}

static void parse_query_and_fragment(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/x?a=b#frag", NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_query_get(u, "a"), "b") == 0);
    assert(strcmp(aethernet_uri_fragment(u), "frag") == 0);
    aethernet_uri_free(u);
}

static void parse_flag_query(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/x?flag", NULL, 0);
    assert(u);
    const char *v = aethernet_uri_query_get(u, "flag");
    assert(v && *v == '\0');
    aethernet_uri_free(u);
}

static void parse_uhid_64hex(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/inbox",
        NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_authority(u),
                  "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA") == 0);
    assert(strcmp(aethernet_uri_handler_name(u), "inbox") == 0);
    aethernet_uri_free(u);
}

static void parse_percent_encoded_query_space(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/inbox?title=hello%20world", NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_query_get(u, "title"), "hello world") == 0);
    aethernet_uri_free(u);
}

static void parse_percent_encoded_path_segment(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/inbox/Hello%20World", NULL, 0);
    assert(u);
    assert(aethernet_uri_path_segment_count(u) == 2);
    assert(strcmp(aethernet_uri_path_segment(u, 0), "inbox") == 0);
    assert(strcmp(aethernet_uri_path_segment(u, 1), "Hello World") == 0);
    aethernet_uri_free(u);
}

static void parse_percent_encoded_utf8(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/inbox?title=caf%C3%A9", NULL, 0);
    assert(u);
    /* "café" = c,a,f,é (UTF-8: 0x63,0x61,0x66,0xC3,0xA9) */
    const char *v = aethernet_uri_query_get(u, "title");
    assert(v);
    assert((unsigned char)v[0] == 0x63);
    assert((unsigned char)v[1] == 0x61);
    assert((unsigned char)v[2] == 0x66);
    assert((unsigned char)v[3] == 0xC3);
    assert((unsigned char)v[4] == 0xA9);
    assert(v[5] == '\0');
    aethernet_uri_free(u);
}

static void parse_fragment_with_equals(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/x#t=1m30s", NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_fragment(u), "t=1m30s") == 0);
    aethernet_uri_free(u);
}

/* ── Parse: failure paths ────────────────────────────────────────────────── */

static void parse_rejects_empty(void)
{
    char err[64] = {0};
    aethernet_uri_t *u = aethernet_uri_parse("", err, sizeof(err));
    assert(!u);
    assert(err[0] != '\0');
}

static void parse_rejects_null(void)
{
    char err[64] = {0};
    aethernet_uri_t *u = aethernet_uri_parse(NULL, err, sizeof(err));
    assert(!u);
    assert(err[0] != '\0');
}

static void parse_rejects_wrong_scheme(void)
{
    aethernet_uri_t *u = aethernet_uri_parse("http://KXJB7-MN2P4/", NULL, 0);
    assert(!u);
}

static void parse_rejects_missing_slashslash(void)
{
    aethernet_uri_t *u = aethernet_uri_parse("aether:KXJB7-MN2P4", NULL, 0);
    assert(!u);
}

static void parse_rejects_single_slash(void)
{
    aethernet_uri_t *u = aethernet_uri_parse("aether:/KXJB7-MN2P4", NULL, 0);
    assert(!u);
}

static void parse_rejects_empty_authority(void)
{
    aethernet_uri_t *u = aethernet_uri_parse("aether:///profile", NULL, 0);
    assert(!u);
}

static void parse_rejects_non_crockford(void)
{
    /* 'I' is not a Crockford char; also length 13 disqualifies as UHID. */
    aethernet_uri_t *u = aethernet_uri_parse("aether://INVALID-AUTH1/x", NULL, 0);
    assert(!u);
}

static void parse_rejects_too_short_authority(void)
{
    aethernet_uri_t *u = aethernet_uri_parse("aether://ABC", NULL, 0);
    assert(!u);
}

static void parse_rejects_consecutive_slashes(void)
{
    aethernet_uri_t *u = aethernet_uri_parse("aether://KXJB7-MN2P4/a//b", NULL, 0);
    assert(!u);
}

static void parse_rejects_illegal_path_char(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/has space", NULL, 0);
    assert(!u);
}

static void parse_rejects_malformed_pct(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/inbox/%2", NULL, 0);
    assert(!u);
}

static void parse_rejects_empty_query_key(void)
{
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/x?=value", NULL, 0);
    assert(!u);
}

/* ── Canonical round-trip ────────────────────────────────────────────────── */

static void roundtrip_canonical_stable(const char *input, const char *expect)
{
    aethernet_uri_t *u = aethernet_uri_parse(input, NULL, 0);
    assert(u);
    char *rendered = aethernet_uri_to_string(u);
    assert(rendered);
    assert(strcmp(rendered, expect) == 0);
    aethernet_uri_t *u2 = aethernet_uri_parse(rendered, NULL, 0);
    assert(u2);
    char *rendered2 = aethernet_uri_to_string(u2);
    assert(rendered2);
    assert(strcmp(rendered, rendered2) == 0);
    aethernet_uri_free_string(rendered);
    aethernet_uri_free_string(rendered2);
    aethernet_uri_free(u);
    aethernet_uri_free(u2);
}

static void roundtrip_authority_only(void)
{
    roundtrip_canonical_stable("aether://KXJB7-MN2P4", "aether://KXJB7-MN2P4");
}

static void roundtrip_with_dash_strip(void)
{
    roundtrip_canonical_stable("aether://KXJB7MN2P4", "aether://KXJB7-MN2P4");
}

static void roundtrip_lower_authority(void)
{
    roundtrip_canonical_stable("aether://kxjb7-mn2p4", "aether://KXJB7-MN2P4");
}

static void roundtrip_with_path(void)
{
    roundtrip_canonical_stable("aether://KXJB7-MN2P4/profile",
                               "aether://KXJB7-MN2P4/profile");
}

static void roundtrip_with_query(void)
{
    roundtrip_canonical_stable(
        "aether://KXJB7-MN2P4/content/abc?codec=opus&bitrate=128",
        "aether://KXJB7-MN2P4/content/abc?codec=opus&bitrate=128");
}

static void roundtrip_with_fragment(void)
{
    roundtrip_canonical_stable(
        "aether://KXJB7-MN2P4/stream/live#t=1m30s",
        "aether://KXJB7-MN2P4/stream/live#t=1m30s");
}

static void roundtrip_percent_encoded_space(void)
{
    roundtrip_canonical_stable(
        "aether://KXJB7-MN2P4/inbox?title=hello%20world",
        "aether://KXJB7-MN2P4/inbox?title=hello%20world");
}

static void roundtrip_percent_encoded_path(void)
{
    roundtrip_canonical_stable(
        "aether://KXJB7-MN2P4/inbox/Hello%20World",
        "aether://KXJB7-MN2P4/inbox/Hello%20World");
}

static void roundtrip_utf8(void)
{
    roundtrip_canonical_stable(
        "aether://KXJB7-MN2P4/inbox?title=caf%C3%A9",
        "aether://KXJB7-MN2P4/inbox?title=caf%C3%A9");
}

static void roundtrip_uhid(void)
{
    roundtrip_canonical_stable(
        "aether://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/inbox",
        "aether://AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/inbox");
}

/* ── Equality ────────────────────────────────────────────────────────────── */

static void equals_same_content(void)
{
    aethernet_uri_t *a = aethernet_uri_parse("aether://KXJB7-MN2P4/x?k=v", NULL, 0);
    aethernet_uri_t *b = aethernet_uri_parse("aether://KXJB7-MN2P4/x?k=v", NULL, 0);
    assert(aethernet_uri_equals(a, b));
    aethernet_uri_free(a);
    aethernet_uri_free(b);
}

static void equals_query_order_insensitive(void)
{
    aethernet_uri_t *a = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/x?a=1&b=2", NULL, 0);
    aethernet_uri_t *b = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/x?b=2&a=1", NULL, 0);
    assert(aethernet_uri_equals(a, b));
    aethernet_uri_free(a);
    aethernet_uri_free(b);
}

static void equals_different_authority(void)
{
    aethernet_uri_t *a = aethernet_uri_parse("aether://KXJB7-MN2P4/x", NULL, 0);
    aethernet_uri_t *b = aethernet_uri_parse("aether://KXJB7-MN2P5/x", NULL, 0);
    assert(!aethernet_uri_equals(a, b));
    aethernet_uri_free(a);
    aethernet_uri_free(b);
}

static void equals_self_and_null(void)
{
    aethernet_uri_t *a = aethernet_uri_parse("aether://KXJB7-MN2P4/x", NULL, 0);
    assert(aethernet_uri_equals(a, a));
    assert(!aethernet_uri_equals(a, NULL));
    assert(!aethernet_uri_equals(NULL, a));
    assert(aethernet_uri_equals(NULL, NULL));
    aethernet_uri_free(a);
}

/* ── Builder ─────────────────────────────────────────────────────────────── */

static void builder_basic(void)
{
    aethernet_uri_builder_t *b = aethernet_uri_builder_new();
    assert(b);
    aethernet_uri_builder_authority(b, "KXJB7-MN2P4");
    aethernet_uri_builder_path(b, "content/sha256-abc123");
    aethernet_uri_builder_query(b, "codec", "opus");
    aethernet_uri_builder_fragment(b, "t=1m30s");
    char err[128] = {0};
    aethernet_uri_t *u = aethernet_uri_builder_build(b, err, sizeof(err));
    assert(u);
    char *s = aethernet_uri_to_string(u);
    assert(s);
    assert(strcmp(s,
        "aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus#t=1m30s") == 0);
    aethernet_uri_free_string(s);
    aethernet_uri_free(u);
    aethernet_uri_builder_free(b);
}

static void builder_encodes_spaces(void)
{
    aethernet_uri_builder_t *b = aethernet_uri_builder_new();
    aethernet_uri_builder_authority(b, "KXJB7-MN2P4");
    aethernet_uri_builder_path(b, "inbox");
    aethernet_uri_builder_query(b, "title", "hello world");
    aethernet_uri_t *u = aethernet_uri_builder_build(b, NULL, 0);
    assert(u);
    char *s = aethernet_uri_to_string(u);
    assert(s);
    assert(strstr(s, "hello%20world") != NULL);
    aethernet_uri_free_string(s);
    aethernet_uri_free(u);
    aethernet_uri_builder_free(b);
}

static void builder_append_segment(void)
{
    aethernet_uri_builder_t *b = aethernet_uri_builder_new();
    aethernet_uri_builder_authority(b, "KXJB7-MN2P4");
    aethernet_uri_builder_append_segment(b, "watch");
    aethernet_uri_builder_append_segment(b, "sess-99");
    aethernet_uri_builder_append_segment(b, "join");
    aethernet_uri_t *u = aethernet_uri_builder_build(b, NULL, 0);
    assert(u);
    assert(strcmp(aethernet_uri_path(u), "watch/sess-99/join") == 0);
    aethernet_uri_free(u);
    aethernet_uri_builder_free(b);
}

static void builder_remove_query(void)
{
    aethernet_uri_builder_t *b = aethernet_uri_builder_new();
    aethernet_uri_builder_authority(b, "KXJB7-MN2P4");
    aethernet_uri_builder_path(b, "x");
    aethernet_uri_builder_query(b, "a", "1");
    aethernet_uri_builder_query(b, "b", "2");
    aethernet_uri_builder_remove_query(b, "A");  /* CI key */
    aethernet_uri_t *u = aethernet_uri_builder_build(b, NULL, 0);
    assert(u);
    assert(aethernet_uri_query_count(u) == 1);
    assert(aethernet_uri_query_get(u, "b"));
    assert(!aethernet_uri_query_get(u, "a"));
    aethernet_uri_free(u);
    aethernet_uri_builder_free(b);
}

static void builder_rejects_no_authority(void)
{
    aethernet_uri_builder_t *b = aethernet_uri_builder_new();
    aethernet_uri_builder_path(b, "x");
    char err[64] = {0};
    aethernet_uri_t *u = aethernet_uri_builder_build(b, err, sizeof(err));
    assert(!u);
    assert(err[0] != '\0');
    aethernet_uri_builder_free(b);
}

static void builder_rejects_bad_authority(void)
{
    aethernet_uri_builder_t *b = aethernet_uri_builder_new();
    aethernet_uri_builder_authority(b, "ZZZZ");  /* too short */
    char err[64] = {0};
    aethernet_uri_t *u = aethernet_uri_builder_build(b, err, sizeof(err));
    assert(!u);
    aethernet_uri_builder_free(b);
}

/* ── Manifest resolve ────────────────────────────────────────────────────── */

static aethernet_uri_handler_manifest_t *build_media_manifest(void)
{
    aethernet_uri_handler_manifest_t *m =
        aethernet_uri_handler_manifest_new("aether.media");
    assert(m);

    aethernet_uri_handler_descriptor_t *d;

    d = aethernet_uri_handler_descriptor_new("profile", "", "Get profile.");
    assert(d);
    assert(aethernet_uri_handler_manifest_add(m, d) == 0);

    d = aethernet_uri_handler_descriptor_new("profile", "avatar", "Get avatar.");
    assert(d);
    assert(aethernet_uri_handler_manifest_add(m, d) == 0);

    d = aethernet_uri_handler_descriptor_new("content", "{hash}", "Fetch content.");
    assert(d);
    assert(aethernet_uri_handler_manifest_add(m, d) == 0);

    d = aethernet_uri_handler_descriptor_new("watch", "{sessionId}/join", "Join watch party.");
    assert(d);
    assert(aethernet_uri_handler_manifest_add(m, d) == 0);

    return m;
}

static void manifest_resolves_profile(void)
{
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/profile", NULL, 0);
    assert(u);
    const char *keys[8];
    const char *vals[8];
    size_t cap_count = 0;
    int idx = aethernet_uri_handler_manifest_resolve(m, u, keys, vals, 8, &cap_count);
    assert(idx == 0);
    assert(cap_count == 0);
    aethernet_uri_free(u);
    aethernet_uri_handler_manifest_free(m);
}

static void manifest_resolves_profile_avatar(void)
{
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/profile/avatar", NULL, 0);
    assert(u);
    const char *keys[8]; const char *vals[8]; size_t cap_count = 0;
    int idx = aethernet_uri_handler_manifest_resolve(m, u, keys, vals, 8, &cap_count);
    assert(idx == 1);
    assert(cap_count == 0);
    aethernet_uri_free(u);
    aethernet_uri_handler_manifest_free(m);
}

static void manifest_resolves_content_with_capture(void)
{
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/content/sha256-abc", NULL, 0);
    assert(u);
    const char *keys[8]; const char *vals[8]; size_t cap_count = 0;
    int idx = aethernet_uri_handler_manifest_resolve(m, u, keys, vals, 8, &cap_count);
    assert(idx == 2);
    assert(cap_count == 1);
    assert(strcmp(keys[0], "hash") == 0);
    assert(strcmp(vals[0], "sha256-abc") == 0);
    aethernet_uri_free(u);
    aethernet_uri_handler_manifest_free(m);
}

static void manifest_resolves_watch_with_capture(void)
{
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/watch/sess-99/join", NULL, 0);
    assert(u);
    const char *keys[8]; const char *vals[8]; size_t cap_count = 0;
    int idx = aethernet_uri_handler_manifest_resolve(m, u, keys, vals, 8, &cap_count);
    assert(idx == 3);
    assert(cap_count == 1);
    assert(strcmp(keys[0], "sessionId") == 0);
    assert(strcmp(vals[0], "sess-99") == 0);
    aethernet_uri_free(u);
    aethernet_uri_handler_manifest_free(m);
}

static void manifest_no_match_unknown_handler(void)
{
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/unknown", NULL, 0);
    assert(u);
    const char *keys[8]; const char *vals[8]; size_t cap_count = 0;
    int idx = aethernet_uri_handler_manifest_resolve(m, u, keys, vals, 8, &cap_count);
    assert(idx == -1);
    aethernet_uri_free(u);
    aethernet_uri_handler_manifest_free(m);
}

static void manifest_no_match_partial_template(void)
{
    /* Template "watch/{sessionId}/join" — passing only "watch/sess-99" must
     * NOT match (the trailing "/join" literal segment is required). */
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/watch/sess-99", NULL, 0);
    assert(u);
    const char *keys[8]; const char *vals[8]; size_t cap_count = 0;
    int idx = aethernet_uri_handler_manifest_resolve(m, u, keys, vals, 8, &cap_count);
    assert(idx == -1);
    aethernet_uri_free(u);
    aethernet_uri_handler_manifest_free(m);
}

static void manifest_rejects_bad_app_id(void)
{
    assert(aethernet_uri_handler_manifest_new(NULL) == NULL);
    assert(aethernet_uri_handler_manifest_new("") == NULL);
    assert(aethernet_uri_handler_manifest_new("   ") == NULL);
}

static void descriptor_rejects_empty_name(void)
{
    assert(aethernet_uri_handler_descriptor_new(NULL, "", "") == NULL);
    assert(aethernet_uri_handler_descriptor_new("", "", "") == NULL);
}

/* ── Router ──────────────────────────────────────────────────────────────── */

typedef struct {
    int   invoked;
    char  captured_hash[64];
    char  captured_session[64];
    int   handler_index;
} router_state_t;

static int profile_handler(const aethernet_uri_t *uri,
                           const aethernet_uri_handler_descriptor_t *handler,
                           const char **capture_keys,
                           const char **capture_values,
                           size_t capture_count,
                           void *user_data)
{
    (void)uri; (void)handler; (void)capture_keys; (void)capture_values;
    router_state_t *s = (router_state_t *)user_data;
    s->invoked++;
    s->handler_index = 0;
    assert(capture_count == 0);
    return 42;
}

static int content_handler(const aethernet_uri_t *uri,
                           const aethernet_uri_handler_descriptor_t *handler,
                           const char **capture_keys,
                           const char **capture_values,
                           size_t capture_count,
                           void *user_data)
{
    (void)uri; (void)handler;
    router_state_t *s = (router_state_t *)user_data;
    s->invoked++;
    s->handler_index = 2;
    assert(capture_count == 1);
    assert(strcmp(capture_keys[0], "hash") == 0);
    snprintf(s->captured_hash, sizeof(s->captured_hash), "%s", capture_values[0]);
    return 0;
}

static int watch_handler(const aethernet_uri_t *uri,
                         const aethernet_uri_handler_descriptor_t *handler,
                         const char **capture_keys,
                         const char **capture_values,
                         size_t capture_count,
                         void *user_data)
{
    (void)uri; (void)handler;
    router_state_t *s = (router_state_t *)user_data;
    s->invoked++;
    s->handler_index = 3;
    assert(capture_count == 1);
    assert(strcmp(capture_keys[0], "sessionId") == 0);
    snprintf(s->captured_session, sizeof(s->captured_session), "%s", capture_values[0]);
    return 0;
}

static void router_dispatches_profile(void)
{
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_router_t *r = aethernet_uri_router_new(m);
    assert(r);
    router_state_t st = {0};
    assert(aethernet_uri_router_register(r, 0, profile_handler, &st) == 0);

    aethernet_uri_t *u = aethernet_uri_parse(
        "aether://KXJB7-MN2P4/profile", NULL, 0);
    int rc = aethernet_uri_router_dispatch(r, u);
    assert(rc == 42);
    assert(st.invoked == 1);
    assert(st.handler_index == 0);

    aethernet_uri_free(u);
    aethernet_uri_router_free(r);
    aethernet_uri_handler_manifest_free(m);
}

static void router_dispatches_content_with_capture(void)
{
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_router_t *r = aethernet_uri_router_new(m);
    router_state_t st = {0};
    assert(aethernet_uri_router_register(r, 2, content_handler, &st) == 0);

    int rc = aethernet_uri_router_dispatch_string(
        r, "aether://KXJB7-MN2P4/content/sha256-abc", NULL, 0);
    assert(rc == 0);
    assert(st.invoked == 1);
    assert(strcmp(st.captured_hash, "sha256-abc") == 0);

    aethernet_uri_router_free(r);
    aethernet_uri_handler_manifest_free(m);
}

static void router_dispatches_watch(void)
{
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_router_t *r = aethernet_uri_router_new(m);
    router_state_t st = {0};
    assert(aethernet_uri_router_register(r, 3, watch_handler, &st) == 0);

    int rc = aethernet_uri_router_dispatch_string(
        r, "aether://KXJB7-MN2P4/watch/sess-99/join", NULL, 0);
    assert(rc == 0);
    assert(st.invoked == 1);
    assert(strcmp(st.captured_session, "sess-99") == 0);

    aethernet_uri_router_free(r);
    aethernet_uri_handler_manifest_free(m);
}

static void router_no_match_returns_neg1(void)
{
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_router_t *r = aethernet_uri_router_new(m);
    router_state_t st = {0};
    assert(aethernet_uri_router_register(r, 0, profile_handler, &st) == 0);

    int rc = aethernet_uri_router_dispatch_string(
        r, "aether://KXJB7-MN2P4/unknown", NULL, 0);
    assert(rc == -1);
    assert(st.invoked == 0);

    aethernet_uri_router_free(r);
    aethernet_uri_handler_manifest_free(m);
}

static void router_no_callback_returns_neg1(void)
{
    /* Match found but no callback registered → -1 */
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_router_t *r = aethernet_uri_router_new(m);
    int rc = aethernet_uri_router_dispatch_string(
        r, "aether://KXJB7-MN2P4/profile", NULL, 0);
    assert(rc == -1);
    aethernet_uri_router_free(r);
    aethernet_uri_handler_manifest_free(m);
}

static void router_parse_failure_returns_neg3(void)
{
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_router_t *r = aethernet_uri_router_new(m);
    char err[128] = {0};
    int rc = aethernet_uri_router_dispatch_string(r, "not a uri", err, sizeof(err));
    assert(rc == -3);
    assert(err[0] != '\0');
    aethernet_uri_router_free(r);
    aethernet_uri_handler_manifest_free(m);
}

static void router_null_inputs(void)
{
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_router_t *r = aethernet_uri_router_new(m);
    assert(aethernet_uri_router_dispatch(NULL, NULL) == -2);
    assert(aethernet_uri_router_dispatch(r, NULL) == -2);
    /* Bad handler index rejected */
    assert(aethernet_uri_router_register(r, 99, profile_handler, NULL) == -1);
    assert(aethernet_uri_router_register(r, -1, profile_handler, NULL) == -1);
    assert(aethernet_uri_router_register(r, 0, NULL, NULL) == -1);
    aethernet_uri_router_free(r);
    aethernet_uri_handler_manifest_free(m);
}

static void router_register_replaces(void)
{
    /* Re-registering the same index should replace the callback. */
    aethernet_uri_handler_manifest_t *m = build_media_manifest();
    aethernet_uri_router_t *r = aethernet_uri_router_new(m);
    router_state_t st = {0};
    assert(aethernet_uri_router_register(r, 0, profile_handler, &st) == 0);
    assert(aethernet_uri_router_register(r, 0, profile_handler, &st) == 0);
    int rc = aethernet_uri_router_dispatch_string(
        r, "aether://KXJB7-MN2P4/profile", NULL, 0);
    assert(rc == 42);
    assert(st.invoked == 1); /* not double-invoked */
    aethernet_uri_router_free(r);
    aethernet_uri_handler_manifest_free(m);
}

/* ── main ────────────────────────────────────────────────────────────────── */

int main(void)
{
    printf("=== AetherNet URI tests ===\n");

    /* Parse: happy paths */
    RUN(parse_authority_only);
    RUN(parse_authority_no_dash_canonicalises);
    RUN(parse_authority_lowercase_canonicalises);
    RUN(parse_scheme_case_insensitive);
    RUN(parse_single_segment_path);
    RUN(parse_two_segment_path);
    RUN(parse_with_query);
    RUN(parse_with_fragment);
    RUN(parse_query_and_fragment);
    RUN(parse_flag_query);
    RUN(parse_uhid_64hex);
    RUN(parse_percent_encoded_query_space);
    RUN(parse_percent_encoded_path_segment);
    RUN(parse_percent_encoded_utf8);
    RUN(parse_fragment_with_equals);

    /* Parse: failure paths */
    RUN(parse_rejects_empty);
    RUN(parse_rejects_null);
    RUN(parse_rejects_wrong_scheme);
    RUN(parse_rejects_missing_slashslash);
    RUN(parse_rejects_single_slash);
    RUN(parse_rejects_empty_authority);
    RUN(parse_rejects_non_crockford);
    RUN(parse_rejects_too_short_authority);
    RUN(parse_rejects_consecutive_slashes);
    RUN(parse_rejects_illegal_path_char);
    RUN(parse_rejects_malformed_pct);
    RUN(parse_rejects_empty_query_key);

    /* Round-trip */
    RUN(roundtrip_authority_only);
    RUN(roundtrip_with_dash_strip);
    RUN(roundtrip_lower_authority);
    RUN(roundtrip_with_path);
    RUN(roundtrip_with_query);
    RUN(roundtrip_with_fragment);
    RUN(roundtrip_percent_encoded_space);
    RUN(roundtrip_percent_encoded_path);
    RUN(roundtrip_utf8);
    RUN(roundtrip_uhid);

    /* Equality */
    RUN(equals_same_content);
    RUN(equals_query_order_insensitive);
    RUN(equals_different_authority);
    RUN(equals_self_and_null);

    /* Builder */
    RUN(builder_basic);
    RUN(builder_encodes_spaces);
    RUN(builder_append_segment);
    RUN(builder_remove_query);
    RUN(builder_rejects_no_authority);
    RUN(builder_rejects_bad_authority);

    /* Manifest */
    RUN(manifest_resolves_profile);
    RUN(manifest_resolves_profile_avatar);
    RUN(manifest_resolves_content_with_capture);
    RUN(manifest_resolves_watch_with_capture);
    RUN(manifest_no_match_unknown_handler);
    RUN(manifest_no_match_partial_template);
    RUN(manifest_rejects_bad_app_id);
    RUN(descriptor_rejects_empty_name);

    /* Router */
    RUN(router_dispatches_profile);
    RUN(router_dispatches_content_with_capture);
    RUN(router_dispatches_watch);
    RUN(router_no_match_returns_neg1);
    RUN(router_no_callback_returns_neg1);
    RUN(router_parse_failure_returns_neg3);
    RUN(router_null_inputs);
    RUN(router_register_replaces);

    printf("\nAll %d tests passed.\n", tests_run);
    return 0;
}
