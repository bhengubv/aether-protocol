/**
 * Delay-tolerant networking on top of the Aether mesh.
 * SPDX-License-Identifier: MIT
 */

export type { IDtnBundleStore } from "./IDtnBundleStore.js";
export { InMemoryDtnBundleStore } from "./IDtnBundleStore.js";
export type { IBundleReplicationStrategy } from "./IBundleReplicationStrategy.js";
export { GeohashEpidemicStrategy } from "./IBundleReplicationStrategy.js";
export { DtnService } from "./DtnService.js";
