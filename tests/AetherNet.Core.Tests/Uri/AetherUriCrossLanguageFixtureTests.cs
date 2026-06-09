// SPDX-License-Identifier: MIT

using System.IO;
using System.Linq;
using System.Text.Json;
using AetherNet.Addressing;
using Xunit;

namespace AetherNet.Core.Tests.Uri;

/// <summary>
/// Drives the C# reference implementation through the same JSON corpus that every
/// other AetherNet SDK consumes. If a fixture passes here and fails in another
/// language port, the port is wrong — not the corpus. The corpus lives at
/// <c>tests/cross-language/uri-fixtures.json</c>.
/// </summary>
public class AetherUriCrossLanguageFixtureTests
{
    private static readonly JsonElement Root = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        // Walk up from the test bin dir until we hit a folder containing tests/cross-language.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "cross-language", "uri-fixtures.json");
            if (File.Exists(candidate))
                return JsonDocument.Parse(File.ReadAllText(candidate)).RootElement.Clone();
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate tests/cross-language/uri-fixtures.json walking up from " +
            AppContext.BaseDirectory);
    }

    public static IEnumerable<object[]> ValidCases() =>
        Root.GetProperty("valid").EnumerateArray()
            .Select(e => new object[] { e.GetProperty("name").GetString()!, e });

    public static IEnumerable<object[]> InvalidCases() =>
        Root.GetProperty("invalid").EnumerateArray()
            .Select(e => new object[] { e.GetProperty("name").GetString()!, e });

    public static IEnumerable<object[]> ManifestCases() =>
        Root.GetProperty("manifest").GetProperty("matches").EnumerateArray()
            .Select(e => new object[] { e.GetProperty("input").GetString()!, e });

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void Valid_Fixture_ParsesToExpectedComponents(string name, JsonElement fixture)
    {
        _ = name;
        var input = fixture.GetProperty("input").GetString()!;
        var canonical = fixture.GetProperty("canonical").GetString()!;
        var u = AetherUri.Parse(input);
        Assert.Equal(canonical, u.ToString());
        Assert.Equal(fixture.GetProperty("authority").GetString(), u.Authority);
        Assert.Equal(fixture.GetProperty("path").GetString(), u.Path);
        Assert.Equal(fixture.GetProperty("handlerName").GetString(), u.HandlerName);
        Assert.Equal(fixture.GetProperty("fragment").GetString(), u.Fragment);

        var expectedQuery = fixture.GetProperty("query").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
        Assert.Equal(expectedQuery.Count, u.Query.Count);
        foreach (var kv in expectedQuery)
            Assert.Equal(kv.Value, u.Query[kv.Key]);

        var expectedSegs = fixture.GetProperty("pathSegments").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Equal(expectedSegs, u.PathSegments.ToArray());
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Invalid_Fixture_FailsToParse(string name, JsonElement fixture)
    {
        _ = name;
        var input = fixture.GetProperty("input").GetString()!;
        Assert.False(AetherUri.TryParse(input, out _, out _));
    }

    [Theory]
    [MemberData(nameof(ManifestCases))]
    public void Manifest_Fixture_ResolvesAsExpected(string input, JsonElement fixture)
    {
        // Build the manifest from the fixture's manifest definition.
        var manifestDef = Root.GetProperty("manifest");
        var appId = manifestDef.GetProperty("appId").GetString()!;
        var handlers = manifestDef.GetProperty("handlers").EnumerateArray()
            .Select(h => new AetherUriHandlerDescriptor(
                h.GetProperty("handlerName").GetString()!,
                h.GetProperty("pathTemplate").GetString()!))
            .ToList();
        var manifest = new AetherUriHandlerManifest(appId, handlers);

        var u = AetherUri.Parse(input);
        var resolved = manifest.Resolve(u);
        var expectedMatched = fixture.GetProperty("matched").GetBoolean();

        if (!expectedMatched)
        {
            Assert.Null(resolved);
            return;
        }

        Assert.NotNull(resolved);
        var expectedIndex = fixture.GetProperty("handlerIndex").GetInt32();
        Assert.Same(handlers[expectedIndex], resolved!.Value.Handler);
        var expectedCaps = fixture.GetProperty("captures").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Equal(expectedCaps.Count, resolved.Value.Captures.Count);
        foreach (var kv in expectedCaps)
            Assert.Equal(kv.Value, resolved.Value.Captures[kv.Key]);
    }
}
