# Versioning policy — aether-protocol

## Current version: `0.1.0`

The project follows [Semantic Versioning 2.0.0](https://semver.org/).

---

## Version bump rules

| Change type | Example | Bump |
|---|---|---|
| Incompatible wire-format change | UUID byte-order fix, new mandatory packet field, ratchet info-string change | **MAJOR** |
| New backward-compatible feature | New packet type, new DI extension method, new language implementation | **MINOR** |
| Bug fix, security patch, doc update | Nonce-dedup fix, P-256 deadline removal, typo | **PATCH** |

> **Wire-break rule**: Any change that causes two versions to produce different bytes for the same inputs (serialization, X3DH, ratchet, KDF_RK) is a **MAJOR** bump. This applies across *all 8 language implementations*. If only one language diverges from the others, that language is the bug — fix it as a PATCH.

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
All 9 packable C# libraries share the version via `Directory.Build.props`:
```xml
<VersionPrefix>0.1.0</VersionPrefix>
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
