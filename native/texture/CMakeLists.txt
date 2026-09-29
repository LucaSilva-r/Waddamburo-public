# BC7 texture encoding for the upscaled-texture cache: bc7enc / bc7decomp from bc7enc_rdo (MIT or
# public domain, vendored unchanged in third_party/bc7enc) behind a small project C ABI.
add_library(waddamburo_bc7enc OBJECT
  third_party/bc7enc/bc7enc.cpp
  third_party/bc7enc/bc7decomp.cpp)
# Third-party code keeps its own warnings: the project's -Werror policy applies to our sources only.
set_target_properties(waddamburo_bc7enc PROPERTIES
  POSITION_INDEPENDENT_CODE ON CXX_VISIBILITY_PRESET hidden VISIBILITY_INLINES_HIDDEN ON)
target_compile_features(waddamburo_bc7enc PRIVATE cxx_std_17)
target_include_directories(waddamburo_bc7enc PUBLIC third_party/bc7enc)
# Always optimized: unoptimized, BC7 encoding is far too slow even for development runs.
# (MSVC Debug keeps its defaults: /O2 conflicts with its /RTC1 checks.)
if(NOT MSVC)
  target_compile_options(waddamburo_bc7enc PRIVATE -w -O3)
endif()

add_library(waddamburo_texture SHARED src/texture.cpp $<TARGET_OBJECTS:waddamburo_bc7enc>)
add_library(Waddamburo::Texture ALIAS waddamburo_texture)
waddamburo_configure_native_target(waddamburo_texture)
if(MINGW)
  set_target_properties(waddamburo_texture PROPERTIES PREFIX "")
  target_link_options(waddamburo_texture PRIVATE -static)
endif()
target_compile_definitions(waddamburo_texture PRIVATE WADDAMBURO_TEXTURE_BUILDING_LIBRARY=1)
target_include_directories(waddamburo_texture PUBLIC $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>)
target_include_directories(waddamburo_texture SYSTEM PRIVATE third_party/bc7enc)
set_target_properties(waddamburo_texture PROPERTIES
  C_VISIBILITY_PRESET hidden CXX_VISIBILITY_PRESET hidden VISIBILITY_INLINES_HIDDEN true)

if(BUILD_TESTING)
  add_executable(waddamburo_texture_abi_test tests/texture_abi_test.c)
  waddamburo_configure_native_target(waddamburo_texture_abi_test)
  target_link_libraries(waddamburo_texture_abi_test PRIVATE Waddamburo::Texture)
  add_test(NAME texture_abi COMMAND waddamburo_texture_abi_test)
endif()
