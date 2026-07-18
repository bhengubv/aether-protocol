// SPDX-License-Identifier: MIT
//
// Cross-language BitTorrent fixture verifier. Asserts this C port reproduces every
// vector in fixtures/bittorrent/vectors.json BYTE-FOR-BYTE — the same corpus Go and
// C# already pass. Mirrors the assertion logic in go/bittorrent/fixture_test.go across
// all 7 categories: bencode_roundtrip, info_hash, peer_messages, utp_packets, merkle,
// compact, krpc. Any wire drift fails here.
//
// JSON parsing uses cJSON (compiled into aethernet-protocol via FetchContent, the same
// parser the SDK uses for its own signalling — mirrors test_sync.c). Run from the repo
// root so the relative fixtures/bittorrent/vectors.json path resolves.

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <cjson/cJSON.h>

#include "aethernet/bittorrent.h"

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
static void bytes_to_hex(const uint8_t *b, size_t len, char *out) {
    static const char HEX[] = "0123456789abcdef";
    for (size_t i = 0; i < len; i++) {
        out[i * 2] = HEX[(b[i] >> 4) & 0xF];
        out[i * 2 + 1] = HEX[b[i] & 0xF];
    }
    out[len * 2] = '\0';
}
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

static const char *req_str(const cJSON *obj, const char *key) {
    const cJSON *j = cJSON_GetObjectItemCaseSensitive(obj, key);
    if (!cJSON_IsString(j) || j->valuestring == NULL) FAILF("missing string field \"%s\"", key);
    return j->valuestring;
}
static double req_num(const cJSON *obj, const char *key) {
    const cJSON *j = cJSON_GetObjectItemCaseSensitive(obj, key);
    if (!cJSON_IsNumber(j)) FAILF("missing number field \"%s\"", key);
    return j->valuedouble;
}

/* fillBytes: b[i] = (uint8_t)(i*mult + add) — matches Go's byte(i*mult+add). */
static uint8_t *fill_bytes(int n, int mult, int add) {
    uint8_t *b = (uint8_t *)malloc(n ? (size_t)n : 1);
    if (!b) FAILF("fill_bytes: OOM");
    for (int i = 0; i < n; i++) b[i] = (uint8_t)(i * mult + add);
    return b;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Category tests.
 * ═══════════════════════════════════════════════════════════════════════════ */

static void test_bencode(const cJSON *root) {
    const cJSON *arr = cJSON_GetObjectItemCaseSensitive(root, "bencode_roundtrip");
    if (!cJSON_IsArray(arr)) FAILF("bencode_roundtrip is not an array");
    const cJSON *it;
    cJSON_ArrayForEach(it, arr) {
        const char *hs = it->valuestring;
        size_t inlen = 0;
        uint8_t *in = hex_to_bytes(hs, &inlen);
        aethernet_benc_value_t *v = aethernet_benc_decode(in, inlen);
        if (!v) FAILF("bencode decode failed for %s", hs);
        size_t enclen = 0;
        uint8_t *enc = aethernet_benc_encode(v, &enclen);
        if (!enc) FAILF("bencode encode failed for %s", hs);
        char *got = bytes_to_hex_alloc(enc, enclen);
        if (strcmp(got, hs) != 0) FAILF("bencode roundtrip %s -> %s", hs, got);
        free(got); free(enc); aethernet_benc_free(v); free(in);
        tests_run++;
    }
}

static void test_info_hash(const cJSON *root) {
    const cJSON *arr = cJSON_GetObjectItemCaseSensitive(root, "info_hash");
    if (!cJSON_IsArray(arr)) FAILF("info_hash is not an array");
    const cJSON *ic;
    cJSON_ArrayForEach(ic, arr) {
        const char *name = req_str(ic, "name");
        int size = (int)req_num(ic, "size");
        int mult = (int)req_num(ic, "mult");
        int add = (int)req_num(ic, "add");
        const char *name_str = req_str(ic, "name_str");
        int piece_length = (int)req_num(ic, "piece_length");
        const char *want = req_str(ic, "info_hash_hex");

        uint8_t *content = fill_bytes(size, mult, add);
        size_t tblen = 0;
        uint8_t *tb = aethernet_bt_build_single_file_torrent(name_str, content, (size_t)size,
                                                             piece_length, "", &tblen);
        if (!tb) FAILF("%s: build torrent failed", name);
        aethernet_bt_metainfo_t *m = aethernet_bt_parse_torrent(tb, tblen);
        if (!m) FAILF("%s: parse torrent failed", name);
        char got[41];
        aethernet_bt_metainfo_info_hash_v1_hex(m, got);
        if (strcmp(got, want) != 0) FAILF("%s info-hash %s want %s", name, got, want);
        aethernet_bt_metainfo_free(m); free(tb); free(content);
        tests_run++;
    }
}

static void test_peer_messages(const cJSON *root) {
    const cJSON *arr = cJSON_GetObjectItemCaseSensitive(root, "peer_messages");
    if (!cJSON_IsArray(arr)) FAILF("peer_messages is not an array");
    const cJSON *pm;
    cJSON_ArrayForEach(pm, arr) {
        const char *name = req_str(pm, "name");
        const char *kind = req_str(pm, "kind");
        uint32_t a = (uint32_t)req_num(pm, "a");
        uint32_t b = (uint32_t)req_num(pm, "b");
        uint32_t c = (uint32_t)req_num(pm, "c");
        const char *want = req_str(pm, "wire_hex");

        aethernet_bt_message_t msg;
        bool ok;
        if (strcmp(kind, "keepalive") == 0) ok = aethernet_bt_keepalive(&msg);
        else if (strcmp(kind, "choke") == 0) ok = aethernet_bt_choke(&msg);
        else if (strcmp(kind, "unchoke") == 0) ok = aethernet_bt_unchoke(&msg);
        else if (strcmp(kind, "interested") == 0) ok = aethernet_bt_interested(&msg);
        else if (strcmp(kind, "have") == 0) ok = aethernet_bt_have(a, &msg);
        else if (strcmp(kind, "request") == 0) ok = aethernet_bt_request(a, b, c, &msg);
        else if (strcmp(kind, "port") == 0) ok = aethernet_bt_port((uint16_t)a, &msg);
        else { FAILF("unknown peer kind %s", kind); return; }
        if (!ok) FAILF("%s: message build failed", name);

        size_t wlen = 0;
        uint8_t *wire = aethernet_bt_message_to_bytes(&msg, &wlen);
        if (!wire) FAILF("%s: to_bytes failed", name);
        char *got = bytes_to_hex_alloc(wire, wlen);
        if (strcmp(got, want) != 0) FAILF("%s wire %s want %s", name, got, want);
        free(got); free(wire); aethernet_bt_message_free(&msg);
        tests_run++;
    }
}

static void test_utp(const cJSON *root) {
    const cJSON *arr = cJSON_GetObjectItemCaseSensitive(root, "utp_packets");
    if (!cJSON_IsArray(arr)) FAILF("utp_packets is not an array");
    const cJSON *uc;
    cJSON_ArrayForEach(uc, arr) {
        const char *name = req_str(uc, "name");
        const char *want = req_str(uc, "wire_hex");
        const char *payload_hex = req_str(uc, "payload_hex");
        size_t plen = 0;
        uint8_t *payload = hex_to_bytes(payload_hex, &plen);

        aethernet_bt_utp_packet_t p;
        p.type = (aethernet_bt_utp_type_t)(int)req_num(uc, "type");
        p.connection_id = (uint16_t)req_num(uc, "conn_id");
        p.timestamp_micros = (uint32_t)req_num(uc, "timestamp");
        p.timestamp_diff = (uint32_t)req_num(uc, "timestamp_diff");
        p.window_size = (uint32_t)req_num(uc, "window");
        p.seq_nr = (uint16_t)req_num(uc, "seq");
        p.ack_nr = (uint16_t)req_num(uc, "ack");
        p.payload = payload;
        p.payload_len = plen;

        size_t wlen = 0;
        uint8_t *wire = aethernet_bt_utp_to_bytes(&p, &wlen);
        if (!wire) FAILF("%s: utp to_bytes failed", name);
        char *got = bytes_to_hex_alloc(wire, wlen);
        if (strcmp(got, want) != 0) FAILF("%s utp wire %s want %s", name, got, want);
        free(got); free(wire); free(payload);
        tests_run++;
    }
}

static void test_merkle(const cJSON *root) {
    const cJSON *arr = cJSON_GetObjectItemCaseSensitive(root, "merkle");
    if (!cJSON_IsArray(arr)) FAILF("merkle is not an array");
    const cJSON *mc;
    cJSON_ArrayForEach(mc, arr) {
        const char *name = req_str(mc, "name");
        int size = (int)req_num(mc, "size");
        int mult = (int)req_num(mc, "mult");
        int add = (int)req_num(mc, "add");
        const char *want = req_str(mc, "root_hex");

        uint8_t *content = fill_bytes(size, mult, add);
        uint8_t rootb[32];
        aethernet_bt_merkle_root(content, (size_t)size, rootb);
        char *got = bytes_to_hex_alloc(rootb, 32);
        if (strcmp(got, want) != 0) FAILF("%s root %s want %s", name, got, want);
        free(got); free(content);
        tests_run++;
    }
}

/* Parse a dotted-quad IPv4 into 4 bytes. */
static void parse_ipv4(const char *s, uint8_t out[4]) {
    unsigned a = 0, b = 0, c = 0, d = 0;
    if (sscanf(s, "%u.%u.%u.%u", &a, &b, &c, &d) != 4) FAILF("bad IPv4 %s", s);
    out[0] = (uint8_t)a; out[1] = (uint8_t)b; out[2] = (uint8_t)c; out[3] = (uint8_t)d;
}

static void test_compact(const cJSON *root) {
    const cJSON *arr = cJSON_GetObjectItemCaseSensitive(root, "compact");
    if (!cJSON_IsArray(arr)) FAILF("compact is not an array");
    const cJSON *cc;
    cJSON_ArrayForEach(cc, arr) {
        const char *name = req_str(cc, "name");
        const char *kind = req_str(cc, "kind");
        const char *want = req_str(cc, "wire_hex");
        size_t wirelen = 0;
        uint8_t *wire = hex_to_bytes(want, &wirelen);

        if (strcmp(kind, "node") == 0) {
            /* decode → re-encode round-trip (mirrors Go) */
            size_t cnt = 0;
            aethernet_bt_dht_contact_t *nodes = aethernet_bt_decode_compact_nodes(wire, wirelen, &cnt);
            if (!nodes) FAILF("%s: decode compact nodes failed", name);
            size_t rlen = 0;
            uint8_t *re = aethernet_bt_encode_compact_nodes(nodes, cnt, &rlen);
            char *got = bytes_to_hex_alloc(re, rlen);
            if (strcmp(got, want) != 0) FAILF("%s compact node roundtrip %s want %s", name, got, want);
            /* stronger: the decoded id must equal the fixture's id_hex */
            const char *id_hex = req_str(cc, "id_hex");
            size_t idlen = 0;
            uint8_t *id = hex_to_bytes(id_hex, &idlen);
            if (idlen != 20 || cnt != 1 || memcmp(nodes[0].id.bytes, id, 20) != 0)
                FAILF("%s: decoded node id mismatch", name);
            free(id); free(got); free(re); free(nodes);
        } else if (strcmp(kind, "peers") == 0) {
            /* decode → re-encode round-trip (mirrors Go) */
            size_t cnt = 0;
            aethernet_bt_peer_addr_t *peers = aethernet_bt_decode_compact_peers(wire, wirelen, &cnt);
            if (!peers) FAILF("%s: decode compact peers failed", name);
            size_t rlen = 0;
            uint8_t *re = aethernet_bt_encode_compact_peers(peers, cnt, &rlen);
            char *got = bytes_to_hex_alloc(re, rlen);
            if (strcmp(got, want) != 0) FAILF("%s compact peers roundtrip %s want %s", name, got, want);
            free(got); free(re); free(peers);
            /* stronger: build peers from the fixture's list → wire_hex */
            const cJSON *plist = cJSON_GetObjectItemCaseSensitive(cc, "peers");
            if (cJSON_IsArray(plist)) {
                int pc = cJSON_GetArraySize(plist);
                aethernet_bt_peer_addr_t *built = (aethernet_bt_peer_addr_t *)malloc((size_t)(pc ? pc : 1) * sizeof(*built));
                int i = 0;
                const cJSON *pj;
                cJSON_ArrayForEach(pj, plist) {
                    parse_ipv4(req_str(pj, "ip"), built[i].ip);
                    built[i].port = (uint16_t)req_num(pj, "port");
                    i++;
                }
                size_t blen = 0;
                uint8_t *bw = aethernet_bt_encode_compact_peers(built, (size_t)pc, &blen);
                char *bgot = bytes_to_hex_alloc(bw, blen);
                if (strcmp(bgot, want) != 0) FAILF("%s compact peers build %s want %s", name, bgot, want);
                free(bgot); free(bw); free(built);
            }
        } else {
            FAILF("unknown compact kind %s", kind);
        }
        free(wire);
        tests_run++;
    }
}

static void test_krpc(const cJSON *root) {
    const cJSON *arr = cJSON_GetObjectItemCaseSensitive(root, "krpc");
    if (!cJSON_IsArray(arr)) FAILF("krpc is not an array");
    const cJSON *kc;
    cJSON_ArrayForEach(kc, arr) {
        const char *name = req_str(kc, "name");
        const char *kind = req_str(kc, "kind");
        const char *tx_hex = req_str(kc, "tx_hex");
        const char *want = req_str(kc, "wire_hex");
        size_t txlen = 0;
        uint8_t *tx = hex_to_bytes(tx_hex, &txlen);

        aethernet_bt_krpc_message_t m;
        memset(&m, 0, sizeof(m));
        m.transaction_id = tx;
        m.transaction_id_len = txlen;

        aethernet_benc_value_t *args = NULL;
        uint8_t *id = NULL, *ih = NULL;
        if (strcmp(kind, "get_peers") == 0) {
            const char *id_hex = req_str(kc, "id_hex");
            const char *ih_hex = req_str(kc, "info_hash_hex");
            size_t idlen = 0, ihlen = 0;
            id = hex_to_bytes(id_hex, &idlen);
            ih = hex_to_bytes(ih_hex, &ihlen);
            args = aethernet_benc_dict();
            aethernet_benc_dict_add(args, "id", aethernet_benc_str(id, idlen));
            aethernet_benc_dict_add(args, "info_hash", aethernet_benc_str(ih, ihlen));
            m.type = AETHERNET_BT_KRPC_QUERY;
            m.method = (char *)"get_peers";
            m.arguments = args;
        } else if (strcmp(kind, "error") == 0) {
            m.type = AETHERNET_BT_KRPC_ERROR;
            m.error_code = (int64_t)req_num(kc, "error_code");
            m.error_message = (char *)req_str(kc, "error_message");
        } else {
            FAILF("unknown krpc kind %s", kind);
        }

        size_t enclen = 0;
        uint8_t *enc = aethernet_bt_krpc_encode(&m, &enclen);
        if (!enc) FAILF("%s: krpc encode failed", name);
        char *got = bytes_to_hex_alloc(enc, enclen);
        if (strcmp(got, want) != 0) FAILF("%s krpc %s want %s", name, got, want);
        free(got); free(enc);
        aethernet_benc_free(args);
        free(id); free(ih); free(tx);
        tests_run++;
    }
}

int main(void) {
    size_t len = 0;
    char *raw = read_file("fixtures/bittorrent/vectors.json", &len);
    if (!raw) {
        /* fall back to a couple of relative locations for local runs */
        raw = read_file("../../fixtures/bittorrent/vectors.json", &len);
    }
    if (!raw) FAILF("cannot read fixtures/bittorrent/vectors.json (run from repo root)");

    cJSON *root = cJSON_ParseWithLength(raw, len);
    if (!root) FAILF("cannot parse vectors.json");

    test_bencode(root);
    test_info_hash(root);
    test_peer_messages(root);
    test_utp(root);
    test_merkle(root);
    test_compact(root);
    test_krpc(root);

    cJSON_Delete(root);
    free(raw);

    printf("BitTorrent fixture tests: %d vectors passed (7 categories)\n", tests_run);
    return 0;
}
