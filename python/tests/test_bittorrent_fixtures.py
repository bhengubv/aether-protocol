# SPDX-License-Identifier: MIT
"""Cross-language BitTorrent fixture verifier: the Python SDK asserts byte-identity
against fixtures/bittorrent/vectors.json (the Go-oracle + C#-cross-verified corpus)."""
import json
import pathlib

from aethernet.bittorrent import bencode, dht, extensions, krpc, logic, merkle, metainfo, utp, wire


def _corpus():
    here = pathlib.Path(__file__).resolve()
    for parent in here.parents:
        f = parent / "fixtures" / "bittorrent" / "vectors.json"
        if f.exists():
            return json.loads(f.read_text())
    raise FileNotFoundError("fixtures/bittorrent/vectors.json not found")


def _fill(n, mult, add):
    return bytes((i * mult + add) & 0xFF for i in range(n))


def test_bencode_roundtrip():
    for hs in _corpus()["bencode_roundtrip"]:
        raw = bytes.fromhex(hs)
        assert bencode.encode(bencode.decode(raw)).hex() == hs


def test_info_hash():
    for ic in _corpus()["info_hash"]:
        content = _fill(ic["size"], ic["mult"], ic["add"])
        tb = metainfo.build_single_file_torrent(ic["name_str"], content, ic["piece_length"])
        assert metainfo.parse_torrent(tb).info_hash_v1_hex == ic["info_hash_hex"]


def test_peer_messages():
    for pm in _corpus()["peer_messages"]:
        k, a = pm["kind"], pm["a"]
        msg = {
            "keepalive": lambda: wire.keep_alive(),
            "choke": lambda: wire.choke(),
            "unchoke": lambda: wire.unchoke(),
            "interested": lambda: wire.interested(),
            "have": lambda: wire.have(a),
            "request": lambda: wire.request(a, pm["b"], pm["c"]),
            "port": lambda: wire.port(a),
        }[k]()
        assert msg.to_bytes().hex() == pm["wire_hex"]


def test_utp_packets():
    for uc in _corpus()["utp_packets"]:
        p = utp.UtpPacket(uc["type"], uc["conn_id"], uc["timestamp"], uc["timestamp_diff"],
                          uc["window"], uc["seq"], uc["ack"], bytes.fromhex(uc["payload_hex"]))
        assert p.to_bytes().hex() == uc["wire_hex"]


def test_merkle():
    for mc in _corpus()["merkle"]:
        assert merkle.merkle_root(_fill(mc["size"], mc["mult"], mc["add"])).hex() == mc["root_hex"]


def test_compact():
    for cc in _corpus()["compact"]:
        data = bytes.fromhex(cc["wire_hex"])
        if cc["kind"] == "node":
            assert dht.encode_compact_nodes(dht.decode_compact_nodes(data)).hex() == cc["wire_hex"]
        elif cc["kind"] == "peers":
            built = dht.encode_compact_peers([(p["ip"], p["port"]) for p in cc["peers"]])
            assert built.hex() == cc["wire_hex"]


def test_krpc():
    for kc in _corpus()["krpc"]:
        tx = bytes.fromhex(kc["tx_hex"])
        if kc["kind"] == "get_peers":
            args = {b"id": bytes.fromhex(kc["id_hex"]), b"info_hash": bytes.fromhex(kc["info_hash_hex"])}
            enc = krpc.encode_query(tx, "get_peers", args)
        elif kc["kind"] == "error":
            enc = krpc.encode_error(tx, kc["error_code"], kc["error_message"])
        else:
            raise AssertionError(kc["kind"])
        assert enc.hex() == kc["wire_hex"]


# ── core logic + extensions sanity (not byte-fixtured, but proven) ──

def test_picker_and_store_roundtrip():
    data = bytes((i * 7) & 0xFF for i in range(5000))
    store = logic.piece_store_from_content(data, 1024)
    assert store.is_complete() and store.assemble() == data

    picker = logic.RarestFirstPicker(4)
    for i in (0, 1, 2):
        picker.peer_has_piece("A", i)
    for i in (1, 2, 3):
        picker.peer_has_piece("B", i)
    assert picker.pick_for("A") == 0  # rarest (availability 1)


def test_extensions_roundtrip():
    payload = extensions.build_extension_handshake({"ut_metadata": 1, "ut_pex": 2}, 1024)
    sub, body = extensions.split_extended(payload)
    h = extensions.parse_extension_handshake(body)
    assert sub == 0 and h["supported"]["ut_metadata"] == 1 and h["metadata_size"] == 1024

    m = extensions.parse_metadata(extensions.build_metadata_data(0, 100, b"\x01\x02\x03"))
    assert m["type"] == 1 and m["piece"] == 0 and m["total_size"] == 100 and m["data"] == b"\x01\x02\x03"

    peers = extensions.parse_pex_added(extensions.build_pex_added([("1.2.3.4", 1000), ("5.6.7.8", 2000)]))
    assert peers == [("1.2.3.4", 1000), ("5.6.7.8", 2000)]
