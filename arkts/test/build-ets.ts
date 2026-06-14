/*
 * Node-side build helper for the ArkTS parity harness.
 *
 * The HarmonyOS ArkTS sources live in `../src/main/ets/**.ets`. Node's ESM loader
 * cannot import `.ets` directly, and the ArkTS-idiomatic dynamic-JSON type
 * `ESObject` is not a Node type. esbuild solves both: it transpiles `.ets` as
 * TypeScript (type annotations — including `ESObject` — are erased without
 * checking) and bundles the `@noble/*` crypto dependencies resolved from this
 * harness's own node_modules.
 *
 * This is ONLY the Node parity proof. The real on-device build is the gated
 * DevEco Studio / hvigor step (see ../README.md). esbuild here is a test harness
 * tool, not part of the HarmonyOS toolchain.
 *
 * SPDX-License-Identifier: MIT
 */

import { build } from 'esbuild';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// The @noble/* crypto deps are installed in THIS harness directory's node_modules.
// The .ets entry lives one level up (arkts/), which has no node_modules, so point
// esbuild's module resolution at the harness node_modules explicitly.
const HARNESS_NODE_MODULES = join(dirname(fileURLToPath(import.meta.url)), 'node_modules');

/**
 * Bundles an `.ets` (or `.ts`) entry point into a single ESM file and dynamically
 * imports it, returning the module's exports. `@noble/*` packages are bundled in;
 * `ESObject` annotations are erased by esbuild's TS loader.
 */
export async function loadEtsModule<T>(entryEtsPath: string): Promise<T> {
  const result = await build({
    entryPoints: [entryEtsPath],
    bundle: true,
    format: 'esm',
    platform: 'node',
    target: 'node18',
    // Treat ArkTS `.ets` as TypeScript: annotations (incl. ESObject) are stripped.
    loader: { '.ets': 'ts' },
    // ArkTS imports are extensionless (e.g. `from './PacketType'`); make esbuild
    // try `.ets` first when resolving, then the usual TS/JS extensions.
    resolveExtensions: ['.ets', '.ts', '.tsx', '.mjs', '.js', '.json'],
    // Resolve bare imports (@noble/*) against the harness node_modules.
    nodePaths: [HARNESS_NODE_MODULES],
    write: false,
    logLevel: 'warning',
    // Match ArkTS class-field semantics (plain assignment, not defineProperty).
    tsconfigRaw: '{"compilerOptions":{"useDefineForClassFields":false}}',
  });

  const code = result.outputFiles[0].text;
  const dir = mkdtempSync(join(tmpdir(), 'aether-arkts-'));
  const outFile = join(dir, 'bundle.mjs');
  writeFileSync(outFile, code, 'utf8');
  const mod = (await import(pathToFileURL(outFile).href)) as T;
  return mod;
}
