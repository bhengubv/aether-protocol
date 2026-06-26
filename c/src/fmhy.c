// SPDX-License-Identifier: MIT
// FMHY content catalogue — see aethernet/fmhy.h. Hand-rolled markdown parser
// (no regex dependency) mirroring the C# reference grammar.

#include "aethernet/fmhy.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ─── small helpers ───────────────────────────────────────

static bool is_ws(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

// Duplicate src[0..n) with leading/trailing ASCII whitespace trimmed.
static char *trim_ndup(const char *src, size_t n) {
    size_t start = 0, end = n;
    while (start < end && is_ws(src[start])) start++;
    while (end > start && is_ws(src[end - 1])) end--;
    size_t len = end - start;
    char *out = (char *)malloc(len + 1);
    if (!out) return NULL;
    memcpy(out, src + start, len);
    out[len] = '\0';
    return out;
}

static char *str_dup_fmhy(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *out = (char *)malloc(n);
    if (out) memcpy(out, s, n);
    return out;
}

static bool starts_with(const char *s, const char *prefix) {
    return strncmp(s, prefix, strlen(prefix)) == 0;
}

static char to_lower_ascii(char c) { return (c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c; }

// Case-insensitive substring test (ASCII fold).
static bool contains_ci(const char *hay, const char *needle) {
    if (!hay || !needle || !*needle) return true;
    size_t nl = strlen(needle);
    for (const char *h = hay; *h; h++) {
        size_t i = 0;
        while (i < nl && h[i] && to_lower_ascii(h[i]) == to_lower_ascii(needle[i])) i++;
        if (i == nl) return true;
    }
    return false;
}

// ─── entry lifecycle ─────────────────────────────────────

static void free_entry_fields(aethernet_fmhy_entry_t *e) {
    free(e->name);
    free(e->url);
    free(e->description);
    free(e->category);
    for (int32_t i = 0; i < e->mirror_count; i++) free(e->mirrors[i]);
    free(e->mirrors);
    e->name = e->url = e->description = e->category = NULL;
    e->mirrors = NULL;
    e->mirror_count = 0;
}

void aethernet_fmhy_entries_free(aethernet_fmhy_entry_t *entries, int32_t count) {
    if (!entries) return;
    for (int32_t i = 0; i < count; i++) free_entry_fields(&entries[i]);
    free(entries);
}

static aethernet_fmhy_entry_t copy_entry(const aethernet_fmhy_entry_t *e) {
    aethernet_fmhy_entry_t c;
    memset(&c, 0, sizeof(c));
    c.name = str_dup_fmhy(e->name);
    c.url = str_dup_fmhy(e->url);
    c.description = str_dup_fmhy(e->description);
    c.category = str_dup_fmhy(e->category);
    c.is_starred = e->is_starred;
    c.mirror_count = e->mirror_count;
    if (e->mirror_count > 0) {
        c.mirrors = (char **)calloc((size_t)e->mirror_count, sizeof(char *));
        if (c.mirrors) {
            for (int32_t i = 0; i < e->mirror_count; i++) c.mirrors[i] = str_dup_fmhy(e->mirrors[i]);
        } else {
            c.mirror_count = 0;
        }
    }
    return c;
}

// ─── growable list ───────────────────────────────────────

typedef struct {
    aethernet_fmhy_entry_t *items;
    int32_t count;
    int32_t cap;
} entry_list_t;

static bool list_push(entry_list_t *l, aethernet_fmhy_entry_t e) {
    if (l->count == l->cap) {
        int32_t ncap = l->cap ? l->cap * 2 : 16;
        aethernet_fmhy_entry_t *ni = (aethernet_fmhy_entry_t *)realloc(l->items, sizeof(*ni) * (size_t)ncap);
        if (!ni) return false;
        l->items = ni;
        l->cap = ncap;
    }
    l->items[l->count++] = e;
    return true;
}

// ─── parser ──────────────────────────────────────────────

// Find the first **[name](url)** in content; on success malloc name+url and set
// *bold_end to the offset after ")**". Returns true on success.
static bool find_bold(const char *content, char **out_name, char **out_url, size_t *bold_end) {
    size_t len = strlen(content);
    size_t from = 0;
    while (from < len) {
        const char *bp = strstr(content + from, "**[");
        if (!bp) break;
        size_t p = (size_t)(bp - content);
        size_t name_start = p + 3;
        const char *rb = strchr(content + name_start, ']');
        if (rb) {
            size_t name_end = (size_t)(rb - content);
            if (content[name_end + 1] == '(') {
                size_t url_start = name_end + 2;
                const char *rp = strchr(content + url_start, ')');
                if (rp) {
                    size_t url_end = (size_t)(rp - content);
                    if (content[url_end + 1] == '*' && content[url_end + 2] == '*') {
                        *out_name = trim_ndup(content + name_start, name_end - name_start);
                        *out_url = trim_ndup(content + url_start, url_end - url_start);
                        *bold_end = url_end + 3;
                        return true;
                    }
                }
            }
        }
        from = p + 3;
    }
    return false;
}

// Extract plain [name](url) URLs from region[0..rlen). Appends trimmed urls
// (malloc'd) to *list (grown via realloc); updates *n / *cap.
static void collect_plain_urls(const char *region, size_t rlen, char ***list, int32_t *n, int32_t *cap) {
    size_t from = 0;
    while (from < rlen) {
        const char *lb = memchr(region + from, '[', rlen - from);
        if (!lb) break;
        size_t p = (size_t)(lb - region);
        const char *rb = memchr(region + p + 1, ']', rlen - (p + 1));
        if (rb) {
            size_t name_end = (size_t)(rb - region);
            if (name_end + 1 < rlen && region[name_end + 1] == '(') {
                size_t url_start = name_end + 2;
                const char *rp = (url_start < rlen) ? memchr(region + url_start, ')', rlen - url_start) : NULL;
                if (rp) {
                    size_t url_end = (size_t)(rp - region);
                    char *u = trim_ndup(region + url_start, url_end - url_start);
                    if (u) {
                        if (*n == *cap) {
                            int32_t nc = *cap ? *cap * 2 : 4;
                            char **nl = (char **)realloc(*list, sizeof(char *) * (size_t)nc);
                            if (nl) { *list = nl; *cap = nc; }
                        }
                        if (*n < *cap) (*list)[(*n)++] = u; else free(u);
                    }
                    from = url_end + 1;
                    continue;
                }
            }
        }
        from = p + 1;
    }
}

// Strip [name](url) markdown links from text, keeping the name. Returns malloc'd.
static char *strip_links(const char *text) {
    size_t len = strlen(text);
    char *out = (char *)malloc(len + 1);
    if (!out) return NULL;
    size_t o = 0, from = 0;
    while (from < len) {
        const char *lb = memchr(text + from, '[', len - from);
        if (!lb) break;
        size_t p = (size_t)(lb - text);
        memcpy(out + o, text + from, p - from);
        o += p - from;
        const char *rb = memchr(text + p + 1, ']', len - (p + 1));
        bool consumed = false;
        if (rb) {
            size_t name_end = (size_t)(rb - text);
            if (name_end + 1 < len && text[name_end + 1] == '(') {
                size_t url_start = name_end + 2;
                const char *rp = (url_start < len) ? memchr(text + url_start, ')', len - url_start) : NULL;
                if (rp) {
                    size_t url_end = (size_t)(rp - text);
                    memcpy(out + o, text + p + 1, name_end - (p + 1)); // keep name
                    o += name_end - (p + 1);
                    from = url_end + 1;
                    consumed = true;
                }
            }
        }
        if (!consumed) {
            out[o++] = '[';
            from = p + 1;
        }
    }
    memcpy(out + o, text + from, len - from);
    o += len - from;
    out[o] = '\0';
    return out;
}

aethernet_fmhy_entry_t *aethernet_fmhy_parse_markdown(const char *markdown, int32_t *out_count) {
    entry_list_t list = {0};
    if (out_count) *out_count = 0;
    if (!markdown) return NULL;

    char *h1 = str_dup_fmhy("");
    char *h2 = str_dup_fmhy("");

    const char *cursor = markdown;
    while (*cursor || cursor == markdown) {
        const char *nl = strchr(cursor, '\n');
        size_t raw_len = nl ? (size_t)(nl - cursor) : strlen(cursor);
        // Trailing-whitespace-trimmed copy of the line.
        size_t line_len = raw_len;
        while (line_len > 0 && is_ws(cursor[line_len - 1])) line_len--;

        if (line_len > 0) {
            char *line = (char *)malloc(line_len + 1);
            if (line) {
                memcpy(line, cursor, line_len);
                line[line_len] = '\0';

                // Heading: 1-2 leading '#', then whitespace.
                size_t hashes = 0;
                while (line[hashes] == '#') hashes++;
                if ((hashes == 1 || hashes == 2) && (line[hashes] == ' ' || line[hashes] == '\t')) {
                    char *title = trim_ndup(line + hashes, line_len - hashes);
                    if (hashes == 1) { free(h1); h1 = title; free(h2); h2 = str_dup_fmhy(""); }
                    else { free(h2); h2 = title; }
                    free(line);
                    goto next_line;
                }

                // Bullet: optional leading ws, '*' or '-', required ws, content.
                size_t bi = 0;
                while (is_ws(line[bi])) bi++;
                if (line[bi] == '*' || line[bi] == '-') {
                    size_t after = bi + 1;
                    if (is_ws(line[after])) {
                        while (is_ws(line[after])) after++;
                        if (line[after] != '\0') {
                            const char *content = line + after;
                            bool is_starred = strstr(content, "\xE2\xAD\x90") != NULL; // ⭐
                            char *name = NULL, *url = NULL;
                            size_t bold_end = 0;
                            if (find_bold(content, &name, &url, &bold_end)
                                && url && url[0] != '\0' && url[0] != '#') {
                                size_t clen = strlen(content);
                                // Description after first " - " past the bold link.
                                char *description = NULL;
                                const char *sep_ptr = strstr(content + bold_end, " - ");
                                size_t desc_sep = sep_ptr ? (size_t)(sep_ptr - content) : (size_t)-1;
                                if (sep_ptr) {
                                    char *raw_desc = trim_ndup(content + desc_sep + 3, clen - (desc_sep + 3));
                                    if (raw_desc) {
                                        char *stripped = strip_links(raw_desc);
                                        free(raw_desc);
                                        if (stripped) {
                                            char *trimmed = trim_ndup(stripped, strlen(stripped));
                                            free(stripped);
                                            if (trimmed && trimmed[0] != '\0') description = trimmed;
                                            else free(trimmed);
                                        }
                                    }
                                }
                                // Mirrors between the bold link and the description.
                                size_t region_len = (sep_ptr ? desc_sep : clen) - bold_end;
                                char **mirrors = NULL;
                                int32_t mn = 0, mcap = 0;
                                collect_plain_urls(content + bold_end, region_len, &mirrors, &mn, &mcap);
                                // Drop mirrors equal to the primary url or starting with '#'.
                                int32_t kept = 0;
                                for (int32_t i = 0; i < mn; i++) {
                                    if (mirrors[i][0] != '\0' && strcmp(mirrors[i], url) != 0 && mirrors[i][0] != '#') {
                                        mirrors[kept++] = mirrors[i];
                                    } else {
                                        free(mirrors[i]);
                                    }
                                }
                                mn = kept;

                                // Category.
                                char *category;
                                if (h2[0] != '\0') {
                                    size_t cl = strlen(h1) + 3 + strlen(h2) + 1;
                                    category = (char *)malloc(cl);
                                    if (category) snprintf(category, cl, "%s / %s", h1, h2);
                                } else {
                                    category = str_dup_fmhy(h1);
                                }

                                aethernet_fmhy_entry_t e;
                                memset(&e, 0, sizeof(e));
                                e.name = name;
                                e.url = url;
                                e.description = description;
                                e.category = category;
                                e.is_starred = is_starred;
                                e.mirrors = mirrors;
                                e.mirror_count = mn;
                                if (!list_push(&list, e)) free_entry_fields(&e);
                                name = url = NULL; // ownership moved
                            }
                            free(name);
                            free(url);
                        }
                    }
                }
                free(line);
            }
        }

    next_line:
        if (!nl) break;
        cursor = nl + 1;
    }

    free(h1);
    free(h2);
    if (out_count) *out_count = list.count;
    return list.items;
}

// ─── tracker sources ─────────────────────────────────────

static const aethernet_fmhy_tracker_source_t kTrackerSources[] = {
    {"ngosang/trackerslist", "https://ngosang.github.io/trackerslist/trackers_all.txt", "Community-maintained list of all known public BitTorrent trackers."},
    {"XIU2/TrackersListCollection (all)", "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/all.txt", "Comprehensive tracker collection maintained by XIU2, updated daily."},
    {"XIU2/TrackersListCollection (best)", "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/best.txt", "Curated best-performing tracker subset from the XIU2 collection."},
    {"newtrackon (stable)", "https://newtrackon.com/api/stable", "Live-monitored stable tracker list from newtrackon.com."},
    {"openwebtorrent", "https://openwebtorrent.com/", "Free WebTorrent-compatible tracker for browser-based torrenting."},
};

const aethernet_fmhy_tracker_source_t *aethernet_fmhy_tracker_sources(int32_t *out_count) {
    if (out_count) *out_count = (int32_t)(sizeof(kTrackerSources) / sizeof(kTrackerSources[0]));
    return kTrackerSources;
}

// ─── service ─────────────────────────────────────────────

struct aethernet_fmhy_service {
    aethernet_fmhy_entry_t *entries;
    int32_t count;
};

aethernet_fmhy_service_t *aethernet_fmhy_service_new(void) {
    return (aethernet_fmhy_service_t *)calloc(1, sizeof(aethernet_fmhy_service_t));
}

void aethernet_fmhy_service_free(aethernet_fmhy_service_t *service) {
    if (!service) return;
    aethernet_fmhy_entries_free(service->entries, service->count);
    free(service);
}

void aethernet_fmhy_sync(aethernet_fmhy_service_t *service, const char *markdown) {
    if (!service) return;
    aethernet_fmhy_entries_free(service->entries, service->count);
    service->entries = aethernet_fmhy_parse_markdown(markdown, &service->count);
}

int32_t aethernet_fmhy_entry_count(aethernet_fmhy_service_t *service) {
    return service ? service->count : 0;
}

static aethernet_fmhy_entry_t *filter_copy(
    aethernet_fmhy_service_t *service, const char *category_filter, bool starred_only, int32_t *out_count) {
    if (out_count) *out_count = 0;
    if (!service) return NULL;
    bool has_filter = category_filter && category_filter[0] != '\0';

    entry_list_t out = {0};
    for (int32_t i = 0; i < service->count; i++) {
        const aethernet_fmhy_entry_t *e = &service->entries[i];
        if (starred_only && !e->is_starred) continue;
        if (has_filter && !contains_ci(e->category, category_filter)) continue;
        aethernet_fmhy_entry_t c = copy_entry(e);
        if (!list_push(&out, c)) free_entry_fields(&c);
    }
    if (out_count) *out_count = out.count;
    return out.items;
}

aethernet_fmhy_entry_t *aethernet_fmhy_browse(aethernet_fmhy_service_t *service, const char *category_filter, int32_t *out_count) {
    return filter_copy(service, category_filter, false, out_count);
}

aethernet_fmhy_entry_t *aethernet_fmhy_get_starred(aethernet_fmhy_service_t *service, const char *category_filter, int32_t *out_count) {
    return filter_copy(service, category_filter, true, out_count);
}
