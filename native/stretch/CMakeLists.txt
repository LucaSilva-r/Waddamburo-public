include(FetchContent)

# Bungee Basic (open-source edition), the time-stretcher training mode plays slowed or sped-up songs through at their
# own pitch. Pinned sources: Bungee, and the Eigen and PFFFT commits its repository pins as submodules. Built here as a
# static library from its sources (Bungee's own CMake also builds a shared library and its command-line tool).
set(WADDAMBURO_BUNGEE_COMMIT 61f52bf34be577d189d388772ddfa3696dee77d8)
set(WADDAMBURO_EIGEN_COMMIT c29c800126982c561e8d0b9255dc65474cd98de3)
set(WADDAMBURO_PFFFT_COMMIT 02fe7715a5bf8bfd914681c53429600f94e0f536)
set(_downloads "${CMAKE_SOURCE_DIR}/../out/downloads")
set(_sources "${CMAKE_SOURCE_DIR}/../out/source")

FetchContent_Declare(waddamburo_bungee_source
  URL "https://github.com/bungee-audio-stretch/bungee/archive/${WADDAMBURO_BUNGEE_COMMIT}.tar.gz"
  URL_HASH "SHA256=33a00cb1b8a47dd6bc012ee98c419205243e052d1880aca5ff08efea19064566"
  DOWNLOAD_DIR "${_downloads}" DOWNLOAD_NAME "bungee-${WADDAMBURO_BUNGEE_COMMIT}.tar.gz"
  SOURCE_DIR "${_sources}/bungee-${WADDAMBURO_BUNGEE_COMMIT}"
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
  SOURCE_SUBDIR none) # download only: no add_subdirectory
FetchContent_Declare(waddamburo_eigen_source
  URL "https://github.com/eigenteam/eigen-git-mirror/archive/${WADDAMBURO_EIGEN_COMMIT}.tar.gz"
  URL_HASH "SHA256=57bad130ebe3ec0eda84e0cf998b9c5083473e520e96a97616bc69ca08c68514"
  DOWNLOAD_DIR "${_downloads}" DOWNLOAD_NAME "eigen-${WADDAMBURO_EIGEN_COMMIT}.tar.gz"
  SOURCE_DIR "${_sources}/eigen-${WADDAMBURO_EIGEN_COMMIT}"
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
  SOURCE_SUBDIR none)
FetchContent_Declare(waddamburo_pffft_source
  URL "https://bitbucket.org/jpommier/pffft/get/${WADDAMBURO_PFFFT_COMMIT}.tar.gz"
  URL_HASH "SHA256=9adeb18ac7bb52e9fb921c31c0c6a4e9ae150cc6fcb20a899d4b3a2275176ded"
  DOWNLOAD_DIR "${_downloads}" DOWNLOAD_NAME "pffft-${WADDAMBURO_PFFFT_COMMIT}.tar.gz"
  SOURCE_DIR "${_sources}/pffft-${WADDAMBURO_PFFFT_COMMIT}"
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
  SOURCE_SUBDIR none)
FetchContent_MakeAvailable(waddamburo_bungee_source waddamburo_eigen_source waddamburo_pffft_source)

set(_bungee "${waddamburo_bungee_source_SOURCE_DIR}")
set(_pffft "${waddamburo_pffft_source_SOURCE_DIR}")
file(GLOB _bungee_sources CONFIGURE_DEPENDS "${_bungee}/src/*.cpp")
add_library(waddamburo_bungee STATIC ${_bungee_sources} "${_pffft}/pffft.c" "${_pffft}/fftpack.c")
set_target_properties(waddamburo_bungee PROPERTIES
  CXX_STANDARD 20 CXX_STANDARD_REQUIRED ON POSITION_INDEPENDENT_CODE ON
  C_VISIBILITY_PRESET hidden CXX_VISIBILITY_PRESET hidden VISIBILITY_INLINES_HIDDEN ON)
target_include_directories(waddamburo_bungee PUBLIC "${_bungee}")
target_include_directories(waddamburo_bungee SYSTEM PRIVATE "${waddamburo_eigen_source_SOURCE_DIR}" "${_pffft}")
# Upstream's definitions (its CMakeLists.txt), the library linked statically (no export attribute).
target_compile_definitions(waddamburo_bungee PRIVATE
  BUNGEE_VISIBILITY= BUNGEE_SELF_TEST=0 eigen_assert=BUNGEE_ASSERT1 EIGEN_DONT_PARALLELIZE=1)
set_source_files_properties("${_bungee}/src/version.cpp" PROPERTIES COMPILE_DEFINITIONS "BUNGEE_VERSION=\"${WADDAMBURO_BUNGEE_COMMIT}\"")
target_compile_options(waddamburo_bungee PRIVATE $<$<COMPILE_LANG_AND_ID:CXX,GNU,Clang,AppleClang>:-fwrapv>)
set_source_files_properties("${_pffft}/pffft.c" "${_pffft}/fftpack.c" PROPERTIES
  COMPILE_OPTIONS "$<$<C_COMPILER_ID:GNU,Clang,AppleClang>:-ffast-math;-fno-finite-math-only>")
if(ANDROID)
  target_link_libraries(waddamburo_bungee PRIVATE log)
endif()

# Licenses beside the build (eng/package.sh collects them): PFFFT's sits in pffft.c's opening comment.
set(_licenses "${CMAKE_BINARY_DIR}/bungee/licenses")
file(COPY "${_bungee}/LICENSE" DESTINATION "${_licenses}/Bungee")
file(GLOB _eigen_licenses "${waddamburo_eigen_source_SOURCE_DIR}/COPYING.*")
file(COPY ${_eigen_licenses} DESTINATION "${_licenses}/Eigen")
file(READ "${_pffft}/pffft.c" _pffft_text)
string(FIND "${_pffft_text}" "*/" _pffft_end)
math(EXPR _pffft_end "${_pffft_end} + 2")
string(SUBSTRING "${_pffft_text}" 0 ${_pffft_end} _pffft_license)
file(WRITE "${_licenses}/PFFFT/LICENSE.txt" "${_pffft_license}\n")
