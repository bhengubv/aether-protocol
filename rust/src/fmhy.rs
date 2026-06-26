// SPDX-License-Identifier: MIT
//! Free Media Heck Yeah (FMHY) content catalogue (Phase-2 extension).
//!
//! Propagated over the Aether mesh so offline peers benefit from entries fetched
//! by connected peers. Port of the C# reference (AetherNet.Fmhy): a markdown
//! parser for the FMHY single-page dump plus an in-memory catalogue. The parser
//! is hand-rolled (the crate has no `regex` dependency) but mirrors the reference
//! grammar exactly.

/// A single resource parsed from the FMHY directory.
#[derive(Clone, Debug)]
pub struct FmhyEntry {
    pub name: String,
    pub url: String,
    pub description: Option<String>,
    pub category: String, // "H1" or "H1 / H2"
    pub is_starred: bool,
    pub mirrors: Vec<String>,
}

impl FmhyEntry {
    /// All URLs: primary followed by any mirrors.
    pub fn all_urls(&self) -> Vec<String> {
        let mut v = Vec::with_capacity(1 + self.mirrors.len());
        v.push(self.url.clone());
        v.extend(self.mirrors.iter().cloned());
        v
    }
}

/// A known torrent tracker-list aggregator.
#[derive(Clone, Debug)]
pub struct TrackerSource {
    pub name: String,
    pub url: String,
    pub description: String,
}

/// The public FMHY single-page endpoint.
pub const FMHY_API_URL: &str = "https://api.fmhy.net/single-page";

/// Well-known public tracker-list aggregators bundled with this release.
pub fn built_in_tracker_sources() -> Vec<TrackerSource> {
    fn t(name: &str, url: &str, desc: &str) -> TrackerSource {
        TrackerSource { name: name.to_string(), url: url.to_string(), description: desc.to_string() }
    }
    vec![
        t("ngosang/trackerslist", "https://ngosang.github.io/trackerslist/trackers_all.txt", "Community-maintained list of all known public BitTorrent trackers."),
        t("XIU2/TrackersListCollection (all)", "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/all.txt", "Comprehensive tracker collection maintained by XIU2, updated daily."),
        t("XIU2/TrackersListCollection (best)", "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/best.txt", "Curated best-performing tracker subset from the XIU2 collection."),
        t("newtrackon (stable)", "https://newtrackon.com/api/stable", "Live-monitored stable tracker list from newtrackon.com."),
        t("openwebtorrent", "https://openwebtorrent.com/", "Free WebTorrent-compatible tracker for browser-based torrenting."),
    ]
}

// Heading: 1-2 leading '#', then whitespace, then title. Returns (level, title).
fn parse_heading(line: &str) -> Option<(usize, String)> {
    let b = line.as_bytes();
    let mut i = 0;
    while i < b.len() && b[i] == b'#' {
        i += 1;
    }
    if (i == 1 || i == 2) && i < b.len() && (b[i] == b' ' || b[i] == b'\t') {
        return Some((i, line[i..].trim().to_string()));
    }
    None
}

// Bullet: optional leading whitespace, '*' or '-', required whitespace, content.
fn parse_bullet(line: &str) -> Option<&str> {
    let trimmed = line.trim_start();
    let b = trimmed.as_bytes();
    if b.is_empty() || (b[0] != b'*' && b[0] != b'-') {
        return None;
    }
    let rest = &trimmed[1..];
    let content = rest.trim_start();
    if rest.len() == content.len() || content.is_empty() {
        return None; // require whitespace after the bullet, and non-empty content
    }
    Some(content)
}

// First **[name](url)** in `content`. Returns (name, url, byte offset after ")**").
fn find_bold_link(content: &str) -> Option<(String, String, usize)> {
    let mut from = 0;
    while let Some(rel) = content[from..].find("**[") {
        let p = from + rel;
        let name_start = p + 3;
        if let Some(nrel) = content[name_start..].find(']') {
            let name_end = name_start + nrel;
            if content[name_end..].starts_with("](") {
                let url_start = name_end + 2;
                if let Some(urel) = content[url_start..].find(')') {
                    let url_end = url_start + urel;
                    if content[url_end..].starts_with(")**") {
                        return Some((
                            content[name_start..name_end].trim().to_string(),
                            content[url_start..url_end].trim().to_string(),
                            url_end + 3,
                        ));
                    }
                }
            }
        }
        from = p + 3;
    }
    None
}

// Plain [name](url) links in `region`; returns their URLs.
fn plain_link_urls(region: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut from = 0;
    while let Some(rel) = region[from..].find('[') {
        let p = from + rel;
        if let Some(nrel) = region[p + 1..].find(']') {
            let name_end = p + 1 + nrel;
            if region[name_end..].starts_with("](") {
                let url_start = name_end + 2;
                if let Some(urel) = region[url_start..].find(')') {
                    let url_end = url_start + urel;
                    out.push(region[url_start..url_end].to_string());
                    from = url_end + 1;
                    continue;
                }
            }
        }
        from = p + 1;
    }
    out
}

// Replace [name](url) with name (strip residual markdown links from a description).
fn strip_links(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut from = 0;
    while let Some(rel) = text[from..].find('[') {
        let p = from + rel;
        out.push_str(&text[from..p]);
        if let Some(nrel) = text[p + 1..].find(']') {
            let name_end = p + 1 + nrel;
            if text[name_end..].starts_with("](") {
                let url_start = name_end + 2;
                if let Some(urel) = text[url_start..].find(')') {
                    let url_end = url_start + urel;
                    out.push_str(&text[p + 1..name_end]); // keep the name
                    from = url_end + 1;
                    continue;
                }
            }
        }
        out.push('[');
        from = p + 1;
    }
    out.push_str(&text[from..]);
    out
}

/// Parse a raw FMHY markdown string into a flat list of entries in document order.
pub fn parse_fmhy_markdown(markdown: &str) -> Vec<FmhyEntry> {
    let mut entries = Vec::new();
    let mut h1 = String::new();
    let mut h2 = String::new();

    for raw in markdown.split('\n') {
        let line = raw.trim_end_matches([' ', '\t', '\r']);
        if line.is_empty() {
            continue;
        }

        if let Some((level, title)) = parse_heading(line) {
            if level == 1 {
                h1 = title;
                h2 = String::new();
            } else {
                h2 = title;
            }
            continue;
        }

        let content = match parse_bullet(line) {
            Some(c) => c,
            None => continue,
        };
        let is_starred = content.contains('\u{2B50}'); // ⭐

        let (name, url, bold_end) = match find_bold_link(content) {
            Some(v) => v,
            None => continue,
        };
        if url.is_empty() || url.starts_with('#') {
            continue;
        }

        let mut description: Option<String> = None;
        let desc_sep = content[bold_end..].find(" - ").map(|r| r + bold_end);
        if let Some(sep) = desc_sep {
            let d = strip_links(content[sep + 3..].trim()).trim().to_string();
            if !d.is_empty() {
                description = Some(d);
            }
        }

        let mirror_region = match desc_sep {
            Some(sep) => &content[bold_end..sep],
            None => &content[bold_end..],
        };
        let mut mirrors = Vec::new();
        for mu in plain_link_urls(mirror_region) {
            let mu = mu.trim().to_string();
            if !mu.is_empty() && mu != url && !mu.starts_with('#') {
                mirrors.push(mu);
            }
        }

        let category = if !h2.is_empty() {
            format!("{h1} / {h2}")
        } else {
            h1.clone()
        };
        entries.push(FmhyEntry { name, url, description, category, is_starred, mirrors });
    }
    entries
}

/// In-memory FMHY catalogue, seeded optionally and updated via [`InMemoryFmhyCatalogueService::sync`].
#[derive(Default)]
pub struct InMemoryFmhyCatalogueService {
    entries: Vec<FmhyEntry>,
    last_synced_at_secs: Option<u64>,
}

impl InMemoryFmhyCatalogueService {
    pub fn new(seed: Vec<FmhyEntry>) -> Self {
        Self { entries: seed, last_synced_at_secs: None }
    }

    pub fn entry_count(&self) -> usize {
        self.entries.len()
    }

    pub fn last_synced_at_secs(&self) -> Option<u64> {
        self.last_synced_at_secs
    }

    /// Replace the catalogue from a fresh FMHY markdown string.
    pub fn sync(&mut self, markdown: &str) {
        self.entries = parse_fmhy_markdown(markdown);
        self.last_synced_at_secs = Some(
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_secs())
                .unwrap_or(0),
        );
    }

    /// All entries, optionally filtered by a case-insensitive category substring.
    pub fn browse(&self, category_filter: Option<&str>) -> Vec<FmhyEntry> {
        match category_filter {
            None | Some("") => self.entries.clone(),
            Some(cf) => {
                let cf = cf.to_lowercase();
                self.entries
                    .iter()
                    .filter(|e| e.category.to_lowercase().contains(&cf))
                    .cloned()
                    .collect()
            }
        }
    }

    /// Only starred entries, optionally category-filtered.
    pub fn get_starred(&self, category_filter: Option<&str>) -> Vec<FmhyEntry> {
        let cf = category_filter.filter(|s| !s.is_empty()).map(|s| s.to_lowercase());
        self.entries
            .iter()
            .filter(|e| {
                e.is_starred && cf.as_ref().map_or(true, |c| e.category.to_lowercase().contains(c))
            })
            .cloned()
            .collect()
    }

    /// The bundled tracker-list aggregators.
    pub fn get_tracker_sources(&self) -> Vec<TrackerSource> {
        built_in_tracker_sources()
    }
}
