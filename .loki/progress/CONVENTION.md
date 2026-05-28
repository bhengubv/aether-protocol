# Agent Progress Logging Convention

Every background agent MUST write progress checkpoints to:
  .loki/progress/{track-name}.log

## Format
[HH:mm:ss] STEP: {description}
[HH:mm:ss] DONE: {summary} | Tests: {n} passed

## Track names
  csharp, python, typescript, rust, go, kotlin, swift, c,
  aether-space, aether-forge, aether-vault, aether-market,
  benchmarks, docs, security
