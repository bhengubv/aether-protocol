// SPDX-License-Identifier: MIT

/**
 * Handler descriptors + manifest for the `aether://` URI surface an app
 * exposes. The router resolves incoming URIs against this manifest and
 * dispatches each to the registered callback.
 *
 * ## Path template syntax
 *
 * ```
 * "content/{hash}"             // matches /content/abc → { hash: "abc" }
 * "watch/{sessionId}/join"     // matches /watch/123/join → { sessionId: "123" }
 * "profile"                    // matches /profile exactly
 * "profile/avatar"             // matches /profile/avatar exactly
 * ```
 */

import { AetherUri } from "./AetherUri.js";
import { AetherUriError } from "./AetherUri.js";

/**
 * Options accepted by the {@link HandlerDescriptor} constructor.
 */
export interface HandlerDescriptorOptions {
  /** Handler name — the first path segment (e.g. `"content"`, `"stream"`). */
  name: string;
  /** Path template (e.g. `"{hash}"`). Empty for a root handler. */
  pathTemplate?: string;
  /** Optional list of expected query keys (informational only). */
  expectedQueryKeys?: readonly string[];
  /** Human-readable description used for diagnostics + docs. */
  description?: string;
}

/**
 * Describes a single handler an app exposes on its `aether://` URI surface.
 *
 * A handler is identified by its first path segment ({@link HandlerDescriptor.name})
 * plus an optional path template that captures route parameters.
 */
export class HandlerDescriptor {
  /** Handler name — the first path segment. */
  readonly name: string;
  /** Path template — empty for a root handler. */
  readonly pathTemplate: string;
  /** Expected query keys (informational). */
  readonly expectedQueryKeys: readonly string[];
  /** Human-readable description. */
  readonly description: string;

  constructor(options: HandlerDescriptorOptions) {
    if (!options || !options.name || options.name.trim().length === 0) {
      throw new AetherUriError("HandlerName is required.");
    }
    this.name = options.name;
    this.pathTemplate = options.pathTemplate ?? "";
    this.expectedQueryKeys = Object.freeze(
      options.expectedQueryKeys ? [...options.expectedQueryKeys] : [],
    );
    this.description = options.description ?? "";
  }

  /**
   * Match an incoming URI's path (e.g. `"content/abc"`) against this
   * descriptor's template. Returns the captured route parameters on success,
   * or null on no match. Keys in the returned map are stored as-written in
   * the template; lookup is case-sensitive.
   */
  match(path: string): ReadonlyMap<string, string> | null {
    const templateSegs =
      this.pathTemplate.length === 0
        ? [this.name]
        : (this.name + "/" + this.pathTemplate.replace(/^\/+/, "")).split("/");
    const pathSegs = path.split("/");
    if (templateSegs.length !== pathSegs.length) return null;

    const captures = new Map<string, string>();
    for (let i = 0; i < templateSegs.length; i++) {
      const t = templateSegs[i]!;
      const p = pathSegs[i]!;
      if (t.length >= 2 && t[0] === "{" && t[t.length - 1] === "}") {
        captures.set(t.substring(1, t.length - 1), p);
      } else if (t !== p) {
        return null;
      }
    }
    return captures;
  }
}

/**
 * Result of a successful manifest resolve.
 */
export interface ManifestResolveResult {
  /** The matched descriptor. */
  handler: HandlerDescriptor;
  /** The captured route parameters from the path template. */
  captures: ReadonlyMap<string, string>;
}

/**
 * An app's complete `aether://` handler manifest — the set of paths it accepts.
 * Each app registers exactly one manifest at startup; the router dispatches
 * against it.
 */
export class AetherUriHandlerManifest {
  /** The owning app's identifier (e.g. `"aether.media"`, `"aether.txtme"`). */
  readonly appId: string;
  /** All registered handler descriptors. */
  readonly handlers: readonly HandlerDescriptor[];

  constructor(appId: string, handlers: readonly HandlerDescriptor[]) {
    if (!appId || appId.trim().length === 0) {
      throw new AetherUriError("AppId is required.");
    }
    this.appId = appId;
    this.handlers = Object.freeze(handlers ? [...handlers] : []);
  }

  /**
   * Resolve an incoming URI against this manifest. Returns the matched
   * descriptor and its captured route parameters, or null if no handler
   * matched.
   */
  resolve(uri: AetherUri): ManifestResolveResult | null {
    const handlerName = uri.handlerName;
    for (const h of this.handlers) {
      if (h.name !== handlerName) continue;
      const captures = h.match(uri.path);
      if (captures !== null) {
        return { handler: h, captures };
      }
    }
    return null;
  }
}
