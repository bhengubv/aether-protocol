// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/Models.kt (github.com/offgridpete/fieldwatch, cf6562d).
// Field names and defaults follow Fieldwatch's signature-pack format ("fieldwatch-signatures", format version 1).
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

using System.Text.Json.Serialization;

namespace AetherNet.Aware;

/// <summary>What a match rule looks at.</summary>
public enum RuleKind
{
    /// <summary>The first three bytes of the address, or a Wi-Fi vendor element's OUI.</summary>
    Oui,

    /// <summary>The address starts with this (a whole address pins one radio).</summary>
    MacPrefix,
    NameContains,

    /// <summary>The whole name matches a pattern: <c>*</c> any run, <c>?</c> one character.</summary>
    NameGlob,
    ServiceUuid,
    ServiceData,
    ManufacturerId,
    ManufacturerData,
    RadioKind,
    HiddenSsid,
    VendorIeOui,
}

/// <summary>One rule of a signature.</summary>
public sealed record MatchRule
{
    public required RuleKind Kind { get; init; }

    public string Text { get; init; } = "";

    public int CompanyId { get; init; }

    public string DataPrefixHex { get; init; } = "";

    /// <summary>Only this radio; null means either.</summary>
    public RadioKind? Radio { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Whether the rule could match a Bluetooth advert.</summary>
    public bool CouldMatchBle() => Kind switch
    {
        RuleKind.HiddenSsid or RuleKind.VendorIeOui => false,
        RuleKind.ServiceUuid or RuleKind.ServiceData or RuleKind.ManufacturerId or RuleKind.ManufacturerData => true,
        _ => Radio != AetherNet.Aware.RadioKind.Wifi,
    };
}

/// <summary>
/// A signature ("fleet" in Fieldwatch's format): a named family of radios and the rules that recognise it.
/// </summary>
public sealed record Fleet
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Carried for the person's mute; the matcher matches every row it is given, as Fieldwatch's does.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>True: any rule matching is enough. False: every rule must match.</summary>
    public bool MatchAny { get; init; } = true;

    public int ColorIndex { get; init; }

    public IReadOnlyList<MatchRule> Rules { get; init; } = [];

    /// <summary>Above zero: only label a radio when this many of its kind are heard together.</summary>
    public int MinPeers { get; init; }

    public int PeerWindowSec { get; init; } = 60;

    public bool ClusterByOui { get; init; }

    public bool SequentialMac { get; init; }

    public string Notes { get; init; } = "";

    /// <summary>A caution for the person. Empty means none.</summary>
    public string AttentionNote { get; init; } = "";

    public bool BuiltIn { get; init; }

    public SignatureClass Kind { get; init; } = SignatureClass.Other;

    /// <summary>How to read the advert's own fields (a sensor's temperature, a drone's position). Null means none.</summary>
    public FleetDecode? Decode { get; init; }

    /// <summary>Decode fields only apply to Bluetooth adverts.</summary>
    public bool CanHaveBleDecode() => Rules.Count == 0 || Rules.Any(r => r.CouldMatchBle());
}

/// <summary>Where a decode map reads its bytes from.</summary>
public enum DecodeSource
{
    ManufacturerData,
    ServiceData,

    /// <summary>A source this version does not read; the pack's decode map is dropped and the signature kept.</summary>
    Unsupported,
}

public enum DecodeType
{
    [JsonStringEnumMemberName("u8")] U8,
    [JsonStringEnumMemberName("i8")] I8,
    [JsonStringEnumMemberName("u16")] U16,
    [JsonStringEnumMemberName("i16")] I16,
    [JsonStringEnumMemberName("u24")] U24,
    [JsonStringEnumMemberName("u32")] U32,
    [JsonStringEnumMemberName("i32")] I32,
    [JsonStringEnumMemberName("f32")] F32,
    [JsonStringEnumMemberName("bits")] Bits,
    [JsonStringEnumMemberName("utf8")] Utf8,
    [JsonStringEnumMemberName("hex")] Hex,
    [JsonStringEnumMemberName("mac")] Mac,
    [JsonStringEnumMemberName("bool")] Bool,
}

public enum DecodeEndian
{
    [JsonStringEnumMemberName("le")] Le,
    [JsonStringEnumMemberName("be")] Be,
}

public enum DecodeWhenOp
{
    [JsonStringEnumMemberName("eq")] Eq,
    [JsonStringEnumMemberName("neq")] Neq,

    /// <summary>Every 1-bit in the hex is set in the payload slice.</summary>
    [JsonStringEnumMemberName("mask")] Mask,

    /// <summary>Every 1-bit in the hex is clear in the payload slice.</summary>
    [JsonStringEnumMemberName("nmask")] Nmask,

    /// <summary>The payload is exactly <see cref="DecodeWhen.Length"/> bytes long.</summary>
    [JsonStringEnumMemberName("len")] Len,
}

/// <summary>A condition on the payload before a field is read.</summary>
public sealed record DecodeWhen
{
    public required int Offset { get; init; }

    public int Length { get; init; } = 1;

    public required DecodeWhenOp Op { get; init; }

    public required string ValueHex { get; init; }

    /// <summary>A further condition; both must hold.</summary>
    public DecodeWhen? And { get; init; }
}

/// <summary>One field of a decode map.</summary>
public sealed record DecodeField
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required int Offset { get; init; }

    public int? Length { get; init; }

    public required DecodeType Type { get; init; }

    public DecodeEndian Endian { get; init; } = DecodeEndian.Le;

    public int? BitOffset { get; init; }

    public int? BitWidth { get; init; }

    public double? Scale { get; init; }

    public double? OffsetAdd { get; init; }

    /// <summary>Remainder after the read, before <see cref="Scale"/>.</summary>
    public double? Modulo { get; init; }

    public string? Unit { get; init; }

    [JsonPropertyName("enum")]
    public IReadOnlyDictionary<string, string>? EnumLabels { get; init; }

    /// <summary>Show this field's value beside the signature name.</summary>
    public bool Live { get; init; }

    /// <summary>Raw values whose live label is the stronger one.</summary>
    public IReadOnlyList<string> LiveEmphasis { get; init; } = [];

    /// <summary>A sentence for a named value, keyed like <see cref="EnumLabels"/>.</summary>
    public IReadOnlyDictionary<string, string>? EnumNotes { get; init; }

    [JsonPropertyName("when")]
    public DecodeWhen? Gate { get; init; }

    /// <summary>How many bytes the field reads.</summary>
    public int ResolvedLength()
    {
        if (Length is > 0)
        {
            return Length.Value;
        }
        if (Type == DecodeType.Bits)
        {
            var start = BitOffset ?? 0;
            var width = BitWidth ?? 1;
            return Math.Max((start + width + 7) / 8, 1);
        }
        return Type.DefaultLength();
    }
}

/// <summary>A signature's decode map.</summary>
public sealed record FleetDecode
{
    public required DecodeSource Source { get; init; }

    public string? ServiceUuid { get; init; }

    public int? CompanyId { get; init; }

    /// <summary>Put the two-byte little-endian company ID in front of the manufacturer data first.</summary>
    public bool IncludeCompanyId { get; init; }

    public IReadOnlyList<DecodeField> Fields { get; init; } = [];
}

public static class DecodeTypeLength
{
    public static int DefaultLength(this DecodeType type) => type switch
    {
        DecodeType.U8 or DecodeType.I8 or DecodeType.Bool => 1,
        DecodeType.U16 or DecodeType.I16 => 2,
        DecodeType.U24 => 3,
        DecodeType.U32 or DecodeType.I32 or DecodeType.F32 => 4,
        DecodeType.Mac => 6,
        _ => 1,
    };
}
