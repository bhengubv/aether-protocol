// SPDX-License-Identifier: MIT
//
// Behavioural tests for the FMHY catalogue: the markdown parser (headings ->
// category, bold link -> entry, ⭐ -> starred) and the in-memory catalogue
// (sync + entryCount + category browse + getStarred).

import test from "node:test";
import assert from "node:assert/strict";

import { InMemoryFmhyCatalogueService, parseFmhyMarkdown } from "../src/fmhy/FmhyCatalogue.js";

const MD = `# Video
## Streaming
* **[FreeFlix](https://freeflix.example)** - Free movies and shows
* ⭐ **[BestStream](https://best.example)** - The top pick

# Audio
* **[TunePort](https://tune.example)** - Music streaming
`;

test("fmhy markdown parse + catalogue behaviour", async () => {
  const parsed = parseFmhyMarkdown(MD);
  assert.equal(parsed.length, 3);
  assert.equal(parsed[0].category, "Video / Streaming");
  assert.equal(parsed[0].name, "FreeFlix");
  assert.equal(parsed[1].isStarred, true);
  assert.equal(parsed[1].name, "BestStream");
  assert.equal(parsed[2].category, "Audio");

  const svc = new InMemoryFmhyCatalogueService();
  assert.equal(svc.entryCount, 0);
  let synced = 0;
  svc.onSynced = () => {
    synced++;
  };
  await svc.sync(MD);
  assert.equal(svc.entryCount, 3);
  assert.equal(synced, 1);

  assert.equal(svc.browse().length, 3);
  assert.equal(svc.browse("video").length, 2);
  assert.equal(svc.browse("audio").length, 1);
  assert.equal(svc.browse("nonexistent").length, 0);

  const starred = svc.getStarred();
  assert.equal(starred.length, 1);
  assert.equal(starred[0].name, "BestStream");
});
