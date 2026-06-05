// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using AetherMesh.Fmhy.Models;

namespace AetherMesh.Fmhy;

/// <summary>
/// Parses the FMHY single-page Markdown dump into a flat list of <see cref="FmhyEntry"/> records.
///
/// <para>
/// FMHY format (from <c>api.fmhy.net/single-page</c>):
/// <list type="bullet">
///   <item><c># Category</c> — H1 headings define top-level categories.</item>
///   <item><c>## Subcategory</c> — H2 appended to the current H1 for context.</item>
///   <item><c>* ⭐ **[Name](URL)** - Description</c> — starred entry.</item>
///   <item><c>* **[Name](URL)**, [Mirror](URL2) - Description</c> — entry with mirrors.</item>
/// </list>
/// </para>
/// </summary>
public static partial class FmhyMarkdownParser
{
    // Matches **[Name](URL)** link groups.
    [GeneratedRegex(@"\*\*\[([^\]]+)\]\(([^)]+)\)\*\*", RegexOptions.Compiled)]
    private static partial Regex BoldLinkRe();

    // Matches plain [Name](URL) links (mirrors, secondary links).
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex PlainLinkRe();

    // Matches H1 and H2 headings.
    [GeneratedRegex(@"^(#{1,2})\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex HeadingRe();

    // Matches bullet lines (* or -).
    [GeneratedRegex(@"^\s*[*\-]\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex BulletRe();

    /// <summary>
    /// Parse a raw FMHY Markdown string into a flat list of entries.
    /// Lines that do not match the expected bullet pattern are skipped silently.
    /// </summary>
    /// <param name="markdown">Full content of the FMHY single-page dump.</param>
    /// <returns>Parsed entries in document order.</returns>
    public static IReadOnlyList<FmhyEntry> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var entries  = new List<FmhyEntry>(4096);
        var h1       = string.Empty;
        var h2       = string.Empty;

        foreach (var rawLine in markdown.AsSpan().EnumerateLines())
        {
            var line = rawLine.ToString().TrimEnd();
            if (line.Length == 0) continue;

            // ── Heading ──────────────────────────────────────────────────────
            var hm = HeadingRe().Match(line);
            if (hm.Success)
            {
                var level = hm.Groups[1].Value.Length;
                var title = hm.Groups[2].Value.Trim();
                if (level == 1) { h1 = title; h2 = string.Empty; }
                else              h2 = title;
                continue;
            }

            // ── Bullet entry ─────────────────────────────────────────────────
            var bm = BulletRe().Match(line);
            if (!bm.Success) continue;

            var content  = bm.Groups[1].Value;
            var isStarred = content.Contains('⭐');

            // Primary link: first **[Name](URL)**
            var boldMatch = BoldLinkRe().Match(content);
            if (!boldMatch.Success) continue;

            var name        = boldMatch.Groups[1].Value.Trim();
            var url         = boldMatch.Groups[2].Value.Trim();
            if (string.IsNullOrEmpty(url) || url.StartsWith('#')) continue;

            // Description: text after first " - " separator that follows all links.
            var descSep     = content.IndexOf(" - ", boldMatch.Index + boldMatch.Length, StringComparison.Ordinal);
            string? desc    = descSep >= 0
                ? content[(descSep + 3)..].Trim()
                : null;
            // Strip any residual markdown from the description.
            if (desc != null) desc = Regex.Replace(desc, @"\[([^\]]+)\]\([^)]+\)", "$1").Trim();
            if (string.IsNullOrEmpty(desc)) desc = null;

            // Mirrors: any plain [Name](URL) after the primary bold link.
            var mirrors = new List<string>();
            var afterBold = content[(boldMatch.Index + boldMatch.Length)..];
            // Stop at the " - " description separator.
            var mirrorRegion = descSep >= 0
                ? content.Substring(boldMatch.Index + boldMatch.Length,
                                    descSep - boldMatch.Index - boldMatch.Length)
                : afterBold;
            foreach (Match pm in PlainLinkRe().Matches(mirrorRegion))
            {
                var mu = pm.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(mu) && mu != url && !mu.StartsWith('#'))
                    mirrors.Add(mu);
            }

            var category = h2.Length > 0 ? $"{h1} / {h2}" : h1;
            entries.Add(new FmhyEntry(name, url, desc, category, isStarred, [.. mirrors]));
        }

        return entries.AsReadOnly();
    }
}
