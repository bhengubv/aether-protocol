# packaging/

Nothing in here holds any code. `src/` holds the code — about three dozen projects, every one of them
`IsPackable=false` — and this folder decides what shape that code takes on NuGet.

## The four bundles

`AetherNet`, `AetherNet.Node`, `AetherNet.WebRtc` and `AetherNet.Sqlite` are the packages we publish.
Each one carries the assemblies of the `src/` projects it names, so a consumer writes one
`PackageReference` instead of twenty.

The split is by what a dependency makes somebody carry, not by how the code is laid out:

| Package             | Why it is its own package                                                     |
|---------------------|-------------------------------------------------------------------------------|
| `AetherNet`         | The protocol. Weight every consumer carries anyway — crypto alone is referenced by eleven of these projects, so separating it saved nobody anything. |
| `AetherNet.Node`    | Talking to AetherNetService rather than embedding the protocol. A different audience. |
| `AetherNet.WebRtc`  | Brings SIPSorcery. Sending a message over Bluetooth should not cost you a media stack. |
| `AetherNet.Sqlite`  | Brings a native SQLite per platform.                                           |

They carry assemblies through `BuildOutputInPackage`, and only the ones named in `CarriedNames` — a
project reference drags its own references along, so without that allow-list the optional bundles would
each ship a second copy of `AetherNet.Core`. The three optional bundles depend on `AetherNet` for the rest.

These four are in `AetherNetProtocol.slnx`.

## The twenty pointers

`*.Pointer` are the twenty ids that used to be packages of their own before the bundles existed.
Folding them in would have left them at 3.0.0 — twenty packages that quietly stopped, with nothing on
their pages saying where the code went, and a consumer bumping to the current version finding no such
version. So each one is still published, under its real id, carrying **no `lib/`** and one dependency on
the bundle that holds its assembly. Referencing `AetherNet.Core` restores and compiles exactly as it
always did; nothing ships twice, because the pointer ships nothing.

Two things about them are deliberate and will look wrong otherwise:

- **The project is named `<Id>.Pointer`, and `PackageId` puts the real id back.** NuGet names every
  project in a restore graph by its `PackageId` and refuses a graph holding two projects called
  `AetherNet.Core`. `src/AetherNet.Core` is already one of them.
- **The bundle is a `PackageReference`, not a `ProjectReference`.** A project reference would pull
  `src/AetherNet.Core` into this project's graph and bring that same collision back.

That second point is why **the pointers are not in the solution**: they reference a published package,
and a plain `dotnet build` of the repo should not go and download the repo's own output. The publisher
packs each `.csproj` directly, so they do not need to be.

## Publishing

```powershell
. C:\Dev\Solutions\com.bhengubv\thegeeknetwork\Deployment\deployment-credentials.ps1
& C:\Dev\Solutions\com.bhengubv\Toolbox\Publish-NuGet.ps1 `
    -RepoPath C:\Dev\Solutions\com.bhengubv\aether-protocol `
    -Prefix AetherNet -SourceDir packaging -ApiKey $NUGET_CREDENTIALS.API_KEY
```

`-SourceDir packaging` is the part that matters: the script defaults to `src/`, where every project is
unpackable, and would report "Nothing to push." It packs the bundles and the pointers together, and
pushes with `--skip-duplicate`, so re-running is safe. GitHub Packages is the mirror and takes the same
`.nupkg` files — see `VERSIONING.md`.
