add_library(waddamburo_media SHARED src/media.c)
add_library(Waddamburo::Media ALIAS waddamburo_media)

waddamburo_configure_native_target(waddamburo_media)
target_compile_definitions(waddamburo_media PRIVATE WADDAMBURO_MEDIA_BUILDING_LIBRARY=1)
target_include_directories(waddamburo_media
  PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>)

option(WADDAMBURO_MEDIA_FFMPEG "Build the media decoder with FFmpeg" OFF)
set(WADDAMBURO_FFMPEG_ROOT "" CACHE PATH "Optional FFmpeg installation prefix")
if(WADDAMBURO_MEDIA_FFMPEG)
  if(WADDAMBURO_FFMPEG_ROOT)
    set(_ffmpeg_include "${WADDAMBURO_FFMPEG_ROOT}/include")
    if(NOT EXISTS "${_ffmpeg_include}/libavcodec/avcodec.h")
      message(FATAL_ERROR "WADDAMBURO_FFMPEG_ROOT does not contain FFmpeg headers")
    endif()
    find_library(_avformat avformat PATHS "${WADDAMBURO_FFMPEG_ROOT}/lib" NO_DEFAULT_PATH REQUIRED)
    find_library(_avcodec avcodec PATHS "${WADDAMBURO_FFMPEG_ROOT}/lib" NO_DEFAULT_PATH REQUIRED)
    find_library(_swresample swresample PATHS "${WADDAMBURO_FFMPEG_ROOT}/lib" NO_DEFAULT_PATH REQUIRED)
    find_library(_avutil avutil PATHS "${WADDAMBURO_FFMPEG_ROOT}/lib" NO_DEFAULT_PATH REQUIRED)
    target_include_directories(waddamburo_media PRIVATE "${_ffmpeg_include}")
    target_link_libraries(waddamburo_media PRIVATE
      "${_avformat}" "${_avcodec}" "${_swresample}" "${_avutil}")
  else()
    find_package(PkgConfig REQUIRED)
    pkg_check_modules(WADDAMBURO_FFMPEG REQUIRED IMPORTED_TARGET
      libavformat libavcodec libswresample libavutil)
    target_link_libraries(waddamburo_media PRIVATE PkgConfig::WADDAMBURO_FFMPEG)
  endif()
  target_compile_definitions(waddamburo_media PRIVATE WADDAMBURO_MEDIA_HAS_FFMPEG=1)
endif()

option(WADDAMBURO_MEDIA_VGMSTREAM "Build the media decoder with vgmstream" OFF)
set(WADDAMBURO_VGMSTREAM_ROOT "" CACHE PATH "vgmstream installation prefix")
if(WADDAMBURO_MEDIA_VGMSTREAM)
  if(NOT WADDAMBURO_MEDIA_FFMPEG)
    message(FATAL_ERROR "The vgmstream backend currently requires the FFmpeg backend for output conversion")
  endif()
  if(NOT WADDAMBURO_VGMSTREAM_ROOT)
    message(FATAL_ERROR "WADDAMBURO_VGMSTREAM_ROOT is required when vgmstream is enabled")
  endif()
  find_path(_vgmstream_include libvgmstream.h
    PATHS "${WADDAMBURO_VGMSTREAM_ROOT}/include/vgmstream"
    NO_DEFAULT_PATH REQUIRED)
  find_library(_vgmstream_library NAMES vgmstream libvgmstream
    PATHS "${WADDAMBURO_VGMSTREAM_ROOT}/lib" "${WADDAMBURO_VGMSTREAM_ROOT}/lib64"
    NO_DEFAULT_PATH REQUIRED)
  target_include_directories(waddamburo_media PRIVATE "${_vgmstream_include}")
  target_link_libraries(waddamburo_media PRIVATE "${_vgmstream_library}")
  target_compile_definitions(waddamburo_media PRIVATE WADDAMBURO_MEDIA_HAS_VGMSTREAM=1)
endif()
set_target_properties(waddamburo_media PROPERTIES
  C_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN true)

if(BUILD_TESTING)
  add_executable(waddamburo_media_abi_test tests/media_abi_test.c)
  waddamburo_configure_native_target(waddamburo_media_abi_test)
  target_link_libraries(waddamburo_media_abi_test PRIVATE Waddamburo::Media)
  if(WADDAMBURO_MEDIA_FFMPEG)
    target_compile_definitions(waddamburo_media_abi_test PRIVATE WADDAMBURO_MEDIA_HAS_FFMPEG=1)
  endif()
  if(WADDAMBURO_MEDIA_VGMSTREAM)
    target_compile_definitions(waddamburo_media_abi_test PRIVATE WADDAMBURO_MEDIA_HAS_VGMSTREAM=1)
  endif()
  add_test(NAME media_abi COMMAND waddamburo_media_abi_test)
endif()
