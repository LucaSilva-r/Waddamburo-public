include(ExternalProject)

# bgfx renderer (D3D11/D3D12/Vulkan/Metal/OpenGL/GLES) as one shared library, plus the
# shaderc tool that compiles the .sc shaders for each backend. The bgfx.cmake release
# tarball carries matching bgfx, bx and bimg sources. Pinned: the renderer scope is fixed.
set(WADDAMBURO_BGFX_VERSION "v1.161.9510-579")
set(WADDAMBURO_BGFX_URL
  "https://github.com/bkaradzic/bgfx.cmake/releases/download/${WADDAMBURO_BGFX_VERSION}/bgfx.cmake.${WADDAMBURO_BGFX_VERSION}.tar.gz")
set(WADDAMBURO_BGFX_SHA256
  "2b489206be79d0841009c15853aa8717749373528a694933b926b797b4cfc992")

set(_bgfx_root "${CMAKE_BINARY_DIR}/bgfx")
set(_bgfx_prefix "${_bgfx_root}/prefix")
set(WADDAMBURO_BGFX_PREFIX "${_bgfx_prefix}" PARENT_SCOPE)

set(_toolchain_args)
if(CMAKE_TOOLCHAIN_FILE)
  set(_toolchain_args -DCMAKE_TOOLCHAIN_FILE=${CMAKE_TOOLCHAIN_FILE})
endif()

# bgfx's desktop GL renderer needs GL 4.3 (config.h clamps lower requests), which rules out
# Ivy Bridge-era GPUs (GL 4.2 on Mesa). On Linux build the GLES 3.0 renderer instead: Mesa
# exposes ES 3.0 there, and modern GPUs use Vulkan anyway. Windows keeps D3D11 for them.
set(_renderer_args)
set(_tools ON)
if(CMAKE_CROSSCOMPILING OR WADDAMBURO_PACKAGE)
  set(_tools OFF) # shaderc is a developer host tool; releases only need the library
endif()
# Release libraries carry their C++ runtime and link only what they use (the GLES build
# never touches the desktop GL/GLU libraries bgfx.cmake links; EGL is loaded at runtime).
if(MINGW)
  list(APPEND _renderer_args "-DCMAKE_SHARED_LINKER_FLAGS=-static")
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  list(APPEND _renderer_args "-DCMAKE_SHARED_LINKER_FLAGS=-Wl,--as-needed")
endif()
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  # Naming any renderer skips all of config.h's defaults, so Vulkan is listed too.
  list(APPEND _renderer_args "-DCMAKE_CXX_FLAGS=-DBGFX_CONFIG_RENDERER_OPENGL=0 -DBGFX_CONFIG_RENDERER_OPENGLES=30 -DBGFX_CONFIG_RENDERER_VULKAN=1")
endif()

ExternalProject_Add(waddamburo_bgfx_build
  URL "${WADDAMBURO_BGFX_URL}"
  URL_HASH "SHA256=${WADDAMBURO_BGFX_SHA256}"
  DOWNLOAD_DIR "${CMAKE_SOURCE_DIR}/../out/downloads"
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
  SOURCE_DIR "${CMAKE_SOURCE_DIR}/../out/source/bgfx.cmake"
  BINARY_DIR "${_bgfx_root}/build"
  CMAKE_ARGS
    ${_toolchain_args}
    ${_renderer_args}
    -DCMAKE_BUILD_TYPE=Release
    -DCMAKE_INSTALL_PREFIX=${_bgfx_prefix}
    -DCMAKE_INSTALL_LIBDIR=lib
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON
    -DBGFX_LIBRARY_TYPE=SHARED
    -DBGFX_BUILD_EXAMPLES=OFF
    -DBGFX_BUILD_TESTS=OFF
    -DBGFX_BUILD_TOOLS=${_tools}
    -DBGFX_BUILD_TOOLS_SHADER=${_tools}
    -DBGFX_BUILD_TOOLS_TEXTURE=OFF
    -DBGFX_BUILD_TOOLS_GEOMETRY=OFF
    -DBGFX_BUILD_TOOLS_BIN2C=OFF
    -DBGFX_CUSTOM_TARGETS=OFF
    -DBGFX_CONFIG_VIDEO=OFF
    # The app runs bgfx's render thread itself, so input is polled while a frame presents.
    -DBGFX_CONFIG_MULTITHREADED=ON
  INSTALL_COMMAND "${CMAKE_COMMAND}" --install <BINARY_DIR> --config Release
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${_bgfx_prefix}/licenses/bgfx"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
      <SOURCE_DIR>/bgfx/LICENSE "${_bgfx_prefix}/licenses/bgfx/")

# Stage the library with the other release libraries, under the name P/Invoke loads
# ("bgfx": bgfx.dll / libbgfx.so; mingw would call it libbgfx.dll).
set(_stage "${CMAKE_BINARY_DIR}/stage/${CMAKE_BUILD_TYPE}")
if(WIN32)
  if(MINGW)
    set(_built "${_bgfx_prefix}/bin/libbgfx.dll")
  else()
    set(_built "${_bgfx_prefix}/bin/bgfx.dll")
  endif()
  set(_staged "${_stage}/bin/bgfx.dll")
else()
  set(_built "${_bgfx_prefix}/lib/libbgfx.so")
  set(_staged "${_stage}/lib/libbgfx.so")
endif()
ExternalProject_Add_Step(waddamburo_bgfx_build stage
  DEPENDEES install
  COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${_built}" "${_staged}")
