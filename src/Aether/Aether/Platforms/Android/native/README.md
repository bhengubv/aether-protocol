# Android native libsodium

`arm64-v8a/libsodium.so` is **our own build of libsodium 1.0.20** (ISC-licensed),
cross-compiled for Android with NDK r28. It is committed here on purpose.

## Why this exists

The protocol's crypto is NSec.Cryptography, which does `[DllImport("libsodium")]`.
The `libsodium` NuGet that NSec pulls transitively **ships no Android native**, so a
.NET-for-Android build silently packages the **Linux aarch64** `libsodium.so` into
`lib/arm64-v8a/`. That Linux build declares `NEEDED libpthread.so.0` — a glibc soname
that does not exist on Android (bionic folds pthread into `libc`). On-device the load
fails:

```
E monodroid-assembly: Could not load library '.../lib/arm64/libsodium.so'.
    dlopen failed: library "libpthread.so.0" not found
```

…and every crypto call throws, so the app launches to a blank page. This was only ever
caught by running on a real device (Huawei P30 Lite) — desktop/browser builds hide it.

Providing a real Android `libsodium.so` as an `AndroidNativeLibrary` (see the app csproj)
makes NSec resolve to it instead of the Linux one.

## Verify the committed binary

```
llvm-readelf -h  libsodium.so   # Class ELF64, Machine AArch64
llvm-readelf -d  libsodium.so   # NEEDED: libdl.so, libc.so  — and NO libpthread.so.0
```

## Reproducible build (Windows host, this repo's dev box)

libsodium's `dist-build/android-*.sh` scripts assume a Unix host; three fixes make them
work with the Windows NDK + git-bash:

1. **Source** — the release tarball (has a pre-generated `configure`, so no autotools):
   `curl -sSLO https://download.libsodium.org/libsodium/releases/libsodium-1.0.20-stable.tar.gz`
2. **Toolchain env** (NDK r28 at `%LOCALAPPDATA%\Android\Sdk\ndk\28.0.13004108`):
   - `ANDROID_NDK_HOME` = the NDK (Windows-style `C:/...`)
   - `NDK_PLATFORM=android-24` (matches the app's `SupportedOSPlatformVersion`)
   - force `TOOLCHAIN_OS_DIR=windows-x86_64/` (the scripts derive it from `uname`, which
     is wrong under git-bash)
   - PATH must use **POSIX** form (`/c/...`, not `C:/...`) so git-bash resolves `make`/clang
3. **Three Windows-specific patches to `dist-build/android-build.sh`:**
   - `--with-sysroot=no` (libtool rejects the `C:/...` sysroot as "not absolute")
   - run `make` with a **space-free shell** — git-bash's `sh.exe` lives under
     `C:/Program Files/…` and make drops that unquoted into libtool recipes → `Error 127`
     (`C:/Program: No such file or directory`). Use MSYS2's `sh.exe` (`C:/msys64/…`, no
     space). Simplest split that works: run **`./configure` under git-bash** (correct
     `--prefix`), then **`make install` with `C:/msys64/usr/bin` prepended** (space-free
     sh + consistent coreutils).
   - `-j1` (git-bash `fork()` emulation is flaky under parallel make)

Output: `src/libsodium/.libs/libsodium.so` → copied to `arm64-v8a/libsodium.so` here.

## TODO (other ABIs)

Only `arm64-v8a` is built (the P30 Lite benchmark). For full device coverage, build
`armeabi-v7a`, `x86`, `x86_64` the same way (`android-armv7-a.sh`, `android-x86.sh`,
`android-x86_64.sh`) and add matching `AndroidNativeLibrary` entries.
