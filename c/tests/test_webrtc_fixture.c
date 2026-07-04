// SPDX-License-Identifier: MIT
// Cross-language WebRTC-signalling frame parity: the C port must reproduce the
// shared oracle's byte vectors (fixtures/webrtc/expected/<name>.bin) byte-for-byte
// for every case in fixtures/webrtc/inputs.json, then deserialize each back to
// matching fields. Cases are transcribed in C (no JSON parser on the test
// surface, mirroring test_circuit_relay_fixture.c / test_dtn_fixture.c). Run from
// the repo root so the relative fixture paths resolve.
//
// The expected .bin frames are the SINGLE shared fixture the TypeScript / Python /
// C# references also assert against, so passing them proves cross-language
// byte-identity of the AWS1 + System.Text.Json framing (including STJ's exotic-char
// escaping: `+ < > & '` and non-ASCII emitted as UPPERCASE \uXXXX per UTF-16 code
// unit). The frame BYTES are NEVER hardcoded here — they are read from the .bin.

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "aethernet/webrtc_signaling.h"

static int tests_run = 0;

#define FAILF(name, ...) do { \
    fprintf(stderr, "FAIL [%s]: ", (name)); fprintf(stderr, __VA_ARGS__); fprintf(stderr, "\n"); \
    exit(1); \
} while (0)

// Read fixtures/webrtc/expected/<name>.bin (relative to the repo root, which the
// CTest WORKING_DIRECTORY sets — same approach as test_circuit_relay_fixture.c).
static uint8_t *read_expected(const char *name, long *out_len) {
    char path[256];
    snprintf(path, sizeof path, "fixtures/webrtc/expected/%s.bin", name);
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long len = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (len < 0) { fclose(f); return NULL; }
    uint8_t *buf = (uint8_t *)malloc((size_t)(len > 0 ? len : 1));
    if (!buf) { fclose(f); return NULL; }
    size_t got = fread(buf, 1, (size_t)len, f);
    fclose(f);
    if ((long)got != len) { free(buf); return NULL; }
    *out_len = len;
    return buf;
}

// Serialize a signal, assert byte-identity with the shared oracle vector, then
// decode the vector back and assert every field round-trips. Empty (NULL/"")
// sdp / candidate / sdp_mid mean "omitted on the wire" — the caller passes "" and
// this leaves the fixed struct buffer zeroed for them, matching the encoder's
// WhenWritingNull omission.
static void check(const char *name, const char *from_uhid, const char *to_uhid,
                  aethernet_webrtc_signal_type_t type, const char *sdp,
                  const char *candidate, const char *sdp_mid, int32_t sdp_mline_index) {
    aethernet_webrtc_signal_t sig;
    memset(&sig, 0, sizeof sig);
    strncpy(sig.from_uhid, from_uhid ? from_uhid : "", sizeof(sig.from_uhid) - 1);
    strncpy(sig.to_uhid,   to_uhid   ? to_uhid   : "", sizeof(sig.to_uhid) - 1);
    sig.type = type;
    if (sdp)       strncpy(sig.sdp,       sdp,       sizeof(sig.sdp) - 1);
    if (candidate) strncpy(sig.candidate, candidate, sizeof(sig.candidate) - 1);
    if (sdp_mid)   strncpy(sig.sdp_mid,   sdp_mid,   sizeof(sig.sdp_mid) - 1);
    sig.sdp_mline_index = sdp_mline_index;

    size_t enc_len = 0;
    uint8_t *enc = aethernet_webrtc_signal_frame_encode(&sig, &enc_len);
    if (!enc) FAILF(name, "encode failed");

    long exp_len = 0;
    uint8_t *exp = read_expected(name, &exp_len);
    if (!exp) FAILF(name, "missing fixtures/webrtc/expected/%s.bin (run from repo root)", name);
    if ((long)enc_len != exp_len || memcmp(enc, exp, enc_len) != 0) {
        FAILF(name, "serialize byte mismatch (got %zu bytes, expected %ld)\n  got:  %.*s\n  want: %.*s",
              enc_len, exp_len,
              (int)(enc_len > 4 ? enc_len - 4 : 0), (const char *)(enc + 4),
              (int)(exp_len > 4 ? exp_len - 4 : 0), (const char *)(exp + 4));
    }
    free(enc);

    aethernet_webrtc_signal_t out;
    memset(&out, 0, sizeof out);
    if (!aethernet_webrtc_signal_frame_decode(exp, (size_t)exp_len, &out))
        FAILF(name, "decode returned false");
    if (strcmp(out.from_uhid, sig.from_uhid) != 0) FAILF(name, "from_uhid");
    if (strcmp(out.to_uhid,   sig.to_uhid)   != 0) FAILF(name, "to_uhid");
    if (out.type != type)                          FAILF(name, "type");
    if (strcmp(out.sdp,       sig.sdp)       != 0) FAILF(name, "sdp");
    if (strcmp(out.candidate, sig.candidate) != 0) FAILF(name, "candidate");
    if (strcmp(out.sdp_mid,   sig.sdp_mid)   != 0) FAILF(name, "sdp_mid");
    if (out.sdp_mline_index != sdp_mline_index)    FAILF(name, "sdp_mline_index");
    free(exp);

    printf("  %s OK\n", name);
    tests_run++;
}

int main(void) {
    printf("Aether WebRTC Signalling Frame — Cross-Language Fixture Parity\n");
    printf("=============================================================\n");

    // offer_basic — SDP offer, Candidate/SdpMid omitted, m-line index 0.
    check("offer_basic", "alice", "bob", AETHERNET_WEBRTC_SIGNAL_OFFER,
          "v=0\r\no=- 1 1 IN IP4 0.0.0.0", "", "", 0);

    // answer_basic — SDP answer, Candidate/SdpMid omitted, m-line index 0.
    check("answer_basic", "bob", "alice", AETHERNET_WEBRTC_SIGNAL_ANSWER,
          "v=0\r\na=answer", "", "", 0);

    // candidate_basic — Candidate + SdpMid present, Sdp omitted, m-line index 0.
    check("candidate_basic", "alice", "bob", AETHERNET_WEBRTC_SIGNAL_CANDIDATE,
          "", "candidate:1 1 udp 2130706431 192.168.1.5 54321 typ host", "0", 0);

    // offer_exotic_ascii — SDP full of STJ-escaped exotic ASCII: base64 `+`,
    // `< > &`, plus literal `/` and `=`. The encoder emits `+`→+, `<`→<,
    // `>`→>, `&`→& (UPPERCASE) and leaves `/ =` and spaces literal.
    check("offer_exotic_ascii", "a", "b", AETHERNET_WEBRTC_SIGNAL_OFFER,
          "a=fingerprint:sha-256 AB+/CD=xy <t> &z ual/set+ice", "", "", 0);

    // candidate_exotic_unicode — Candidate carrying base64 `+`, `< > &`, literal
    // `/`, AND non-ASCII UTF-8 (ç U+00E7, é U+00E9, 世 U+4E16 as literal UTF-8
    // bytes) — each non-ASCII code point becomes an UPPERCASE \uXXXX per UTF-16
    // code unit. Real SdpMLineIndex of 3 (emitted verbatim) and an SdpMid with `+`.
    check("candidate_exotic_unicode", "u", "v", AETHERNET_WEBRTC_SIGNAL_CANDIDATE,
          "", "a+b/c=d<e>f&g:h \xC3\xA7 \xC3\xA9 \xE4\xB8\x96", "m/i+d", 3);

    printf("\n%d fixture cases passed.\n", tests_run);
    return 0;
}
