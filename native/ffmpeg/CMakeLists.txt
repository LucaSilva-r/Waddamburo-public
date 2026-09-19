include(ExternalProject)

set(WADDAMBURO_FFMPEG_VERSION "8.1.2")
set(WADDAMBURO_FFMPEG_ARCHIVE "ffmpeg-${WADDAMBURO_FFMPEG_VERSION}.tar.xz")
set(WADDAMBURO_FFMPEG_URL
  "https://ffmpeg.org/releases/${WADDAMBURO_FFMPEG_ARCHIVE}")
set(WADDAMBURO_FFMPEG_SHA256
  "464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c")

find_program(WADDAMBURO_MAKE_EXECUTABLE NAMES gmake make REQUIRED)

set(_ffmpeg_platform_options --arch=x86_64 --disable-x86asm)
set(_ffmpeg_configure_command <SOURCE_DIR>/configure)

if(WIN32)
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
  --disable-swscale
  --enable-decoder=atrac3p,mp3,mp3float,vorbis,flac,opus,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,pcm_u8
  --enable-demuxer=wav,mp3,ogg,flac
  --enable-parser=mpegaudio,vorbis,flac,opus
  --enable-protocol=file
  --disable-network
  --disable-programs
  --disable-doc
  --disable-debug
  --enable-small
  --disable-static
  --enable-shared)

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
  CONFIGURE_COMMAND
    ${_ffmpeg_configure_command}
    ${_ffmpeg_common_options}
    ${_ffmpeg_platform_options}
  BUILD_COMMAND ${WADDAMBURO_MAKE_EXECUTABLE} -j2
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
    -DFFMPEG_FORBIDDEN_PATH=${_repository_root}
    -P "${CMAKE_CURRENT_SOURCE_DIR}/audit.cmake"
  DEPENDS waddamburo_ffmpeg
  VERBATIM)
