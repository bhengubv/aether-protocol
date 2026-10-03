# Notices — AetherNet.Aware

## Fieldwatch

AetherNet.Aware is ported from Fieldwatch (<https://github.com/offgridpete/fieldwatch>, commit
`cf6562daf3f3a03d7bcb9aed3f5e1d4be46d7799`). Every ported file says which Fieldwatch file it came from.
`Signatures/fieldwatch-signatures-v2.json` is Fieldwatch's `dist/fieldwatch-signatures-v2.json` at that commit,
byte for byte (git blob `2b1f85ba133e3cd2621205db8b25ce3b071019e4`).

Fieldwatch's licence:

```
MIT License

Copyright (c) 2026 Off Grid Pete LLC

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## IEEE address prefixes in the signature pack

Some signatures in the pack list address prefixes (OUIs) that Fieldwatch took from the IEEE Registration Authority's
public MA-L listing. Fieldwatch's own NOTICE keeps those lists outside its MIT grant. The IEEE has said it "does not
assert any copyright in the OUI Public Listing or attempt to restrict distribution of the listing in any way"
(recorded in Debian's `ieee-data` package copyright file).

## Not included

These parts of Fieldwatch are not in this library, because they are not covered by its MIT grant:

- `radiodb.bin` and its lookups: Bluetooth SIG Assigned Numbers (company names, GAP Appearance, 16-bit UUID names).
  The Bluetooth SIG's terms for redistributing those have not been checked yet. Until they are, nothing here turns
  an address or company ID into a maker's name (`Sighting.Vendor` stays empty).
- `FastPairModels.kt`: Fast Pair model names compiled from public partner and community listings.
