include(ExternalProject)

set(WADDAMBURO_FFMPEG_VERSION "8.1.2")
set(WADDAMBURO_FFMPEG_ARCHIVE "ffmpeg-${WADDAMBURO_FFMPEG_VERSION}.tar.xz")
set(WADDAMBURO_FFMPEG_URL
  "https://ffmpeg.org/releases/${WADDAMBURO_FFMPEG_ARCHIVE}")
set(WADDAMBURO_FFMPEG_SHA256
  "464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c")

find_program(WADDAMBURO_MAKE_EXECUTABLE NAMES gmake make REQUIRED)
cmake_host_system_information(RESULT _ffmpeg_jobs QUERY NUMBER_OF_LOGICAL_CORES)

set(_ffmpeg_platform_options --arch=x86_64 --disable-x86asm)
set(_ffmpeg_configure_command <SOURCE_DIR>/configure)

if(MINGW AND CMAKE_CROSSCOMPILING)
  list(APPEND _ffmpeg_platform_options
    --enable-cross-compile
    --cross-prefix=${WADDAMBURO_CROSS_PREFIX}
    --target-os=mingw32
    --enable-w32threads)
elseif(WIN32)
  find_program(WADDAMBURO_BASH_EXECUTABLE NAMES bash REQUIRED)
  set(_ffmpeg_configure_command ${WADDAMBURO_BASH_EXECUTABLE} <SOURCE_DIR>/configure)
  list(APPEND _ffmpeg_platform_options
    --toolchain=msvc
    --target-os=win64
    --enable-w32threads)
else()
  list(APPEND _ffmpeg_platform_options
    --target-os=linux
    --enable-pthreads
    --enable-pic)
endif()

set(_ffmpeg_prefix "${CMAKE_BINARY_DIR}/ffmpeg/prefix")

# libopus (BSD-3-Clause) for the decoded-audio cache's Opus encoder: built static with PIC and
# folded into avcodec, installed into the FFmpeg prefix so its configure finds it by pkg-config.
set(WADDAMBURO_OPUS_VERSION "1.5.2")
set(WADDAMBURO_OPUS_SHA256 "65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1")
set(_opus_cmake_options
  -DCMAKE_INSTALL_PREFIX=${_ffmpeg_prefix}
  -DCMAKE_INSTALL_LIBDIR=lib
  -DCMAKE_BUILD_TYPE=Release
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON
  -DBUILD_SHARED_LIBS=OFF
  -DOPUS_BUILD_PROGRAMS=OFF
  -DOPUS_BUILD_TESTING=OFF)
if(CMAKE_TOOLCHAIN_FILE)
  list(APPEND _opus_cmake_options -DCMAKE_TOOLCHAIN_FILE=${CMAKE_TOOLCHAIN_FILE})
endif()
if(DEFINED WADDAMBURO_CROSS_PREFIX)
  list(APPEND _opus_cmake_options -DWADDAMBURO_CROSS_PREFIX=${WADDAMBURO_CROSS_PREFIX})
endif()
ExternalProject_Add(waddamburo_opus
  URL "https://downloads.xiph.org/releases/opus/opus-${WADDAMBURO_OPUS_VERSION}.tar.gz"
  URL_HASH "SHA256=${WADDAMBURO_OPUS_SHA256}"
  DOWNLOAD_DIR "${CMAKE_SOURCE_DIR}/../out/downloads"
  SOURCE_DIR "${CMAKE_SOURCE_DIR}/../out/source/opus-${WADDAMBURO_OPUS_VERSION}"
  BINARY_DIR "${CMAKE_BINARY_DIR}/opus/build"
  DOWNLOAD_EXTRACT_TIMESTAMP true
  CMAKE_ARGS ${_opus_cmake_options}
  INSTALL_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --target install
    COMMAND ${CMAKE_COMMAND} -E make_directory "${_ffmpeg_prefix}/licenses/opus"
    COMMAND ${CMAKE_COMMAND} -E copy <SOURCE_DIR>/COPYING "${_ffmpeg_prefix}/licenses/opus/COPYING"
  BUILD_BYPRODUCTS "${_ffmpeg_prefix}/lib/${CMAKE_STATIC_LIBRARY_PREFIX}opus${CMAKE_STATIC_LIBRARY_SUFFIX}")
set(_ffmpeg_build_dir "${CMAKE_BINARY_DIR}/ffmpeg/build")
get_filename_component(_repository_root "${CMAKE_SOURCE_DIR}/.." ABSOLUTE)
set(_ffmpeg_common_options
  --prefix=${_ffmpeg_prefix}
  --disable-autodetect
  --disable-everything
  --disable-iconv
  --disable-gpl
  --disable-nonfree
  --disable-version3
  --enable-avcodec
  --enable-avformat
  --enable-avutil
  --enable-swresample
  --disable-avdevice
  --disable-avfilter
  --enable-swscale
  --enable-decoder=atrac3p,h264,mp3,mp3float,vorbis,flac,opus,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,pcm_u8
  --enable-demuxer=wav,mp3,ogg,flac,mpegps,h264
  # The decoded-audio cache (vgmstream-cli output) is re-encoded as Opus in Ogg.
  --enable-libopus
  --pkg-config-flags=--static
  --enable-encoder=libopus
  --enable-muxer=ogg
  --enable-parser=mpegaudio,vorbis,flac,opus,h264
  --enable-protocol=file
  --disable-network
  --disable-programs
  --disable-doc
  --disable-debug
  --enable-small)
set(_ffmpeg_byproducts)
if(WADDAMBURO_PACKAGE)
  foreach(_library avformat avcodec swresample swscale avutil)
    list(APPEND _ffmpeg_byproducts "${_ffmpeg_prefix}/lib/lib${_library}.a")
  endforeach()
  # Folded into waddamburo_media so a release ships one self-contained media library.
  list(APPEND _ffmpeg_common_options --enable-static --disable-shared --enable-pic)
else()
  list(APPEND _ffmpeg_common_options --disable-static --enable-shared)
endif()

string(JOIN "\n" _ffmpeg_configure_manifest
  "FFMPEG_VERSION=${WADDAMBURO_FFMPEG_VERSION}"
  "FFMPEG_URL=${WADDAMBURO_FFMPEG_URL}"
  "FFMPEG_SHA256=${WADDAMBURO_FFMPEG_SHA256}"
  "CONFIGURE_OPTIONS="
  ${_ffmpeg_common_options}
  ${_ffmpeg_platform_options})
string(REPLACE "${_ffmpeg_prefix}" "<INSTALL_PREFIX>"
  _ffmpeg_configure_manifest "${_ffmpeg_configure_manifest}")
file(GENERATE
  OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/configure-options.txt"
  CONTENT "${_ffmpeg_configure_manifest}\n")

ExternalProject_Add(waddamburo_ffmpeg
  URL "${WADDAMBURO_FFMPEG_URL}"
  URL_HASH "SHA256=${WADDAMBURO_FFMPEG_SHA256}"
  DOWNLOAD_NAME "${WADDAMBURO_FFMPEG_ARCHIVE}"
  DOWNLOAD_DIR "${CMAKE_SOURCE_DIR}/../out/downloads"
  SOURCE_DIR "${CMAKE_SOURCE_DIR}/../out/source/ffmpeg-${WADDAMBURO_FFMPEG_VERSION}"
  BINARY_DIR "${_ffmpeg_build_dir}"
  DOWNLOAD_EXTRACT_TIMESTAMP true
  DEPENDS waddamburo_opus
  CONFIGURE_COMMAND
    ${CMAKE_COMMAND} -E env "PKG_CONFIG_PATH=${_ffmpeg_prefix}/lib/pkgconfig"
    ${_ffmpeg_configure_command}
    ${_ffmpeg_common_options}
    ${_ffmpeg_platform_options}
  BUILD_COMMAND ${WADDAMBURO_MAKE_EXECUTABLE} -j${_ffmpeg_jobs}
  INSTALL_COMMAND
    ${WADDAMBURO_MAKE_EXECUTABLE} install
    COMMAND ${CMAKE_COMMAND} -E make_directory "${_ffmpeg_prefix}/licenses/FFmpeg"
    COMMAND ${CMAKE_COMMAND} -E copy <SOURCE_DIR>/COPYING.LGPLv2.1
            "${_ffmpeg_prefix}/licenses/FFmpeg/COPYING.LGPLv2.1"
    COMMAND ${CMAKE_COMMAND} -E copy <SOURCE_DIR>/LICENSE.md
            "${_ffmpeg_prefix}/licenses/FFmpeg/LICENSE.md"
    COMMAND ${CMAKE_COMMAND} -E copy
            "${CMAKE_CURRENT_BINARY_DIR}/configure-options.txt"
            "${_ffmpeg_prefix}/licenses/FFmpeg/configure-options.txt"
  BUILD_BYPRODUCTS ${_ffmpeg_byproducts}
  USES_TERMINAL_DOWNLOAD true
  USES_TERMINAL_CONFIGURE true
  USES_TERMINAL_BUILD true
  USES_TERMINAL_INSTALL true)

add_custom_target(waddamburo_ffmpeg_audit
  COMMAND ${CMAKE_COMMAND} -E copy
    "${CMAKE_CURRENT_BINARY_DIR}/configure-options.txt"
    "${_ffmpeg_prefix}/licenses/FFmpeg/configure-options.txt"
  COMMAND ${CMAKE_COMMAND}
    -DFFMPEG_BUILD_DIR=${_ffmpeg_build_dir}
    -DFFMPEG_PREFIX=${_ffmpeg_prefix}
    -DFFMPEG_WINDOWS=${WIN32}
    -DFFMPEG_STATIC=${WADDAMBURO_PACKAGE}
    -DFFMPEG_FORBIDDEN_PATH=${_repository_root}
    -P "${CMAKE_CURRENT_SOURCE_DIR}/audit.cmake"
  DEPENDS waddamburo_ffmpeg
  VERBATIM)
