# AetherNet.Voice

Voice calls over the mesh — 1-to-1 (IVoiceCallService) and group rooms (IGroupVoiceCallService) with jitter buffer, opus framing, signalling envelopes for codec negotiation, and IGroupKeyProvider for group key rotation under member churn (proved forward-secret in formal/group-voice-rotation/).

```bash
dotnet add package AetherNet.Voice
```

```csharp
using AetherNet.Voice;

IVoiceCallService voice = sp.GetRequiredService<IVoiceCallService>();

var session = await voice.StartCallAsync(peerUhid: "KXJB7-MN2P4",
                                        codec: "opus",
                                        frameDurationMs: 20);
await voice.SendFrameAsync(session.Id, opusFrameBytes);

voice.FrameReceived += (s, frame) => audioRenderer.Play(frame.Pcm);
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
