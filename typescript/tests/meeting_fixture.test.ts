/**
 * Cross-language rendezvous parity: TS must reproduce the C# reference vectors
 * (fixtures/meeting/meeting_basic.json) byte-for-byte. SPDX-License-Identifier: MIT
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { Meeting, LENGTH } from "../src/meeting/Meeting.js";

const fixturePath = fileURLToPath(new URL("../../fixtures/meeting/meeting_basic.json", import.meta.url));
const F = JSON.parse(readFileSync(fixturePath, "utf8"));

const hex = (b: Uint8Array): string => Buffer.from(b).toString("hex");

test("meeting byte-parity with the C# reference fixture", () => {
  assert.equal(F.info, "aether-meeting-v1");
  assert.equal(F.length, LENGTH);

  for (const c of F.cases) {
    const m = Meeting.with(c.my_tag, c.their_tag);
    assert.ok(m, `${c.name}: expected a meeting`);
    assert.equal(m.rendezvous, c.rendezvous, `${c.name} rendezvous`);
    assert.equal(m.iStart, c.i_start, `${c.name} i_start`);
    assert.equal(m.uuidString(), c.uuid_string, `${c.name} uuid_string`);
    assert.equal(hex(m.uuidBytes()), c.uuid, `${c.name} uuid`);
    for (const [bits, want] of Object.entries(c.address)) {
      assert.equal(m.address(Number(bits)), want, `${c.name} addr@${bits}`);
    }
    assert.equal(m.rendezvous.length, F.length, `${c.name} length`);
    assert.ok([...m.rendezvous].every((ch) => F.alphabet.includes(ch)), `${c.name} alphabet`);
  }
});

test("the same pair either way round meets at one place with opposite host roles", () => {
  const a = Meeting.with("BH8CZ-B09CA", "DY5CF-84G9T");
  const b = Meeting.with("DY5CF-84G9T", "BH8CZ-B09CA");
  assert.ok(a && b);
  assert.equal(a.rendezvous, b.rendezvous);
  assert.equal(a.uuidString(), b.uuidString());
  assert.notEqual(a.iStart, b.iStart);
});

test("rejected inputs yield no meeting", () => {
  for (const r of F.rejects) {
    assert.equal(Meeting.with(r.my_tag ?? "", r.their_tag ?? ""), null, `${r.name}`);
  }
});
