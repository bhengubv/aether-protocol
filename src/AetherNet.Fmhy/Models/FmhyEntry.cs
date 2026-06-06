// SPDX-License-Identifier: MIT

namespace AetherNet.Fmhy.Models;

/// <summary>
/// A single resource entry parsed from the FMHY directory.
/// </summary>
/// <param name="Name">Display name of the resource.</param>
/// <param name="Url">Primary URL.</param>
/// <param name="Description">Free-text description (null if not present).</param>
/// <param name="Category">H1 heading under which this entry appears in the FMHY markdown.</param>
/// <param name="IsStarred">True when the entry carries the FMHY ⭐ star (highly recommended).</param>
/// <param name="Mirrors">Additional mirror or related URLs listed on the same line.</param>
public sealed record FmhyEntry(
    string   Name,
    string   Url,
    string?  Description,
    string   Category,
    bool     IsStarred,
    string[] Mirrors)
{
    /// <summary>All URLs for this entry: primary + mirrors.</summary>
    public IEnumerable<string> AllUrls => Mirrors.Length == 0
        ? [Url]
        : Mirrors.Prepend(Url);
}
