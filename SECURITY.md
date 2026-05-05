# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in Aether Protocol, please report it
responsibly.

**Email:** security@thegeeknetwork.co.za
**PGP:** available on request — ask in the initial email if you prefer
encrypted correspondence.

Please include:

- Description of the vulnerability
- Steps to reproduce (ideally a failing test or PoC against a tagged commit)
- Potential impact
- Suggested fix, if any
- Whether you would like public credit on disclosure

Do **not** file vulnerabilities as public GitHub issues.

## Disclosure Timeline

- We will acknowledge receipt within **48 hours**.
- We will provide an initial assessment within **7 days**.
- We aim to release a fix within **30 days** for critical issues.
- We follow a **90-day coordinated disclosure** timeline. If we have not
  shipped a fix within that window we will agree on an extension or
  publish jointly.

## Scope

In scope for security reports:

- Protocol implementation vulnerabilities (routing, packet handling, DTN
  custody, SOS flood)
- Cryptographic issues — key exchange, encryption, packet signing,
  Double-Ratchet state corruption, replay or freshness-window bypass
- Transport-layer attacks against the protocol (replay, injection,
  spoofing, packet-format ambiguity)
- Memory safety — key material exposure, buffer-handling bugs, missing
  zeroisation
- Denial-of-service vectors against the protocol layer (RREQ/RREP
  amplification, DTN custody exhaustion, OPK pool exhaustion beyond the
  configured pool size)
- Documentation that materially overstates security guarantees

The boundary between "what the protocol defends" and "what is the host's
responsibility" is documented in [`docs/THREAT_MODEL.md`](docs/THREAT_MODEL.md).
Reports that target the explicitly-out-of-scope items in §3 of that
document are still welcome — knowing what we do not defend against is
useful — but they will not be triaged as protocol vulnerabilities.

## Out of Scope

- Issues in upstream dependencies — report those to the relevant project.
- Social engineering.
- Issues requiring physical access to an unlocked device.
- Side-channel attacks not specifically addressed by the protocol (see
  THREAT_MODEL §3.2).
- Traffic-analysis attacks against the wire format (see THREAT_MODEL §3.3).
- Quantum attacks against X25519 / Ed25519 (see THREAT_MODEL §3.4).

## Recognition

We will credit reporters in the release notes for the fix unless the
reporter prefers to remain anonymous. Indicate your preference in the
initial email.
