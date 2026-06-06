# Parallel Agent Rules — aether-protocol

## Safe file ownership (never assign two agents the same file)

| File / Area | Owner |
|---|---|
| AetherNetMedia.slnx / aether-protocol.slnx | ONE agent only |
| Directory.Build.props | ONE agent only |
| src/AetherNet.Core/Constants/ProtocolConstants.cs | ONE agent only |
| src/AetherNet.Core/Protocol/MeshPacket.cs | ONE agent only |
| Any *.sln or *.slnx | ONE agent only |

## Safe: non-overlapping language tracks
  Agent 1 -> python/          (new files only)
  Agent 2 -> typescript/      (new files only)
  Agent 3 -> rust/            (new files only)
  Agent 4 -> go/              (new files only)
  Agent 5 -> kotlin/          (new files only)
  Agent 6 -> swift/           (new files only)
  Agent 7 -> c/               (new files only)

## Unsafe: shared file collision
  Agent A modifies MeshPacket.cs
  Agent B also modifies MeshPacket.cs   <- COLLISION

## Rule
Protocol constants and packet type additions touch shared files.
Sequence those tasks: A finishes -> B starts.
Language implementation tracks are fully independent -- safe to parallelise.

## Progress log convention (mandatory)
Every agent MUST write to .loki/progress/{track-name}.log after each major step.
Format: [HH:mm:ss] STEP: description
Final:  [HH:mm:ss] DONE: summary | Tests: N passed

## PowerShell log helper
$log = "C:\Dev\Solutions\com.bhengubv\aether-protocol\.loki\progress\track-N.log"
Add-Content $log "[$(Get-Date -Format HH:mm:ss)] STEP: Created IRoutingService.py"
