// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherMesh.Fmhy.Models;

namespace AetherMesh.Fmhy;

/// <summary>
/// Loads the bundled FMHY seed catalogue from an embedded JSON resource.
/// The seed is used to pre-populate <see cref="InMemoryFmhyCatalogueService"/>
/// at construction so content discovery works immediately offline.
/// </summary>
public static class FmhySeedLoader
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true,
    };

    /// <summary>
    /// Deserialise a FMHY seed catalogue from a raw JSON string.
    /// The JSON must be an array of objects with fields:
    /// <c>name</c>, <c>url</c>, <c>description</c>, <c>category</c>,
    /// <c>isStarred</c>, <c>mirrors</c>.
    /// </summary>
    public static IReadOnlyList<FmhyEntry> LoadFromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var dtos = JsonSerializer.Deserialize<FmhyEntryDto[]>(json, _opts)
                   ?? Array.Empty<FmhyEntryDto>();

        return dtos.Select(d => new FmhyEntry(
            Name:        d.Name        ?? string.Empty,
            Url:         d.Url         ?? string.Empty,
            Description: string.IsNullOrWhiteSpace(d.Description) ? null : d.Description,
            Category:    d.Category    ?? string.Empty,
            IsStarred:   d.IsStarred,
            Mirrors:     d.Mirrors     ?? []))
        .Where(e => !string.IsNullOrEmpty(e.Url))
        .ToArray();
    }

    // Private DTO to bridge JSON camelCase ↔ record constructors.
    private sealed class FmhyEntryDto
    {
        [JsonPropertyName("name")]        public string?   Name        { get; set; }
        [JsonPropertyName("url")]         public string?   Url         { get; set; }
        [JsonPropertyName("description")] public string?   Description { get; set; }
        [JsonPropertyName("category")]    public string?   Category    { get; set; }
        [JsonPropertyName("isStarred")]   public bool      IsStarred   { get; set; }
        [JsonPropertyName("mirrors")]     public string[]? Mirrors     { get; set; }
    }
}
