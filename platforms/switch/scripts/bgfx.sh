#!/bin/bash
# Inside the devkitA64 image (build.sh bgfx): bgfx (GLES 3.0 over devkitPro's Mesa nouveau EGL) as static libs for
# libnx -> $OUT/bgfx/prefix, from native/'s pinned bgfx.cmake with platforms/switch/patches applied (build.sh deps).
set -e
SRC=$OUT/ext/bgfx-src
F="-include $HERE/shim/gcc-compat.h -DEGL_NO_PLATFORM_SPECIFIC_TYPES -I$HERE/shim/compat -D__NX__ -DBGFX_CONFIG_RENDERER_OPENGLES=30 -DBGFX_CONFIG_RENDERER_OPENGL=0 -DBGFX_CONFIG_RENDERER_VULKAN=0 -DBGFX_CONFIG_RENDERER_NVN=0 -DBGFX_CONFIG_MAX_TEXTURES=16384"
cmake -S "$SRC" -B "$OUT/bgfx/build" -G "Unix Makefiles" -DCMAKE_TOOLCHAIN_FILE=$DEVKITPRO/cmake/Switch.cmake \
  -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$OUT/bgfx/prefix" -DCMAKE_CXX_FLAGS="$F" -DCMAKE_C_FLAGS="$F" \
  -DBGFX_LIBRARY_TYPE=STATIC -DBGFX_BUILD_EXAMPLES=OFF -DBGFX_BUILD_TESTS=OFF -DBGFX_BUILD_TOOLS=OFF \
  -DBGFX_CUSTOM_TARGETS=OFF -DBGFX_CONFIG_VIDEO=OFF -DBGFX_CONFIG_MULTITHREADED=ON > "$OUT/bgfx-config.log" 2>&1 || { tail -30 "$OUT/bgfx-config.log"; exit 1; }
cmake --build "$OUT/bgfx/build" -j8 --target bgfx bimg bimg_decode bx > "$OUT/bgfx-build.log" 2>&1 || { grep -E "error" -A3 "$OUT/bgfx-build.log" | head -40; exit 1; }
mkdir -p "$OUT/bgfx/prefix/lib" && cp $(find "$OUT/bgfx/build" -name "*.a") "$OUT/bgfx/prefix/lib/"
mkdir -p "$OUT/bgfx/include" && cp -r "$SRC/bgfx/include/bgfx" "$SRC/bx/include/bx" "$OUT/bgfx/include/"
mkdir -p "$OUT/bgfx/licenses" && cp "$SRC/LICENSE" "$OUT/bgfx/licenses/bgfx.cmake.LICENSE" && cp "$SRC/bgfx/LICENSE" "$OUT/bgfx/licenses/bgfx.LICENSE"
ls "$OUT/bgfx/prefix/lib"
