// SPDX-License-Identifier: MIT

/**
 * `aether://` URI scheme — public API.
 *
 * Re-exports the parser/encoder, fluent builder, handler manifest, and the
 * in-process router. See `docs/aether-uri-scheme.md` for the grammar and
 * design notes.
 */

export {
  AetherUri,
  AetherUriError,
  SCHEME,
  type TryParseResult,
} from "./AetherUri.js";
export { AetherUriBuilder } from "./AetherUriBuilder.js";
export {
  AetherUriHandlerManifest,
  HandlerDescriptor,
  type HandlerDescriptorOptions,
  type ManifestResolveResult,
} from "./AetherUriHandlerManifest.js";
export {
  AetherUriRouter,
  type DispatchContext,
  type HandlerCallback,
  type IAetherUriRouter,
} from "./AetherUriRouter.js";
