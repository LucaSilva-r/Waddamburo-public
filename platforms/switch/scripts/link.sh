#!/bin/bash
# Inside the devkitA64 image (build.sh link): links ILC's object, the NativeAOT runtime ($OUT/rt), the shim and the
# native libraries into $ROOT/out/package/Waddamburo.nro ($OUT/app.elf keeps the symbols for tools/hitch.sh).
set -e
export PATH=$DEVKITPRO/devkitA64/bin:$DEVKITPRO/tools/bin:$PATH
cd "$OUT"
NM=aarch64-none-elf-nm
syms() { "$NM" "$@" --format=just-symbols 2>/dev/null | grep -v -e ':$' -e '^$' | sort -u; }

# The runtime is built for glibc: every libc symbol it needs and nobody here defines is renamed to lx_<name>, which the
# shim implements on libnx; its exe entry becomes lx_dotnet_main (libnx owns main).
rm -rf rt-link && cp -r rt rt-link
syms --undefined-only rt-link/* > undef.txt
syms --defined-only rt-link/* > def.txt
{ syms --defined-only natives/prefix/lib/*.a bgfx/prefix/lib/*.a
  (cd "$DEVKITPRO/portlibs/switch/lib" && "$NM" --defined-only libz.a libfreetype.a libharfbuzz.a libpng16.a libbz2.a libopus.a 2>/dev/null | awk 'NF==3{print $3}')
} | sort -u > ours.txt
comm -23 undef.txt def.txt | comm -23 - ours.txt | grep -v -e '^__start_' -e '^__stop_' -e '^SDL_' -e '^sdlnx_' -e '^nxtls_' -e '^nxfwd_' -e '^_Z' \
  | awk '{print $1" lx_"$1}' > rename.map
echo "main lx_dotnet_main" >> rename.map
for f in rt-link/*; do aarch64-none-elf-objcopy --redefine-syms=rename.map "$f"; done
# __security_cookie lives in startup.cpp.o .rodata and the runtime writes it once: Horizon homebrew cannot mprotect
# its own image, so the .rodata of that object becomes writable data.
(cd rt-link && aarch64-none-elf-ar x libRuntime.WorkstationGC.a startup.cpp.o &&
  aarch64-none-elf-objcopy --rename-section .rodata=.data.rt_startup_rodata,alloc,load,data,contents startup.cpp.o &&
  aarch64-none-elf-ar r libRuntime.WorkstationGC.a startup.cpp.o && rm startup.cpp.o)
# libdrm_nouveau's buffer objects get their own allocator (shim.c nx_bo_memalign): malloc fragmented under them.
cp "$DEVKITPRO/portlibs/switch/lib/libdrm_nouveau.a" libdrm_nouveau_nx.a
aarch64-none-elf-objcopy --redefine-sym memalign=nx_bo_memalign --redefine-sym free=nx_bo_free libdrm_nouveau_nx.a
# FreeType reads fonts from memory (shim.c nx_font_fopen) instead of streaming them from the SD card.
cp "$DEVKITPRO/portlibs/switch/lib/libfreetype.a" libfreetype_nx.a
aarch64-none-elf-objcopy --redefine-sym fopen=nx_font_fopen libfreetype_nx.a
# Hashing/HMAC on mbedTLS: the shim's CryptoNative_* override the OpenSSL-backed ones (weakened in the archive).
ARCH="-march=armv8-a+crc+crypto -mtune=cortex-a57 -mtp=soft -fPIE"
CF="-g -O2 -Wall -ffunction-sections $ARCH -D__SWITCH__ -I$DEVKITPRO/libnx/include"
S=$HERE/shim
mkdir -p obj
aarch64-none-elf-gcc $CF -I"$OUT/ext/sqlite-amalgamation-3530300" -c "$S/shim.c" -o obj/shim.o
aarch64-none-elf-gcc $CF -c "$S/main.c" -o obj/main.o
aarch64-none-elf-gcc $CF -c "$S/net.c" -o obj/net.o
aarch64-none-elf-gcc $CF -c "$S/tls.c" -o obj/tls.o
aarch64-none-elf-gcc $CF -c "$S/exc.S" -o obj/exc.o
# The home-menu forwarder's program: patched nx-hbloader (deps), its title id in the NPDM; forwarder.c embeds both.
FWD=$OUT/forwarder
if [ ! -f "$FWD/forwarder.nso" ]; then
  rm -rf "$FWD" && cp -r "$OUT/ext/nx-hbloader" "$FWD"
  # an application (hbloader itself is an applet: program type 2), with Waddamburo's title id
  sed -e 's/"name": "hbloader"/"name": "Waddamburo"/' -e 's/0x010000000000100D/0x0500005744420000/g' \
    -e '/"type": "application_type"/{n;s/"value": 2/"value": 1/}' "$FWD/hbl.json" > "$FWD/forwarder.json"
  grep -A1 '"application_type"' "$FWD/forwarder.json" | grep -q '"value": 1' || { echo "forwarder.json: application_type not set"; exit 1; }
  make -C "$FWD" CONFIG_JSON=forwarder.json TARGET=forwarder APP_VERSION=2.4.5 > "$FWD.log" 2>&1 || { tail -20 "$FWD.log"; exit 1; }
fi
aarch64-none-elf-gcc $CF -DFORWARDER_NSO="\"$FWD/forwarder.nso\"" -DFORWARDER_NPDM="\"$FWD/forwarder.npdm\"" -c "$S/forwarder.c" -o obj/forwarder.o
aarch64-none-elf-gcc $CF -I"$OUT/ext/sdl3/include" -c "$S/sdl3nx.c" -o obj/sdl3nx.o
aarch64-none-elf-gcc $CF -I"$OUT/bgfx/include" -include "$S/gcc-compat.h" -D__NX__ -c "$S/gfx.c" -o obj/gfx.o
aarch64-none-elf-gcc $CF -I"$DEVKITPRO/portlibs/switch/include" -c "$S/cryptonx.c" -o obj/cryptonx.o
"$NM" --defined-only obj/cryptonx.o | awk '$2=="T"{print $3}' > crypto-overrides.txt
aarch64-none-elf-objcopy --weaken-symbols=crypto-overrides.txt rt-link/libSystem.Security.Cryptography.Native.OpenSsl.a

# The link (also ffmpeg-relink-switch.zip's link.sh): R runtime, N natives, F FFmpeg, B bgfx; NRO the output.
R=${R:-rt-link} N=${N:-natives/prefix/lib} B=${B:-bgfx/prefix/lib} F=${FFMPEG_LIB:-natives/prefix/lib}
NRO=${NRO:-$ROOT/out/package/Waddamburo.nro}
aarch64-none-elf-g++ -specs="$DEVKITPRO/libnx/switch.specs" -g $ARCH -Wl,--eh-frame-hdr -Wl,--wrap=__syscall_thread_create \
  -Wl,--wrap=__syscall_thread_join -Wl,--wrap=nvMapCreate -Wl,--wrap=nvAddressSpaceMap -Wl,-Map,app.map -o app.elf \
  obj/main.o obj/shim.o obj/net.o obj/tls.o obj/exc.o obj/forwarder.o obj/gfx.o obj/sdl3nx.o $R/app.o $R/libbootstrapper.o obj/cryptonx.o \
  -Wl,--start-group $R/libRuntime.WorkstationGC.a $R/libSystem.Native.a $R/libSystem.Globalization.Native.a \
  $R/libSystem.IO.Compression.Native.a $R/libSystem.Net.Security.Native.a $R/libSystem.Security.Cryptography.Native.OpenSsl.a \
  $R/libbrotlienc.a $R/libbrotlidec.a $R/libbrotlicommon.a $R/libeventpipe-disabled.a $R/libstandalonegc-disabled.a \
  $R/libaotminipal.a $N/libwaddamburo_media.a $N/libvgmstream.a $F/libavformat.a $F/libavcodec.a $F/libswresample.a \
  $F/libswscale.a $F/libavutil.a $N/libwaddamburo_text.a $N/libwaddamburo_texture.a $N/libe_sqlite3.a \
  -L"$DEVKITPRO/portlibs/switch/lib" -lopus libfreetype_nx.a -lharfbuzz libfreetype_nx.a -lpng16 -lbz2 -lz -lmbedcrypto \
  -Wl,--end-group $B/libbgfx.a $B/libbimg_decode.a $B/libbimg.a $B/libbx.a \
  -lEGL -lGLESv2 -lglapi libdrm_nouveau_nx.a -L"$DEVKITPRO/libnx/lib" -lnx -lm > link.log 2>&1 || {
  grep -o "undefined reference to .lx_[A-Za-z0-9_]*" link.log | sed "s/.*lx_/lx_/" | sort -u > missing.txt
  if [ -s missing.txt ]; then echo "MISSING shim functions:"; tr "\n" " " < missing.txt; echo; fi
  grep -v "in function" link.log | head -20
  exit 1; }
nacptool --create "Waddamburo" "Waddamburo" "$VERSION" app.nacp
elf2nro app.elf "$NRO" --icon="$HERE/icon.jpg" --nacp=app.nacp
ls -la "$NRO"
