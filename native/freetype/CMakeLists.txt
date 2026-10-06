include(ExternalProject)

# Minimal static FreeType for the release title rasterizer: TrueType/OpenType outlines
# only, no system zlib/bzip2/png/brotli/HarfBuzz, so waddamburo_text has no extra deps.
set(WADDAMBURO_FREETYPE_VERSION "2.14.2")
# Tried in order: Savannah was unreachable from GitHub's runners for a release build.
set(WADDAMBURO_FREETYPE_URL
  "https://download.savannah.gnu.org/releases/freetype/freetype-${WADDAMBURO_FREETYPE_VERSION}.tar.xz"
  "https://downloads.sourceforge.net/project/freetype/freetype2/${WADDAMBURO_FREETYPE_VERSION}/freetype-${WADDAMBURO_FREETYPE_VERSION}.tar.xz")
set(WADDAMBURO_FREETYPE_SHA256
  "4b62dcab4c920a1a860369933221814362e699e26f55792516d671e6ff55b5e1")

set(_freetype_root "${CMAKE_BINARY_DIR}/freetype")
set(_freetype_prefix "${_freetype_root}/prefix")
set(WADDAMBURO_FREETYPE_INCLUDE_DIR "${_freetype_prefix}/include/freetype2" PARENT_SCOPE)
set(WADDAMBURO_FREETYPE_LIBRARY "${_freetype_prefix}/lib/libfreetype.a" PARENT_SCOPE)

set(_toolchain_args)
if(CMAKE_TOOLCHAIN_FILE)
  set(_toolchain_args -DCMAKE_TOOLCHAIN_FILE=${CMAKE_TOOLCHAIN_FILE})
endif()

ExternalProject_Add(waddamburo_freetype_build
  URL ${WADDAMBURO_FREETYPE_URL}
  URL_HASH "SHA256=${WADDAMBURO_FREETYPE_SHA256}"
  DOWNLOAD_DIR "${CMAKE_SOURCE_DIR}/../out/downloads"
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
  SOURCE_DIR "${CMAKE_SOURCE_DIR}/../out/source/freetype-${WADDAMBURO_FREETYPE_VERSION}"
  BINARY_DIR "${_freetype_root}/build"
  CMAKE_ARGS
    ${_toolchain_args}
    -DCMAKE_BUILD_TYPE=Release
    -DCMAKE_INSTALL_PREFIX=${_freetype_prefix}
    -DCMAKE_INSTALL_LIBDIR=lib
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON
    -DBUILD_SHARED_LIBS=OFF
    -DFT_DISABLE_ZLIB=ON
    -DFT_DISABLE_BZIP2=ON
    -DFT_DISABLE_PNG=ON
    -DFT_DISABLE_HARFBUZZ=ON
    -DFT_DISABLE_BROTLI=ON
  INSTALL_COMMAND "${CMAKE_COMMAND}" --install <BINARY_DIR> --config Release
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${_freetype_prefix}/licenses/FreeType"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
      <SOURCE_DIR>/LICENSE.TXT <SOURCE_DIR>/docs/FTL.TXT "${_freetype_prefix}/licenses/FreeType/"
  BUILD_BYPRODUCTS "${_freetype_prefix}/lib/libfreetype.a")
