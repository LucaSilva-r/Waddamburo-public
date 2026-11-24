# Waddamburo on the Nintendo Switch (homebrew)

A native `.nro` for hbmenu (Atmosphère, title takeover for the full memory). It is the same `Waddamburo.App` as every
other release: compiled with .NET NativeAOT for linux-arm64, and linked against libnx through a glibc shim. The game's
data goes in `sdmc:/switch/taiko/USRDIR` as elsewhere (the user's own files; nothing of the game ships with this).

## Build

```sh
platforms/switch/build.sh            # -> out/package/Waddamburo.nro
platforms/switch/build.sh app        # after C# changes only (natives and bgfx are kept in out/switch)
```

Needs the .NET 10 SDK, curl and Docker (the devkitA64 steps run in the pinned `devkitpro/devkita64` image), or runs
entirely inside that image when `DEVKITPRO` is set (CI). Sources are pinned: vgmstream and bgfx.cmake come from
`native/`'s CMake files (the same as the desktop builds), with `patches/` applied to bgfx; FFmpeg is devkitPro's Switch
one (7.1 and its patch: averne's nvtegra, NVDEC hardware video decoding), so the `.nro` is GPL-2.0-or-later as a whole; SQLite 3.53.3 and the SDL 3.5
headers (`ppy.SDL3-CS`'s ABI, which `shim/sdl3nx.c` implements) are pinned in `build.sh`. `out/switch` keeps the build.

## Test on a console

hbmenu's netloader (press **Y**), then:

```sh
export SWITCH_IP=192.168.x.y
platforms/switch/tools/send.sh                        # nxlink the .nro; keeps out/switch/sent.elf
platforms/switch/tools/log.sh                         # the game's log (sys-ftpd; SWITCH_FTP_USER/PASSWORD/PORT)
platforms/switch/tools/log.sh switch/taiko/shim.log > err.log # the shim's stderr: [hitch] profiles of frames over 25 ms (*)
platforms/switch/tools/hitch.sh err.log               # symbolized per 10 s window, main and render thread
```

(*) The shim's sampling profiler runs only when an empty file `sdmc:/switch/taiko/profile` exists (it pauses every
thread each millisecond). Waddamburo's own slow-frame lines need `WADDAMBURO_HITCH_TRACE=1` in the shim's built-in
environment (`g_env` in `shim/shim.c`).

A frozen game: attach Atmosphère's GDB stub (port 22225) with `out/switch/sent.elf` at the module's base. Emulators are
not useful past boot: Ryujinx's thread pause/register calls do not behave like Horizon's (the GC's suspension hangs) and
it does not hand faults to the game's exception handler (a caught NullReferenceException is fatal there).

## How it works

- **Linking**: every libc symbol the runtime imports and nothing here defines is renamed to `lx_<name>`
  (`objcopy --redefine-syms`, `scripts/link.sh`); `shim/shim.c` implements that glibc/Linux ABI on newlib and Horizon.
  The runtime's exe entry becomes `lx_dotnet_main`, called by libnx's `main` (`shim/main.c`).
- **Memory**: no `svcMapPhysicalMemory` for homebrew. The GC's heap comes straight from the hbloader heap
  (`DOTNET_GCRegionRange`, `GCHeapHardLimit`, a 256 MiB gen0 budget: the built-in environment in `lx_getenv`).
- **TLS**: runtime code reads `tpidr_el0`, libnx uses `__aarch64_read_tp`; the shim copies the pointer on every thread.
- **Threads**: libnx threads behind glibc-sized pthread objects, spread over cores 0-2 (Horizon does not time-slice
  equal priorities). Exit pauses every thread ever started and unmaps its stack (they would run on inside hbmenu).
- **Exceptions**: `shim/exc.S` replaces libnx's exception entry: the faulting context is saved on the thread's own
  stack, turned into a glibc `ucontext_t` for the runtime's SIGSEGV handler, and resumed with `svcReturnFromException`.
- **GC suspension**: Horizon can pause a thread and read its registers but not write them, so the suspending thread
  calls the runtime's `Thread::HijackCallback` (as on Windows): return addresses on the stack are hijacked. Gap: a
  managed loop that never calls or returns blocks a collection. Rule for Switch-facing code: never spin on a flag
  without a call (`Thread.Sleep`, `SpinWait`, a wait handle).
- **Graphics**: bgfx's GLES 3.0 backend on devkitPro's Mesa nouveau (`-D__NX__`, EGL window = the default NWindow).
  `patches/`: opaque present, no buffer orphaning, no timer queries (each forced a `glFinish`). libdrm_nouveau's buffer
  objects get their own allocator, FreeType reads fonts from memory. The system refuses GPU buffers past ~4870 (one a
  texture): the texture atlas and `ScenePresenter`'s budget keep far below.
- **SDL3**: `shim/sdl3nx.c` is the subset Waddamburo calls (window, focus and HOME-menu events, 8 pads from a 1 kHz HID
  thread, audout at 48 kHz mixing bound streams with per-stream gain on its own thread). Text input opens the system
  keyboard (swkbd); dialogs are stubs.
- **Crypto**: hashing and HMAC on mbedTLS (`shim/cryptonx.c` overrides the OpenSSL-backed `CryptoNative_*`).
- **Network**: BSD sockets, DNS and epoll on libnx (`shim/net.c`); TLS through the console's ssl service (`shim/tls.c`).

## HOME-menu icon (forwarder)

Service menu › HOME MENU ICON writes `sdmc:/switch/taiko/Waddamburo.nsp`, which the user installs with Goldleaf (or
DBI/Tinfoil; sigpatches needed, the signatures are zero). It is made on the console from the console's own
`sdmc:/switch/prod.keys` (Lockpick_RCM from hekate): `header_key` for the NCA headers, `key_area_key_application_00`
for the key areas. Program: nx-hbloader patched to start the NRO the menu ran from (its path is written into the NSO,
whose segments are stored plain) and to exit after it; it runs as an application (program type 1), so the game gets the
full memory. Control: the NRO's own NACP and icon. Title id `0500005744420000` (homebrew range). `shim/forwarder.c`
also builds on a PC (`cc -DFORWARDER_TEST shim/forwarder.c -lcrypto`) to check its output with hactool.

## Open

Nothing known.
