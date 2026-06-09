// SPDX-License-Identifier: MIT

/**
 * Aether URI — the canonical addressing format for resources on the Aether mesh.
 *
 * ## Grammar (ABNF, RFC 5234)
 *
 * ```
 * aether-uri   = "aether://" authority [ "/" path ] [ "?" query ] [ "#" fragment ]
 * authority    = aether-tag / uhid
 * aether-tag   = 5(crockford) [ "-" ] 5(crockford)         ; case-insensitive
 * uhid         = 64(HEXDIG)                                ; SHA-256 hex of public key
 * path         = path-segment *( "/" path-segment )
 * path-segment = 1*( unreserved / pct-encoded / sub-delims / ":" / "@" )
 * query        = query-param *( "&" query-param )
 * query-param  = key [ "=" value ]
 * key          = 1*( unreserved / pct-encoded )
 * value        = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
 * fragment     = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
 * unreserved   = ALPHA / DIGIT / "-" / "." / "_" / "~"
 * pct-encoded  = "%" HEXDIG HEXDIG
 * sub-delims   = "!" / "$" / "&" / "'" / "(" / ")" / "*" / "+" / "," / ";" / "="
 * ```
 *
 * ## Components
 *
 * - **Scheme**: always `aether`. Case-insensitive on parse, lowercase on emit.
 * - **Authority**: an {@link AetherNetTag} (10 Crockford base-32 chars, dash
 *   optional) or a UHID (64 hex chars). Case-insensitive, canonicalised upper.
 * - **Path**: opaque to the protocol — names a handler within the destination.
 *   Case-sensitive. Empty string means "root".
 * - **Query**: handler arguments. Keys case-insensitive (stored lowercase),
 *   values case-sensitive.
 * - **Fragment**: client-side hint; never transmitted on the wire.
 */

import { AetherNetTag } from "../identity/AetherNetTag.js";

/** The fixed scheme name — `aether`. */
export const SCHEME = "aether";

const SCHEME_PREFIX = "aether://";

// ── Errors ────────────────────────────────────────────────────────────────────

/** Thrown when an `aether://` URI fails to parse or build. */
export class AetherUriError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "AetherUriError";
  }
}

// ── Character class helpers ──────────────────────────────────────────────────

function isUnreserved(c: string): boolean {
  const code = c.charCodeAt(0);
  return (
    (code >= 0x41 && code <= 0x5a) || // A–Z
    (code >= 0x61 && code <= 0x7a) || // a–z
    (code >= 0x30 && code <= 0x39) || // 0–9
    c === "-" ||
    c === "." ||
    c === "_" ||
    c === "~"
  );
}

function isSubDelim(c: string): boolean {
  return (
    c === "!" ||
    c === "$" ||
    c === "&" ||
    c === "'" ||
    c === "(" ||
    c === ")" ||
    c === "*" ||
    c === "+" ||
    c === "," ||
    c === ";" ||
    c === "="
  );
}

function isHexChar(c: string): boolean {
  const code = c.charCodeAt(0);
  return (
    (code >= 0x30 && code <= 0x39) || // 0–9
    (code >= 0x41 && code <= 0x46) || // A–F
    (code >= 0x61 && code <= 0x66) // a–f
  );
}

function isHex(s: string): boolean {
  for (const c of s) {
    if (!isHexChar(c)) return false;
  }
  return true;
}

function hexValue(c: string): number {
  const code = c.charCodeAt(0);
  if (code <= 0x39) return code - 0x30;
  if (code <= 0x46) return code - 0x41 + 10;
  return code - 0x61 + 10;
}

// ── Percent-encoding ─────────────────────────────────────────────────────────

type EncodeKind = "path-segment" | "query-key" | "query-value" | "fragment";

function isAllowedUnencoded(c: string, kind: EncodeKind): boolean {
  if (isUnreserved(c)) return true;
  switch (kind) {
    case "path-segment":
      // pchar = unreserved / pct-encoded / sub-delims / ":" / "@"
      return isSubDelim(c) || c === ":" || c === "@";
    case "query-key":
      // Always encode '&' and '=' in keys; allow ':' '@' and selected sub-delims.
      return (
        c === ":" ||
        c === "@" ||
        c === "!" ||
        c === "$" ||
        c === "'" ||
        c === "(" ||
        c === ")" ||
        c === "*" ||
        c === "+" ||
        c === "," ||
        c === ";"
      );
    case "query-value":
      // Allow sub-delims except '&'; '=' is fine inside a value.
      return (
        c === ":" ||
        c === "@" ||
        c === "/" ||
        c === "?" ||
        c === "!" ||
        c === "$" ||
        c === "'" ||
        c === "(" ||
        c === ")" ||
        c === "*" ||
        c === "+" ||
        c === "," ||
        c === ";" ||
        c === "="
      );
    case "fragment":
      // fragment = *( pchar / "/" / "?" )
      return isSubDelim(c) || c === ":" || c === "@" || c === "/" || c === "?";
  }
}

const UTF8_ENCODER = new TextEncoder();
const UTF8_DECODER = new TextDecoder("utf-8", { fatal: false });

function encodeComponent(value: string, kind: EncodeKind): string {
  let out = "";
  for (const c of value) {
    // c may be a surrogate pair — handle as a single code point so the UTF-8
    // bytes line up with the C# behaviour.
    if (c.length === 1 && isAllowedUnencoded(c, kind)) {
      out += c;
      continue;
    }
    const bytes = UTF8_ENCODER.encode(c);
    for (const b of bytes) {
      out += "%" + b.toString(16).toUpperCase().padStart(2, "0");
    }
  }
  return out;
}

function encodePath(path: string): string {
  const segs = path.split("/");
  for (let i = 0; i < segs.length; i++) {
    segs[i] = encodeComponent(segs[i], "path-segment");
  }
  return segs.join("/");
}

function percentDecode(input: string): string {
  if (input.indexOf("%") < 0) return input;
  const bytes: number[] = [];
  for (let i = 0; i < input.length; i++) {
    const c = input[i]!;
    if (
      c === "%" &&
      i + 2 < input.length &&
      isHexChar(input[i + 1]!) &&
      isHexChar(input[i + 2]!)
    ) {
      bytes.push((hexValue(input[i + 1]!) << 4) | hexValue(input[i + 2]!));
      i += 2;
    } else {
      // Non-encoded character — emit its UTF-8 bytes.
      const enc = UTF8_ENCODER.encode(c);
      for (const b of enc) bytes.push(b);
    }
  }
  return UTF8_DECODER.decode(new Uint8Array(bytes));
}

function percentDecodePath(path: string): string {
  if (path.length === 0) return path;
  // Decode each segment independently so '/' isn't lost.
  const segs = path.split("/");
  for (let i = 0; i < segs.length; i++) segs[i] = percentDecode(segs[i]!);
  return segs.join("/");
}

// ── Authority canonicalisation ───────────────────────────────────────────────

function canonicaliseAuthority(raw: string): {
  authority?: string;
  error?: string;
} {
  // Try UHID (64 hex chars).
  if (raw.length === 64 && isHex(raw)) {
    return { authority: raw.toUpperCase() };
  }
  // Try AetherNetTag (10 Crockford chars with optional dash).
  const tag = AetherNetTag.tryParse(raw);
  if (tag !== null) {
    return { authority: tag.value };
  }
  return {
    error: `Authority '${raw}' is neither a valid AetherTag nor a 64-char hex UHID.`,
  };
}

// ── Path validation ──────────────────────────────────────────────────────────

function validatePath(path: string): string | null {
  if (path.length === 0) return null;
  for (const segment of path.split("/")) {
    if (segment.length === 0) {
      return "Empty path segment (consecutive slashes).";
    }
    for (let i = 0; i < segment.length; i++) {
      const c = segment[i]!;
      if (isUnreserved(c) || isSubDelim(c) || c === ":" || c === "@") continue;
      if (c === "%") {
        if (
          i + 2 >= segment.length ||
          !isHexChar(segment[i + 1]!) ||
          !isHexChar(segment[i + 2]!)
        ) {
          return `Malformed percent-encoding at position ${i} of segment '${segment}'.`;
        }
        i += 2;
        continue;
      }
      return `Illegal character '${c}' in path segment '${segment}'.`;
    }
  }
  return null;
}

// ── AetherUri ────────────────────────────────────────────────────────────────

/**
 * Result of a non-throwing parse. Exactly one of `uri` or `error` is set.
 */
export interface TryParseResult {
  uri?: AetherUri;
  error?: string;
}

/**
 * A parsed, validated, canonical `aether://` URI.
 *
 * Instances are immutable. Construct via {@link AetherUri.parse}, the
 * non-throwing {@link AetherUri.tryParse}, or the
 * {@link import("./AetherUriBuilder.js").AetherUriBuilder fluent builder}.
 */
export class AetherUri {
  /** The destination authority (AetherTag or UHID), canonicalised to upper case. */
  readonly authority: string;
  /** The handler path, without the leading slash. Empty string means "root". */
  readonly path: string;
  /** Decoded query parameters. Keys are lowercased for case-insensitive lookup. */
  readonly query: ReadonlyMap<string, string>;
  /** The fragment, with the leading `#` stripped. Empty if none. */
  readonly fragment: string;

  private constructor(
    authority: string,
    path: string,
    query: ReadonlyMap<string, string>,
    fragment: string,
  ) {
    this.authority = authority;
    this.path = path;
    this.query = query;
    this.fragment = fragment;
  }

  // ── Parsing ────────────────────────────────────────────────────────────────

  /**
   * Parse an `aether://` URI. Throws {@link AetherUriError} on any syntactic
   * violation. Use {@link AetherUri.tryParse} for a non-throwing parse.
   */
  static parse(input: string): AetherUri {
    if (input === null || input === undefined) {
      throw new AetherUriError("Input is null.");
    }
    const result = AetherUri.tryParse(input);
    if (result.uri === undefined) {
      throw new AetherUriError(result.error ?? "Invalid aether URI.");
    }
    return result.uri;
  }

  /**
   * Attempt to parse an `aether://` URI. On success returns `{ uri }`; on
   * failure returns `{ error }` with a human-readable diagnostic.
   */
  static tryParse(input: string): TryParseResult {
    if (input === null || input === undefined || input.length === 0) {
      return { error: "Input is null or empty." };
    }

    // Scheme is case-insensitive per RFC 3986.
    if (
      input.length < SCHEME_PREFIX.length ||
      input.substring(0, SCHEME_PREFIX.length).toLowerCase() !== SCHEME_PREFIX
    ) {
      return { error: `Scheme must be '${SCHEME}://'.` };
    }

    let rest = input.substring(SCHEME_PREFIX.length);

    // Split on fragment first (only one '#' is allowed).
    const fragmentSplit = rest.indexOf("#");
    let fragment = "";
    if (fragmentSplit >= 0) {
      fragment = percentDecode(rest.substring(fragmentSplit + 1));
      rest = rest.substring(0, fragmentSplit);
    }

    // Then query.
    const querySplit = rest.indexOf("?");
    const query = new Map<string, string>();
    if (querySplit >= 0) {
      const queryRaw = rest.substring(querySplit + 1);
      rest = rest.substring(0, querySplit);
      for (const pair of queryRaw.split("&")) {
        if (pair.length === 0) continue; // tolerate "&&"
        const eq = pair.indexOf("=");
        const key = eq >= 0 ? pair.substring(0, eq) : pair;
        const value = eq >= 0 ? pair.substring(eq + 1) : "";
        const decodedKey = percentDecode(key);
        if (decodedKey.length === 0) {
          return { error: "Empty query parameter key." };
        }
        // Store lower-cased keys for case-insensitive lookup. Last write wins.
        query.set(decodedKey.toLowerCase(), percentDecode(value));
      }
    }

    // Then path.
    const pathSplit = rest.indexOf("/");
    let authorityRaw: string;
    let pathRaw: string;
    if (pathSplit >= 0) {
      authorityRaw = rest.substring(0, pathSplit);
      pathRaw = rest.substring(pathSplit + 1);
    } else {
      authorityRaw = rest;
      pathRaw = "";
    }

    if (authorityRaw.length === 0) {
      return { error: "Authority is missing." };
    }

    const authResult = canonicaliseAuthority(authorityRaw);
    if (authResult.authority === undefined) {
      return { error: authResult.error };
    }

    const pathError = validatePath(pathRaw);
    if (pathError !== null) {
      return { error: pathError };
    }

    return {
      uri: new AetherUri(
        authResult.authority,
        percentDecodePath(pathRaw),
        query,
        fragment,
      ),
    };
  }

  // ── Path helpers ───────────────────────────────────────────────────────────

  /**
   * The path split into already-decoded segments. Returns an empty array for
   * the root path.
   */
  get pathSegments(): readonly string[] {
    if (this.path.length === 0) return [];
    // `this.path` is already decoded.
    return Object.freeze(this.path.split("/"));
  }

  /** The first path segment (the "handler name"), or empty string for root. */
  get handlerName(): string {
    if (this.path.length === 0) return "";
    const slash = this.path.indexOf("/");
    return slash >= 0 ? this.path.substring(0, slash) : this.path;
  }

  // ── Canonical encoder ──────────────────────────────────────────────────────

  /**
   * Returns the canonical string form of this URI. Two URIs that compare equal
   * produce byte-identical `toString()` output.
   */
  toString(): string {
    let out = SCHEME_PREFIX + this.authority;
    if (this.path.length > 0) {
      out += "/" + encodePath(this.path);
    }
    if (this.query.size > 0) {
      out += "?";
      let first = true;
      for (const [k, v] of this.query) {
        if (!first) out += "&";
        first = false;
        out += encodeComponent(k, "query-key");
        if (v.length > 0) {
          out += "=" + encodeComponent(v, "query-value");
        }
      }
    }
    if (this.fragment.length > 0) {
      out += "#" + encodeComponent(this.fragment, "fragment");
    }
    return out;
  }

  // ── Equality ───────────────────────────────────────────────────────────────

  /**
   * Order-insensitive, key-case-insensitive equality.
   *
   * Two URIs are equal iff they share the same authority, path, fragment, and
   * a set-equal query map (keys compared case-insensitively, values exactly).
   */
  equals(other: AetherUri): boolean {
    if (this.authority !== other.authority) return false;
    if (this.path !== other.path) return false;
    if (this.fragment !== other.fragment) return false;
    if (this.query.size !== other.query.size) return false;
    for (const [k, v] of this.query) {
      // Keys are already stored lowercase in both maps.
      const otherV = other.query.get(k);
      if (otherV === undefined || otherV !== v) return false;
    }
    return true;
  }
}
