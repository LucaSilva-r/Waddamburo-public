if(NOT DEFINED VGMSTREAM_PREFIX)
  message(FATAL_ERROR "VGMSTREAM_PREFIX is required")
endif()

if(NOT EXISTS "${VGMSTREAM_PREFIX}/include/vgmstream/libvgmstream.h")
  message(FATAL_ERROR "The vgmstream public API header was not installed")
endif()
if(NOT EXISTS "${VGMSTREAM_PREFIX}/licenses/vgmstream/COPYING")
  message(FATAL_ERROR "The vgmstream license was not installed")
endif()
if(NOT EXISTS "${VGMSTREAM_PREFIX}/NON-REDISTRIBUTABLE-G719.txt")
  message(FATAL_ERROR "The G.719 non-redistribution marker was not installed")
endif()
file(GLOB _libraries
  "${VGMSTREAM_PREFIX}/lib/libvgmstream.so*"
  "${VGMSTREAM_PREFIX}/lib64/libvgmstream.so*")
if(NOT _libraries)
  message(FATAL_ERROR "The vgmstream shared library was not installed")
endif()
