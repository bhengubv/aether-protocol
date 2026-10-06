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
Every packable C# library shares the version via `Directory.Build.props` - **37** of the
39 projects in `src/`, the two exceptions being the apps `AetherNetService` and
`AetherNetService.Setup`, which set `<IsPackable>false</IsPackable>`. **18** are live on
nuget.org at `3.0.0` (counted 2026-10-05); the rest pack but have not been pushed.
The old text said "9", which was true of a much smaller repo:
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
