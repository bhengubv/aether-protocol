// SPDX-License-Identifier: MIT
//
// Behavioural test for the FMHY catalogue: the markdown parser (headings ->
// category, bold link -> entry, star -> starred) and the in-memory catalogue
// (sync + entry_count + category browse + get_starred).

#include <stdio.h>
#include <string.h>

#include "aethernet/fmhy.h"

static int g_failures = 0;

#define CHECK(cond, msg)                                                      \
    do {                                                                      \
        if (!(cond)) {                                                        \
            fprintf(stderr, "FAIL: %s (%s:%d)\n", (msg), __FILE__, __LINE__); \
            g_failures++;                                                     \
        }                                                                     \
    } while (0)

static const char *MD =
    "# Video\n"
    "## Streaming\n"
    "* **[FreeFlix](https://freeflix.example)** - Free movies and shows\n"
    "* \xE2\xAD\x90 **[BestStream](https://best.example)** - The top pick\n" // ⭐ (U+2B50, UTF-8)
    "\n"
    "# Audio\n"
    "* **[TunePort](https://tune.example)** - Music streaming\n";

int main(void) {
    // Parser: headings -> category, bold link -> entry, star -> starred.
    int32_t n = 0;
    aethernet_fmhy_entry_t *parsed = aethernet_fmhy_parse_markdown(MD, &n);
    CHECK(n == 3, "parsed 3 entries");
    if (parsed != NULL && n == 3) {
        CHECK(strcmp(parsed[0].category, "Video / Streaming") == 0, "entry0 category");
        CHECK(strcmp(parsed[0].name, "FreeFlix") == 0, "entry0 name");
        CHECK(parsed[1].is_starred, "entry1 starred");
        CHECK(strcmp(parsed[1].name, "BestStream") == 0, "entry1 name");
        CHECK(strcmp(parsed[2].category, "Audio") == 0, "entry2 category");
    }
    aethernet_fmhy_entries_free(parsed, n);

    // Catalogue: sync replaces entries; browse/get_starred filter.
    aethernet_fmhy_service_t *svc = aethernet_fmhy_service_new();
    CHECK(svc != NULL, "fmhy_service_new");
    CHECK(aethernet_fmhy_entry_count(svc) == 0, "seed-less count 0");
    aethernet_fmhy_sync(svc, MD);
    CHECK(aethernet_fmhy_entry_count(svc) == 3, "post-sync count 3");

    int32_t bc = 0;
    aethernet_fmhy_entry_t *all = aethernet_fmhy_browse(svc, NULL, &bc);
    CHECK(bc == 3, "browse all 3");
    aethernet_fmhy_entries_free(all, bc);

    aethernet_fmhy_entry_t *vid = aethernet_fmhy_browse(svc, "video", &bc);
    CHECK(bc == 2, "browse video 2");
    aethernet_fmhy_entries_free(vid, bc);

    aethernet_fmhy_entry_t *aud = aethernet_fmhy_browse(svc, "audio", &bc);
    CHECK(bc == 1, "browse audio 1");
    aethernet_fmhy_entries_free(aud, bc);

    aethernet_fmhy_entry_t *nope = aethernet_fmhy_browse(svc, "nonexistent", &bc);
    CHECK(bc == 0, "browse nonexistent 0");
    aethernet_fmhy_entries_free(nope, bc);

    int32_t sc = 0;
    aethernet_fmhy_entry_t *starred = aethernet_fmhy_get_starred(svc, NULL, &sc);
    CHECK(sc == 1, "starred 1");
    if (starred != NULL && sc == 1) {
        CHECK(strcmp(starred[0].name, "BestStream") == 0, "starred name");
    }
    aethernet_fmhy_entries_free(starred, sc);

    aethernet_fmhy_service_free(svc);

    if (g_failures == 0) {
        printf("test_fmhy: all checks passed\n");
        return 0;
    }
    fprintf(stderr, "test_fmhy: %d check(s) failed\n", g_failures);
    return 1;
}
