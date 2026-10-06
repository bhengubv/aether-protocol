# packaging/

Nothing in here holds any code. `src/` holds the code — thirty-nine projects, every one of them
`IsPackable=false` — and this folder decides what shape that code takes on NuGet.

## One package, one assembly

**`AetherNet` is the only package, and `AetherNet.dll` is the only assembly in it** — one per target
framework. `AetherNet/AetherNet.csproj` compiles the source of the thirty-seven library projects in `src/`
directly into that one assembly. It has no project references; it names their files.

`src/` is not changed by any of this. Its projects still build one by one, which is what the tests and the
two apps use. Only the shape on NuGet is decided here.

Three settings in that project look odd without the reason, and the project file says so beside each:

- **`Microsoft.NET.Sdk.Razor`**, because the mesh browser is a Razor library and its components must be
  compiled by the Razor SDK.
- **`RootNamespace` is `AetherNet.Browser`.** The components declare no `@namespace`, so they take the root
  namespace; this keeps them at `AetherNet.Browser.AetherBrowser` and `AetherNet.Browser.CardEditor`.
  Nothing else reads it — every C# file declares its own namespace.
- **Every embedded resource keeps an explicit `LogicalName`**, because the code reads them back by exactly
  those names.

It has been four shapes, and only the last was asked for: thirty-two packages (never chosen — every project
was packable by default), four at 3.1.x, one package carrying thirty-seven assemblies plus twenty-three
pointer packages at 3.2.0, and **one package, one assembly, nothing beside it** from 3.3.0.

What one package costs, stated rather than hidden: referencing `AetherNet` brings **SIPSorcery** and a
**native SQLite** whether you use them or not, and pins **BouncyCastle 2.7.0**, which SIPSorcery requires.

## When the one-assembly build breaks

Two projects can hold names that never meet while they are separate assemblies and collide when they are
one. 3.3.0 found exactly one: a type called `Handshake` in BitTorrent and a namespace called
`AetherNet.Handshake` in Core. C# looks a name up in the enclosing namespaces before it looks at the file's
`using` directives, so the namespace wins — and no build setting changes that order. The fix is in the code:
qualify the name. If the build here fails where `src/` builds cleanly, that is the first thing to look for.

## Licences

The package carries Aether Aware, ported from Fieldwatch (MIT), and embeds two typefaces under the SIL Open
Font License. Both notices are packed under `licenses/` — the project names them, so a new file of that kind
needs a line there too.

## Publishing

```powershell
. C:\Dev\Solutions\com.bhengubv\thegeeknetwork\Deployment\deployment-credentials.ps1
& C:\Dev\Solutions\com.bhengubv\Toolbox\Publish-NuGet.ps1 `
    -RepoPath C:\Dev\Solutions\com.bhengubv\aether-protocol `
    -Prefix AetherNet -SourceDir packaging -ApiKey $NUGET_CREDENTIALS.API_KEY
```

`-SourceDir packaging` is the part that matters: the script defaults to `src/`, where nothing is packable, and
would report "Nothing to push." It finds exactly one project here and pushes exactly one package. GitHub
Packages is the mirror and takes the same `.nupkg`; `gh` already holds `write:packages`. See `VERSIONING.md`.
