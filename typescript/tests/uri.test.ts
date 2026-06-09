// SPDX-License-Identifier: MIT
/**
 * Tests for the `aether://` URI scheme.
 *
 * Drives the cross-language corpus at `tests/cross-language/uri-fixtures.json`
 * and adds hand-written tests for the builder + router.
 *
 * Run with: tsx --test typescript/tests/uri.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

import {
  AetherUri,
  AetherUriBuilder,
  AetherUriError,
  AetherUriHandlerManifest,
  AetherUriRouter,
  HandlerDescriptor,
  SCHEME,
  type DispatchContext,
} from "../src/uri/index.js";

// ── Corpus loading ───────────────────────────────────────────────────────────

interface ValidFixture {
  name: string;
  input: string;
  canonical: string;
  authority: string;
  path: string;
  handlerName: string;
  pathSegments: string[];
  query: Record<string, string>;
  fragment: string;
}

interface InvalidFixture {
  name: string;
  input: string;
}

interface ManifestEntry {
  handlerName: string;
  pathTemplate: string;
}

interface ManifestMatch {
  input: string;
  matched: boolean;
  handlerIndex?: number;
  captures?: Record<string, string>;
}

interface Corpus {
  valid: ValidFixture[];
  invalid: InvalidFixture[];
  manifest: {
    appId: string;
    handlers: ManifestEntry[];
    matches: ManifestMatch[];
  };
}

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

/** Walk up from this test file until we find tests/cross-language/uri-fixtures.json. */
function findCorpus(): string {
  let dir = __dirname;
  for (let i = 0; i < 10; i++) {
    const candidate = resolve(dir, "tests", "cross-language", "uri-fixtures.json");
    if (existsSync(candidate)) return candidate;
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  throw new Error(
    `Could not locate tests/cross-language/uri-fixtures.json starting from ${__dirname}`,
  );
}

const corpus: Corpus = JSON.parse(
  readFileSync(findCorpus(), "utf-8"),
) as Corpus;

// ── Corpus tests — valid fixtures ────────────────────────────────────────────

describe("AetherUri — corpus valid fixtures", () => {
  for (const fx of corpus.valid) {
    it(`parses + canonicalises ${fx.name}`, () => {
      const uri = AetherUri.parse(fx.input);
      assert.equal(uri.authority, fx.authority, "authority");
      assert.equal(uri.path, fx.path, "path");
      assert.equal(uri.handlerName, fx.handlerName, "handlerName");
      assert.deepEqual([...uri.pathSegments], fx.pathSegments, "pathSegments");
      assert.equal(uri.fragment, fx.fragment, "fragment");

      // Query: keys in corpus may be any case, parser stores them lower-case.
      const expectedQueryKeys = Object.keys(fx.query);
      assert.equal(
        uri.query.size,
        expectedQueryKeys.length,
        "query.size mismatch",
      );
      for (const k of expectedQueryKeys) {
        assert.equal(uri.query.get(k.toLowerCase()), fx.query[k], `query[${k}]`);
      }

      // Canonical re-encode must match the fixture exactly.
      assert.equal(uri.toString(), fx.canonical, "canonical form");
    });
  }

  it("round-trips: parse(canonical).toString() === canonical for every valid fixture", () => {
    for (const fx of corpus.valid) {
      const uri = AetherUri.parse(fx.canonical);
      assert.equal(
        uri.toString(),
        fx.canonical,
        `round-trip failed for ${fx.name}`,
      );
    }
  });
});

// ── Corpus tests — invalid fixtures ──────────────────────────────────────────

describe("AetherUri — corpus invalid fixtures", () => {
  for (const fx of corpus.invalid) {
    it(`tryParse rejects ${fx.name}`, () => {
      const result = AetherUri.tryParse(fx.input);
      assert.equal(result.uri, undefined, `${fx.name} should not parse`);
      assert.ok(
        typeof result.error === "string" && result.error.length > 0,
        `${fx.name} should report an error string`,
      );
    });

    it(`parse throws AetherUriError for ${fx.name}`, () => {
      assert.throws(
        () => AetherUri.parse(fx.input),
        (err: unknown) => err instanceof AetherUriError,
        `${fx.name} should throw AetherUriError`,
      );
    });
  }
});

// ── Corpus tests — manifest matches ──────────────────────────────────────────

describe("AetherUriHandlerManifest — corpus matches", () => {
  const handlers = corpus.manifest.handlers.map(
    (h) =>
      new HandlerDescriptor({
        name: h.handlerName,
        pathTemplate: h.pathTemplate,
      }),
  );
  const manifest = new AetherUriHandlerManifest(
    corpus.manifest.appId,
    handlers,
  );

  for (const m of corpus.manifest.matches) {
    it(`resolves ${m.input} → matched=${m.matched}`, () => {
      const uri = AetherUri.parse(m.input);
      const resolved = manifest.resolve(uri);
      if (!m.matched) {
        assert.equal(resolved, null, "expected no match");
        return;
      }
      assert.ok(resolved !== null, "expected a match");
      assert.equal(
        handlers.indexOf(resolved.handler),
        m.handlerIndex,
        "handlerIndex",
      );
      const expectedCaptures = m.captures ?? {};
      assert.equal(
        resolved.captures.size,
        Object.keys(expectedCaptures).length,
        "captures size",
      );
      for (const [k, v] of Object.entries(expectedCaptures)) {
        assert.equal(resolved.captures.get(k), v, `capture ${k}`);
      }
    });
  }
});

// ── Hand-written: builder ────────────────────────────────────────────────────

describe("AetherUriBuilder", () => {
  it("builds a complete URI fluently", () => {
    const uri = new AetherUriBuilder()
      .authority("KXJB7-MN2P4")
      .path("content/sha256-abc123")
      .query("codec", "opus")
      .fragment("t=1m30s")
      .build();
    assert.equal(
      uri.toString(),
      "aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus#t=1m30s",
    );
  });

  it("appendSegment composes paths", () => {
    const uri = new AetherUriBuilder()
      .authority("KXJB7-MN2P4")
      .appendSegment("watch")
      .appendSegment("sess-99")
      .appendSegment("join")
      .build();
    assert.equal(uri.path, "watch/sess-99/join");
    assert.deepEqual([...uri.pathSegments], ["watch", "sess-99", "join"]);
  });

  it("removeQuery clears a previously-set key (case-insensitive)", () => {
    const builder = new AetherUriBuilder()
      .authority("KXJB7-MN2P4")
      .path("x")
      .query("CODEC", "opus")
      .query("bitrate", "128");
    builder.removeQuery("codec"); // different case from the .query() call
    const uri = builder.build();
    assert.equal(uri.query.size, 1);
    assert.equal(uri.query.get("bitrate"), "128");
  });

  it("build() throws AetherUriError when authority is missing", () => {
    assert.throws(
      () => new AetherUriBuilder().path("profile").build(),
      (err: unknown) => err instanceof AetherUriError,
    );
  });

  it("path() strips leading slashes", () => {
    const uri = new AetherUriBuilder()
      .authority("KXJB7-MN2P4")
      .path("///profile/avatar")
      .build();
    assert.equal(uri.path, "profile/avatar");
  });

  it("authority() canonicalises a lowercase tag without dash", () => {
    const uri = new AetherUriBuilder().authority("kxjb7mn2p4").build();
    assert.equal(uri.authority, "KXJB7-MN2P4");
  });
});

// ── Hand-written: router ─────────────────────────────────────────────────────

describe("AetherUriRouter", () => {
  function buildRouter() {
    const profile = new HandlerDescriptor({ name: "profile" });
    const content = new HandlerDescriptor({
      name: "content",
      pathTemplate: "{hash}",
    });
    const watch = new HandlerDescriptor({
      name: "watch",
      pathTemplate: "{sessionId}/join",
    });
    const manifest = new AetherUriHandlerManifest("aether.test", [
      profile,
      content,
      watch,
    ]);
    return { manifest, profile, content, watch };
  }

  it("dispatches to the matching handler with captures", async () => {
    const { manifest, content } = buildRouter();
    const router = new AetherUriRouter(manifest);

    const captured: { hash?: string; uri?: string } = {};
    router.registerHandler(content, async (ctx: DispatchContext) => {
      captured.hash = ctx.routeParameters.get("hash");
      captured.uri = ctx.uri.toString();
    });

    const ok = await router.dispatch(
      "aether://KXJB7-MN2P4/content/sha256-abc",
    );
    assert.equal(ok, true);
    assert.equal(captured.hash, "sha256-abc");
    assert.equal(captured.uri, "aether://KXJB7-MN2P4/content/sha256-abc");
  });

  it("accepts both AetherUri and string input on dispatch", async () => {
    const { manifest, profile } = buildRouter();
    const router = new AetherUriRouter(manifest);
    let calls = 0;
    router.registerHandler(profile, async () => {
      calls += 1;
    });
    const uri = AetherUri.parse("aether://KXJB7-MN2P4/profile");
    assert.equal(await router.dispatch(uri), true);
    assert.equal(await router.dispatch("aether://KXJB7-MN2P4/profile"), true);
    assert.equal(calls, 2);
  });

  it("returns false when no handler matches", async () => {
    const { manifest } = buildRouter();
    const router = new AetherUriRouter(manifest);
    const ok = await router.dispatch("aether://KXJB7-MN2P4/unknown");
    assert.equal(ok, false);
  });

  it("returns false when a descriptor matches but no callback is registered", async () => {
    const { manifest } = buildRouter();
    const router = new AetherUriRouter(manifest);
    // No registerHandler call — `profile` descriptor matches but is unbound.
    const ok = await router.dispatch("aether://KXJB7-MN2P4/profile");
    assert.equal(ok, false);
  });

  it("registerHandler rejects descriptors not in the manifest", () => {
    const { manifest } = buildRouter();
    const router = new AetherUriRouter(manifest);
    const stranger = new HandlerDescriptor({ name: "stranger" });
    assert.throws(
      () => router.registerHandler(stranger, async () => {}),
      (err: unknown) => err instanceof AetherUriError,
    );
  });

  it("re-registering replaces the previous callback", async () => {
    const { manifest, profile } = buildRouter();
    const router = new AetherUriRouter(manifest);
    let chosen = "";
    router.registerHandler(profile, async () => {
      chosen = "first";
    });
    router.registerHandler(profile, async () => {
      chosen = "second";
    });
    await router.dispatch("aether://KXJB7-MN2P4/profile");
    assert.equal(chosen, "second");
  });

  it("handler exceptions propagate to the caller of dispatch", async () => {
    const { manifest, profile } = buildRouter();
    const router = new AetherUriRouter(manifest);
    router.registerHandler(profile, async () => {
      throw new Error("boom");
    });
    await assert.rejects(
      router.dispatch("aether://KXJB7-MN2P4/profile"),
      /boom/,
    );
  });

  it("string dispatch throws AetherUriError for bad input", async () => {
    const { manifest } = buildRouter();
    const router = new AetherUriRouter(manifest);
    await assert.rejects(
      router.dispatch("not-a-uri"),
      (err: unknown) => err instanceof AetherUriError,
    );
  });
});

// ── Hand-written: equality ───────────────────────────────────────────────────

describe("AetherUri — equality", () => {
  it("two URIs with differently-cased query keys compare equal", () => {
    const a = AetherUri.parse("aether://KXJB7-MN2P4/x?Codec=opus&BITRATE=128");
    const b = AetherUri.parse("aether://KXJB7-MN2P4/x?codec=opus&bitrate=128");
    assert.ok(a.equals(b));
    assert.ok(b.equals(a));
  });

  it("two URIs with differently-ordered queries compare equal", () => {
    const a = AetherUri.parse("aether://KXJB7-MN2P4/x?a=1&b=2");
    const b = AetherUri.parse("aether://KXJB7-MN2P4/x?b=2&a=1");
    assert.ok(a.equals(b));
  });

  it("differing fragments compare not-equal", () => {
    const a = AetherUri.parse("aether://KXJB7-MN2P4/x#one");
    const b = AetherUri.parse("aether://KXJB7-MN2P4/x#two");
    assert.equal(a.equals(b), false);
  });

  it("scheme constant is the public name 'aether'", () => {
    assert.equal(SCHEME, "aether");
  });
});
