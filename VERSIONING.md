# Versioning policy — aether-protocol

## Last published version: `3.0.0`

> **This number is what is ON nuget.org, not what is in the tree.** Step 1 of a release
> bumps `<VersionPrefix>` in `Directory.Build.props`, and steps 2-4 (changelog, tag,
> publish) can be days later - so a bumped `Directory.Build.props` AHEAD of this line is
> the normal mid-release state and is NOT a stale document. Verified 2026-10-05:
> nuget.org's newest `AetherNet.Core` is `3.0.0`, the newest tag is `v3.0.0`, the newest
> `CHANGELOG.md` section is `[3.0.0]`, and `Directory.Build.props` reads `3.1.0` - bumped,
> not yet released.
>
> The old heading said "Current version", which is ambiguous between the two, and that
> ambiguity cost real work: a correct `3.0.0` here was read as stale against a `3.1.0`
> props file and "corrected" to `3.1.0`, asserting a release that had not happened.

The project follows [Semantic Versioning 2.0.0](https://semver.org/).

---

## Version bump rules

| Change type | Example | Bump |
|---|---|---|
| Incompatible wire-format change | UUID byte-order fix, new mandatory packet field, ratchet info-string change | **MAJOR** |
| New backward-compatible feature | New packet type, new DI extension method, new language implementation | **MINOR** |
| Bug fix, security patch, doc update | Nonce-dedup fix, P-256 deadline removal, typo | **PATCH** |

> **Wire-break rule**: Any change that causes two versions to produce different bytes for the same inputs (serialization, X3DH, ratchet, KDF_RK) is a **MAJOR** bump. This applies across *all 8 language implementations*. If only one language diverges from the others, that language is the bug — fix it as a PATCH.

> **Milestone rule**: a landmark, category-expanding feature MAY be released as a **MAJOR** even when it introduces **no** wire-break — provided the release notes state **explicitly** that it is backward-compatible. The major digit then signals *significance*, not breakage. Example: `3.0.0` — real, interoperable BitTorrent + a gateway that bridges swarm content into the offline mesh + a resilient segmented downloader; all additive, no existing packet type / serialization / fixture changed. When in doubt, prefer a MINOR — reserve milestone-majors for genuine category shifts, and always label them non-breaking.

---

## How to release

### Pre-release (CI)
```bash
# In CI, set VersionSuffix for pre-release builds:
dotnet pack -c Release -p:VersionSuffix=alpha.1
# Produces: AetherNet.Core.0.1.0-alpha.1.nupkg
```

### Stable release (manual)
1. Bump `<VersionPrefix>` in `Directory.Build.props`.
2. Update `CHANGELOG.md` (or create it) with the release summary.
3. Tag: `git tag v0.X.Y && git push origin v0.X.Y`.
4. CI publishes `artifacts/packages/*.nupkg` to NuGet.org.

### Single-place version bump
Everything shares the version via `Directory.Build.props`. **Four** projects are packable
and they all live in `packaging/`: `AetherNet`, `AetherNet.Node`, `AetherNet.WebRtc`,
`AetherNet.Sqlite`. **All 39** projects in `src/` set `<IsPackable>false</IsPackable>` and
ship *inside* those bundles — `38cd664` ("Thirty-two packages become four") made that the
shape, and each csproj carries the reason on the line above it. Asked of MSBuild rather
than grepped, 2026-10-06.

> Two counts have been wrong here. The original said **9**, true of a much smaller repo. I
> replaced it with **37**, from a `git grep` for `<IsPackable>false`, whose angle brackets
> the shell ate — so it found 2 of 39 and I believed it, one commit after being told why an
> unverified number does not belong in a document. `dotnet msbuild -getProperty:IsPackable`
> is the answer that cannot be wrong.

On nuget.org, **18** `AetherNet.*` ids are live at `3.0.0` from the old thirty-two-package
shape, and **none of the four bundle ids exists yet** (checked 2026-10-06; all four return
404). So the next publish claims four new names and leaves those 18 at `3.0.0`, which is a
decision about the shape on NuGet rather than a mechanical release step:
```xml
<VersionPrefix>3.1.0</VersionPrefix>   <!-- in the tree today; 3.0.0 is the last published -->
```
Bump it once; all packages move together.

---

## Cross-language parity contract

Every non-patch release **must** pass the cross-language fixture suite before tagging:

```bash
# C#
dotnet test tests/cross-language/runners/csharp/AetherNet.InteropTest.csproj

# Go
cd go && go test ./...

# Python
cd python && pytest tests/

# TypeScript
cd typescript && npx jest

# Rust
cd rust && cargo test --tests

# Kotlin
cd kotlin && ./gradlew test

# Swift
cd swift && swift test
```

The `fixtures/signal/` corpus pins the exact byte outputs for X3DH, HMAC ratchet, and KDF_RK. Any fixture failure = wire break = MAJOR version bump if intentional, or a bug if not.
