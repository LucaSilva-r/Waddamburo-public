# iOS arm64 devices (Xcode's iPhone SDK). A toolchain file rather than preset variables: the ExternalProject
# dependencies (FreeType, Opus, bgfx, vgmstream) get only CMAKE_TOOLCHAIN_FILE.
set(CMAKE_SYSTEM_NAME iOS)
set(CMAKE_OSX_SYSROOT iphoneos)
set(CMAKE_OSX_ARCHITECTURES arm64)
set(CMAKE_OSX_DEPLOYMENT_TARGET 15.0)
set(CMAKE_SYSTEM_PROCESSOR arm64) # FFmpeg and Opus pick their ARM code from it
