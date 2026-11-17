# Builds the Android arm64 release libraries with the NDK. A wrapper around the NDK's own
# toolchain file so the ExternalProject dependencies (FFmpeg, opus, vgmstream, FreeType, bgfx),
# which only receive CMAKE_TOOLCHAIN_FILE, build for the same ABI, API level and C++ runtime.
set(ANDROID_ABI arm64-v8a)
set(ANDROID_PLATFORM android-26)
# One self-contained library each: no libc++_shared.so to ship beside them.
set(ANDROID_STL c++_static)

# The NDK is a tool location, not a build setting: ANDROID_NDK_HOME / ANDROID_NDK_ROOT, else
# the newest side-by-side NDK of the SDK in ANDROID_HOME (default ~/Android/Sdk).
set(_ndk "$ENV{ANDROID_NDK_HOME}")
if(NOT _ndk)
  set(_ndk "$ENV{ANDROID_NDK_ROOT}")
endif()
if(NOT _ndk)
  set(_sdk "$ENV{ANDROID_HOME}")
  if(NOT _sdk)
    set(_sdk "$ENV{HOME}/Android/Sdk")
  endif()
  file(GLOB _ndks LIST_DIRECTORIES true "${_sdk}/ndk/*")
  list(SORT _ndks COMPARE NATURAL ORDER DESCENDING)
  list(GET _ndks 0 _ndk)
endif()
if(NOT EXISTS "${_ndk}/build/cmake/android.toolchain.cmake")
  message(FATAL_ERROR "Android NDK not found: set ANDROID_NDK_HOME (looked in '${_ndk}').")
endif()
include("${_ndk}/build/cmake/android.toolchain.cmake")
