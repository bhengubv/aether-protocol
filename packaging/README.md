# packaging/

Nothing in here holds any code. `src/` holds the code — about three dozen projects, every one of them
`IsPackable=false` — and this folder decides what shape that code takes on NuGet.

## One package

**`AetherNet` is the package.** It carries the assemblies of every `src/` project, so a consumer writes one
`PackageReference` and has the protocol: messaging, every transport including WebRTC, crypto, storage, the
SQLite-backed stores, Aether Aware, and the client for talking to AetherNetService.

It has been three shapes, and only the last one was asked for:

- **Thirty-two.** Never chosen — every project was packable by default, so the folder layout silently became
  the package layout, while one `VersionPrefix` versioned them in lockstep anyway.
- **Four**, at 3.1.x. A judgement call about weight: WebRTC drags SIPSorcery, SQLite drags a native binary per
  platform, the node client is a different audience. One of those three arguments was good. The request had
  been for *one*, and the choice was never put back to the person who made it.
- **One**, from 3.2.0.

What one package costs, stated rather than hidden: referencing `AetherNet` brings **SIPSorcery** and a
**native SQLite** whether you use them or not, and it pins **BouncyCastle 2.7.0**, because SIPSorcery requires
it and the tree was on 2.4.0.

What it gains: the WebRTC transport and the SQLite stores now reach **Android and Windows**. The separate
packages targeted `net9.0;net10.0` only, so a phone app could never get them from NuGet at all.

Assemblies travel via `BuildOutputInPackage`, filtered by the `CarriedNames` allow-list. The allow-list stays
even with one package: a project reference drags its own references along, so without it the package would also
ship copies of the Microsoft.Extensions and SIPSorcery assemblies, which must arrive as *dependencies*, not as
files.

## The 23 pointers

`*.Pointer` are the ids that used to be packages. Retiring an id is not the same as folding it in: left alone
they freeze at their last version, their pages say nothing about where the code went, and a consumer bumping to
the current version finds no such version. So each is still published, under its real id, carrying **no `lib/`**
and one dependency on `AetherNet`. Referencing `AetherNet.Core` restores and compiles exactly as it always did;
nothing ships twice, because the pointer ships nothing.

Two things about them are deliberate and will look wrong otherwise:

- **The project is named `<Id>.Pointer`, and `PackageId` puts the real id back.** NuGet names every project in
  a restore graph by its `PackageId` and refuses a graph holding two projects called `AetherNet.Core`.
  `src/AetherNet.Core` is already one of them.
- **`AetherNet` is a `PackageReference`, not a `ProjectReference`.** A project reference would pull
  `src/AetherNet.Core` into this project's graph and bring that same collision back.

That second point is why **the pointers are not in the solution**: they reference a published package, and a
plain `dotnet build` of the repo should not go and download the repo's own output. The publisher packs each
`.csproj` directly, so they do not need to be.

A pointer advertises the same frameworks as the package it stands in for, so a consumer lands on the asset that
holds its assembly — which is why `AetherNet.Transport.Windows` is Windows-only and `AetherNet.Node` carries the
platform targets it always had.

They are a migration layer, not a design. Once consumers have moved, they can be deprecated and the answer to
"how many packages" becomes one with nothing after it.

## Publishing

```powershell
. C:\Dev\Solutions\com.bhengubv\thegeeknetwork\Deployment\deployment-credentials.ps1
& C:\Dev\Solutions\com.bhengubv\Toolbox\Publish-NuGet.ps1 `
    -RepoPath C:\Dev\Solutions\com.bhengubv\aether-protocol `
    -Prefix AetherNet -SourceDir packaging -ApiKey $NUGET_CREDENTIALS.API_KEY
```

`-SourceDir packaging` is the part that matters: the script defaults to `src/`, where every project is
unpackable, and would report "Nothing to push."

⚠️ **`AetherNet` has to reach the feed before the pointers can pack**, because each pointer restores it as a
package. On a version nuget.org has not indexed yet, set `$env:RestoreSources` for that shell only — the feed
plus the local output folder — rather than committing a NuGet.config that points at a path on one machine.

GitHub Packages is the mirror and takes the same `.nupkg` files; `gh` already holds `write:packages`, so it
needs no new credential. See `VERSIONING.md`.
