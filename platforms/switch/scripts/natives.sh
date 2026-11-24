#!/bin/bash
# Inside the devkitA64 image (build.sh natives): Waddamburo's native dependencies as static libs for libnx ->
# $OUT/natives/prefix, from the pinned sources build.sh fetched (FFmpeg with native/'s options for this platform).
# $1 selects the step: ffmpeg, vgmstream, sqlite, waddamburo or all.
set -e
WHAT=${1:-all}
. "$OUT/ext/versions.env"
export PATH=$DEVKITPRO/devkitA64/bin:$DEVKITPRO/portlibs/switch/bin:$DEVKITPRO/tools/bin:$PATH
P=$OUT/natives/prefix; mkdir -p $P/lib $P/include
ARCH="-march=armv8-a+crc+crypto -mtune=cortex-a57 -mtp=soft -fPIE"
CF="-O2 -ffunction-sections -fdata-sections $ARCH -D__SWITCH__ -I$DEVKITPRO/libnx/include -I$DEVKITPRO/portlibs/switch/include"
if [ $WHAT = ffmpeg -o $WHAT = all ]; then
  B=$OUT/natives/ffmpeg; mkdir -p $B; cd $B
  $OUT/ext/ffmpeg-$FFMPEG/configure --prefix=$P --enable-cross-compile --cross-prefix=aarch64-none-elf- \
    --arch=aarch64 --cpu=cortex-a57 --target-os=none --pkg-config=aarch64-none-elf-pkg-config \
    --extra-cflags="$CF -D_GNU_SOURCE" --extra-ldflags="$ARCH -specs=$DEVKITPRO/libnx/switch.specs -L$DEVKITPRO/portlibs/switch/lib -L$DEVKITPRO/libnx/lib" \
    --extra-libs="-lnx" --enable-static --disable-shared --enable-pic --enable-pthreads --disable-runtime-cpudetect \
    --disable-autodetect --disable-everything --disable-iconv --disable-gpl --disable-nonfree --disable-version3 \
    --enable-avcodec --enable-avformat --enable-avutil --enable-swresample --disable-avdevice --disable-avfilter --enable-swscale \
    --enable-decoder=atrac3p,h264,mp3,mp3float,vorbis,flac,opus,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,pcm_u8 \
    --enable-decoder=mpeg4,flv,vp6f,msmpeg4v1,msmpeg4v2,msmpeg4v3,wmv3,mpeg1video,mpeg2video,mjpeg \
    --enable-demuxer=wav,mp3,ogg,flac,mpegps,h264,mov,avi,flv,matroska,asf,mpegvideo --enable-libopus --pkg-config-flags=--static --enable-encoder=libopus \
    --enable-muxer=ogg --enable-parser=mpegaudio,vorbis,flac,opus,h264,mpeg4video,mpegvideo,vc1,mjpeg --enable-protocol=file --disable-network \
    --disable-programs --disable-doc --disable-debug --enable-small > $OUT/natives-ffmpeg-config.log 2>&1 || { tail -20 ffbuild/config.log; exit 1; }
  make -j8 > $OUT/natives-ffmpeg-build.log 2>&1 || { grep -E "error" -A3 $OUT/natives-ffmpeg-build.log | head -30; exit 1; }
  make install > /dev/null
  ls $P/lib
fi
if [ $WHAT = vgmstream -o $WHAT = all ]; then
  # Same options as native/vgmstream; G.722.1 helpers removed from a copy (not redistributable).
  rm -rf $OUT/natives/vgmstream-src && cp -r $OUT/ext/vgmstream-$VGMSTREAM $OUT/natives/vgmstream-src
  rm -f $OUT/natives/vgmstream-src/src/coding/libs/g7221_lib.c $OUT/natives/vgmstream-src/src/coding/libs/g7221_aes.c
  cmake -S $OUT/natives/vgmstream-src -B $OUT/natives/vgmstream -DCMAKE_TOOLCHAIN_FILE=$DEVKITPRO/cmake/Switch.cmake \
    -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_CLI=OFF -DBUILD_V123=OFF -DBUILD_AUDACIOUS=OFF \
    -DUSE_FFMPEG=OFF -DUSE_MPEG=OFF -DUSE_VORBIS=OFF -DUSE_G7221=OFF -DUSE_G719=OFF -DUSE_ATRAC9=OFF -DUSE_CELT=OFF -DUSE_SPEEX=OFF \
    > $OUT/natives-vgm-config.log 2>&1 || { tail -20 $OUT/natives-vgm-config.log; exit 1; }
  cmake --build $OUT/natives/vgmstream --target libvgmstream -j8 > $OUT/natives-vgm-build.log 2>&1 || { grep -E "error" -A3 $OUT/natives-vgm-build.log | head -30; exit 1; }
  cp $OUT/natives/vgmstream/src/libvgmstream.a $P/lib/
  mkdir -p $P/include/vgmstream && cp $OUT/natives/vgmstream-src/src/libvgmstream*.h $P/include/vgmstream/
  ls -la $P/lib/libvgmstream.a
fi
if [ $WHAT = sqlite -o $WHAT = all ]; then
  mkdir -p $OUT/natives/obj
  # e_sqlite3 (SQLitePCLRaw's P/Invoke name): 3.53.3 like the bundled one, same feature options (from its binary).
  # libnx has no POSIX file locks and no WAL is used: the lock-free unix-none VFS, no mmap.
  aarch64-none-elf-gcc $CF -std=gnu11 -w -DSQLITE_DEFAULT_AUTOVACUUM -DSQLITE_DEFAULT_CACHE_SIZE=-2000 -DSQLITE_DEFAULT_FILE_FORMAT=4 -DSQLITE_DEFAULT_FOREIGN_KEYS -DSQLITE_DEFAULT_JOURNAL_SIZE_LIMIT=-1 -DSQLITE_DEFAULT_PAGE_SIZE=4096 -DSQLITE_DEFAULT_PCACHE_INITSZ=20 -DSQLITE_DEFAULT_RECURSIVE_TRIGGERS -DSQLITE_DEFAULT_SECTOR_SIZE=4096 -DSQLITE_DEFAULT_SYNCHRONOUS=2 -DSQLITE_DEFAULT_WAL_SYNCHRONOUS=2 -DSQLITE_DEFAULT_WORKER_THREADS=0 -DSQLITE_ENABLE_COLUMN_METADATA -DSQLITE_ENABLE_FTS3 -DSQLITE_ENABLE_FTS3_PARENTHESIS -DSQLITE_ENABLE_FTS4 -DSQLITE_ENABLE_FTS5 -DSQLITE_ENABLE_GEOPOLY -DSQLITE_ENABLE_MATH_FUNCTIONS -DSQLITE_ENABLE_PREUPDATE_HOOK -DSQLITE_ENABLE_RTREE -DSQLITE_ENABLE_SESSION -DSQLITE_ENABLE_SNAPSHOT -DSQLITE_MAX_ATTACHED=10 -DSQLITE_MAX_COLUMN=2000 -DSQLITE_MAX_COMPOUND_SELECT=500 -DSQLITE_MAX_DEFAULT_PAGE_SIZE=8192 -DSQLITE_MAX_EXPR_DEPTH=1000 -DSQLITE_MAX_FUNCTION_ARG=1000 -DSQLITE_MAX_LENGTH=1000000000 -DSQLITE_MAX_LIKE_PATTERN_LENGTH=50000 -DSQLITE_MAX_PAGE_COUNT=0xfffffffe -DSQLITE_MAX_PAGE_SIZE=65536 -DSQLITE_MAX_SQL_LENGTH=1000000000 -DSQLITE_MAX_TRIGGER_DEPTH=1000 -DSQLITE_MAX_VARIABLE_NUMBER=32766 -DSQLITE_MAX_VDBE_OP=250000000 -DSQLITE_MAX_WORKER_THREADS=8 -DSQLITE_TEMP_STORE=1 -DSQLITE_DEFAULT_WAL_AUTOCHECKPOINT=1000 \
    -DSQLITE_DEFAULT_UNIX_VFS=\"unix-none\" -DSQLITE_MAX_MMAP_SIZE=0 -DSQLITE_DEFAULT_MMAP_SIZE=0 -DSQLITE_OMIT_LOAD_EXTENSION -DSQLITE_OMIT_WAL -DUSE_PREAD -DSQLITE_DISABLE_DIRSYNC \
    -DHAVE_FCHOWN=0 -DHAVE_READLINK=0 -DHAVE_LSTAT=0 \
    -c $OUT/ext/sqlite-amalgamation-3530300/sqlite3.c -o $OUT/natives/obj/sqlite3.o
  aarch64-none-elf-ar rcs $P/lib/libe_sqlite3.a $OUT/natives/obj/sqlite3.o
  ls -la $P/lib/libe_sqlite3.a
fi
if [ $WHAT = waddamburo -o $WHAT = all ]; then
  N=$ROOT/native; O=$OUT/natives/obj; mkdir -p $O
  gcc() { aarch64-none-elf-gcc "$@"; }
  aarch64-none-elf-gcc $CF -std=gnu11 -DWADDAMBURO_MEDIA_BUILDING_LIBRARY=1 -DWADDAMBURO_MEDIA_HAS_FFMPEG=1 -DWADDAMBURO_MEDIA_HAS_VGMSTREAM=1 \
    -I$N/media/include -I$P/include -I$OUT/natives/vgmstream-src/src -c $N/media/src/media.c -o $O/media.o
  aarch64-none-elf-gcc $CF -std=gnu11 -DWADDAMBURO_MEDIA_BUILDING_LIBRARY=1 -DWADDAMBURO_MEDIA_HAS_FFMPEG=1 -DWADDAMBURO_MEDIA_HAS_VGMSTREAM=1 \
    -I$N/media/include -I$P/include -I$OUT/natives/vgmstream-src/src -c $N/media/src/video.c -o $O/video.o
  aarch64-none-elf-gcc $CF -std=gnu11 -DWADDAMBURO_MEDIA_BUILDING_LIBRARY=1 -DWADDAMBURO_MEDIA_HAS_FFMPEG=1 -DWADDAMBURO_MEDIA_HAS_VGMSTREAM=1 \
    -I$N/media/include -I$P/include -I$OUT/natives/vgmstream-src/src -c $N/media/src/transcode.c -o $O/transcode.o
  aarch64-none-elf-ar rcs $P/lib/libwaddamburo_media.a $O/media.o $O/video.o $O/transcode.o
  aarch64-none-elf-gcc $CF -std=gnu11 -DWADDAMBURO_TEXT_BUILDING_LIBRARY=1 -I$N/text/include -I$DEVKITPRO/portlibs/switch/include/freetype2 \
    -c $N/text/src/text.c -o $O/text.o
  aarch64-none-elf-ar rcs $P/lib/libwaddamburo_text.a $O/text.o
  aarch64-none-elf-g++ $CF -std=c++17 -w -O3 -I$N/texture/third_party/bc7enc -c $N/texture/third_party/bc7enc/bc7enc.cpp -o $O/bc7enc.o
  aarch64-none-elf-g++ $CF -std=c++17 -w -O3 -I$N/texture/third_party/bc7enc -c $N/texture/third_party/bc7enc/bc7decomp.cpp -o $O/bc7decomp.o
  aarch64-none-elf-g++ $CF -std=c++17 -DWADDAMBURO_TEXTURE_BUILDING_LIBRARY=1 -I$N/texture/include -I$N/texture/third_party/bc7enc \
    -c $N/texture/src/texture.cpp -o $O/texture.o
  aarch64-none-elf-ar rcs $P/lib/libwaddamburo_texture.a $O/texture.o $O/bc7enc.o $O/bc7decomp.o
  ls -la $P/lib/libwaddamburo_*.a
fi
