include(ExternalProject)

# vgmstream decodes the game's nuSound2 banks (SE cues are subsongs). G.719 is never
# built: its reference code has no redistribution license. Files that need it are
# decoded by a user-supplied vgmstream-cli instead (see native/media/README.md).
set(WADDAMBURO_VGMSTREAM_REVISION "09c9f40caae4747e44b6a993b3d5b654cef4d1f7")
set(WADDAMBURO_VGMSTREAM_URL
  "https://github.com/vgmstream/vgmstream/archive/${WADDAMBURO_VGMSTREAM_REVISION}.tar.gz")
set(WADDAMBURO_VGMSTREAM_SHA256
  "7f012e143fe2945b2edae44bbfdfbad4671d5277c925d38d968a32659369383c")

set(_vgmstream_root "${CMAKE_BINARY_DIR}/vgmstream")
set(_vgmstream_source "${CMAKE_SOURCE_DIR}/../out/source/vgmstream-${WADDAMBURO_VGMSTREAM_REVISION}")
set(_vgmstream_build "${_vgmstream_root}/build")
set(WADDAMBURO_VGMSTREAM_INCLUDE_DIR "${_vgmstream_source}/src" PARENT_SCOPE)
set(WADDAMBURO_VGMSTREAM_LIBRARY "${_vgmstream_build}/src/libvgmstream.a" PARENT_SCOPE)

set(_toolchain_args)
if(CMAKE_TOOLCHAIN_FILE)
  set(_toolchain_args -DCMAKE_TOOLCHAIN_FILE=${CMAKE_TOOLCHAIN_FILE})
endif()

ExternalProject_Add(waddamburo_vgmstream_build
  URL "${WADDAMBURO_VGMSTREAM_URL}"
  URL_HASH "SHA256=${WADDAMBURO_VGMSTREAM_SHA256}"
  DOWNLOAD_DIR "${CMAKE_SOURCE_DIR}/../out/downloads"
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
  SOURCE_DIR "${_vgmstream_source}"
  BINARY_DIR "${_vgmstream_build}"
  CMAKE_ARGS
    ${_toolchain_args}
    -DCMAKE_BUILD_TYPE=Release
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON
    -DBUILD_SHARED_LIBS=OFF
    -DBUILD_CLI=OFF
    -DBUILD_V123=OFF
    -DBUILD_AUDACIOUS=OFF
    -DUSE_FFMPEG=OFF
    -DUSE_MPEG=OFF
    -DUSE_VORBIS=OFF
    -DUSE_G7221=ON
    -DUSE_G719=OFF
    -DUSE_ATRAC9=OFF
    -DUSE_CELT=OFF
    -DUSE_SPEEX=OFF
  BUILD_COMMAND "${CMAKE_COMMAND}" --build <BINARY_DIR> --config Release --target libvgmstream
  INSTALL_COMMAND "${CMAKE_COMMAND}" -E make_directory "${_vgmstream_root}/licenses/vgmstream"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
    <SOURCE_DIR>/COPYING "${_vgmstream_root}/licenses/vgmstream/COPYING"
  BUILD_BYPRODUCTS "${_vgmstream_build}/src/libvgmstream.a")
