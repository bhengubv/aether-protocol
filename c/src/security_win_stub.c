// SPDX-License-Identifier: MIT
// Minimal Windows stub for the three security primitives required by
// protocol.c on Windows test builds that lack libsodium.
//
// Used ONLY when building test_routing.exe directly with cl.exe on Windows.
// Do NOT link this file on Linux/macOS or in production CMake builds.

#ifdef _WIN32

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <wincrypt.h>   /* CryptAcquireContext / CryptGenRandom */
#include <bcrypt.h>     /* BCryptHash (SHA256) */
#include <string.h>
#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "advapi32.lib")

/* ── aether_random_bytes ──────────────────────────────────────────── */
bool aether_random_bytes(uint8_t *out, size_t len) {
    if (!out || len == 0) return false;
    /* rand_s fills 4 bytes at a time; use CryptGenRandom for arbitrary lengths */
    HCRYPTPROV hProv = 0;
    if (!CryptAcquireContextW(&hProv, NULL, NULL,
                              PROV_RSA_FULL,
                              CRYPT_VERIFYCONTEXT | CRYPT_SILENT)) {
        return false;
    }
    BOOL ok = CryptGenRandom(hProv, (DWORD)len, (BYTE *)out);
    CryptReleaseContext(hProv, 0);
    return ok != 0;
}

/* ── aether_sha256 ────────────────────────────────────────────────── */
bool aether_sha256(const uint8_t *data, size_t data_len, uint8_t *out_hash) {
    if (!out_hash) return false;
    BCRYPT_ALG_HANDLE hAlg = NULL;
    BCRYPT_HASH_HANDLE hHash = NULL;
    NTSTATUS status;
    bool result = false;

    status = BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM,
                                         NULL, 0);
    if (!BCRYPT_SUCCESS(status)) goto cleanup;

    status = BCryptCreateHash(hAlg, &hHash, NULL, 0, NULL, 0, 0);
    if (!BCRYPT_SUCCESS(status)) goto cleanup;

    if (data && data_len > 0) {
        status = BCryptHashData(hHash, (PUCHAR)(uintptr_t)data,
                                (ULONG)data_len, 0);
        if (!BCRYPT_SUCCESS(status)) goto cleanup;
    }

    status = BCryptFinishHash(hHash, (PUCHAR)out_hash, 32, 0);
    if (!BCRYPT_SUCCESS(status)) goto cleanup;

    result = true;
cleanup:
    if (hHash) BCryptDestroyHash(hHash);
    if (hAlg)  BCryptCloseAlgorithmProvider(hAlg, 0);
    return result;
}

/* ── aether_zeroize ───────────────────────────────────────────────── */
void aether_zeroize(void *mem, size_t len) {
    if (mem && len > 0) {
        SecureZeroMemory(mem, len);
    }
}

#endif /* _WIN32 */
