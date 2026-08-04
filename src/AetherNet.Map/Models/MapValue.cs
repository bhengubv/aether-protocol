// SPDX-License-Identifier: MIT
using System.Globalization;

namespace AetherNet.Map.Models;

/// <summary>Scalar type tag for a <see cref="MapValue"/> — pins how the value serializes on the wire.</summary>
public enum MapValueKind : byte
{
    String = 0,
    Int = 1,
    Float = 2,
    Bool = 3,
}

/// <summary>
/// A typed scalar attribute value (a storefront's name/hours/phone, a "ramp = yes/no", a sensor reading).
/// Stored as its canonical invariant-culture text so equality and merge are trivial; the wire codec
/// re-encodes numerics as fixed binary (typed by <see cref="Kind"/>) for cross-language byte-identity.
/// </summary>
public readonly record struct MapValue(MapValueKind Kind, string Text)
{
    public static MapValue String(string value) => new(MapValueKind.String, value ?? string.Empty);
    public static MapValue Int(long value) => new(MapValueKind.Int, value.ToString(CultureInfo.InvariantCulture));
    public static MapValue Float(double value) => new(MapValueKind.Float, value.ToString("R", CultureInfo.InvariantCulture));
    public static MapValue Bool(bool value) => new(MapValueKind.Bool, value ? "true" : "false");

    public long AsInt() => long.Parse(Text, CultureInfo.InvariantCulture);
    public double AsFloat() => double.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture);
    public bool AsBool() => string.Equals(Text, "true", StringComparison.Ordinal);

    public override string ToString() => Text;
}
