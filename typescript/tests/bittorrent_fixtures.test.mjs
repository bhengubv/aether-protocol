// SPDX-License-Identifier: MIT
// Cross-language BitTorrent fixture verifier: the TS SDK asserts byte-identity against
// fixtures/bittorrent/vectors.json (Go-oracle + C#-cross-verified). Run after `tsc`:
//   node --test typescript/tests/bittorrent_fixtures.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import * as bt from '../dist-bt/index.js';

function corpus() {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (let i = 0; i < 12; i++) {
    const f = join(dir, 'fixtures', 'bittorrent', 'vectors.json');
    if (existsSync(f)) return JSON.parse(readFileSync(f, 'utf8'));
    dir = dirname(dir);
  }
  throw new Error('fixtures/bittorrent/vectors.json not found');
}

function fill(n, mult, add) {
  const b = Buffer.alloc(n);
  for (let i = 0; i < n; i++) b[i] = (i * mult + add) & 0xff;
  return b;
}

test('bencode_roundtrip', () => {
  for (const hs of corpus().bencode_roundtrip) {
    const raw = Buffer.from(hs, 'hex');
    assert.equal(bt.bencode.encode(bt.bencode.decode(raw)).toString('hex'), hs);
  }
});

test('info_hash', () => {
  for (const ic of corpus().info_hash) {
    const tb = bt.metainfo.buildSingleFileTorrent(ic.name_str, fill(ic.size, ic.mult, ic.add), ic.piece_length);
    assert.equal(bt.metainfo.parseTorrent(tb).infoHashV1Hex, ic.info_hash_hex);
  }
});

test('peer_messages', () => {
  for (const pm of corpus().peer_messages) {
    let m;
    switch (pm.kind) {
      case 'keepalive': m = bt.wire.keepAlive(); break;
      case 'choke': m = bt.wire.choke(); break;
      case 'unchoke': m = bt.wire.unchoke(); break;
      case 'interested': m = bt.wire.interested(); break;
      case 'have': m = bt.wire.have(pm.a); break;
      case 'request': m = bt.wire.request(pm.a, pm.b, pm.c); break;
      case 'port': m = bt.wire.port(pm.a); break;
      default: throw new Error(`unknown kind ${pm.kind}`);
    }
    assert.equal(m.toBytes().toString('hex'), pm.wire_hex);
  }
});

test('utp_packets', () => {
  for (const uc of corpus().utp_packets) {
    const p = new bt.utp.UtpPacket(uc.type, uc.conn_id, uc.timestamp, uc.timestamp_diff, uc.window, uc.seq, uc.ack, Buffer.from(uc.payload_hex, 'hex'));
    assert.equal(p.toBytes().toString('hex'), uc.wire_hex);
  }
});

test('merkle', () => {
  for (const mc of corpus().merkle) {
    assert.equal(bt.merkle.merkleRoot(fill(mc.size, mc.mult, mc.add)).toString('hex'), mc.root_hex);
  }
});

test('compact', () => {
  for (const cc of corpus().compact) {
    const data = Buffer.from(cc.wire_hex, 'hex');
    if (cc.kind === 'node') {
      assert.equal(bt.dht.encodeCompactNodes(bt.dht.decodeCompactNodes(data)).toString('hex'), cc.wire_hex);
    } else if (cc.kind === 'peers') {
      const built = bt.dht.encodeCompactPeers(cc.peers.map((p) => ({ ip: p.ip, port: p.port })));
      assert.equal(built.toString('hex'), cc.wire_hex);
    }
  }
});

test('krpc', () => {
  for (const kc of corpus().krpc) {
    const tx = Buffer.from(kc.tx_hex, 'hex');
    let enc;
    if (kc.kind === 'get_peers') {
      enc = bt.krpc.encodeQuery(tx, 'get_peers', { id: Buffer.from(kc.id_hex, 'hex'), info_hash: Buffer.from(kc.info_hash_hex, 'hex') });
    } else if (kc.kind === 'error') {
      enc = bt.krpc.encodeError(tx, kc.error_code, kc.error_message);
    }
    assert.equal(enc.toString('hex'), kc.wire_hex);
  }
});
