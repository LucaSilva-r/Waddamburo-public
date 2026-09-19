cmake_minimum_required(VERSION 3.25)

project(WaddamburoNative LANGUAGES C CXX)

set(_waddamburo_build_types Debug Release Sanitize)
set_property(CACHE CMAKE_BUILD_TYPE PROPERTY STRINGS ${_waddamburo_build_types})

if(CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_CONFIGURATION_TYPES ${_waddamburo_build_types} CACHE STRING "Supported build configurations" FORCE)
elseif(NOT CMAKE_BUILD_TYPE IN_LIST _waddamburo_build_types)
  message(FATAL_ERROR "CMAKE_BUILD_TYPE must be Debug, Release, or Sanitize")
endif()

add_library(waddamburo_native_options INTERFACE)
add_library(Waddamburo::NativeOptions ALIAS waddamburo_native_options)

target_compile_features(waddamburo_native_options INTERFACE c_std_17 cxx_std_20)
set_property(TARGET waddamburo_native_options PROPERTY
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug,Sanitize>:Debug>DLL")

if(MSVC)
  target_compile_options(waddamburo_native_options INTERFACE
    /W4
    /WX
    /permissive-
    $<$<CONFIG:Sanitize>:/Od>
    $<$<CONFIG:Sanitize>:/Zi>
    $<$<CONFIG:Sanitize>:/fsanitize=address>)
  target_link_options(waddamburo_native_options INTERFACE
    $<$<CONFIG:Sanitize>:/DEBUG>)
elseif(CMAKE_C_COMPILER_ID MATCHES "Clang|GNU")
  target_compile_options(waddamburo_native_options INTERFACE
    -Wall
    -Wextra
    -Wpedantic
    -Werror
    $<$<CONFIG:Sanitize>:-O1>
    $<$<CONFIG:Sanitize>:-g>
    $<$<CONFIG:Sanitize>:-fno-omit-frame-pointer>
    $<$<CONFIG:Sanitize>:-fsanitize=address,undefined>)
  target_link_options(waddamburo_native_options INTERFACE
    $<$<CONFIG:Sanitize>:-fsanitize=address,undefined>)
else()
  message(FATAL_ERROR "Unsupported native compiler: ${CMAKE_C_COMPILER_ID}")
endif()

function(waddamburo_configure_native_target target)
  target_link_libraries(${target} PRIVATE Waddamburo::NativeOptions)
  set_target_properties(${target} PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/stage/$<CONFIG>/lib"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/stage/$<CONFIG>/lib"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/stage/$<CONFIG>/bin")
endfunction()

include(CTest)

add_subdirectory(media)

option(WADDAMBURO_BUILD_FFMPEG "Enable the pinned minimal FFmpeg dependency target" OFF)
if(WADDAMBURO_BUILD_FFMPEG)
  add_subdirectory(ffmpeg)
endif()

if(BUILD_TESTING)
  add_executable(waddamburo_native_build_policy_test tests/build_policy_test.c)
  waddamburo_configure_native_target(waddamburo_native_build_policy_test)
  target_compile_definitions(waddamburo_native_build_policy_test PRIVATE
    $<$<CONFIG:Debug>:WADDAMBURO_BUILD_DEBUG=1>
    $<$<CONFIG:Release>:WADDAMBURO_BUILD_RELEASE=1>
    $<$<CONFIG:Sanitize>:WADDAMBURO_BUILD_SANITIZE=1>)
  add_test(NAME native_build_policy COMMAND waddamburo_native_build_policy_test)
endif()
