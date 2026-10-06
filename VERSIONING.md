# Versioning policy — aether-protocol

## Last published version: `3.3.1`

> **This number is what is ON nuget.org, not what is in the tree.** Step 1 of a release
> bumps `<VersionPrefix>` in `Directory.Build.props`, and the rest (changelog, tag,
> publish) can be days later - so a bumped `Directory.Build.props` AHEAD of this line is
> the normal mid-release state and is NOT a stale document. Verified 2026-10-06:
> `Directory.Build.props`, the newest tag `v3.3.1`, the newest `CHANGELOG.md` section
> `[3.3.1]` and nuget.org's own index all agree on `3.3.1` - nothing in flight. Verified
> 2026-10-07 from the feed, not from the word "pushed".
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

### Pre-release
A pre-release is the same hand-run publish with a suffix on it - there is no CI to set it:
```bash
dotnet pack packaging/AetherNet/AetherNet.csproj -c Release -p:VersionSuffix=alpha.1
# Produces: AetherNet.3.3.1-alpha.1.nupkg
```

### Stable release
1. Bump `<VersionPrefix>` in `Directory.Build.props`.
2. Update `CHANGELOG.md` (or create it) with the release summary.
3. Tag: `git tag -a v0.X.Y -m "..." && git push origin v0.X.Y`.
4. Publish **by hand, to both feeds**. There is no CI doing this - commits carry
   `[skip ci]` - and an earlier version of this list said there was, which is a good way
   to tag a release that never reaches anybody:

   ```powershell
   . C:\Dev\Solutions\com.bhengubv	hegeeknetwork\Deployment\deployment-credentials.ps1
   & C:\Dev\Solutions\com.bhengubv\Toolbox\Publish-NuGet.ps1 `
       -RepoPath C:\Dev\Solutions\com.bhengubvether-protocol `
       -Prefix AetherNet -SourceDir packaging -ApiKey $NUGET_CREDENTIALS.API_KEY
   ```

   Then the GitHub Packages mirror, which needs no new credential because `gh` already
   holds `write:packages`:

   ```bash
   for f in artifacts-nuget/*.nupkg; do
     dotnet nuget push "$f" -s https://nuget.pkg.github.com/bhengubv/index.json        -k "$(gh auth token)" --skip-duplicate
   done
   ```

   `-SourceDir packaging` is the part that is easy to get wrong: the script defaults to
   `src/`, where nothing is packable, and reports "Nothing to push."
5. **Verify from the feed, never from the word "pushed."** nuget.org's flat container lags
   its own validation by up to an hour, so a missing version there is not a failed push -
   re-push it and read the result: `409 ... already exists` means it landed.
6. `gh release create` with the notes and the APKs attached.

### Single-place version bump
Everything shares the version via `Directory.Build.props`. **One** package is published
from `packaging/`: **`AetherNet`**, holding **one assembly**, `AetherNet.dll`, per target
framework. It is compiled from the source of the 37 library projects in `src/`; **all 39**
projects in `src/` set `<IsPackable>false</IsPackable>`. Asked of MSBuild rather than
grepped, 2026-10-06: 39 projects, 39 `false`, 0 `true`. Nothing else is published beside it.

The shape got here in three moves, and only the last was asked for: `38cd664`
("Thirty-two packages become four"), `81bde6f` ("Thirty-two packages become one", which
was still thirty-seven assemblies plus twenty-three pointer packages) and `63986e4`
("One package, one assembly").

> Two counts have been wrong here. The original said **9**, true of a much smaller repo. I
> replaced it with **37**, from a `git grep` for `<IsPackable>false`, whose angle brackets
> the shell ate — so it found 2 of 39 and I believed it, one commit after being told why an
> unverified number does not belong in a document. `dotnet msbuild -getProperty:IsPackable`
> is the answer that cannot be wrong.

A release publishes **`AetherNet` and nothing else**. The twenty-three other `AetherNet.*`
ids owned by `bhengubv` were pointer packages at 3.2.0 and are not published again: they
stay on nuget.org at `3.2.0`, keep restoring, and resolve `AetherNet 3.2.0`. Anybody on one
of them moves forward by referencing `AetherNet` directly. They cannot be removed - the
key cannot unlist - so do not describe them as gone.

```xml
<VersionPrefix>3.3.1</VersionPrefix>
```
Bump it once.

⚠️ **The publish key can push but not unlist, and `gh` cannot delete packages** - both
return 403. Superseded versions therefore stay listed, and nuget.org has **no supported
API for deprecation** either (the website UI only; `dotnet nuget deprecate` is an open
feature request). Do not write "unlisted" or "deprecated" into release notes without
having actually done it.

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
