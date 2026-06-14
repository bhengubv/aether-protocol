/*
 * Strict type-check of the ArkTS `.ets` sources under stock TypeScript.
 *
 * esbuild (the parity harness bundler) ERASES type annotations without checking
 * them, so it proves runtime byte-parity but not type conformance. This script
 * closes that gap: it shadow-copies the `.ets` sources to `.ts`, supplies an
 * ambient `ESObject` (modelled as `unknown` — the strictest reading, forcing every
 * access to be narrowed via a `typeof` guard exactly as the sources do), and runs
 * `tsc --strict --noEmit`. A real DevEco/hvigor compile additionally applies the
 * ArkTS-specific lint rules, but this confirms the port is strict-type-clean.
 *
 * Run: npx tsx typecheck.ts
 * SPDX-License-Identifier: MIT
 */

import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const hereDir = dirname(fileURLToPath(import.meta.url));
const etsRoot = resolve(hereDir, '..', 'src', 'main', 'ets');
const indexEts = resolve(hereDir, '..', 'Index.ets');

// Work INSIDE the harness dir so `@noble/*` resolves against test/node_modules.
const work = mkdtempSync(join(hereDir, '.tc-'));
const etsOut = join(work, 'ets');

function shadowDir(dir: string): void {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, entry.name);
    if (entry.isDirectory()) {
      shadowDir(p);
    } else if (entry.name.endsWith('.ets')) {
      const rel = relative(etsRoot, p).replace(/\.ets$/, '.ts');
      const dest = join(etsOut, rel);
      mkdirSync(dirname(dest), { recursive: true });
      writeFileSync(dest, readFileSync(p, 'utf8'), 'utf8');
    }
  }
}

shadowDir(etsRoot);

// Index.ets references './src/main/ets/...'; rewrite to the shadow layout.
const indexRewritten = readFileSync(indexEts, 'utf8').replace(/\.\/src\/main\/ets\//g, './ets/');
writeFileSync(join(work, 'Index.ts'), indexRewritten, 'utf8');

// ESObject ambient — `unknown`, so every read must still be narrowed.
writeFileSync(join(work, 'arkts-ambient.d.ts'), 'type ESObject = unknown;\n', 'utf8');

const tsconfig = {
  compilerOptions: {
    target: 'ES2022',
    module: 'ESNext',
    moduleResolution: 'Bundler',
    lib: ['ES2022'],
    strict: true,
    noEmit: true,
    skipLibCheck: true,
    esModuleInterop: true,
    forceConsistentCasingInFileNames: true,
    // The work dir is a child of test/, so @noble/* resolves via node_modules
    // walk-up to test/node_modules.
    types: [],
  },
  include: ['Index.ts', 'ets/**/*.ts', 'arkts-ambient.d.ts'],
};
writeFileSync(join(work, 'tsconfig.json'), JSON.stringify(tsconfig, null, 2), 'utf8');

const tscBin = resolve(hereDir, 'node_modules', 'typescript', 'bin', 'tsc');
console.log('Type-checking ArkTS .ets sources (shadowed → .ts, tsc --strict --noEmit) ...');
try {
  execFileSync(process.execPath, [tscBin, '-p', join(work, 'tsconfig.json')], {
    stdio: 'inherit',
  });
  console.log('OK — ArkTS sources are strict-type-clean (0 errors).');
} catch {
  console.error('FAILED — type errors above.');
  process.exitCode = 1;
} finally {
  rmSync(work, { recursive: true, force: true });
}
