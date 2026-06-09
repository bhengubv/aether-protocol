// SPDX-License-Identifier: MIT

/**
 * Fluent builder for {@link AetherUri}. Use when programmatically constructing
 * an Aether URI from parts; for parsing an existing string, use
 * {@link AetherUri.parse}.
 *
 * ## Example
 *
 * ```ts
 * const uri = new AetherUriBuilder()
 *   .authority("KXJB7-MN2P4")
 *   .path("content/sha256-abc123")
 *   .query("codec", "opus")
 *   .fragment("t=1m30s")
 *   .build();
 * // -> aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus#t=1m30s
 * ```
 */

import { AetherNetTag } from "../identity/AetherNetTag.js";
import { AetherUri, AetherUriError, SCHEME } from "./AetherUri.js";

export class AetherUriBuilder {
  private _authority: string | undefined;
  private _path = "";
  private readonly _query = new Map<string, string>();
  private _fragment = "";

  /**
   * Set the authority from either a string (AetherTag or 64-char hex UHID) or
   * an {@link AetherNetTag} instance.
   */
  authority(authority: string | AetherNetTag): this {
    if (authority instanceof AetherNetTag) {
      this._authority = authority.value;
      return this;
    }
    if (!authority || typeof authority !== "string") {
      throw new AetherUriError("Authority is null or empty.");
    }
    // Validate by round-tripping through the parser.
    const result = AetherUri.tryParse(`${SCHEME}://${authority}`);
    if (result.uri === undefined) {
      throw new AetherUriError(result.error ?? "Invalid authority.");
    }
    this._authority = result.uri.authority;
    return this;
  }

  /** Set the path component (any leading `/` is stripped). */
  path(path: string): this {
    this._path = (path ?? "").replace(/^\/+/, "");
    return this;
  }

  /** Append a single segment to the path. */
  appendSegment(segment: string): this {
    if (!segment) return this;
    const trimmed = segment.replace(/^\/+/, "");
    this._path = this._path.length === 0 ? trimmed : `${this._path}/${trimmed}`;
    return this;
  }

  /**
   * Add or replace a query parameter. Keys are stored case-insensitively (the
   * last write for a given case-folded key wins).
   */
  query(key: string, value: string): this {
    if (!key) {
      throw new AetherUriError("Query key is null or empty.");
    }
    this._query.set(key.toLowerCase(), value ?? "");
    return this;
  }

  /** Remove a query parameter by key (case-insensitive). */
  removeQuery(key: string): this {
    if (!key) return this;
    this._query.delete(key.toLowerCase());
    return this;
  }

  /** Set the fragment (any leading `#` is stripped). */
  fragment(fragment: string): this {
    this._fragment = (fragment ?? "").replace(/^#+/, "");
    return this;
  }

  /**
   * Build the final {@link AetherUri}. Throws {@link AetherUriError} if any
   * component is invalid. The result is canonicalised by round-tripping
   * through {@link AetherUri.parse}.
   */
  build(): AetherUri {
    if (!this._authority) {
      throw new AetherUriError("Authority is required.");
    }
    return AetherUri.parse(this.toString());
  }

  /**
   * Returns the URI string this builder currently represents without
   * validation. Useful for diagnostics; prefer {@link build} for production use.
   */
  toString(): string {
    if (!this._authority) return "";
    let out = `${SCHEME}://${this._authority}`;
    if (this._path.length > 0) {
      out += "/" + this._path;
    }
    if (this._query.size > 0) {
      out += "?";
      let first = true;
      for (const [k, v] of this._query) {
        if (!first) out += "&";
        first = false;
        out += k;
        if (v.length > 0) out += "=" + v;
      }
    }
    if (this._fragment.length > 0) {
      out += "#" + this._fragment;
    }
    return out;
  }
}
