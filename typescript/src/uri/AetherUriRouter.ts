// SPDX-License-Identifier: MIT

/**
 * Dispatches an incoming `aether://` URI to the registered handler for its
 * route. The router is per-app — each app constructs one with its own manifest.
 *
 * ## Lifecycle
 *
 * 1. App startup: build an {@link AetherUriHandlerManifest} describing every
 *    route the app accepts.
 * 2. App startup: register a callback per {@link HandlerDescriptor} via
 *    {@link IAetherUriRouter.registerHandler}.
 * 3. At runtime: when a URI is received (incoming intent, deep link, or
 *    in-mesh dispatch), call {@link IAetherUriRouter.dispatch} to invoke the
 *    right callback.
 */

import { AetherUri, AetherUriError } from "./AetherUri.js";
import type {
  AetherUriHandlerManifest,
  HandlerDescriptor,
} from "./AetherUriHandlerManifest.js";

/**
 * Context delivered to a registered URI handler when its route matches.
 * Carries the original URI plus any captured route parameters.
 */
export interface DispatchContext {
  /** The URI being dispatched. */
  readonly uri: AetherUri;
  /** The descriptor that matched. */
  readonly handler: HandlerDescriptor;
  /** Captured route parameters from the path template. */
  readonly routeParameters: ReadonlyMap<string, string>;
}

/**
 * Callback signature for a registered handler. May throw or reject; the
 * exception propagates to the caller of {@link IAetherUriRouter.dispatch}.
 */
export type HandlerCallback = (ctx: DispatchContext) => Promise<void>;

/**
 * Router contract. The reference implementation is {@link AetherUriRouter}.
 */
export interface IAetherUriRouter {
  /** The manifest the router resolves against. */
  readonly manifest: AetherUriHandlerManifest;

  /**
   * Register a callback for a handler descriptor. The descriptor must be one
   * present in {@link IAetherUriRouter.manifest}. Re-registering replaces the
   * existing callback.
   */
  registerHandler(descriptor: HandlerDescriptor, callback: HandlerCallback): void;

  /**
   * Resolve and dispatch a URI (or a string that parses to one). Returns
   * `true` if a handler was matched AND invoked; returns `false` if no handler
   * matched or no callback was registered for the matched descriptor. The
   * handler's exceptions propagate to the caller. If `uri` is a string that
   * fails to parse, throws {@link AetherUriError}.
   */
  dispatch(uri: AetherUri | string): Promise<boolean>;
}

/**
 * Reference in-process implementation of {@link IAetherUriRouter}.
 */
export class AetherUriRouter implements IAetherUriRouter {
  readonly manifest: AetherUriHandlerManifest;
  private readonly _handlers = new Map<HandlerDescriptor, HandlerCallback>();

  constructor(manifest: AetherUriHandlerManifest) {
    if (manifest === null || manifest === undefined) {
      throw new AetherUriError("Manifest is null.");
    }
    this.manifest = manifest;
  }

  registerHandler(descriptor: HandlerDescriptor, callback: HandlerCallback): void {
    if (descriptor === null || descriptor === undefined) {
      throw new AetherUriError("Descriptor is null.");
    }
    if (callback === null || callback === undefined) {
      throw new AetherUriError("Handler is null.");
    }
    if (!this.manifest.handlers.includes(descriptor)) {
      throw new AetherUriError(
        `Descriptor '${descriptor.name}' is not in the manifest.`,
      );
    }
    this._handlers.set(descriptor, callback);
  }

  async dispatch(uri: AetherUri | string): Promise<boolean> {
    const parsedUri =
      typeof uri === "string" ? AetherUri.parse(uri) : uri;
    const resolved = this.manifest.resolve(parsedUri);
    if (resolved === null) return false;
    const cb = this._handlers.get(resolved.handler);
    if (cb === undefined) return false;
    const ctx: DispatchContext = {
      uri: parsedUri,
      handler: resolved.handler,
      routeParameters: resolved.captures,
    };
    await cb(ctx);
    return true;
  }
}
