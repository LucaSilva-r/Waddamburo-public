# The release builds' toolchains, baked once (.github/workflows/ci-image.yml) and pulled from ghcr.io, so a release
# never waits on Ubuntu's package mirrors. One target per release job, each on the base its job always used:
#   linux    Ubuntu 22.04 (its glibc is the oldest the AppImage runs on), the bgfx X11/GL/Wayland headers
#   windows  Ubuntu 24.04 with the mingw-w64 cross compiler (posix threads)
# .NET comes from actions/setup-dotnet in the job (Microsoft's CDN), as before. zstd: actions/cache compresses with it.
# Local check: docker build -f eng/ci/Dockerfile --target windows -t waddamburo-build:windows eng/ci

FROM ubuntu:22.04 AS linux
ARG CMAKE_VERSION=3.31.6
ARG CMAKE_SHA256=5a1133ff103c71eb5120e2cc3de922733e7d8a26a98ae716397e8676adb367bf
RUN apt-get -o Acquire::Retries=5 update \
    && DEBIAN_FRONTEND=noninteractive apt-get -o Acquire::Retries=5 install -y --no-install-recommends \
        ca-certificates curl git zip unzip xz-utils zstd file pkg-config python3 patch make gcc g++ ninja-build nasm \
        libicu70 libssl3 zlib1g libx11-dev libgl-dev libwayland-dev \
    && rm -rf /var/lib/apt/lists/*
# CMake 3.25 or later (native/CMakeLists.txt); 22.04 has 3.22.
RUN curl -fsSL -o /tmp/cmake.tar.gz \
        "https://github.com/Kitware/CMake/releases/download/v${CMAKE_VERSION}/cmake-${CMAKE_VERSION}-linux-x86_64.tar.gz" \
    && echo "${CMAKE_SHA256}  /tmp/cmake.tar.gz" | sha256sum --check \
    && tar -xzf /tmp/cmake.tar.gz -C /opt && rm /tmp/cmake.tar.gz \
    && ln -s /opt/cmake-${CMAKE_VERSION}-linux-x86_64/bin/* /usr/local/bin/

FROM ubuntu:24.04 AS windows
RUN apt-get -o Acquire::Retries=5 update \
    && DEBIAN_FRONTEND=noninteractive apt-get -o Acquire::Retries=5 install -y --no-install-recommends \
        ca-certificates curl git zip unzip xz-utils zstd file pkg-config python3 patch make gcc g++ ninja-build nasm cmake \
        libicu74 libssl3t64 zlib1g gcc-mingw-w64-x86-64-posix g++-mingw-w64-x86-64-posix \
    && rm -rf /var/lib/apt/lists/*
