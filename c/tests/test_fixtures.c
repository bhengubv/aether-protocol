// SPDX-License-Identifier: MIT
//
// Cross-language wire-format fixture verifier. Reads ../../fixtures/inputs.json
// and ../../fixtures/expected/<name>.bin and asserts that this language's
// PacketSerializer produces byte-identical output for each canonical input.
// See fixtures/README.md.
//
// JSON parsing here is a tiny hand-rolled extractor — the schema is a flat
// array of flat objects, so we can read it without a JSON library.

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aether/constants.h"
#include "aether/protocol.h"

typedef struct {
    char *name;
    char *id;            // canonical UUID string
    int type;
    char *source_uhid;
    char *destination_uhid;
    int32_t ttl;
    int priority;
    uint8_t *payload;       size_t payload_len;
    uint8_t *packet_nonce;  size_t packet_nonce_len;
    uint8_t *signature;     size_t signature_len;
    int64_t timestamp_ms;
    int protocol_version;
} fixture_input_t;

static char *str_dup_c(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    memcpy(out, s, n);
    return out;
}

// Parses a hex string into a freshly allocated byte buffer. Returns NULL with
// *out_len = 0 for empty input. Caller frees.
static uint8_t *hex_decode(const char *hex, size_t *out_len) {
    size_t n = strlen(hex);
    *out_len = n / 2;
    if (*out_len == 0) return NULL;
    uint8_t *out = (uint8_t *)malloc(*out_len);
    for (size_t i = 0; i < *out_len; i++) {
        char b[3] = { hex[i * 2], hex[i * 2 + 1], 0 };
        out[i] = (uint8_t)strtoul(b, NULL, 16);
    }
    return out;
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
    out[n] = 0;
    return out;
}

static long long json_num_field(const char *obj, const char *key) {
    char needle[64];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = strstr(obj, needle);
    if (!p) return 0;
    p += strlen(needle);
    while (*p && (*p == ' ' || *p == '\t')) p++;
    return strtoll(p, NULL, 10);
}

static void parse_uuid(const char *s, uint8_t out[16]) {
    int n = 0;
    for (int i = 0; s[i] && n < 16; i++) {
        if (s[i] == '-') continue;
        char b[3] = { s[i], s[i + 1], 0 };
        out[n++] = (uint8_t)strtoul(b, NULL, 16);
        i++;
    }
}

static char *read_file(const char *path, size_t *out_len) {
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    char *buf = (char *)malloc((size_t)sz + 1);
    fread(buf, 1, (size_t)sz, f);
    buf[sz] = 0;
    fclose(f);
    if (out_len) *out_len = (size_t)sz;
    return buf;
}

// Splits the inputs.json array into individual object substrings and parses each.
// Returns count and writes a malloc'd array via *out.
static int parse_inputs(const char *json, fixture_input_t **out) {
    int count = 0;
    int cap = 16;
    fixture_input_t *items = (fixture_input_t *)calloc((size_t)cap, sizeof(fixture_input_t));

    const char *p = strchr(json, '[');
    if (!p) return 0;
    p++;
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
        obj[obj_len] = 0;

        if (count == cap) {
            cap *= 2;
            items = (fixture_input_t *)realloc(items, (size_t)cap * sizeof(fixture_input_t));
        }
        fixture_input_t *fi = &items[count++];
        memset(fi, 0, sizeof(*fi));
        fi->name = json_str_field(obj, "name");
        fi->id = json_str_field(obj, "id");
        fi->type = (int)json_num_field(obj, "type");
        fi->source_uhid = json_str_field(obj, "source_uhid");
        fi->destination_uhid = json_str_field(obj, "destination_uhid");
        fi->ttl = (int32_t)json_num_field(obj, "ttl");
        fi->priority = (int)json_num_field(obj, "priority");
        char *ph = json_str_field(obj, "payload_hex");
        fi->payload = hex_decode(ph, &fi->payload_len); free(ph);
        char *nh = json_str_field(obj, "packet_nonce_hex");
        fi->packet_nonce = hex_decode(nh, &fi->packet_nonce_len); free(nh);
        char *sh = json_str_field(obj, "signature_hex");
        fi->signature = hex_decode(sh, &fi->signature_len); free(sh);
        fi->timestamp_ms = (int64_t)json_num_field(obj, "timestamp_ms");
        fi->protocol_version = (int)json_num_field(obj, "protocol_version");

        free(obj);
    }
    *out = items;
    return count;
}

static aether_mesh_packet_t *build_packet(const fixture_input_t *fi) {
    aether_mesh_packet_t *p = aether_packet_new();
    parse_uuid(fi->id, p->packet_id);
    p->type = (uint8_t)fi->type;
    p->ttl = fi->ttl;
    p->priority = (uint8_t)fi->priority;
    p->protocol_version = (uint8_t)fi->protocol_version;
    p->timestamp_ms = fi->timestamp_ms;
    aether_packet_set_source_uhid(p, fi->source_uhid);
    aether_packet_set_destination_uhid(p, fi->destination_uhid);
    if (fi->payload_len) {
        aether_packet_set_payload(p, fi->payload, fi->payload_len);
    }
    if (fi->signature_len) {
        aether_packet_set_signature(p, fi->signature, fi->signature_len);
    }
    if (fi->packet_nonce_len) {
        memcpy(p->packet_nonce, fi->packet_nonce,
               fi->packet_nonce_len < AETHER_PACKET_NONCE_SIZE
                   ? fi->packet_nonce_len : AETHER_PACKET_NONCE_SIZE);
    }
    return p;
}

static void free_input(fixture_input_t *fi) {
    free(fi->name);
    free(fi->id);
    free(fi->source_uhid);
    free(fi->destination_uhid);
    free(fi->payload);
    free(fi->packet_nonce);
    free(fi->signature);
}

int main(void) {
    printf("Aether Cross-Language Fixture Tests (C)\n");
    printf("=========================================\n");

    // Walk up from the executable's likely CWD until we find fixtures/.
    const char *candidates[] = {
        "fixtures/inputs.json",
        "../fixtures/inputs.json",
        "../../fixtures/inputs.json",
        "../../../fixtures/inputs.json",
        NULL,
    };
    char *json = NULL;
    char fixture_root[256] = {0};
    for (int i = 0; candidates[i]; i++) {
        size_t len = 0;
        json = read_file(candidates[i], &len);
        if (json) {
            // Strip "/inputs.json" off
            size_t n = strlen(candidates[i]) - strlen("/inputs.json");
            memcpy(fixture_root, candidates[i], n);
            fixture_root[n] = 0;
            break;
        }
    }
    if (!json) {
        fprintf(stderr, "FAIL: could not locate fixtures/inputs.json\n");
        return 1;
    }

    fixture_input_t *inputs = NULL;
    int n = parse_inputs(json, &inputs);
    free(json);
    if (n == 0) {
        fprintf(stderr, "FAIL: parsed zero fixtures\n");
        return 1;
    }

    int failed = 0;
    for (int i = 0; i < n; i++) {
        const fixture_input_t *fi = &inputs[i];
        aether_mesh_packet_t *p = build_packet(fi);

        size_t cap = aether_packet_estimate_size(p) + 64;
        uint8_t *out = (uint8_t *)malloc(cap);
        int written = aether_packet_serialize(p, out, cap);
        if (written < 0) {
            fprintf(stderr, "TEST: %-20s SERIALIZE FAILED\n", fi->name);
            failed++;
            free(out); aether_packet_free(p); continue;
        }

        char path[512];
        snprintf(path, sizeof(path), "%s/expected/%s.bin", fixture_root, fi->name);
        size_t exp_len = 0;
        char *exp = read_file(path, &exp_len);
        if (!exp) {
            fprintf(stderr, "TEST: %-20s NO EXPECTED FILE (%s)\n", fi->name, path);
            failed++;
            free(out); aether_packet_free(p); continue;
        }

        if ((size_t)written != exp_len || memcmp(out, exp, exp_len) != 0) {
            fprintf(stderr, "TEST: %-20s BYTE MISMATCH (got %d want %zu)\n",
                    fi->name, written, exp_len);
            failed++;
        } else {
            printf("TEST: %-20s OK\n", fi->name);
        }

        free(exp); free(out); aether_packet_free(p);
    }

    for (int i = 0; i < n; i++) free_input(&inputs[i]);
    free(inputs);

    if (failed) {
        fprintf(stderr, "\n%d/%d tests failed.\n", failed, n);
        return 1;
    }
    printf("\n%d tests passed.\n", n);
    return 0;
}
