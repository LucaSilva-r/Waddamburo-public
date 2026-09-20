include(ExternalProject)

if(WIN32)
  message(FATAL_ERROR "The opt-in G.719 vgmstream dependency recipe is currently supported on Linux only")
endif()

set(WADDAMBURO_VGMSTREAM_REVISION "09c9f40caae4747e44b6a993b3d5b654cef4d1f7")
set(WADDAMBURO_VGMSTREAM_URL
  "https://github.com/vgmstream/vgmstream/archive/${WADDAMBURO_VGMSTREAM_REVISION}.tar.gz")
set(WADDAMBURO_VGMSTREAM_SHA256
  "7f012e143fe2945b2edae44bbfdfbad4671d5277c925d38d968a32659369383c")
set(WADDAMBURO_G719_REVISION "da90ad8a676876c6c47889bcea6a753f9bbf7a73")
set(WADDAMBURO_G719_SOURCE_DIR "" CACHE PATH
  "Local libg719_decode checkout; required and never copied into the source repository")

if(NOT IS_DIRECTORY "${WADDAMBURO_G719_SOURCE_DIR}/reference_code")
  message(FATAL_ERROR
    "WADDAMBURO_G719_SOURCE_DIR must name a local libg719_decode checkout. "
    "This dependency has no open-source redistribution license and is never downloaded by Waddamburo.")
endif()
find_package(Git REQUIRED)
execute_process(
  COMMAND "${GIT_EXECUTABLE}" -C "${WADDAMBURO_G719_SOURCE_DIR}" rev-parse HEAD
  RESULT_VARIABLE _g719_revision_result
  OUTPUT_VARIABLE _g719_revision
  ERROR_QUIET
  OUTPUT_STRIP_TRAILING_WHITESPACE)
if(NOT _g719_revision_result EQUAL 0 OR
   NOT _g719_revision STREQUAL WADDAMBURO_G719_REVISION)
  message(FATAL_ERROR
    "WADDAMBURO_G719_SOURCE_DIR must be a Git checkout at reviewed revision "
    "${WADDAMBURO_G719_REVISION}; found '${_g719_revision}'")
endif()

set(_vgmstream_root "${CMAKE_BINARY_DIR}/vgmstream")
set(_vgmstream_source "${CMAKE_SOURCE_DIR}/../out/source/vgmstream-${WADDAMBURO_VGMSTREAM_REVISION}")
set(_vgmstream_build "${_vgmstream_root}/build")
set(_vgmstream_prefix "${_vgmstream_root}/prefix")
set(_g719_staging "${_vgmstream_root}/g719-source")
configure_file(nonredistributable-g719.txt.in
  "${_vgmstream_root}/NON-REDISTRIBUTABLE-G719.txt" @ONLY)

ExternalProject_Add(waddamburo_vgmstream_build
  URL "${WADDAMBURO_VGMSTREAM_URL}"
  URL_HASH "SHA256=${WADDAMBURO_VGMSTREAM_SHA256}"
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
  SOURCE_DIR "${_vgmstream_source}"
  BINARY_DIR "${_vgmstream_build}"
  CMAKE_ARGS
    -DCMAKE_BUILD_TYPE=Release
    -DCMAKE_INSTALL_PREFIX=${_vgmstream_prefix}
    -DBUILD_SHARED_LIBS=ON
    -DBUILD_CLI=OFF
    -DBUILD_V123=OFF
    -DBUILD_AUDACIOUS=OFF
    -DUSE_FFMPEG=OFF
    -DUSE_MPEG=OFF
    -DUSE_VORBIS=OFF
    -DUSE_G7221=ON
    -DUSE_G719=ON
    -DG719_PATH=${_g719_staging}
    -DUSE_ATRAC9=OFF
    -DUSE_CELT=OFF
    -DUSE_SPEEX=OFF
  BUILD_COMMAND "${CMAKE_COMMAND}" --build <BINARY_DIR> --config Release --target libvgmstream_shared
  INSTALL_COMMAND "${CMAKE_COMMAND}" --install <BINARY_DIR> --config Release
  BUILD_BYPRODUCTS "${_vgmstream_prefix}/lib64/libvgmstream.so")

ExternalProject_Add_Step(waddamburo_vgmstream_build stage_g719
  COMMAND "${CMAKE_COMMAND}" -E rm -rf "${_g719_staging}"
  COMMAND "${CMAKE_COMMAND}" -E copy_directory
    "${WADDAMBURO_G719_SOURCE_DIR}" "${_g719_staging}"
  DEPENDEES download
  DEPENDERS configure)

ExternalProject_Add_Step(waddamburo_vgmstream_build install_notices
  COMMAND "${CMAKE_COMMAND}" -E make_directory "${_vgmstream_prefix}/licenses/vgmstream"
  COMMAND "${CMAKE_COMMAND}" -E copy_if_different
    "${_vgmstream_source}/COPYING" "${_vgmstream_prefix}/licenses/vgmstream/COPYING"
  COMMAND "${CMAKE_COMMAND}" -E copy_if_different
    "${_vgmstream_root}/NON-REDISTRIBUTABLE-G719.txt"
    "${_vgmstream_prefix}/NON-REDISTRIBUTABLE-G719.txt"
  DEPENDEES install)

add_custom_target(waddamburo_vgmstream_audit
  COMMAND "${CMAKE_COMMAND}"
    -DVGMSTREAM_PREFIX=${_vgmstream_prefix}
    -P "${CMAKE_CURRENT_SOURCE_DIR}/audit.cmake"
  DEPENDS waddamburo_vgmstream_build)
