# SPDX-License-Identifier: MIT
"""Unit tests for the RLNC engine (GF(2⁸), encoder, decoder, codec)."""

from __future__ import annotations

import unittest

from aethernet.transport.rlnc import (
    RlncCodec,
    RlncDecoder,
    RlncEncoder,
    gf256_add,
    gf256_inv,
    gf256_mul,
)


# ── GF(2⁸) arithmetic ─────────────────────────────────────────────────────────

class TestGf256Arithmetic(unittest.TestCase):

    def test_add_is_xor(self):
        self.assertEqual(gf256_add(0xAB, 0xCD), 0xAB ^ 0xCD)
        self.assertEqual(gf256_add(0, 0), 0)

    def test_mul_by_zero(self):
        for v in range(256):
            self.assertEqual(gf256_mul(v, 0), 0)
            self.assertEqual(gf256_mul(0, v), 0)

    def test_mul_by_one(self):
        for v in range(1, 256):
            self.assertEqual(gf256_mul(v, 1), v)
            self.assertEqual(gf256_mul(1, v), v)

    def test_mul_inv_round_trip(self):
        """Mul(a, Inv(a)) == 1 for all non-zero a."""
        for v in range(1, 256):
            self.assertEqual(gf256_mul(v, gf256_inv(v)), 1,
                             f"Mul({v}, Inv({v})) != 1")

    def test_mul_commutativity(self):
        for a in range(1, 32):
            for b in range(1, 32):
                self.assertEqual(gf256_mul(a, b), gf256_mul(b, a))

    def test_mul_distributivity(self):
        """a*(b+c) == a*b + a*c  (spot-check)."""
        a, b, c = 0x53, 0xCA, 0x77
        lhs = gf256_mul(a, gf256_add(b, c))
        rhs = gf256_add(gf256_mul(a, b), gf256_mul(a, c))
        self.assertEqual(lhs, rhs)

    def test_inv_of_one(self):
        self.assertEqual(gf256_inv(1), 1)

    def test_inv_raises_for_zero(self):
        with self.assertRaises(ZeroDivisionError):
            gf256_inv(0)


# ── RlncEncoder ───────────────────────────────────────────────────────────────

class TestRlncEncoder(unittest.TestCase):

    def _make_encoder(self, k: int, sym_size: int = 3, systematic: bool = True):
        source = [bytearray(range(i, i + sym_size)) for i in range(k)]
        return RlncEncoder(source, systematic=systematic), source

    def test_systematic_first_k_packets_identity_coeff(self):
        k = 4
        enc, source = self._make_encoder(k)
        for i in range(k):
            coeff, data = enc.next_packet()
            # Coefficient vector must be e_i.
            self.assertEqual(len(coeff), k)
            for j in range(k):
                self.assertEqual(coeff[j], 1 if j == i else 0,
                                 f"pkt {i}: coeff[{j}] should be {1 if j==i else 0}")
            # Data must equal source symbol.
            self.assertEqual(bytes(data), bytes(source[i]),
                             f"systematic pkt {i} data mismatch")

    def test_repair_packets_not_all_zero(self):
        enc, _ = self._make_encoder(k=3, systematic=False)
        for i in range(20):
            coeff, _ = enc.next_packet()
            self.assertFalse(all(c == 0 for c in coeff),
                             f"repair pkt {i} has all-zero coefficient vector")

    def test_error_on_empty_source(self):
        with self.assertRaises(ValueError):
            RlncEncoder([])


# ── RlncDecoder ───────────────────────────────────────────────────────────────

class TestRlncDecoder(unittest.TestCase):

    def test_starts_at_rank_zero(self):
        dec = RlncDecoder(k=4, symbol_size=8)
        self.assertEqual(dec.rank, 0)
        self.assertFalse(dec.is_complete)

    def test_linearly_dependent_packet_rejected(self):
        dec = RlncDecoder(k=3, symbol_size=4)
        coeff = bytes([1, 0, 0])
        data  = bytes([10, 20, 30, 40])
        self.assertTrue(dec.add_packet(coeff, data))
        self.assertFalse(dec.add_packet(coeff, data),
                         "duplicate packet must not increase rank")
        self.assertEqual(dec.rank, 1)

    def test_complete_after_k_independent_packets(self):
        k, s = 3, 2
        dec = RlncDecoder(k=k, symbol_size=s)
        for i in range(k):
            coeff = bytearray(k)
            coeff[i] = 1
            data = bytes([i + 1, i + 100])
            dec.add_packet(bytes(coeff), data)
        self.assertTrue(dec.is_complete)

    def test_try_decode_returns_none_when_incomplete(self):
        dec = RlncDecoder(k=4, symbol_size=4)
        self.assertIsNone(dec.try_decode())

    def test_try_decode_symbol_ordering(self):
        k, s = 3, 2
        dec = RlncDecoder(k=k, symbol_size=s)
        sources = [(0xAA, 0xBB), (0xCC, 0xDD), (0xEE, 0xFF)]
        for i, (a, b) in enumerate(sources):
            coeff = bytearray(k)
            coeff[i] = 1
            dec.add_packet(bytes(coeff), bytes([a, b]))
        result = dec.try_decode()
        self.assertIsNotNone(result)
        for i, (a, b) in enumerate(sources):
            self.assertEqual(result[i * s],     a, f"symbol[{i}][0]")
            self.assertEqual(result[i * s + 1], b, f"symbol[{i}][1]")


# ── RlncCodec: round-trips ────────────────────────────────────────────────────

class TestRlncCodecRoundTrip(unittest.TestCase):

    def _decode_split(self, encoded: bytes, k: int, count: int):
        pkt_size = len(encoded) // count
        return [encoded[i * pkt_size:(i + 1) * pkt_size] for i in range(count)]

    def test_k4_systematic_decode(self):
        source = b"aether-rlnc-python-test-k4"
        codec  = RlncCodec(generation_size=4)
        encoded = codec.encode(source, 4)
        pkts = self._decode_split(encoded, 4, 4)
        decoded = codec.try_decode(pkts, 4)
        self.assertIsNotNone(decoded)
        self.assertEqual(decoded[:len(source)], source)

    def test_k4_with_repair_overhead(self):
        source = b"round-trip with extra repair packets"
        codec  = RlncCodec(generation_size=4)
        encoded = codec.encode(source, 6)
        pkts = self._decode_split(encoded, 4, 6)[:4]  # just systematic
        decoded = codec.try_decode(pkts, 4)
        self.assertIsNotNone(decoded)
        self.assertEqual(decoded[:len(source)], source)

    def test_repair_only_decode(self):
        source = b"repair-only round-trip in Python"
        codec  = RlncCodec(generation_size=4)
        encoded = codec.encode(source, 8)
        pkts = self._decode_split(encoded, 4, 8)[4:]  # skip systematic
        decoded = codec.try_decode(pkts, 4)
        self.assertIsNotNone(decoded, "repair-only decode failed")
        self.assertEqual(decoded[:len(source)], source)

    def test_k1_single_symbol(self):
        source = b"z"
        codec  = RlncCodec(generation_size=1)
        encoded = codec.encode(source, 2)
        pkts = self._decode_split(encoded, 1, 2)[:1]
        decoded = codec.try_decode(pkts, 1)
        self.assertIsNotNone(decoded)
        self.assertEqual(decoded[0], ord("z"))

    def test_k16_large_payload(self):
        source = bytes(range(256)) * 4  # 1024 bytes
        codec  = RlncCodec(generation_size=16)
        encoded = codec.encode(source, 20)
        pkts = self._decode_split(encoded, 16, 20)
        decoded = codec.try_decode(pkts, 16)
        self.assertIsNotNone(decoded)
        self.assertEqual(decoded[:len(source)], source)

    def test_empty_received_returns_none(self):
        codec = RlncCodec(generation_size=4)
        self.assertIsNone(codec.try_decode([], 4))


# ── Codec metadata ─────────────────────────────────────────────────────────────

class TestRlncCodecMetadata(unittest.TestCase):

    def test_codec_name(self):
        self.assertEqual(RlncCodec().codec_name, "RLNC-GF256")

    def test_overhead_fraction(self):
        self.assertAlmostEqual(RlncCodec().overhead_fraction, 0.05)

    def test_rejects_generation_size_zero(self):
        with self.assertRaises(ValueError):
            RlncCodec(generation_size=0)

    def test_rejects_generation_size_256(self):
        with self.assertRaises(ValueError):
            RlncCodec(generation_size=256)


if __name__ == "__main__":
    unittest.main()
