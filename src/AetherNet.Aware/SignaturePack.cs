// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/SignatureExchange.kt (github.com/offgridpete/fieldwatch,
// cf6562d): the pack format and reading it. Exporting, importing and merging packs come with packs over the mesh.
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherNet.Aware;

/// <summary>A signature pack in Fieldwatch's format ("fieldwatch-signatures", format version 1).</summary>
public sealed record SignaturePack
{
    public const string FieldwatchFormat = "fieldwatch-signatures";

    /// <summary>The format's earlier name; still read.</summary>
    public const string SpectreFormat = "spectre-signatures";

    public string Format { get; init; } = FieldwatchFormat;

    public int FormatVersion { get; init; } = 1;

    public string ExportedAt { get; init; } = "";

    public string AppVersion { get; init; } = "";

    public int CatalogVersion { get; init; }

    public IReadOnlyList<Fleet> Fleets { get; init; } = [];
}

/// <summary>A read pack, and how many decode maps were dropped because their source is not one this version reads.</summary>
public sealed record ParsedSignaturePack(SignaturePack Pack, int SkippedDecode);

public static class SignatureExchange
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        Converters =
        {
            new JsonStringEnumConverter<RadioKind>(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false),
            new JsonStringEnumConverter<RuleKind>(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false),
            new JsonStringEnumConverter<SignatureClass>(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false),
            new JsonStringEnumConverter<DecodeType>(allowIntegerValues: false),
            new JsonStringEnumConverter<DecodeEndian>(allowIntegerValues: false),
            new JsonStringEnumConverter<DecodeWhenOp>(allowIntegerValues: false),
            new DecodeSourceConverter(),
        },
    };

    public static SignaturePack Parse(string text) => ParsePack(text).Pack;

    /// <summary>
    /// Reads a pack. Throws <see cref="ArgumentException"/> with a plain reason when the text is empty, not a pack,
    /// or has no signatures. A decode map with a source this version does not read is dropped; its signature stays.
    /// </summary>
    public static ParsedSignaturePack ParsePack(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim().TrimStart('﻿');
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("This file is empty.");
        }
        SignaturePack? pack;
        try
        {
            pack = JsonSerializer.Deserialize<SignaturePack>(trimmed, Json);
        }
        catch (JsonException e)
        {
            throw new ArgumentException("Not a Fieldwatch signature pack.", e);
        }
        if (pack is null)
        {
            throw new ArgumentException("Not a Fieldwatch signature pack.");
        }
        if (pack.Format != SignaturePack.FieldwatchFormat && pack.Format != SignaturePack.SpectreFormat)
        {
            throw new ArgumentException(
                "Not a Fieldwatch signature pack (open a fieldwatch-signatures JSON file; spectre-signatures still imports).");
        }
        if (pack.Fleets.Count == 0)
        {
            throw new ArgumentException("This pack has no signatures.");
        }
        var skipped = 0;
        var fleets = new List<Fleet>(pack.Fleets.Count);
        foreach (var fleet in pack.Fleets)
        {
            // List elements are not null-checked by the reader; Kotlin's reader refuses them, so this does too.
            if (fleet is null || fleet.Rules.Any(r => r is null) || (fleet.Decode?.Fields.Any(f => f is null) ?? false))
            {
                throw new ArgumentException("Not a Fieldwatch signature pack.");
            }
            if (fleet.Decode?.Source == DecodeSource.Unsupported)
            {
                skipped++;
                fleets.Add(fleet with { Decode = null });
            }
            else
            {
                fleets.Add(fleet);
            }
        }
        return new ParsedSignaturePack(pack with { Fleets = fleets }, skipped);
    }

    /// <summary>"manufacturerData" / "serviceData"; any other name reads as <see cref="DecodeSource.Unsupported"/>.</summary>
    private sealed class DecodeSourceConverter : JsonConverter<DecodeSource>
    {
        public override DecodeSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("A decode source is a name.");
            }
            return reader.GetString() switch
            {
                "manufacturerData" => DecodeSource.ManufacturerData,
                "serviceData" => DecodeSource.ServiceData,
                _ => DecodeSource.Unsupported,
            };
        }

        public override void Write(Utf8JsonWriter writer, DecodeSource value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value == DecodeSource.ServiceData ? "serviceData" : "manufacturerData");
    }
}

/// <summary>
/// The stock signatures: Fieldwatch's pack (dist/fieldwatch-signatures-v2.json at cf6562d, catalog 90 — 252
/// signatures, 6,981 rules), shipped unchanged inside this library.
/// </summary>
public static class StockSignatures
{
    /// <summary>Tesla phone-key adverts use Apple's iBeacon layout with this prefix; they are Tesla, not a beacon.</summary>
    public const string TeslaIBeaconMfgPrefix = "021574278BDAB64445208F0C720EAF059935";

    /// <summary>Target's in-store Atrius beacons.</summary>
    public const string TargetAtriusIBeaconMfgPrefix = "02155993A94C7D974DF79ABFE493BFD5D000";

    private const string ResourceName = "AetherNet.Aware.fieldwatch-signatures-v2.json";

    private static readonly Lazy<string> RawJson = new(ReadResource);
    private static readonly Lazy<SignaturePack> ParsedPack = new(() => SignatureExchange.Parse(RawJson.Value));

    /// <summary>The pack's text, as shipped.</summary>
    public static string Json => RawJson.Value;

    public static SignaturePack Pack => ParsedPack.Value;

    /// <summary>The stock signatures. The same list every time, so the matcher compiles them once.</summary>
    public static IReadOnlyList<Fleet> Fleets => Pack.Fleets;

    public static int CatalogVersion => Pack.CatalogVersion;

    private static string ReadResource()
    {
        using var stream = typeof(StockSignatures).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The stock signature pack ({ResourceName}) is missing from {typeof(StockSignatures).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
