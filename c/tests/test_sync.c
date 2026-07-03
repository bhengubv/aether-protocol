// SPDX-License-Identifier: MIT
//
// Decentralised multi-device sync parity gate. Loads fixtures/sync/vectors.json
// and asserts this C port reproduces the reference (src/AetherNet.Security/Sync/
// {SyncRecord,SyncRecordSerializer,SyncReconciler,DeviceLink}.cs) byte-for-byte:
//
//   sync_records[]:  hex(serialize(record))     == serialized_hex
//                    deserialize(serialized)     round-trips every field
//   reconcile[]:     hex(winner(records).id)     == winner_record_id
//   device_links[]:  hex(signed_body(...))       == signed_body_hex
//                    hex(create(...seed).sig)     == signature_hex
//                    hex(serialize(link))         == serialized_hex
//                    verify(link, identity_pub)   == true
//                    verify(link, wrong_pub)      == false
//                    deserialize(serialized)      round-trips every field
//
// JSON parsing uses cJSON (the SDK vendors it via FetchContent — the same parser
// the library uses for its own signalling). Unlike the flat-object fixtures
// (test_bip39.c / test_fixtures.c hand-roll a substring extractor), this corpus
// is nested (reconcile[].records[]), so a real parser is used. Run from the repo
// root so the relative fixture path resolves.

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <cjson/cJSON.h>

#include "aethernet/sync.h"

static int tests_run = 0;

#define FAILF(...) do { \
    fprintf(stderr, "FAIL: "); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); \
    exit(1); \
} while (0)

/* ─── hex helpers (mirror test_bip39.c) ─────────────────────────────────── */

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
    if (!b) FAILF("hex_to_bytes: OOM");
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

// hex-encode into a freshly malloc'd string. Caller frees.
static char *bytes_to_hex_alloc(const uint8_t *b, size_t len) {
    char *out = (char *)malloc(len * 2 + 1);
    if (!out) FAILF("bytes_to_hex_alloc: OOM");
    bytes_to_hex(b, len, out);
    return out;
}

/* ─── file + JSON helpers ───────────────────────────────────────────────── */

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

// Required string field of a JSON object.
static const char *req_str(const cJSON *obj, const char *key) {
    const cJSON *j = cJSON_GetObjectItemCaseSensitive(obj, key);
    if (!cJSON_IsString(j) || j->valuestring == NULL)
        FAILF("missing/typed string field \"%s\"", key);
    return j->valuestring;
}

// Required integer field (cJSON stores numbers as double; the sync fixture's
// created_at_ms values ~1.7e12 are well within double's 2^53 exact-integer
// range, so the round-trip is lossless).
static int64_t req_int(const cJSON *obj, const char *key) {
    const cJSON *j = cJSON_GetObjectItemCaseSensitive(obj, key);
    if (!cJSON_IsNumber(j))
        FAILF("missing/typed number field \"%s\"", key);
    return (int64_t)j->valuedouble;
}

/* ─── build a record from a fixture object ──────────────────────────────── */
//
// record_id is a UUID string (with dashes) → 16 big-endian bytes; payload_hex is
// the raw ciphertext bytes. Fields malloc'd here are freed via free_record().

static void build_record(const cJSON *obj, aethernet_sync_record_t *rec) {
    memset(rec, 0, sizeof(*rec));

    // record_id: strip dashes then decode 16 bytes big-endian (as written).
    const char *uuid = req_str(obj, "record_id");
    char hex[33];
    size_t h = 0;
    for (const char *p = uuid; *p && h < 32; p++)
        if (*p != '-') hex[h++] = *p;
    hex[h] = '\0';
    if (h != 32) FAILF("record_id \"%s\" is not 16 bytes", uuid);
    size_t idlen = 0;
    uint8_t *id = hex_to_bytes(hex, &idlen);
    memcpy(rec->record_id, id, AETHERNET_SYNC_RECORD_ID_SIZE);
    free(id);

    const char *device = req_str(obj, "device_id");
    const char *item = req_str(obj, "item_id");
    rec->device_id = (char *)malloc(strlen(device) + 1);
    rec->item_id = (char *)malloc(strlen(item) + 1);
    if (!rec->device_id || !rec->item_id) FAILF("build_record: OOM");
    strcpy(rec->device_id, device);
    strcpy(rec->item_id, item);

    rec->op = (uint8_t)req_int(obj, "op");
    rec->logical_clock = req_int(obj, "logical_clock");
    rec->created_at_ms = req_int(obj, "created_at_ms");

    const char *payload_hex = req_str(obj, "payload_hex");
    if (payload_hex[0] != '\0') {
        size_t plen = 0;
        rec->encrypted_payload = hex_to_bytes(payload_hex, &plen);
        rec->encrypted_payload_len = (uint32_t)plen;
    }
}

static void free_record(aethernet_sync_record_t *rec) {
    free(rec->device_id);
    free(rec->item_id);
    free(rec->encrypted_payload);
    memset(rec, 0, sizeof(*rec));
}

/* ─── sync_records[] : serialize == fixture, deserialize round-trips ────── */

static void check_sync_records(const cJSON *root) {
    const cJSON *arr = cJSON_GetObjectItemCaseSensitive(root, "sync_records");
    if (!cJSON_IsArray(arr)) FAILF("sync_records is not an array");
    int n = cJSON_GetArraySize(arr);
    if (n == 0) FAILF("sync_records is empty");

    for (int i = 0; i < n; i++) {
        const cJSON *obj = cJSON_GetArrayItem(arr, i);
        aethernet_sync_record_t rec;
        build_record(obj, &rec);

        // 1. hex(serialize(record)) == serialized_hex
        const char *want_hex = req_str(obj, "serialized_hex");
        uint8_t *bytes = NULL;
        uint32_t blen = 0;
        if (!aethernet_sync_record_serialize(&rec, &bytes, &blen))
            FAILF("sync_records[%d]: serialize failed", i);
        char *got_hex = bytes_to_hex_alloc(bytes, blen);
        if (strcmp(got_hex, want_hex) != 0)
            FAILF("sync_records[%d] serialize mismatch\n  got:  %s\n  want: %s",
                  i, got_hex, want_hex);

        // 2. deserialize round-trips every field.
        aethernet_sync_record_t *back = NULL;
        if (!aethernet_sync_record_deserialize(bytes, blen, &back))
            FAILF("sync_records[%d]: deserialize failed", i);
        if (memcmp(back->record_id, rec.record_id, AETHERNET_SYNC_RECORD_ID_SIZE) != 0)
            FAILF("sync_records[%d]: record_id round-trip mismatch", i);
        if (back->op != rec.op)
            FAILF("sync_records[%d]: op round-trip mismatch (%u vs %u)", i, back->op, rec.op);
        if (back->logical_clock != rec.logical_clock)
            FAILF("sync_records[%d]: logical_clock round-trip mismatch", i);
        if (back->created_at_ms != rec.created_at_ms)
            FAILF("sync_records[%d]: created_at_ms round-trip mismatch", i);
        if (strcmp(back->device_id ? back->device_id : "",
                   rec.device_id ? rec.device_id : "") != 0)
            FAILF("sync_records[%d]: device_id round-trip mismatch", i);
        if (strcmp(back->item_id ? back->item_id : "",
                   rec.item_id ? rec.item_id : "") != 0)
            FAILF("sync_records[%d]: item_id round-trip mismatch", i);
        if (back->encrypted_payload_len != rec.encrypted_payload_len)
            FAILF("sync_records[%d]: payload_len round-trip mismatch (%u vs %u)",
                  i, back->encrypted_payload_len, rec.encrypted_payload_len);
        if (rec.encrypted_payload_len > 0 &&
            memcmp(back->encrypted_payload, rec.encrypted_payload,
                   rec.encrypted_payload_len) != 0)
            FAILF("sync_records[%d]: payload bytes round-trip mismatch", i);

        aethernet_sync_record_free(back);
        free(got_hex);
        free(bytes);
        free_record(&rec);
        tests_run++;
    }
    printf("  all %d sync_records OK (serialize == hex, deserialize round-trips)\n", n);
}

/* ─── reconcile[] : winner(records).record_id == winner_record_id ───────── */

static void check_reconcile(const cJSON *root) {
    const cJSON *arr = cJSON_GetObjectItemCaseSensitive(root, "reconcile");
    if (!cJSON_IsArray(arr)) FAILF("reconcile is not an array");
    int n = cJSON_GetArraySize(arr);
    if (n == 0) FAILF("reconcile is empty");

    for (int i = 0; i < n; i++) {
        const cJSON *caseobj = cJSON_GetArrayItem(arr, i);
        const char *name = req_str(caseobj, "name");
        const cJSON *records = cJSON_GetObjectItemCaseSensitive(caseobj, "records");
        if (!cJSON_IsArray(records)) FAILF("reconcile[%d] (%s): records not an array", i, name);
        int rc = cJSON_GetArraySize(records);
        if (rc == 0) FAILF("reconcile[%d] (%s): no records", i, name);

        aethernet_sync_record_t *recs =
            (aethernet_sync_record_t *)calloc((size_t)rc, sizeof(*recs));
        if (!recs) FAILF("reconcile[%d]: OOM", i);
        for (int r = 0; r < rc; r++)
            build_record(cJSON_GetArrayItem(records, r), &recs[r]);

        const aethernet_sync_record_t *winner = aethernet_sync_winner(recs, (size_t)rc);
        if (!winner) FAILF("reconcile[%d] (%s): winner returned NULL", i, name);

        char got_id[AETHERNET_SYNC_RECORD_ID_SIZE * 2 + 1];
        bytes_to_hex(winner->record_id, AETHERNET_SYNC_RECORD_ID_SIZE, got_id);

        // Expected winner_record_id is a UUID string — normalise to bare hex.
        const char *want_uuid = req_str(caseobj, "winner_record_id");
        char want_id[33];
        size_t w = 0;
        for (const char *p = want_uuid; *p && w < 32; p++)
            if (*p != '-') want_id[w++] = (char)((*p >= 'A' && *p <= 'F') ? *p + 32 : *p);
        want_id[w] = '\0';

        if (strcmp(got_id, want_id) != 0)
            FAILF("reconcile[%d] (%s) winner mismatch\n  got:  %s\n  want: %s",
                  i, name, got_id, want_id);

        for (int r = 0; r < rc; r++) free_record(&recs[r]);
        free(recs);
        tests_run++;
    }
    printf("  all %d reconcile cases OK (deterministic last-write-wins)\n", n);
}

/* ─── device_links[] : signed body / signature / serialize / verify ─────── */

static void check_device_links(const cJSON *root,
                               const uint8_t *identity_seed,
                               const uint8_t *identity_pub,
                               const uint8_t *wrong_pub) {
    const cJSON *arr = cJSON_GetObjectItemCaseSensitive(root, "device_links");
    if (!cJSON_IsArray(arr)) FAILF("device_links is not an array");
    int n = cJSON_GetArraySize(arr);
    if (n == 0) FAILF("device_links is empty");

    for (int i = 0; i < n; i++) {
        const cJSON *obj = cJSON_GetArrayItem(arr, i);
        const char *device_id = req_str(obj, "device_id");
        int64_t issued_at_ms = req_int(obj, "issued_at_ms");

        size_t keylen = 0;
        uint8_t *device_key = hex_to_bytes(req_str(obj, "device_public_key"), &keylen);
        if (keylen != AETHERNET_SYNC_DEVICE_KEY_SIZE)
            FAILF("device_links[%d]: device_public_key is not 32 bytes", i);

        // 1. hex(signed_body(...)) == signed_body_hex
        const char *want_body = req_str(obj, "signed_body_hex");
        uint8_t *body = NULL;
        uint32_t body_len = 0;
        if (!aethernet_device_link_signed_body(device_id, device_key, issued_at_ms,
                                               &body, &body_len))
            FAILF("device_links[%d]: signed_body failed", i);
        char *got_body = bytes_to_hex_alloc(body, body_len);
        if (strcmp(got_body, want_body) != 0)
            FAILF("device_links[%d] signed_body mismatch\n  got:  %s\n  want: %s",
                  i, got_body, want_body);

        // 2. hex(create(...identity seed).signature) == signature_hex
        const char *want_sig = req_str(obj, "signature_hex");
        aethernet_device_link_t link;
        memset(&link, 0, sizeof(link));
        if (!aethernet_device_link_create(device_id, device_key, issued_at_ms,
                                          identity_seed, &link))
            FAILF("device_links[%d]: create failed", i);
        char *got_sig = bytes_to_hex_alloc(link.signature, AETHERNET_SYNC_SIGNATURE_SIZE);
        if (strcmp(got_sig, want_sig) != 0)
            FAILF("device_links[%d] signature mismatch (Ed25519 is deterministic)\n"
                  "  got:  %s\n  want: %s", i, got_sig, want_sig);

        // 3. hex(serialize(link)) == serialized_hex
        const char *want_ser = req_str(obj, "serialized_hex");
        uint8_t *ser = NULL;
        uint32_t ser_len = 0;
        if (!aethernet_device_link_serialize(&link, &ser, &ser_len))
            FAILF("device_links[%d]: serialize failed", i);
        char *got_ser = bytes_to_hex_alloc(ser, ser_len);
        if (strcmp(got_ser, want_ser) != 0)
            FAILF("device_links[%d] serialize mismatch\n  got:  %s\n  want: %s",
                  i, got_ser, want_ser);

        // 4. verify(link, identity_public) == true
        if (!aethernet_device_link_verify(&link, identity_pub))
            FAILF("device_links[%d]: verify(identity_public) returned false", i);

        // 5. verify(link, wrong_identity_public) == false
        if (aethernet_device_link_verify(&link, wrong_pub))
            FAILF("device_links[%d]: verify(wrong_identity_public) returned true", i);

        // 6. deserialize round-trips every field.
        aethernet_device_link_t back;
        memset(&back, 0, sizeof(back));
        if (!aethernet_device_link_deserialize(ser, ser_len, &back))
            FAILF("device_links[%d]: deserialize failed", i);
        if (strcmp(back.device_id ? back.device_id : "", device_id) != 0)
            FAILF("device_links[%d]: device_id round-trip mismatch", i);
        if (back.issued_at_ms != issued_at_ms)
            FAILF("device_links[%d]: issued_at_ms round-trip mismatch", i);
        if (memcmp(back.device_public_key, device_key, AETHERNET_SYNC_DEVICE_KEY_SIZE) != 0)
            FAILF("device_links[%d]: device_public_key round-trip mismatch", i);
        if (memcmp(back.signature, link.signature, AETHERNET_SYNC_SIGNATURE_SIZE) != 0)
            FAILF("device_links[%d]: signature round-trip mismatch", i);
        // The round-tripped link must still verify.
        if (!aethernet_device_link_verify(&back, identity_pub))
            FAILF("device_links[%d]: deserialized link failed verify(identity_public)", i);

        free(back.device_id);
        free(got_ser);
        free(ser);
        free(got_sig);
        free(link.device_id);
        free(got_body);
        free(body);
        free(device_key);
        tests_run++;
    }
    printf("  all %d device_links OK (signed_body/signature/serialize/verify/round-trip)\n", n);
}

int main(void) {
    printf("AetherNet Multi-Device Sync Parity (C)\n");
    printf("======================================\n");

    const char *candidates[] = {
        "fixtures/sync/vectors.json",
        "../fixtures/sync/vectors.json",
        "../../fixtures/sync/vectors.json",
        "../../../fixtures/sync/vectors.json",
        NULL,
    };
    char *json = NULL;
    for (int i = 0; candidates[i]; i++) {
        json = read_file(candidates[i], NULL);
        if (json) break;
    }
    if (!json) FAILF("could not locate fixtures/sync/vectors.json (run from repo root)");

    cJSON *root = cJSON_Parse(json);
    if (!root) FAILF("failed to parse fixtures/sync/vectors.json as JSON");

    size_t seedlen = 0, publen = 0, wronglen = 0;
    uint8_t *identity_seed = hex_to_bytes(req_str(root, "identity_private"), &seedlen);
    uint8_t *identity_pub = hex_to_bytes(req_str(root, "identity_public"), &publen);
    uint8_t *wrong_pub = hex_to_bytes(req_str(root, "wrong_identity_public"), &wronglen);
    if (seedlen != AETHERNET_SYNC_IDENTITY_SEED_SIZE)
        FAILF("identity_private is not 32 bytes (got %zu)", seedlen);
    if (publen != AETHERNET_SYNC_DEVICE_KEY_SIZE)
        FAILF("identity_public is not 32 bytes (got %zu)", publen);
    if (wronglen != AETHERNET_SYNC_DEVICE_KEY_SIZE)
        FAILF("wrong_identity_public is not 32 bytes (got %zu)", wronglen);

    printf("Loaded fixture (identity seed=%zuB, public=%zuB).\n", seedlen, publen);

    check_sync_records(root);
    check_reconcile(root);
    check_device_links(root, identity_seed, identity_pub, wrong_pub);

    free(identity_seed);
    free(identity_pub);
    free(wrong_pub);
    cJSON_Delete(root);
    free(json);

    printf("\n%d sync parity checks passed.\n", tests_run);
    return 0;
}
