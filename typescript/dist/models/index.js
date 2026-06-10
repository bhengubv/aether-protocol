/**
 * Core data models
 * SPDX-License-Identifier: MIT
 */
import { DTN_MAX_COPIES, DTN_BUNDLE_TTL_HOURS } from "../constants.js";
/**
 * Bitfield representing node capabilities.
 */
export const NodeCapabilities = {
    None: 0,
    BLE: 1,
    WifiDirect: 2,
    Gateway: 4,
    Relay: 8,
    SOS: 16,
    Streaming: 32,
    Voice: 64,
    DtnCarrier: 128,
    NearLink: 256,
    Video: 512,
};
export function isRouteExpired(route, now = new Date()) {
    return now >= route.expiresAt;
}
// ────────────────────────────── DTN ──────────────────────────────
export var BundleStatus;
(function (BundleStatus) {
    BundleStatus[BundleStatus["Pending"] = 0] = "Pending";
    BundleStatus[BundleStatus["InCustody"] = 1] = "InCustody";
    BundleStatus[BundleStatus["Delivered"] = 2] = "Delivered";
    BundleStatus[BundleStatus["Expired"] = 3] = "Expired";
    BundleStatus[BundleStatus["Failed"] = 4] = "Failed";
})(BundleStatus || (BundleStatus = {}));
export var BundlePriority;
(function (BundlePriority) {
    BundlePriority[BundlePriority["Low"] = 0] = "Low";
    BundlePriority[BundlePriority["Normal"] = 1] = "Normal";
    BundlePriority[BundlePriority["High"] = 2] = "High";
    BundlePriority[BundlePriority["Sos"] = 3] = "Sos";
})(BundlePriority || (BundlePriority = {}));
export function newDtnBundle(senderUhid, recipientUhid, encryptedPayload, priority = BundlePriority.Normal) {
    return {
        id: crypto.randomUUID(),
        senderUhid,
        recipientUhid,
        encryptedPayload,
        priority,
        status: BundleStatus.Pending,
        copyCount: 1,
        maxCopies: DTN_MAX_COPIES,
        hopCount: 0,
        createdAt: new Date(),
        expiresAt: new Date(Date.now() + DTN_BUNDLE_TTL_HOURS * 3600 * 1000),
    };
}
export function isBundleExpired(bundle, now = new Date()) {
    return now >= bundle.expiresAt;
}
//# sourceMappingURL=index.js.map