# Cross-Language Fixture System

The canonical fixture documentation lives at
[`fixtures/README.md`](https://github.com/bhengubv/aether-protocol/blob/main/fixtures/README.md)
and [`fixtures/signal/README.md`](https://github.com/bhengubv/aether-protocol/blob/main/fixtures/signal/README.md)
in the repository.

## Why fixtures exist

Per-language round-trip tests prove each implementation is *self-consistent*. They cannot
prove that two languages produce the *same* bytes for the same input. Wire-format drift
(UUID byte order, length-prefix endianness, signed-vs-unsigned ints) only surfaces when
languages must interoperate — these fixtures catch that drift in CI without needing two
devices on a mesh.

## Layout

```
fixtures/
  inputs.json                    # canonical per-case input specs
  expected/
    <case>.bin                   # canonical wire bytes for case <case>
  signal/
    README.md                    # X3DH / Double Ratchet vectors
```

## How interop is verified

Each of the eight reference languages (C#, Rust, Go, TypeScript, Python, Kotlin, Swift, C)
ships a runner that loads `fixtures/inputs.json`, serialises each case, and asserts the
output matches `fixtures/expected/<case>.bin` byte-for-byte. The C# runner is the
authoritative producer — if a fixture changes, C# regenerates and the other seven
languages must match.

## Writing a new fixture

1. Add a case to `fixtures/inputs.json`.
2. Run the C# generator (see `fixtures/scripts/`).
3. Commit the new `.bin` file.
4. Run each language's interop runner; expect failure until each port is updated.
5. Update each port; re-run.
6. CI must be green across all eight runners before the change merges.

## Signal fixtures

`fixtures/signal/` contains X3DH and Double Ratchet test vectors. These pin not just the
wire format but also the *cryptographic state evolution* — the sequence of root keys and
chain keys produced from a deterministic seed. Cross-language Signal interop is the most
fragile part of the multi-language alignment; the fixtures are how we keep it honest.
