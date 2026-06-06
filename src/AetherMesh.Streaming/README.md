# AetherMesh.Streaming

Live video / audio streaming over the mesh — adaptive bitrate (ABR) rung selection, watch-together synchronization with bounded jitter, group video with FullMesh→SFU auto-switch, 1-to-1 video calls. Built on IRoutingService + IMeshSender.

```bash
dotnet add package AetherMesh.Streaming
```

```csharp
using AetherMesh.Streaming;
using AetherMesh.Streaming.Models;

IStreamingService streaming = sp.GetRequiredService<IStreamingService>();

// Publisher
var session = await streaming.StartStreamAsync(streamId: "alice/cooking", title: "Morning cook");
await streaming.PublishSegmentAsync(session.Id, segmentBytes);

// Subscriber
await streaming.SubscribeAsync(session.Id, (segment, ct) => RenderAsync(segment, ct));
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
