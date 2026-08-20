# AetherNet.Content

Hash-addressed content distribution layer. Publish a media file, announce its hash to the mesh, request missing chunks from peers, reassemble from K-of-N. Maps to IContentService + InMemoryContentStore + chunk bitmap exchange.

```bash
dotnet add package AetherNet.Content
```

```csharp
using AetherNet.Content;
using AetherNet.Content.Models;

IContentService content = sp.GetRequiredService<IContentService>();

var descriptor = await content.PublishAsync(myFileStream, "demo.mp4");
await content.AnnounceAsync(descriptor);

// On the receiver:
await content.RequestChunksAsync(descriptor.ContentHash, missingChunkIndices);
var stream = await content.AssembleAsync(descriptor.ContentHash);
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
