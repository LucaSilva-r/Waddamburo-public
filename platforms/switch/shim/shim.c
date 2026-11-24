// glibc/Linux ABI on libnx for the prebuilt linux-arm64 NativeAOT runtime.
// Every libc symbol the runtime imports was renamed to lx_<name> (build.sh), so this file can use
// newlib/libnx freely and convert layouts and constants (glibc aarch64 values) at the boundary.
// ponytail: feasibility spike; unimplemented calls fail with ENOSYS and log once.
#define _GNU_SOURCE
#include <switch.h>
#include <errno.h>
#include <fcntl.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <unistd.h>
#include <time.h>
#include <dirent.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <math.h>
#include <malloc.h>
#include <limits.h>

extern char __eh_frame_hdr_start[], __eh_frame_hdr_end[], __end__[];
// libnx's __start__ is an absolute 0 (linker script), so find the load base from the text mapping.
static char *g_base;
#define __start__ g_base
int g_no_console; // set once bgfx owns the screen
void log_flush(void); // main.c: buffered SD log
#define LX_ENOSYS 38
#define LX_ETIMEDOUT 110
#define LX_ENOTTY 25

static void todo(const char *what) { fprintf(stderr, "[shim] unimplemented: %s\n", what); }
#define NOSYS(name) do { static int once; if (!once++) todo(name); errno = ENOSYS; return -1; } while (0)

// ---------------------------------------------------------------- heap / memory
// Homebrew has no svcMapPhysicalMemory (needs a system resource pool), but the heap is one contiguous, always-backed
// block. It is split in three: malloc, libdrm_nouveau's buffer objects, and the GC's pool. The runtime's mmap reserves
// straight from the pool: reserving is a bump, committing a page zeroes it, decommitting only marks it. (Until 2026-11-22
// the pool was mapped into the stack region with svcMapMemory: that range shrank from launch to launch, 1056 -> 608 MiB,
// until a 25 MiB array did not fit with 2.5 GiB of the app's memory free.)
static void *g_heap_addr; static size_t g_heap_orig;
static int g_heap_fixed;
static size_t MALLOC_HEAP = 512ull << 20, GPU_HEAP = 896ull << 20; // 131 and 381 MiB used at most so far (2026-11-22)
static uintptr_t g_gpu_base, g_res_lo = ~0ull, g_res_hi, g_arena_next; // the pool, and where the next reservation starts
#define HEAP_STEP (2ull << 20)

static Result g_heap_rc;
void __libnx_initheap(void) {
    extern char *fake_heap_start, *fake_heap_end;
    void *addr = NULL;
    if (envHasHeapOverride()) { // hbloader already owns the heap (and our image lives near it): never resize it
        g_heap_orig = envGetHeapOverrideSize(); addr = envGetHeapOverrideAddr(); g_heap_fixed = 1;
    } else {
        u64 total = 0, used = 0;
        svcGetInfo(&total, InfoType_TotalMemorySize, CUR_PROCESS_HANDLE, 0); svcGetInfo(&used, InfoType_UsedMemorySize, CUR_PROCESS_HANDLE, 0);
        g_heap_orig = (total - used - (32ull << 20)) & ~(HEAP_STEP - 1);
        Result rc = svcSetHeapSize(&addr, g_heap_orig);
        if (R_FAILED(rc)) diagAbortWithResult(rc);
    }
    if (g_heap_orig < (2ull << 30)) { MALLOC_HEAP = (g_heap_orig / 4) & ~(HEAP_STEP - 1); GPU_HEAP = MALLOC_HEAP; } // applet mode
    g_heap_addr = addr;
    fake_heap_start = addr; fake_heap_end = (char *)addr + MALLOC_HEAP;
    g_gpu_base = (uintptr_t)addr + MALLOC_HEAP;
    g_res_lo = g_arena_next = (g_gpu_base + GPU_HEAP + 0xffff) & ~0xffffull;
    g_res_hi = ((uintptr_t)addr + g_heap_orig) & ~0xffffull;
}

static Mutex g_mm;
// Memory the GC may still count on: the loader heap is all "used" by the kernel's measure (we own it), so report the
// process total minus what malloc, GPU buffers and the GC really hold, less a reserve. Otherwise the GC sees ~95%
// memory load and collects gen2 over and over.
size_t nx_available(void);
static size_t g_committed; // GC memory in use right now
static u64 *g_pages; // one bit per 4 KiB page of the pool: committed (zeroed and the runtime's)

static char g_region_range[24], g_hard_limit[24]; // DOTNET_GCRegionRange, DOTNET_GCHeapHardLimit (hex): the env table
static void arena_init(void) {
    size_t size = g_res_hi - g_res_lo;
    // A crashed run of the old scheme leaves pages of hbloader's heap mapped away: touching them would fault later.
    for (uintptr_t a = g_res_lo; a < g_res_hi; ) {
        MemoryInfo mi; u32 pi; svcQueryMemory(&mi, &pi, a);
        if ((mi.perm & Perm_Rw) != Perm_Rw || mi.attr) {
            fprintf(stderr, "[shim] heap page %lx not ours (type %x perm %x attr %x): restart the console or the taken-over game\n", a, mi.type, mi.perm, mi.attr);
            log_flush(); diagAbortWithResult(MAKERESULT(Module_Libnx, LibnxError_OutOfMemory));
        }
        a = mi.addr + mi.size;
    }
    g_pages = calloc((size >> 18) + 1, 8);
    // The GC's regions and its bookkeeping (card tables etc., ~1%) share the pool with the runtime's small mappings.
    size_t range = (size - size / 16 - (32ull << 20)) & ~0xfffffull;
    snprintf(g_region_range, sizeof g_region_range, "%lx", range);
    snprintf(g_hard_limit, sizeof g_hard_limit, "%lx", range / 10 * 9); // slack for regions half used
}

static u64 g_commit_ns, g_decommit_ns, g_commit_bytes, g_decommit_bytes; static u32 g_commits, g_decommits; // per 10 s, logged

static int commit(uintptr_t a, uintptr_t end) {
    u64 t0 = armGetSystemTick();
    a &= ~0xfffull;
    while (a < end) { // runs of pages not committed yet: marked under the lock, zeroed outside it
        mutexLock(&g_mm);
        while (a < end && (g_pages[(a - g_res_lo) >> 18] >> ((a - g_res_lo) >> 12 & 63) & 1)) a += 0x1000;
        uintptr_t run = a;
        for (; a < end && !(g_pages[(a - g_res_lo) >> 18] >> ((a - g_res_lo) >> 12 & 63) & 1); a += 0x1000)
            g_pages[(a - g_res_lo) >> 18] |= 1ull << ((a - g_res_lo) >> 12 & 63);
        if (a > run) { g_committed += a - run; g_commits++; g_commit_bytes += a - run; }
        mutexUnlock(&g_mm);
        memset((void *)run, 0, a - run); // Linux gives zero pages
    }
    g_commit_ns += armTicksToNs(armGetSystemTick() - t0);
    return 0;
}

static void decommit(uintptr_t a, uintptr_t end) {
    u64 t0 = armGetSystemTick();
    mutexLock(&g_mm);
    for (; a < end; a += 0x1000) {
        u64 *w = &g_pages[(a - g_res_lo) >> 18], bit = 1ull << ((a - g_res_lo) >> 12 & 63);
        if (*w & bit) { *w &= ~bit; g_committed -= 0x1000; g_decommit_bytes += 0x1000; }
    }
    g_decommits++;
    g_decommit_ns += armTicksToNs(armGetSystemTick() - t0);
    mutexUnlock(&g_mm);
}

static void park_threads(void);
void __attribute__((weak)) sdlnx_shutdown(void);
void userAppExit(void) {
    if (sdlnx_shutdown) sdlnx_shutdown(); // stop the HID/audio threads before libnx closes those services
    log_flush();
    park_threads();
}

static int ours(uintptr_t a) { return a >= g_res_lo && a < g_res_hi; }

#define LX_PROT_NONE 0
#define LX_MAP_FIXED 0x10
#define LX_MAP_ANONYMOUS 0x20
#define PAGE 0x1000ull
#define PAGE_UP(x) (((x) + PAGE - 1) & ~(PAGE - 1))

void *lx_mmap64(void *addr, size_t len, int prot, int flags, int fd, long off) {
    if (!(flags & LX_MAP_ANONYMOUS) || (prot & 4 /*EXEC*/)) { errno = ENODEV; return (void *)-1; }
    len = PAGE_UP(len);
    uintptr_t a;
    if (flags & LX_MAP_FIXED) {
        a = (uintptr_t)addr;
        if (!ours(a)) { errno = EINVAL; return (void *)-1; }
        decommit(a, a + len);   // fresh zero pages, like Linux
    } else {
        // ponytail: bump allocation inside the pool, never released (the runtime reserves once).
        mutexLock(&g_mm);
        a = g_arena_next; g_arena_next = (a + len + 0xffff) & ~0xffffull;
        mutexUnlock(&g_mm);
        if (g_arena_next > g_res_hi) { fprintf(stderr, "[shim] GC pool full for %lx (%lu MiB)\n", len, (g_res_hi - g_res_lo) >> 20); errno = ENOMEM; return (void *)-1; }
    }
    if (prot != LX_PROT_NONE && commit(a, a + len)) { errno = ENOMEM; return (void *)-1; }
    return (void *)a;
}

int lx_munmap(void *addr, size_t len) {
    uintptr_t a = (uintptr_t)addr;
    if (ours(a)) decommit(a, a + PAGE_UP(len));
    return 0;
}

int lx_mprotect(void *addr, size_t len, int prot) {
    uintptr_t a = (uintptr_t)addr & ~(PAGE - 1);
    // ponytail: PROT_NONE keeps pages mapped (no guard pages); RW commits.
    if (ours(a) && prot != LX_PROT_NONE) return commit(a, PAGE_UP((uintptr_t)addr + len)) ? (errno = ENOMEM, -1) : 0;
    return 0;
}

int lx_madvise(void *a, size_t l, int advice) { return 0; }
int lx_mlock(const void *a, size_t l) { return 0; }
int lx_munlock(const void *a, size_t l) { return 0; }

void *lx_malloc(size_t n) { return malloc(n); }
void *lx_calloc(size_t a, size_t b) { return calloc(a, b); }
void lx_free(void *p) { free(p); }

// ---------------------------------------------------------------- libc passthrough
void *lx_memcpy(void *d, const void *s, size_t n) { return memcpy(d, s, n); }
void *lx_memmove(void *d, const void *s, size_t n) { return memmove(d, s, n); }
void *lx_memset(void *d, int c, size_t n) { return memset(d, c, n); }
size_t lx_strlen(const char *s) { return strlen(s); }
int lx_strcmp(const char *a, const char *b) { return strcmp(a, b); }
int lx_strncmp(const char *a, const char *b, size_t n) { return strncmp(a, b, n); }
int lx_strcasecmp(const char *a, const char *b) { return strcasecmp(a, b); }
char *lx_strcpy(char *d, const char *s) { return strcpy(d, s); }
char *lx_strncpy(char *d, const char *s, size_t n) { return strncpy(d, s, n); }
char *lx_strcat(char *d, const char *s) { return strcat(d, s); }
char *lx_strncat(char *d, const char *s, size_t n) { return strncat(d, s, n); }
char *lx_strdup(const char *s) { return strdup(s); }
char *lx_strrchr(const char *s, int c) { return strrchr(s, c); }
char *lx_strstr(const char *a, const char *b) { return strstr(a, b); }
char *lx_strtok_r(char *s, const char *d, char **p) { return strtok_r(s, d, p); }
long long lx_strtoll(const char *s, char **e, int b) { return strtoll(s, e, b); }
unsigned long lx_strtoul(const char *s, char **e, int b) { return strtoul(s, e, b); }
unsigned long long lx_strtoull(const char *s, char **e, int b) { return strtoull(s, e, b); }
char *lx_strerror(int e) { return strerror(e); }
char *lx_strerror_r(int e, char *buf, size_t n) { return strerror(e); } // GNU variant returns char*
double lx_log(double x) { return log(x); }
double lx_cos(double x) { return cos(x); }
float lx_cosf(float x) { return cosf(x); }
double lx_sin(double x) { return sin(x); }
float lx_sinf(float x) { return sinf(x); }
double lx_tan(double x) { return tan(x); }
float lx_tanf(float x) { return tanf(x); }
double lx_acos(double x) { return acos(x); }
float lx_acosf(float x) { return acosf(x); }
double lx_asin(double x) { return asin(x); }
float lx_asinf(float x) { return asinf(x); }
double lx_atan(double x) { return atan(x); }
float lx_atanf(float x) { return atanf(x); }
double lx_exp(double x) { return exp(x); }
float lx_expf(float x) { return expf(x); }
double lx_log10(double x) { return log10(x); }
float lx_log10f(float x) { return log10f(x); }
double lx_log2(double x) { return log2(x); }
float lx_log2f(float x) { return log2f(x); }
double lx_cbrt(double x) { return cbrt(x); }
float lx_cbrtf(float x) { return cbrtf(x); }
double lx_sinh(double x) { return sinh(x); }
float lx_sinhf(float x) { return sinhf(x); }
double lx_cosh(double x) { return cosh(x); }
float lx_coshf(float x) { return coshf(x); }
double lx_tanh(double x) { return tanh(x); }
float lx_tanhf(float x) { return tanhf(x); }
double lx_acosh(double x) { return acosh(x); }
float lx_acoshf(float x) { return acoshf(x); }
double lx_asinh(double x) { return asinh(x); }
float lx_asinhf(float x) { return asinhf(x); }
double lx_atanh(double x) { return atanh(x); }
float lx_atanhf(float x) { return atanhf(x); }
double lx_exp2(double x) { return exp2(x); }
float lx_exp2f(float x) { return exp2f(x); }
double lx_floor(double x) { return floor(x); }
float lx_floorf(float x) { return floorf(x); }
double lx_ceil(double x) { return ceil(x); }
float lx_ceilf(float x) { return ceilf(x); }
double lx_trunc(double x) { return trunc(x); }
float lx_truncf(float x) { return truncf(x); }
double lx_round(double x) { return round(x); }
float lx_roundf(float x) { return roundf(x); }
double lx_pow(double x, double y) { return pow(x, y); }
float lx_powf(float x, float y) { return powf(x, y); }
double lx_atan2(double x, double y) { return atan2(x, y); }
float lx_atan2f(float x, float y) { return atan2f(x, y); }
double lx_fmod(double x, double y) { return fmod(x, y); }
float lx_fmodf(float x, float y) { return fmodf(x, y); }
float lx_logf(float x) { return logf(x); }
int lx_getrusage(int who, void *r) { memset(r, 0, 144); return 0; }
long lx_lrand48(void) { return lrand48(); }
void lx_srand48(long s) { srand48(s); }
time_t lx_time(time_t *t) { return time(t); }
int lx_gettimeofday(struct timeval *tv, void *tz) { return gettimeofday(tv, NULL); }
int lx_atexit(void (*f)(void)) { return atexit(f); }
void lx_exit(int c) { exit(c); }
void log_flush(void);
void lx_abort(void) { log_flush(); fprintf(stderr, "[shim] abort from +%lx\n", (u64)__builtin_return_address(0) - (u64)__start__); if (!g_no_console) consoleUpdate(NULL); svcSleepThread(5000000000ll); abort(); }
static int lx_errno(int e);
int *lx___errno_location(void) { errno = lx_errno(errno); return &errno; } // 1..34 match glibc; the rest is translated
const char *lx_gai_strerror(int e) { return "getaddrinfo error"; }

uintptr_t lx___stack_chk_guard = 0x2b6f9a5c1e3d4f71ull;
void lx___stack_chk_fail(void) { fprintf(stderr, "[shim] stack smashing\n"); lx_abort(); }

FILE *lx_stderr;
int g_nxlink;
int lx_vsnprintf(char *b, size_t n, const char *f, va_list ap) { return vsnprintf(b, n, f, ap); }
int lx_snprintf(char *b, size_t n, const char *f, ...) { va_list ap; va_start(ap, f); int r = vsnprintf(b, n, f, ap); va_end(ap); return r; }
int lx_asprintf(char **o, const char *f, ...) { va_list ap; va_start(ap, f); int r = vasprintf(o, f, ap); va_end(ap); return r; }
int lx_fprintf(FILE *fp, const char *f, ...) { va_list ap; va_start(ap, f); int r = vfprintf(fp, f, ap); va_end(ap); return r; }
int lx_sscanf(const char *s, const char *f, ...) { va_list ap; va_start(ap, f); int r = vsscanf(s, f, ap); va_end(ap); return r; }
int lx_fputs(const char *s, FILE *fp) { return fputs(s, fp); }
size_t lx_fwrite(const void *p, size_t a, size_t b, FILE *fp) { return fwrite(p, a, b, fp); }
int lx_fflush(FILE *fp) { return fflush(fp); }
int lx_fclose(FILE *fp) { return fclose(fp); }
FILE *lx_fopen64(const char *p, const char *m) { return fopen(p, m); }
// Runtime 10.0.15 (minipal's CPU count reads /sys/devices/system/cpu/possible, absent here: it falls back to affinity).
int lx_fgetc(FILE *fp) { return fgetc(fp); }
int lx___isoc99_fscanf(FILE *fp, const char *f, ...) { va_list ap; va_start(ap, f); int r = vfscanf(fp, f, ap); va_end(ap); return r; }
// Static destructors: none run at exit (shim exit parks the threads and leaves).
int lx___cxa_atexit(void (*fn)(void *), void *arg, void *dso) { return 0; }
void *lx___dso_handle = &lx___dso_handle;
ssize_t lx___getdelim(char **l, size_t *n, int d, FILE *fp) { return __getdelim(l, n, d, fp); }

// Built-in environment: the GC must not try to reserve 256 GiB.
static const char *g_env[][2] = {
    { "DOTNET_GCgen0size", "10000000" },        // 256 MiB gen0 budget: garbage waits for the full collection at the next scene switch
    { "DOTNET_GCRegionRange", g_region_range }, // set from the arena actually reserved (arena_init)
    { "DOTNET_GCHeapHardLimit", g_hard_limit }, // what it really has: it compacts before it runs out
};
char *lx_getenv(const char *n) {
    for (size_t i = 0; i < sizeof g_env / sizeof *g_env; i++) if (!strcmp(n, g_env[i][0])) return (char *)g_env[i][1];
    return getenv(n);
}

// ---------------------------------------------------------------- files
// Stat cache fed by directory reads: libnx's fsDirRead gives each entry's type and size, newlib's readdir drops
// them, and an lstat costs ~5 FS IPCs (type, open, size, timestamp, close). A cached lstat costs one (timestamp).
// Entries live 5 s and are dropped by writes/renames/deletes through the shim.
#define SC_SLOTS 32768
typedef struct { char *path; u64 at; s64 size; u8 dir; } StatEntry;
static StatEntry g_sc[SC_SLOTS]; static Mutex g_sc_lock;
static u32 sc_hash(const char *p) { u32 h = 2166136261u; while (*p) h = (h ^ (u8)*p++) * 16777619u; return h; }
static void sc_put(const char *path, s64 size, int dir) {
    u64 now = armGetSystemTick(); u32 i = sc_hash(path) % SC_SLOTS;
    mutexLock(&g_sc_lock);
    for (int k = 0; k < 16; k++, i = (i + 1) % SC_SLOTS)
        if (!g_sc[i].path || !strcmp(g_sc[i].path, path) || armTicksToNs(now - g_sc[i].at) > 5000000000ull) {
            if (!g_sc[i].path || strcmp(g_sc[i].path, path)) { free(g_sc[i].path); g_sc[i].path = strdup(path); }
            g_sc[i].at = now; g_sc[i].size = size; g_sc[i].dir = dir; break;
        }
    mutexUnlock(&g_sc_lock);
}
static int sc_get(const char *path, s64 *size, int *dir) {
    u64 now = armGetSystemTick(); u32 i = sc_hash(path) % SC_SLOTS; int hit = 0;
    mutexLock(&g_sc_lock);
    for (int k = 0; k < 16; k++, i = (i + 1) % SC_SLOTS)
        if (g_sc[i].path && !strcmp(g_sc[i].path, path)) {
            if (armTicksToNs(now - g_sc[i].at) <= 5000000000ull) { *size = g_sc[i].size; *dir = g_sc[i].dir; hit = 1; }
            break;
        }
    mutexUnlock(&g_sc_lock);
    return hit;
}
static void sc_drop(const char *path) {
    u32 i = sc_hash(path) % SC_SLOTS;
    mutexLock(&g_sc_lock);
    for (int k = 0; k < 16; k++, i = (i + 1) % SC_SLOTS) if (g_sc[i].path && !strcmp(g_sc[i].path, path)) { g_sc[i].at = 0; break; }
    mutexUnlock(&g_sc_lock);
}
// libnx's stat turns SD timestamps (local wall time) into UTC through the system's time-zone rules (an IPC per call).
// Same conversion, memoised per hour of timestamp: identical results (the catalog cache compares them), few IPCs.
static time_t nx_local_to_utc(u64 ts) {
    static struct { u64 hour; s64 offset; } memo[256]; static Mutex m;
    u64 hour = ts / 3600; u32 i = hour % 256;
    mutexLock(&m);
    if (memo[i].hour == hour + 1) { s64 o = memo[i].offset; mutexUnlock(&m); return ts + o; }
    mutexUnlock(&m);
    time_t posix = (time_t)ts; struct tm t; gmtime_r(&posix, &t);
    TimeCalendarTime c = { .year = 1900 + t.tm_year, .month = 1 + t.tm_mon, .day = t.tm_mday, .hour = t.tm_hour, .minute = t.tm_min, .second = t.tm_sec };
    u64 out = 0; s32 n = 0;
    if (R_FAILED(timeToPosixTimeWithMyRule(&c, &out, 1, &n)) || n < 1) return ts;
    mutexLock(&m); memo[i].hour = hour + 1; memo[i].offset = (s64)out - (s64)ts; mutexUnlock(&m);
    return out;
}
static FsFileSystem *sdmc_fs(void) { static FsFileSystem *fs; if (!fs) fs = fsdevGetDeviceFileSystem("sdmc"); return fs; }
static const char *fs_path(const char *p) { return !strncmp(p, "sdmc:", 5) ? p + 5 : p; } // "/x" on the SD card

// Synthetic files: /proc (Process.TotalProcessorTime/WorkingSet64, Waddamburo's performance recorder) and
// /dev/urandom (Guid.NewGuid, RandomNumberGenerator via minipal). fds MEM_FD+i hold an in-memory snapshot.
static int g_nthreads; // tentative: defined with the thread list below
#define MEM_FD 0x5000
#define NMEM 16
typedef struct { char *data; size_t len, pos; int used, urandom; } MemFile;
static MemFile g_mem[NMEM];
static Mutex g_mem_lock;
static MemFile *mem_of(int fd) { int i = fd - MEM_FD; return i >= 0 && i < NMEM && g_mem[i].used ? &g_mem[i] : NULL; }
static u64 uptime_ticks100(void) { return armTicksToNs(armGetSystemTick()) / 10000000ull; } // clock ticks at 100 Hz
size_t net_file(const char *p, char *b, size_t n); // net.c: /etc/resolv.conf, /sys/class/net
static size_t proc_content(const char *p, char *b, size_t n) {
    if (strncmp(p, "/proc", 5)) return net_file(p, b, n);
    u64 total = 0, used = 0;
    svcGetInfo(&total, InfoType_TotalMemorySize, CUR_PROCESS_HANDLE, 0);
    svcGetInfo(&used, InfoType_UsedMemorySize, CUR_PROCESS_HANDLE, 0);
    u64 t = uptime_ticks100();
    const char *q = p + 6; // after "/proc/"
    if (!strncmp(q, "self/", 5)) q += 5; else if (!strncmp(q, "42/", 3)) q += 3;
    if (!strcmp(q, "stat") && q == p + 6) // system-wide /proc/stat: ponytail: all time counted as busy on 3 cores
        return snprintf(b, n, "cpu  %lu 0 0 0 0 0 0 0 0 0\ncpu0 %lu 0 0 0 0 0 0 0 0 0\n", t * 3, t);
    if (!strcmp(q, "stat")) // /proc/<pid>/stat: 52 fields; utime(14) = uptime (ponytail: no per-process CPU time), rss(24) in pages
        return snprintf(b, n, "42 (waddamburo) R 1 42 42 0 -1 0 0 0 0 0 %lu 0 0 0 20 0 %d 0 0 %lu %lu 18446744073709551615 0 0 0 0 0 0 0 0 0 0 0 0 17 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n",
                        t, g_nthreads + 1, used, used / 4096);
    if (!strcmp(q, "status"))
        return snprintf(b, n, "Name:\twaddamburo\nState:\tR (running)\nPid:\t42\nVmSize:\t%lu kB\nVmRSS:\t%lu kB\nThreads:\t%d\n",
                        used >> 10, used >> 10, g_nthreads + 1);
    if (!strcmp(q, "meminfo"))
        return snprintf(b, n, "MemTotal:       %lu kB\nMemFree:        %lu kB\nMemAvailable:   %lu kB\n", total >> 10, nx_available() >> 10, nx_available() >> 10);
    if (!strcmp(q, "cpuinfo"))
        return snprintf(b, n, "processor\t: 0\nmodel name\t: NVIDIA Tegra X1 (Cortex-A57)\n");
    return (size_t)-1;
}
static int mem_open(const char *p) {
    int ur = !strcmp(p, "/dev/urandom") || !strcmp(p, "/dev/random");
    char buf[1024]; size_t n = 0;
    if (!ur && (n = proc_content(p, buf, sizeof buf)) == (size_t)-1) { errno = ENOENT; return -1; }
    mutexLock(&g_mem_lock);
    for (int i = 0; i < NMEM; i++) if (!g_mem[i].used) {
        g_mem[i] = (MemFile){ ur ? NULL : memcpy(malloc(n + 1), buf, n), n, 0, 1, ur };
        mutexUnlock(&g_mem_lock); return MEM_FD + i;
    }
    mutexUnlock(&g_mem_lock); errno = EMFILE; return -1;
}
static ssize_t mem_read(MemFile *m, void *b, size_t n) {
    if (m->urandom) { randomGet(b, n); return n; }
    if (m->pos >= m->len) return 0;
    if (n > m->len - m->pos) n = m->len - m->pos;
    memcpy(b, m->data + m->pos, n); m->pos += n; return n;
}
static int is_synthetic(const char *p) {
    return !strncmp(p, "/proc", 5) || !strcmp(p, "/dev/urandom") || !strcmp(p, "/dev/random")
        || !strcmp(p, "/etc/resolv.conf") || !strncmp(p, "/sys/class/net/", 15);
}

// In-process pipes (System.Native's signal thread waits on one). fds PIPE_FD+2i (read) / +1 (write).
#define PIPE_FD 0x4000
#define NPIPES 16
typedef struct { Mutex m; CondVar cv; char buf[4096]; size_t head, len; int used, wclosed; } Pipe;
static Pipe g_pipes[NPIPES];
static Pipe *pipe_of(int fd) { int i = (fd - PIPE_FD) / 2; return fd >= PIPE_FD && i < NPIPES && g_pipes[i].used ? &g_pipes[i] : NULL; }
int lx_pipe2(int *fds, int fl) {
    for (int i = 0; i < NPIPES; i++) if (!g_pipes[i].used) {
        memset(&g_pipes[i], 0, sizeof g_pipes[i]); g_pipes[i].used = 1;
        fds[0] = PIPE_FD + 2 * i; fds[1] = fds[0] + 1; return 0;
    }
    errno = EMFILE; return -1;
}
int lx_pipe(int *fds) { return lx_pipe2(fds, 0); }
static ssize_t pipe_read(Pipe *p, char *b, size_t n) {
    mutexLock(&p->m);
    while (!p->len && !p->wclosed) condvarWait(&p->cv, &p->m);
    size_t k = 0;
    while (k < n && p->len) { b[k++] = p->buf[p->head]; p->head = (p->head + 1) % sizeof p->buf; p->len--; }
    mutexUnlock(&p->m); return k;
}
static ssize_t pipe_write(Pipe *p, const char *b, size_t n) {
    mutexLock(&p->m);
    size_t k = 0; // ponytail: drops bytes when full (only signal numbers travel through it)
    while (k < n && p->len < sizeof p->buf) { p->buf[(p->head + p->len) % sizeof p->buf] = b[k++]; p->len++; }
    condvarWakeAll(&p->cv); mutexUnlock(&p->m); return n;
}
#define LX_O_ACCMODE 3
#define LX_O_CREAT 0100
#define LX_O_EXCL 0200
#define LX_O_TRUNC 01000
#define LX_O_APPEND 02000
int lx_open64(const char *p, int fl, ...) {
    va_list ap; va_start(ap, fl); int mode = va_arg(ap, int); va_end(ap);
    if (is_synthetic(p)) return mem_open(p);
    { fprintf(stderr, "[open] %.3f %s%s\n", armTicksToNs(armGetSystemTick()) / 1e9, p, fl & LX_O_ACCMODE ? " (write)" : ""); }
    if (fl & (LX_O_ACCMODE | LX_O_CREAT | LX_O_TRUNC)) sc_drop(fs_path(p)); // opened for writing: size/time change
    int nf = fl & LX_O_ACCMODE;
    if (fl & LX_O_CREAT) nf |= O_CREAT;
    if (fl & LX_O_EXCL) nf |= O_EXCL;
    if (fl & LX_O_TRUNC) nf |= O_TRUNC;
    if (fl & LX_O_APPEND) nf |= O_APPEND;
    return open(p, nf, mode);
}
int net_close(int fd); void net_io(int fd, int writing, ssize_t r); // net.c
int lx_close(int fd) {
    if (net_close(fd) == 0) return 0; // an epoll set (a socket is forgotten there, then closed below)
    MemFile *mf = mem_of(fd);
    if (mf) { mutexLock(&g_mem_lock); free(mf->data); mf->used = 0; mutexUnlock(&g_mem_lock); return 0; }
    Pipe *p = pipe_of(fd);
    if (p) { if (fd & 1) { mutexLock(&p->m); p->wclosed = 1; condvarWakeAll(&p->cv); mutexUnlock(&p->m); } return 0; }
    return fd <= 2 ? 0 : close(fd);
}
ssize_t lx_read(int fd, void *b, size_t n) {
    MemFile *mf = mem_of(fd); if (mf) return mem_read(mf, b, n);
    Pipe *p = pipe_of(fd); if (p) return pipe_read(p, b, n);
    ssize_t r = read(fd, b, n); net_io(fd, 0, r); return r;
}
ssize_t lx_write(int fd, const void *b, size_t n) {
    Pipe *p = pipe_of(fd); if (p) return pipe_write(p, b, n);
    if (fd > 2) { ssize_t r = write(fd, b, n); net_io(fd, 1, r); return r; }
    fwrite(b, 1, n, stderr);
    if (g_nxlink) write(1, b, n); else if (!g_no_console) { write(1, b, n); consoleUpdate(NULL); }
    return n;
}
long lx_lseek64(int fd, long off, int wh) {
    MemFile *mf = mem_of(fd);
    if (mf) { long b = wh == SEEK_SET ? 0 : wh == SEEK_CUR ? (long)mf->pos : (long)mf->len; mf->pos = b + off; return mf->pos; }
    return lseek(fd, off, wh);
}
ssize_t lx_pread64(int fd, void *b, size_t n, long off) {
    long cur = lseek(fd, 0, SEEK_CUR); if (lseek(fd, off, SEEK_SET) < 0) return -1;
    ssize_t r = read(fd, b, n); lseek(fd, cur, SEEK_SET); return r;
}
int lx_unlink(const char *p) { sc_drop(fs_path(p)); return unlink(p); }
char *lx_getcwd(char *b, size_t n) { // libnx says "sdmc:/x"; .NET wants a rooted Unix path ("/x" resolves to sdmc:)
    char t[PATH_MAX]; if (!getcwd(t, sizeof t)) return NULL;
    const char *p = strncmp(t, "sdmc:", 5) ? t : t + 5;
    if (strlen(p) + 1 > n) { errno = ERANGE; return NULL; }
    return strcpy(b, p);
}
int lx_flock(int fd, int op) { return 0; }
int lx_isatty(int fd) { return 0; }
int lx_ioctl(int fd, unsigned long r, ...) { errno = LX_ENOTTY; return -1; }
int lx_tcgetattr(int fd, void *t) { errno = LX_ENOTTY; return -1; }
int lx_tcsetattr(int fd, int a, const void *t) { errno = LX_ENOTTY; return -1; }
int lx_dup2(int a, int b) { NOSYS("dup2"); }
// glibc's struct statfs64 (aarch64) from the SD card's space (DriveInfo's free space; every path is on sdmc).
struct lx_statfs { s64 type, bsize; u64 blocks, bfree, bavail, files, ffree; int fsid[2]; s64 namelen, frsize, flags, spare[4]; };
int lx_statfs64(const char *p, void *b) {
    struct lx_statfs *o = b;
    FsFileSystem *sd = fsdevGetDeviceFileSystem("sdmc");
    s64 total = 0, free_ = 0;
    if (!sd || R_FAILED(fsFsGetTotalSpace(sd, "/", &total)) || R_FAILED(fsFsGetFreeSpace(sd, "/", &free_))) { errno = EIO; return -1; }
    memset(o, 0, sizeof *o);
    o->type = 0x4d44; o->bsize = o->frsize = 4096; o->namelen = 255; // MSDOS_SUPER_MAGIC (FAT32/exFAT)
    o->blocks = total / 4096; o->bfree = o->bavail = free_ / 4096;
    return 0;
}
int lx_fstatfs64(int fd, void *b) { return lx_statfs64("/", b); }

struct lx_stat { // glibc aarch64 struct stat
    uint64_t st_dev, st_ino; uint32_t st_mode, st_nlink, st_uid, st_gid; uint64_t st_rdev, pad1;
    int64_t st_size; int32_t st_blksize, pad2; int64_t st_blocks;
    struct timespec st_atim, st_mtim, st_ctim; int32_t unused[2];
};
static void conv_stat(const struct stat *s, struct lx_stat *o) {
    memset(o, 0, sizeof *o);
    o->st_dev = s->st_dev; o->st_ino = s->st_ino; o->st_mode = s->st_mode; o->st_nlink = s->st_nlink;
    o->st_size = s->st_size; o->st_blksize = 4096; o->st_blocks = (s->st_size + 511) / 512;
    o->st_atim = s->st_atim; o->st_mtim = s->st_mtim; o->st_ctim = s->st_ctim;
}
int lx___xstat64(int v, const char *p, struct lx_stat *o) {
    if (is_synthetic(p)) { // /proc and /proc/<pid> are folders, the rest files
        memset(o, 0, sizeof *o);
        int dir = !strcmp(p, "/proc") || !strcmp(p, "/proc/self") || !strcmp(p, "/proc/42");
        if (!dir) { char b[1024]; if (strncmp(p, "/dev/", 5) && proc_content(p, b, sizeof b) == (size_t)-1) { errno = ENOENT; return -1; } }
        o->st_mode = dir ? 0040555 : 0100444; return 0;
    }
    s64 size; int dir;
    if (sc_get(fs_path(p), &size, &dir)) {
        memset(o, 0, sizeof *o);
        o->st_mode = dir ? 0040777 : 0100666; o->st_nlink = 1; o->st_size = dir ? 0 : size; o->st_blksize = 4096;
        o->st_blocks = (o->st_size + 511) / 512;
        FsTimeStampRaw ts;
        if (!dir && R_SUCCEEDED(fsFsGetFileTimeStampRaw(sdmc_fs(), fs_path(p), &ts)) && ts.is_valid) {
            o->st_mtim.tv_sec = nx_local_to_utc(ts.modified); o->st_atim.tv_sec = nx_local_to_utc(ts.accessed);
            o->st_ctim.tv_sec = nx_local_to_utc(ts.created);
        }
        return 0;
    }
    struct stat s; if (stat(p, &s)) return -1; conv_stat(&s, o); return 0;
}
int lx___fxstat64(int v, int fd, struct lx_stat *o) {
    struct stat s;
    if (fd <= 2) { memset(o, 0, sizeof *o); o->st_mode = 0020000; return 0; } // char device
    MemFile *mf = mem_of(fd);
    if (mf) { memset(o, 0, sizeof *o); o->st_mode = mf->urandom ? 0020444 : 0100444; o->st_size = mf->len; return 0; }
    if (fstat(fd, &s)) return -1; conv_stat(&s, o); return 0;
}

struct lx_dirent64 { uint64_t d_ino; int64_t d_off; uint16_t d_reclen; uint8_t d_type; char d_name[256]; };

// sdmc folders are read whole at opendir: deleting entries under an open FsDir moves its position, so .NET's recursive
// delete (unlinking while it enumerates) skipped entries. ponytail: ~800 bytes per entry while open.
typedef struct { DIR *d; struct lx_dirent64 e; int proc; int nx; FsDirectoryEntry *buf; s64 n, i; char base[PATH_MAX]; } lx_DIR;
lx_DIR *lx_opendir(const char *p) {
    if (!strcmp(p, "/proc")) { lx_DIR *r = calloc(1, sizeof *r); r->proc = 1; return r; } // one process: us (pid 42)
    if ((p[0] == '/' || !strncmp(p, "sdmc:/", 6)) && sdmc_fs()) {
        lx_DIR *r = calloc(1, sizeof *r);
        snprintf(r->base, sizeof r->base, "%s", fs_path(p));
        FsDir fd;
        if (R_FAILED(fsFsOpenDirectory(sdmc_fs(), r->base, FsDirOpenMode_ReadDirs | FsDirOpenMode_ReadFiles, &fd))) { free(r); errno = ENOENT; return NULL; }
        s64 cap = 0, got;
        for (;;) {
            if (r->n == cap) r->buf = realloc(r->buf, (cap = cap ? cap * 2 : 64) * sizeof *r->buf);
            if (R_FAILED(fsDirRead(&fd, &got, cap - r->n, r->buf + r->n)) || got <= 0) break;
            r->n += got;
        }
        fsDirClose(&fd);
        r->nx = 1; return r;
    }
    DIR *d = opendir(p); if (!d) return NULL; lx_DIR *r = calloc(1, sizeof *r); r->d = d; return r;
}
struct lx_dirent64 *lx_readdir64(lx_DIR *d) {
    if (d->proc) {
        if (d->proc++ > 1) return NULL;
        memset(&d->e, 0, sizeof d->e); d->e.d_type = 4; d->e.d_reclen = sizeof d->e; strcpy(d->e.d_name, "42"); return &d->e;
    }
    if (d->nx) {
        if (d->i >= d->n) return NULL;
        FsDirectoryEntry *f = &d->buf[d->i++];
        memset(&d->e, 0, sizeof d->e); d->e.d_reclen = sizeof d->e;
        d->e.d_type = f->type == FsDirEntryType_Dir ? 4 : 8;
        snprintf(d->e.d_name, sizeof d->e.d_name, "%s", f->name);
        char full[PATH_MAX + 260];
        snprintf(full, sizeof full, "%s%s%s", d->base, d->base[strlen(d->base) - 1] == '/' ? "" : "/", f->name);
        sc_put(full, f->file_size, f->type == FsDirEntryType_Dir);
        return &d->e;
    }
    struct dirent *e = readdir(d->d); if (!e) return NULL;
    memset(&d->e, 0, sizeof d->e); d->e.d_ino = e->d_ino; d->e.d_reclen = sizeof d->e;
    d->e.d_type = e->d_type == DT_DIR ? 4 : e->d_type == DT_REG ? 8 : 0;
    strncpy(d->e.d_name, e->d_name, 255); return &d->e;
}
int lx_closedir(lx_DIR *d) { int r = d->d ? closedir(d->d) : 0; free(d->buf); free(d); return r; }

// ---------------------------------------------------------------- process / system
int lx_getpid(void) { return 42; }
unsigned lx_geteuid(void) { return 0; }
// glibc struct passwd: .NET falls back to it for the home folder (UserProfile, the cache) when HOME is unset.
struct lx_passwd { char *name, *passwd; unsigned uid, gid; char *gecos, *dir, *shell; };
int lx_getpwuid_r(unsigned u, struct lx_passwd *pw, char *b, size_t n, void **res) {
    static const char home[] = "/switch/waddamburo";
    if (n < sizeof home + 12) { *res = NULL; return 34; } // ERANGE
    memcpy(b, home, sizeof home); strcpy(b + sizeof home, "switch");
    *pw = (struct lx_passwd){ b + sizeof home, "", 0, 0, "", b, "" };
    *res = pw; return 0;
}
int lx_kill(int pid, int sig) { NOSYS("kill"); }
int lx_fork(void) { NOSYS("fork"); }
int lx_execv(const char *p, char *const *a) { NOSYS("execv"); }
int lx_waitpid(int p, int *s, int o) { NOSYS("waitpid"); }
int lx_prctl(int o, ...) { return 0; }

long lx_sysconf(int n) {
    switch (n) {
    case 2: return 100;              // _SC_CLK_TCK
    case 4: return 1024;             // _SC_OPEN_MAX
    case 30: return PAGE;            // _SC_PAGESIZE
    case 83: case 84: return 3;      // _SC_NPROCESSORS_CONF/ONLN: cores 0-2 (3 is the system's)
    case 85: { u64 t = 0; svcGetInfo(&t, InfoType_TotalMemorySize, CUR_PROCESS_HANDLE, 0); return t / PAGE; }
    case 86: return nx_available() / PAGE;
    case 190: case 193: return 64;   // cache line sizes
    case 188: return 32 << 10;       // L1D
    case 191: return 2 << 20;        // L2 (Cortex-A57 cluster)
    default: return -1;
    }
}

struct lx_rlimit { uint64_t cur, max; };
int lx_getrlimit64(int r, struct lx_rlimit *l) { l->cur = l->max = ~0ull; if (r == 7) l->cur = l->max = 1024; return 0; }

struct lx_sysinfo { long uptime; unsigned long loads[3], totalram, freeram, sharedram, bufferram, totalswap, freeswap;
                    unsigned short procs, pad; unsigned long totalhigh, freehigh; unsigned mem_unit; char f[4]; };
int lx_sysinfo(struct lx_sysinfo *s) {
    memset(s, 0, sizeof *s); u64 t = 0, u = 0;
    svcGetInfo(&t, InfoType_TotalMemorySize, CUR_PROCESS_HANDLE, 0);
    svcGetInfo(&u, InfoType_UsedMemorySize, CUR_PROCESS_HANDLE, 0);
    s->totalram = t; s->freeram = nx_available(); s->mem_unit = 1; s->procs = 1; return 0;
}

unsigned long lx_getauxval(unsigned long t) {
    if (t == 16) return 0xFB;   // AT_HWCAP: fp asimd aes pmull sha1 sha2 crc32 (no LSE atomics on A57)
    if (t == 6) return PAGE;    // AT_PAGESZ
    return 0;
}

long lx_syscall(long n, ...) {
    if (n == 178) { u64 id = 0; svcGetThreadId(&id, CUR_THREAD_HANDLE); return (long)id; } // gettid
    errno = LX_ENOSYS; return -1; // membarrier etc.
}

// Image description for libunwind (dl_iterate_phdr) and module lookup (dladdr).
typedef struct { uint32_t p_type, p_flags; uint64_t p_offset, p_vaddr, p_paddr, p_filesz, p_memsz, p_align; } lx_phdr;
struct lx_dl_phdr_info { uintptr_t addr; const char *name; const lx_phdr *phdr; uint16_t phnum; uint64_t adds, subs; size_t tls_modid; void *tls_data; };
int lx_dl_iterate_phdr(int (*cb)(struct lx_dl_phdr_info *, size_t, void *), void *data) {
    static lx_phdr ph[2];
    ph[0].p_type = 1; ph[0].p_flags = 5; ph[0].p_memsz = ph[0].p_filesz = __end__ - __start__;
    ph[1].p_type = 0x6474e550; ph[1].p_vaddr = __eh_frame_hdr_start - __start__;
    ph[1].p_memsz = ph[1].p_filesz = __eh_frame_hdr_end - __eh_frame_hdr_start;
    struct lx_dl_phdr_info i = { (uintptr_t)__start__, "", ph, 2, 1, 0, 0, NULL };
    return cb(&i, sizeof i, data);
}
typedef struct { const char *fname; void *fbase; const char *sname; void *saddr; } lx_Dl_info;
int lx_dladdr(const void *a, lx_Dl_info *i) {
    if ((char *)a < __start__ || (char *)a >= __end__) return 0;
    i->fname = "waddamburo.nro"; i->fbase = __start__; i->sname = NULL; i->saddr = NULL; return 1;
}

// ---------------------------------------------------------------- time
static u64 mono_ns(void) { return armTicksToNs(armGetSystemTick()); }
int lx_clock_gettime(int clk, struct timespec *ts) {
    if (clk == 0 || clk == 5) return clock_gettime(CLOCK_REALTIME, ts);
    u64 ns = mono_ns(); ts->tv_sec = ns / 1000000000ull; ts->tv_nsec = ns % 1000000000ull; return 0;
}
static s64 ts_ns(const struct timespec *t) { return (s64)t->tv_sec * 1000000000ll + t->tv_nsec; }
int lx_nanosleep(const struct timespec *r, struct timespec *rem) { svcSleepThread(ts_ns(r)); return 0; }
int lx_clock_nanosleep(int clk, int fl, const struct timespec *r, struct timespec *rem) {
    s64 ns = ts_ns(r);
    if (fl & 1) { struct timespec now; lx_clock_gettime(clk, &now); ns -= ts_ns(&now); }
    if (ns > 0) svcSleepThread(ns);
    return 0;
}

// ---------------------------------------------------------------- signals
struct lx_sigaction { void *handler; uint64_t mask[16]; int flags; int pad; void *restorer; };
struct lx_sigaction g_sig[65];
int lx_sigaction(int s, const struct lx_sigaction *a, struct lx_sigaction *o) {
    if (s <= 0 || s > 64) { errno = EINVAL; return -1; }
    if (o) *o = g_sig[s];
    if (a) g_sig[s] = *a;
    return 0;
}
void *lx_signal(int s, void *h) { return NULL; }
int lx_sigemptyset(uint64_t *m) { memset(m, 0, 128); return 0; }
int lx_sigaddset(uint64_t *m, int s) { m[0] |= 1ull << (s - 1); return 0; }
int lx_pthread_sigmask(int h, const void *s, void *o) { if (o) memset(o, 0, 128); return 0; }
int lx___libc_current_sigrtmin(void) { return 34; }
int lx___libc_current_sigrtmax(void) { return 64; }

// ---------------------------------------------------------------- threads
#define LX_KEYS 64
typedef struct lx_thread {
    Thread thr; void *(*fn)(void *); void *arg;
    void *specific[LX_KEYS];
    uintptr_t stack_lo; size_t stack_size; Handle handle; u64 tp; void *huc;
    volatile int done; // its function returned: lx_pthread_create closes it
} lx_thread;
static __thread lx_thread *t_self;
static lx_thread g_main;
// The runtime's live threads, for exit to park. The thread pool retires idle workers and starts new ones: finished
// threads are closed at the next create (Horizon's thread limit ran out after a sync and an in-process restart).
static lx_thread **g_threads; static int g_nthreads, g_cthreads; static Mutex g_tl; static bool g_exiting;
static void (*g_key_dtor[LX_KEYS])(void *);
static int g_nkeys;

void *__aarch64_read_tp(void);
static void set_tpidr(void) { void *tp = __aarch64_read_tp(); __asm__ volatile("msr tpidr_el0, %0" ::"r"(tp)); }

static lx_thread *self(void) {
    if (!t_self) { // main thread (or a thread not created through us)
        lx_thread *t = &g_main; MemoryInfo mi; u32 pi; uintptr_t sp;
        __asm__("mov %0, sp" : "=r"(sp));
        svcQueryMemory(&mi, &pi, sp);
        t->stack_lo = mi.addr; t->stack_size = mi.size; t->handle = threadGetCurHandle(); t->tp = (u64)__aarch64_read_tp();
        t_self = t;
    }
    return t_self;
}

static void run_dtors(lx_thread *t) {
    for (int it = 0; it < 4; it++) {
        int any = 0;
        for (int k = 0; k < g_nkeys; k++) if (t->specific[k] && g_key_dtor[k]) {
            void *v = t->specific[k]; t->specific[k] = NULL; g_key_dtor[k](v); any = 1;
        }
        if (!any) break;
    }
}

static void thread_entry(void *p) {
    lx_thread *t = p;
    set_tpidr(); t_self = t;
    t->handle = threadGetCurHandle(); t->tp = (u64)__aarch64_read_tp();
    t->fn(t->arg);
    run_dtors(t);
    __atomic_store_n(&t->done, 1, __ATOMIC_RELEASE);
}

struct lx_attr { size_t stacksize; uintptr_t stackaddr; int detached; };
int lx_pthread_attr_init(struct lx_attr *a) { memset(a, 0, 64); return 0; }
int lx_pthread_attr_destroy(struct lx_attr *a) { return 0; }
int lx_pthread_attr_setstacksize(struct lx_attr *a, size_t s) { a->stacksize = s; return 0; }
int lx_pthread_attr_setdetachstate(struct lx_attr *a, int d) { a->detached = d; return 0; }
int lx_pthread_attr_getstack(const struct lx_attr *a, void **addr, size_t *size) { *addr = (void *)a->stackaddr; *size = a->stacksize; return 0; }
int lx_pthread_getattr_np(lx_thread *t, struct lx_attr *a) {
    memset(a, 0, 64);
    if (t == &g_main || !t->thr.stack_mirror) { self(); a->stackaddr = g_main.stack_lo; a->stacksize = g_main.stack_size; }
    else { a->stackaddr = (uintptr_t)t->thr.stack_mirror; a->stacksize = t->thr.stack_sz; }
    return 0;
}

int lx_pthread_create(lx_thread **out, const struct lx_attr *a, void *(*fn)(void *), void *arg) {
    lx_thread *t = calloc(1, sizeof *t);
    t->fn = fn; t->arg = arg;
    size_t ss = a && a->stacksize ? a->stacksize : (1 << 20);
    ss = (ss + 0xFFF) & ~0xFFFull;
    // Horizon does not time-slice equal-priority threads on a core: spread them over cores 0-2 and let them migrate.
    static int next_core; int core = __atomic_fetch_add(&next_core, 1, __ATOMIC_RELAXED) % 3;
    // Horizon schedules by strict priority with no time slicing between equal ones: runtime/worker threads go
    // below the main thread (0x2C) so they never starve frame pacing; the render thread is raised on its first present.
    Result rc = threadCreate(&t->thr, thread_entry, t, NULL, ss, 0x2D, core);
    if (R_FAILED(rc)) { fprintf(stderr, "[shim] threadCreate %x\n", rc); free(t); return EAGAIN; }
    svcSetThreadCoreMask(t->thr.handle, core, 7);
    // Listed before it starts, and none start once exit parks them (it holds the lock while it does).
    mutexLock(&g_tl);
    if (g_exiting) { mutexUnlock(&g_tl); return EAGAIN; }
    // Close the finished ones (all runtime threads are detached: nobody joins them).
    int kept = 0;
    for (int i = 0; i < g_nthreads; i++) {
        lx_thread *old = g_threads[i];
        if (__atomic_load_n(&old->done, __ATOMIC_ACQUIRE)) { threadWaitForExit(&old->thr); threadClose(&old->thr); free(old); }
        else g_threads[kept++] = old;
    }
    g_nthreads = kept;
    if (g_nthreads == g_cthreads) g_threads = realloc(g_threads, (g_cthreads = g_cthreads ? g_cthreads * 2 : 64) * sizeof *g_threads);
    g_threads[g_nthreads++] = t;
    rc = threadStart(&t->thr);
    mutexUnlock(&g_tl);
    if (R_FAILED(rc)) { fprintf(stderr, "[shim] threadStart %x\n", rc); return EAGAIN; }
    *out = t;
    return 0;
}
lx_thread *lx_pthread_self(void) { return self(); }
int lx_pthread_setname_np(lx_thread *t, const char *n) { return 0; }
int lx_pthread_key_create(unsigned *k, void (*d)(void *)) {
    if (g_nkeys >= LX_KEYS) return EAGAIN;
    g_key_dtor[g_nkeys] = d; *k = g_nkeys++; return 0;
}
int lx_pthread_setspecific(unsigned k, const void *v) { self()->specific[k] = (void *)v; return 0; }

// Mutexes: glibc's 48-byte storage holds libnx primitives; all-zero = unlocked normal mutex.
typedef struct { u32 type; u32 pad; union { Mutex m; RMutex rm; }; } lx_mutex;
int lx_pthread_mutexattr_init(int *a) { *a = 0; return 0; }
int lx_pthread_mutexattr_destroy(int *a) { return 0; }
int lx_pthread_mutexattr_settype(int *a, int t) { *a = t; return 0; }
int lx_pthread_mutex_init(lx_mutex *m, const int *a) { memset(m, 0, 40); m->type = a ? *a : 0; if (m->type == 1) rmutexInit(&m->rm); return 0; }
int lx_pthread_mutex_destroy(lx_mutex *m) { return 0; }
int lx_pthread_mutex_lock(lx_mutex *m) { if (m->type == 1) rmutexLock(&m->rm); else mutexLock(&m->m); return 0; }
int lx_pthread_mutex_unlock(lx_mutex *m) { if (m->type == 1) rmutexUnlock(&m->rm); else mutexUnlock(&m->m); return 0; }

typedef struct { CondVar cv; int clock; } lx_cond;
int lx_pthread_condattr_init(int *a) { *a = 0; return 0; }
int lx_pthread_condattr_destroy(int *a) { return 0; }
int lx_pthread_condattr_setclock(int *a, int c) { *a = c; return 0; }
int lx_pthread_cond_init(lx_cond *c, const int *a) { memset(c, 0, 48); c->clock = a ? *a : 0; return 0; }
int lx_pthread_cond_destroy(lx_cond *c) { return 0; }
int lx_pthread_cond_signal(lx_cond *c) { condvarWakeOne(&c->cv); return 0; }
int lx_pthread_cond_broadcast(lx_cond *c) { condvarWakeAll(&c->cv); return 0; }
int lx_pthread_cond_wait(lx_cond *c, lx_mutex *m) { condvarWait(&c->cv, &m->m); return 0; }
int lx_pthread_cond_timedwait(lx_cond *c, lx_mutex *m, const struct timespec *abs) {
    struct timespec now; lx_clock_gettime(c->clock, &now);
    s64 ns = ts_ns(abs) - ts_ns(&now);
    if (ns <= 0) return LX_ETIMEDOUT;
    Result rc = condvarWaitTimeout(&c->cv, &m->m, ns);
    return R_VALUE(rc) == KERNELRESULT(TimedOut) ? LX_ETIMEDOUT : 0;
}

typedef struct { RwLock l; } lx_rwlock;
int lx_pthread_rwlock_rdlock(lx_rwlock *l) { rwlockReadLock(&l->l); return 0; }
int lx_pthread_rwlock_wrlock(lx_rwlock *l) { rwlockWriteLock(&l->l); return 0; }
int lx_pthread_rwlock_unlock(lx_rwlock *l) {
    if (rwlockIsWriteLockHeldByCurrentThread(&l->l)) rwlockWriteUnlock(&l->l); else rwlockReadUnlock(&l->l);
    return 0;
}

int lx_sched_getaffinity(int pid, size_t n, uint64_t *set) { memset(set, 0, n); set[0] = 7; return 0; }
void *lx___sched_cpualloc(size_t count) { return calloc((count + 63) / 64, 8); } // glibc's CPU_ALLOC
void lx___sched_cpufree(void *set) { free(set); }
int lx___sched_cpucount(size_t n, const uint64_t *set) { int c = 0; for (size_t i = 0; i < n / 8; i++) c += __builtin_popcountll(set[i]); return c; }
int lx_sched_getcpu(void) { return svcGetCurrentProcessorNumber(); }
int lx_sched_yield(void) { svcSleepThread(0); return 0; }

#include <sqlite3.h>
char *lx_getcwd(char *b, size_t n);
static void sqlite_log(void *u, int code, const char *msg) { fprintf(stderr, "[sqlite] %d: %s\n", code, msg); }
// libnx's fstat fails on some SD files (real hardware, not Ryujinx): fall back to lseek for the size.
// stat by path fails on hardware too (SQLite stats the database to give its journal the same mode).
static int sqlite_stat(const char *p, struct stat *st) {
    if (!stat(p, st)) return 0;
    int e = errno;
    static int n; if (e != ENOENT && n++ < 5) fprintf(stderr, "[sqlite] stat(%s) failed errno %d: assuming a regular file\n", p, e);
    if (e == ENOENT) return -1;
    // libnx's stat opens the file for its size; Horizon refuses that while SQLite has it open for writing.
    memset(st, 0, sizeof *st);
    st->st_mode = S_IFREG | 0644; st->st_nlink = 1; st->st_blksize = 4096;
    return 0;
}
static int sqlite_fstat(int fd, struct stat *st) {
    if (!fstat(fd, st)) return 0;
    static int once; if (!once++) fprintf(stderr, "[sqlite] fstat(%d) failed errno %d: using lseek\n", fd, errno);
    memset(st, 0, sizeof *st);
    off_t cur = lseek(fd, 0, SEEK_CUR), end = lseek(fd, 0, SEEK_END);
    if (end < 0) return -1;
    lseek(fd, cur, SEEK_SET);
    st->st_mode = S_IFREG | 0644; st->st_size = end; st->st_nlink = 1; st->st_blksize = 4096;
    return 0;
}

void shim_init(void) {
    sqlite3_config(SQLITE_CONFIG_LOG, sqlite_log, NULL); // ponytail: debug aid while porting
    { sqlite3_vfs *v = sqlite3_vfs_find(NULL);
      int a = v ? v->xSetSystemCall(v, "fstat", (sqlite3_syscall_ptr)sqlite_fstat) : -1;
      int b = v ? v->xSetSystemCall(v, "stat", (sqlite3_syscall_ptr)sqlite_stat) : -1;
      if (v) v->xSetSystemCall(v, "getcwd", (sqlite3_syscall_ptr)lx_getcwd); // "/x", not "sdmc:/x"
      fprintf(stderr, "[sqlite] vfs %s, syscall overrides %d %d\n", v ? v->zName : "none", a, b); }
    { extern char **environ; extern char **lx_environ; lx_environ = environ;
      int n = 0; for (char **e = environ; e && *e; e++) { if (!strncmp(*e, "HOME=", 5)) fprintf(stderr, "[shim] env %s\n", *e); n++; }
      fprintf(stderr, "[shim] environ %p, %d entries\n", (void *)environ, n); }
    fprintf(stderr, "[shim] heap %s %lu MiB, malloc %lu MiB, setheap rc %x\n", g_heap_fixed ? "loader" : "own", (g_heap_orig ? g_heap_orig : MALLOC_HEAP) >> 20, MALLOC_HEAP >> 20, g_heap_rc);
    if (g_heap_orig && g_heap_orig < (1ull << 30))
        fprintf(stderr, "[shim] only %lu MiB: launch through title takeover (hold R while starting a game)\n", g_heap_orig >> 20);
    arena_init();
    fprintf(stderr, "[shim] GC pool %lx-%lx (%lu MiB), range %s\n", g_res_lo, g_res_hi, (g_res_hi - g_res_lo) >> 20, g_region_range);
    { MemoryInfo mi; u32 pi; svcQueryMemory(&mi, &pi, (u64)&shim_init); g_base = (char *)mi.addr; }
    lx_stderr = stderr;
    { u64 b=0,z=0; svcGetInfo(&b, InfoType_StackRegionAddress, CUR_PROCESS_HANDLE, 0); svcGetInfo(&z, InfoType_StackRegionSize, CUR_PROCESS_HANDLE, 0);
      fprintf(stderr, "[shim] stack region %lx+%lx heap %p image %p-%p ehhdr %p\n", b, z, g_heap_addr, __start__, __end__, __eh_frame_hdr_start); }
    set_tpidr();
    self();
}

// ---------------------------------------------------------------- hardware exceptions -> signals
typedef struct { u64 x[31], sp, pc, pstate, far, esr, desc, pad; __uint128_t v[32]; u32 fpsr, fpcr; u64 pad2; } lx_regs;
_Static_assert(sizeof(lx_regs) == 832, "lx_regs layout is shared with exc.S");
// glibc aarch64 ucontext_t / mcontext_t / siginfo_t
typedef struct { u32 magic, size; u32 fpsr, fpcr; __uint128_t vregs[32]; } lx_fpsimd;
typedef struct {
    u64 uc_flags; void *uc_link; void *ss_sp; int ss_flags, ss_pad; size_t ss_size; u64 sigmask[16];
    u64 pad_to_176;
    u64 fault_address, regs[31], sp, pc, pstate, pad_to_288;
    union { lx_fpsimd fp; unsigned char reserved[4096]; } __attribute__((aligned(16)));
} lx_ucontext;
_Static_assert(__builtin_offsetof(lx_ucontext, fault_address) == 176, "mcontext offset");
_Static_assert(__builtin_offsetof(lx_ucontext, fp) == 464, "__reserved offset");
typedef struct { int si_signo, si_errno, si_code, pad; void *si_addr; char rest[104]; } lx_siginfo;

volatile int g_exc_fatal;
int lx_exception(u32 desc, lx_regs *r) {
    u32 ec = (r->esr >> 26) & 0x3f;
    { static int n; if (n++ < 8) {
        MemoryInfo mi; u32 pi; svcQueryMemory(&mi, &pi, r->far);
        fprintf(stderr, "[exc] esr %lx far %lx pc +%lx lr +%lx sp %lx | block %lx+%lx type %x perm %x | GC pool %s\n",
                r->esr, r->far, r->pc - (u64)__start__, r->x[30] - (u64)__start__, r->sp, mi.addr, mi.size, mi.type, mi.perm,
                ours(r->far) ? "yes" : "no"); } }
    int sig = ec == 0x24 || ec == 0x25 ? 11 /*SIGSEGV*/ : ec == 0x00 ? 4 /*SIGILL*/ : ec == 0x3c ? 5 /*SIGTRAP*/ : 7 /*SIGBUS*/;
    struct lx_sigaction *sa = &g_sig[sig];
    if (!sa->handler || !(sa->flags & 4 /*SA_SIGINFO*/)) goto fatal;
    lx_ucontext uc; memset(&uc, 0, sizeof uc);
    uc.fault_address = r->far; memcpy(uc.regs, r->x, sizeof uc.regs);
    uc.sp = r->sp; uc.pc = r->pc; uc.pstate = r->pstate;
    uc.fp.magic = 0x46508001; uc.fp.size = sizeof uc.fp; uc.fp.fpsr = r->fpsr; uc.fp.fpcr = r->fpcr;
    memcpy(uc.fp.vregs, r->v, sizeof uc.fp.vregs);
    lx_siginfo si; memset(&si, 0, sizeof si);
    si.si_signo = sig; si.si_code = 1; si.si_addr = (void *)r->far;
    u64 old_pc = r->pc;
    ((void (*)(int, lx_siginfo *, void *))sa->handler)(sig, &si, &uc);
    if (uc.pc == old_pc) goto fatal; // handler did not redirect: it would fault again
    memcpy(r->x, uc.regs, sizeof uc.regs); r->sp = uc.sp; r->pc = uc.pc; r->pstate = uc.pstate;
    memcpy(r->v, uc.fp.vregs, sizeof r->v); r->fpsr = uc.fp.fpsr; r->fpcr = uc.fp.fpcr;
    return 0;
fatal:
    extern void log_flush(void);
    fprintf(stderr, "[shim] fatal exception desc %x esr %lx far %lx pc %lx (offset %lx) lr %lx\n",
            desc, r->esr, r->far, r->pc, r->pc - (u64)__start__, r->x[30]);
    { // the callers (frame pointers; image offsets for addr2line on sent.elf), within this thread's stack
        char buf[1200]; int k = snprintf(buf, sizeof buf, "[shim] callers: %lx", r->x[30] - (u64)g_base);
        u64 fp = r->x[29];
        MemoryInfo mi; u32 pi; svcQueryMemory(&mi, &pi, r->sp);
        for (int i = 0; i < 48 && fp >= mi.addr && fp + 16 <= mi.addr + mi.size && !(fp & 7) && k < 1170; i++) {
            u64 *frame = (u64 *)fp;
            k += snprintf(buf + k, sizeof buf - k, " %lx", frame[1] - (u64)g_base);
            if (frame[0] <= fp) break;
            fp = frame[0];
        }
        fprintf(stderr, "%s\n", buf);
    }
    log_flush();
    g_exc_fatal = 1; // resume unchanged: the re-fault goes to the kernel and Atmosphere reports the real PC
    return 1;
}

// ---------------------------------------------------------------- GC activation (pthread_kill(INJECT_ACTIVATION_SIGNAL))
// Horizon cannot run a signal handler on another thread, but it can pause one and read its registers.
// That is enough for the runtime's return-address hijack, which the Windows build also does from the
// suspending thread: Thread::HijackCallback(ctx, thread, doInlineSuspend=false).
// ponytail: no in-place suspension, so a managed loop that never returns or calls still blocks the GC.
// Upgrade path: make its code page non-executable so it faults into lx_exception and suspends inline.
void _ZN6Thread14HijackCallbackEP14NATIVE_CONTEXTPS_b(void *ctx, void *thread, bool inl);
void _ZN6Thread20SetActivationPendingEb(void *thread, bool pending);
extern __thread char tls_CurrentThread[];

int lx_pthread_kill(lx_thread *t, int sig) {
    if (sig < 34) { errno = EINVAL; return EINVAL; }
    void *rt_thread = (void *)(t->tp + ((u64)tls_CurrentThread - (u64)__aarch64_read_tp()));
    if (t == self() || !t->handle) { _ZN6Thread20SetActivationPendingEb(rt_thread, false); return 0; }
    if (!t->huc) t->huc = calloc(1, sizeof(lx_ucontext));  // persistent: the runtime may keep pointers into it
    lx_ucontext *uc = t->huc;
    Result rc = svcSetThreadActivity(t->handle, ThreadActivity_Paused);
    if (R_FAILED(rc)) { static int o; if (!o++) fprintf(stderr, "[shim] SetThreadActivity failed %x\n", rc); _ZN6Thread20SetActivationPendingEb(rt_thread, false); return 0; }
    ThreadContext c;
    for (int i = 0; i < 100 && R_FAILED(rc = svcGetThreadContext3(&c, t->handle)); i++) svcSleepThread(0);
    if (R_SUCCEEDED(rc)) {
        memset(uc, 0, sizeof *uc);
        for (int i = 0; i < 29; i++) uc->regs[i] = c.cpu_gprs[i].x;
        uc->regs[29] = c.fp; uc->regs[30] = c.lr; uc->sp = c.sp; uc->pc = c.pc.x; uc->pstate = c.psr;
        uc->fp.magic = 0x46508001; uc->fp.size = sizeof uc->fp; uc->fp.fpsr = c.fpsr; uc->fp.fpcr = c.fpcr;
        memcpy(uc->fp.vregs, c.fpu_gprs, sizeof uc->fp.vregs);
        _ZN6Thread14HijackCallbackEP14NATIVE_CONTEXTPS_b(uc, rt_thread, false);
        // A hijack of the return address lands on the stack (real memory). One aimed at LR in the
        // register file is lost; the runtime retries on its next suspension pass.
    }
    _ZN6Thread20SetActivationPendingEb(rt_thread, false);
    svcSetThreadActivity(t->handle, ThreadActivity_Runnable);
    // only now: the paused thread may have held the stdio lock
    if (R_FAILED(rc)) { static int once; if (!once++) fprintf(stderr, "[shim] GetThreadContext3 failed %x\n", rc); }
    return 0;
}

// Debug watchdog: every 5 s print where each thread is (symbolize with addr2line on out/app.elf).
static void dump_thread(const char *name, Handle h) {
    if (!h || h == threadGetCurHandle()) return;
    if (R_FAILED(svcSetThreadActivity(h, ThreadActivity_Paused))) return;
    ThreadContext c; Result rc; u64 frames[16] = {0}; int nf = 0;
    for (int i = 0; i < 100 && R_FAILED(rc = svcGetThreadContext3(&c, h)); i++) svcSleepThread(0);
    if (R_SUCCEEDED(rc)) { u64 fp = c.fp; for (; nf < 16 && fp && (fp & 7) == 0; nf++) { u64 *f = (u64 *)fp; frames[nf] = f[1]; fp = f[0]; } }
    svcSetThreadActivity(h, ThreadActivity_Runnable);
    // no stdio while the thread was paused: it may have held the stdio lock
    if (R_FAILED(rc)) { fprintf(stderr, "[wd] %s ctx failed %x\n", name, rc); return; }
    char buf[512]; int k = snprintf(buf, sizeof buf, "[wd] %s pc 0x%lx lr 0x%lx fp:", name, c.pc.x - (u64)g_base, c.lr - (u64)g_base);
    for (int i = 0; i < nf && k < 480; i++) k += snprintf(buf + k, sizeof buf - k, " 0x%lx", frames[i] - (u64)g_base);
    fprintf(stderr, "%s\n", buf);
}
// Sampling profiler: every ~1 ms pause each thread, record (pc, caller); every 10 s log the busiest threads' top
// locations as image offsets (symbolize with addr2line). Samples in kernel waits are counted as idle.
#define PROF_SLOTS 4096
typedef struct { u64 pc, lr; u32 n; } ProfHit;
typedef struct { Handle h; uintptr_t lo, hi; ProfHit hits[PROF_SLOTS]; u32 total, busy; } ProfThread;
static ProfThread g_prof[24];
static int prof_idle(u64 pc) { // libnx svc stubs: the thread is blocked, not working
    static const void *stubs[] = { (void *)svcWaitSynchronization, (void *)svcWaitProcessWideKeyAtomic, (void *)svcSleepThread,
                                   (void *)svcArbitrateLock }; // IPC (svcSendSyncRequest) counts as busy
    for (size_t i = 0; i < sizeof stubs / sizeof *stubs; i++) if (pc >= (u64)stubs[i] && pc < (u64)stubs[i] + 16) return 1;
    return 0;
}
static void prof_sample(ProfThread *t) {
    if (!t->h || t->h == threadGetCurHandle() || R_FAILED(svcSetThreadActivity(t->h, ThreadActivity_Paused))) return;
    ThreadContext c; Result rc = svcGetThreadContext3(&c, t->h);
    u64 lr = R_SUCCEEDED(rc) ? c.lr : 0; // the LR register: x29 is not a frame pointer in C code built without one
    svcSetThreadActivity(t->h, ThreadActivity_Runnable);
    if (R_FAILED(rc)) return;
    t->total++;
    if (prof_idle(c.pc.x)) return;
    t->busy++;
    u64 key = c.pc.x ^ (lr << 7); u32 i = (u32)(key * 0x9E3779B97F4A7C15ull >> 52) % PROF_SLOTS;
    for (int k = 0; k < 64; k++, i = (i + 1) % PROF_SLOTS) {
        ProfHit *h = &t->hits[i];
        if (!h->n) { h->pc = c.pc.x; h->lr = lr; h->n = 1; return; }
        if (h->pc == c.pc.x && h->lr == lr) { h->n++; return; }
    }
}
static void prof_dump(int idx, ProfThread *t) {
    if (t->busy < t->total / 10 || t->busy < 200) return; // mostly idle: skip
    char buf[640]; int k = snprintf(buf, sizeof buf, "[prof] t%d busy %u/%u:", idx, t->busy, t->total);
    for (int r = 0; r < 12 && k < 600; r++) { // top 12 by repeated max search (ponytail: fine every 10 s)
        ProfHit *best = NULL; for (int i = 0; i < PROF_SLOTS; i++) if (t->hits[i].n && (!best || t->hits[i].n > best->n)) best = &t->hits[i];
        if (!best) break;
        k += snprintf(buf + k, sizeof buf - k, " %u@%lx<%lx", best->n, best->pc - (u64)g_base, best->lr ? best->lr - (u64)g_base : 0);
        best->n = 0;
    }
    fprintf(stderr, "%s\n", buf);
    if (t->busy * 10 < t->total * 3 || !t->hi || R_FAILED(svcSetThreadActivity(t->h, ThreadActivity_Paused))) return; // >= 30% busy
    // one stack walk of a busy thread: frame records checked to lie inside its own stack (C code has no frame pointers)
    ThreadContext c; u64 frames[24]; int nf = 0;
    if (R_SUCCEEDED(svcGetThreadContext3(&c, t->h))) {
        frames[nf++] = c.pc.x; frames[nf++] = c.lr;
        for (u64 fp = c.fp; nf < 24 && fp >= t->lo && fp + 16 <= t->hi && !(fp & 7); ) { u64 *f = (u64 *)fp; frames[nf++] = f[1]; if (f[0] <= fp) break; fp = f[0]; }
    }
    svcSetThreadActivity(t->h, ThreadActivity_Runnable);
    k = snprintf(buf, sizeof buf, "[prof] t%d stack:", idx);
    for (int i = 0; i < nf && k < 600; i++) k += snprintf(buf + k, sizeof buf - k, " %lx", frames[i] - (u64)g_base);
    fprintf(stderr, "%s\n", buf);
}
// Hitch profile: the main thread's stacks (blocked ones too) during the frames that presented late. Samples collect
// per frame; nx_present_stats hands over each present gap, and a long one adds its samples to the table: addresses by
// how many samples had them anywhere on the stack (inclusive), and the pc alone (leaf). hitch.sh symbolizes.
#define HITCH_NS 25000000ull
#define HS 600
#define HD 40
typedef struct { struct { u8 n; u32 f[HD]; } s[HS]; int n; struct { u32 addr, incl, leaf; } t[8192]; u32 total, frames, seen; } Hitch;
static Hitch g_hitch[2]; // 0 the main thread, 1 bgfx's render thread
static Thread *g_render_thread;
void hitch_render_thread(void) { g_render_thread = threadGetSelf(); }
static _Atomic u64 g_gap; static _Atomic u32 g_gap_seq;
void hitch_present(u64 gap) { g_gap = gap; g_gap_seq++; }
static void hitch_add(Hitch *h, u32 a, int leaf) {
    u32 i = (a * 2654435761u) >> 19;
    for (int k = 0; k < 64; k++, i = (i + 1) & 8191) {
        if (h->t[i].addr && h->t[i].addr != a) continue;
        h->t[i].addr = a; if (leaf) h->t[i].leaf++; else h->t[i].incl++; return;
    }
}
static void hitch_sample(Hitch *h, ProfThread *t) {
    if (g_gap_seq != h->seen) { // a frame was presented since the last sample
        h->seen = g_gap_seq;
        if (g_gap >= HITCH_NS) {
            h->frames++;
            for (int s = 0; s < h->n; s++) {
                h->total++; hitch_add(h, h->s[s].f[0], 1);
                for (int i = 0; i < h->s[s].n; i++) {
                    int dup = 0; for (int j = 0; j < i && !dup; j++) dup = h->s[s].f[j] == h->s[s].f[i];
                    if (!dup) hitch_add(h, h->s[s].f[i], 0);
                }
            }
        }
        h->n = 0;
    }
    if (h->n == HS || !t->h || R_FAILED(svcSetThreadActivity(t->h, ThreadActivity_Paused))) return;
    ThreadContext c; int n = 0; u32 *f = h->s[h->n].f;
    if (R_SUCCEEDED(svcGetThreadContext3(&c, t->h))) {
        f[n++] = c.pc.x - (u64)g_base; f[n++] = c.lr - (u64)g_base;
        for (u64 fp = c.fp; n < HD && fp >= t->lo && fp + 16 <= t->hi && !(fp & 7); ) { u64 *r = (u64 *)fp; f[n++] = r[1] - (u64)g_base; if (r[0] <= fp) break; fp = r[0]; }
    }
    svcSetThreadActivity(t->h, ThreadActivity_Runnable);
    if (n) h->s[h->n++].n = n;
}
static void hitch_dump(void) {
    fprintf(stderr, "[mem] 10 s: %u commits %lu MiB %.1f ms, %u decommits %lu MiB %.1f ms; %lu MiB committed of %lu\n", g_commits, g_commit_bytes >> 20, g_commit_ns / 1e6,
            g_decommits, g_decommit_bytes >> 20, g_decommit_ns / 1e6, g_committed >> 20, (g_res_hi - g_res_lo) >> 20);
    g_commits = g_decommits = 0; g_commit_ns = g_decommit_ns = g_commit_bytes = g_decommit_bytes = 0;
    for (int th = 0; th < 2; th++) {
        Hitch *h = &g_hitch[th];
        if (!h->total) continue;
        fprintf(stderr, "[hitch] %u frames, %u samples (%s thread)\n", h->frames, h->total, th ? "render" : "main");
        for (int leaf = 0; leaf < 2; leaf++) {
            char buf[700]; int k = 0, rows = leaf ? 40 : 160;
            for (int r = 0; r < rows; r++) {
                int b = -1; u32 bn = 1 + h->total / 400;
                for (int i = 0; i < 8192; i++) { u32 v = leaf ? h->t[i].leaf : h->t[i].incl; if (v > bn) { bn = v; b = i; } }
                if (b >= 0) { k += snprintf(buf + k, sizeof buf - k, " %u@%x", bn, h->t[b].addr); if (leaf) h->t[b].leaf = 0; else h->t[b].incl = 0; }
                if (k > 640 || ((b < 0 || r == rows - 1) && k)) { fprintf(stderr, "[hitch] %s:%s\n", leaf ? "leaf" : "incl", buf); k = 0; }
                if (b < 0) break;
            }
        }
        memset(h->t, 0, sizeof h->t); h->total = h->frames = 0;
    }
}
static void watchdog(void *arg) {
    u64 start = armTicksToNs(armGetSystemTick());
    for (;;) {
        // threads to sample: main + ours (index 0 = main)
        mutexLock(&g_tl); int n = g_nthreads < 23 ? g_nthreads : 23; mutexUnlock(&g_tl);
        g_prof[0].h = g_main.handle; g_prof[0].lo = g_main.stack_lo; g_prof[0].hi = g_main.stack_lo + g_main.stack_size;
        for (int i = 0; i < n; i++) {
            Thread *th = &g_threads[i]->thr;
            g_prof[i + 1].h = g_threads[i]->handle;
            g_prof[i + 1].lo = (uintptr_t)th->stack_mirror; g_prof[i + 1].hi = (uintptr_t)th->stack_mirror + th->stack_sz;
        }
        svcSleepThread(1000000); // 1 ms
        hitch_sample(&g_hitch[0], &g_prof[0]);
        if (g_render_thread) {
            static ProfThread rt; rt.h = g_render_thread->handle; rt.lo = (uintptr_t)g_render_thread->stack_mirror; rt.hi = rt.lo + g_render_thread->stack_sz;
            hitch_sample(&g_hitch[1], &rt);
        }
        for (int i = 0; i <= n; i++) prof_sample(&g_prof[i]);
        u64 now = armTicksToNs(armGetSystemTick());
        if (now - start >= 10000000000ull) {
            start = now;
            hitch_dump();
            for (int i = 0; i <= n; i++) { prof_dump(i, &g_prof[i]); memset(g_prof[i].hits, 0, sizeof g_prof[i].hits); g_prof[i].total = g_prof[i].busy = 0; }
        }
    }
}
static Thread g_wd;
void shim_start_watchdog(void) { threadCreate(&g_wd, watchdog, NULL, NULL, 0x10000, 0x2B, -2); threadStart(&g_wd); }

// The runtime's threads (GC, finalizer, ...) never exit, but hbloader reuses this process for hbmenu: libnx thread
// stacks are heap pages aliased into the stack region until threadClose. Pause each thread for good and give its
// stack pages back. ponytail: handles leak (a handful per run).
static void park(Thread *t) {
    if (!t->handle || t->handle == threadGetCurHandle()) return;
    svcSetThreadActivity(t->handle, ThreadActivity_Paused);
    size_t sz = ((uintptr_t)t->stack_sz + 0x1000 + 0xFFF) & ~0xFFFull; // stack + tls/reent, as threadCreate maps it
    MemoryInfo mi; u32 pi; svcQueryMemory(&mi, &pi, (u64)t->stack_mirror);
    if (mi.addr == (u64)t->stack_mirror) sz = mi.size;
    svcUnmapMemory(t->stack_mirror, t->stack_mem, sz);
}
// newlib pthreads (FFmpeg's decoders, bx): tracked through link-time wraps of libnx's thread hooks, so exit can
// park them too (they would keep running inside hbmenu, on stacks hbmenu reuses).
static Thread *g_posix[64]; static Mutex g_posix_lock;
int __real___syscall_thread_create(void **t, void *(*f)(void *), void *a, void *sa, size_t ss);
int __wrap___syscall_thread_create(void **t, void *(*f)(void *), void *a, void *sa, size_t ss) {
    int r = __real___syscall_thread_create(t, f, a, sa, ss);
    if (!r && *t) { mutexLock(&g_posix_lock); for (int i = 0; i < 64; i++) if (!g_posix[i]) { g_posix[i] = *t; break; } mutexUnlock(&g_posix_lock); }
    return r;
}
void *__real___syscall_thread_join(void *t);
void *__wrap___syscall_thread_join(void *t) {
    mutexLock(&g_posix_lock); for (int i = 0; i < 64; i++) if (g_posix[i] == t) g_posix[i] = NULL; mutexUnlock(&g_posix_lock);
    return __real___syscall_thread_join(t);
}
static void park_threads(void) {
    park(&g_wd);
    mutexLock(&g_tl);
    g_exiting = true;
    for (int i = 0; i < g_nthreads; i++) park(&g_threads[i]->thr);
    mutexUnlock(&g_tl);
    for (int i = 0; i < 64; i++) if (g_posix[i]) park(g_posix[i]); // first member of struct __pthread_t is the Thread
}

// newlib has memalign but no posix_memalign (bx's aligned allocator calls it).
int posix_memalign(void **out, size_t align, size_t size) { void *p = memalign(align, size); if (!p) return ENOMEM; *out = p; return 0; }

// ---------------------------------------------------------------- the full app's extra imports
#include <sys/types.h>
// newlib errno numbers above 34 differ from glibc's; translate when the runtime reads errno.
static int lx_errno(int e) {
    switch (e) {
    case 88: return 38;   // ENOSYS
    case 90: return 39;   // ENOTEMPTY
    case 91: return 36;   // ENAMETOOLONG
    case 92: return 40;   // ELOOP
    case 116: return 110; // ETIMEDOUT
    case 134: return 95;  // ENOTSUP
    // Sockets (newlib 104-128 -> glibc 88-115; 104, 105, 111 agree).
    case 106: return 97;  // EAFNOSUPPORT
    case 107: return 91;  // EPROTOTYPE
    case 108: return 88;  // ENOTSOCK
    case 109: return 92;  // ENOPROTOOPT
    case 110: return 108; // ESHUTDOWN
    case 112: return 98;  // EADDRINUSE
    case 113: return 103; // ECONNABORTED
    case 114: return 101; // ENETUNREACH
    case 115: return 100; // ENETDOWN
    case 117: return 112; // EHOSTDOWN
    case 118: return 113; // EHOSTUNREACH
    case 119: return 115; // EINPROGRESS
    case 120: return 114; // EALREADY
    case 121: return 89;  // EDESTADDRREQ
    case 122: return 90;  // EMSGSIZE
    case 123: return 93;  // EPROTONOSUPPORT
    case 124: return 94;  // ESOCKTNOSUPPORT
    case 125: return 99;  // EADDRNOTAVAIL
    case 126: return 102; // ENETRESET
    case 127: return 106; // EISCONN
    case 128: return 107; // ENOTCONN
    default: return e;
    }
}

int lx_access(const char *p, int m) { return access(p, m); }
int lx_chdir(const char *p) { return chdir(p); }
int lx_chmod(const char *p, unsigned m) { return 0; }
int lx_fchmod(int fd, unsigned m) { return 0; }
int lx_mkdir(const char *p, unsigned m) { sc_drop(fs_path(p)); return mkdir(p, m); }
// libnx reports a folder that is not empty as EIO; .NET's recursive delete first tries rmdir and only empties the
// folder on ENOTEMPTY.
int lx_rmdir(const char *p) {
    sc_drop(fs_path(p));
    int r = rmdir(p);
    if (r && errno == EIO && sdmc_fs()) {
        char path[FS_MAX_PATH]; snprintf(path, sizeof path, "%s", fs_path(p));
        FsDir d; s64 n = 0;
        if (R_SUCCEEDED(fsFsOpenDirectory(sdmc_fs(), path, FsDirOpenMode_ReadDirs | FsDirOpenMode_ReadFiles, &d))) {
            if (R_SUCCEEDED(fsDirGetEntryCount(&d, &n)) && n > 0) errno = ENOTEMPTY;
            fsDirClose(&d);
        }
    }
    return r;
}
int lx_rename(const char *a, const char *b) { // Linux rename replaces the target; libnx refuses
    sc_drop(fs_path(a)); sc_drop(fs_path(b));
    if (!rename(a, b)) return 0;
    struct stat s; if (errno != EEXIST && stat(b, &s)) return -1;
    if (!strcmp(a, b)) return 0;
    unlink(b); return rename(a, b);
}
int lx_link(const char *a, const char *b) { errno = EPERM; return -1; }
#define SELF_EXE "/switch/waddamburo/waddamburo.nro" // Environment.ProcessPath / AppContext.BaseDirectory
ssize_t lx_readlink(const char *p, char *b, size_t n) {
    if (!strcmp(p, "/proc/self/exe")) { size_t l = strlen(SELF_EXE); if (l > n) l = n; memcpy(b, SELF_EXE, l); return l; }
    errno = EINVAL; return -1; // no symlinks: "not a link"
}
char *lx_realpath(const char *p, char *out) {
    // ponytail: no symlinks on the SD card, so the absolute path is the real path (no ./.. folding beyond the cwd join)
    char *r = out ? out : malloc(PATH_MAX);
    if (!strcmp(p, "/proc/self/exe")) { snprintf(r, PATH_MAX, "%s", SELF_EXE); return r; }
    if (p[0] == '/' || strchr(p, ':')) snprintf(r, PATH_MAX, "%s", p);
    else { char cwd[PATH_MAX]; lx_getcwd(cwd, sizeof cwd); snprintf(r, PATH_MAX, "%s/%s", cwd, p); }
    struct stat s; if (stat(r, &s)) { if (!out) free(r); return NULL; }
    return r;
}
int lx_fsync(int fd) { return fsync(fd); }
int lx_ftruncate64(int fd, long len) { return ftruncate(fd, len); }
int lx_fallocate64(int fd, int mode, long off, long len) { errno = 134; return -1; }
int lx_posix_fadvise64(int fd, long off, long len, int adv) { return 0; }
int lx_futimens(int fd, const struct timespec t[2]) { return 0; }
int lx_utimensat(int dirfd, const char *p, const struct timespec t[2], int fl) { return 0; }
int lx___lxstat64(int v, const char *p, struct lx_stat *o) { return lx___xstat64(v, p, o); } // no symlinks
int lx_msync(void *a, size_t l, int f) { return 0; }
ssize_t lx_pwrite64(int fd, const void *b, size_t n, long off) {
    long cur = lseek(fd, 0, SEEK_CUR); if (lseek(fd, off, SEEK_SET) < 0) return -1;
    ssize_t r = write(fd, b, n); lseek(fd, cur, SEEK_SET); return r;
}
struct lx_iovec { void *base; size_t len; };
ssize_t lx_preadv64(int fd, const struct lx_iovec *v, int n, long off) {
    ssize_t t = 0;
    for (int i = 0; i < n; i++) { ssize_t r = lx_pread64(fd, v[i].base, v[i].len, off + t); if (r < 0) return t ? t : -1; t += r; if ((size_t)r < v[i].len) break; }
    return t;
}
ssize_t lx_pwritev64(int fd, const struct lx_iovec *v, int n, long off) {
    ssize_t t = 0;
    for (int i = 0; i < n; i++) { ssize_t r = lx_pwrite64(fd, v[i].base, v[i].len, off + t); if (r < 0) return t ? t : -1; t += r; }
    return t;
}
ssize_t lx_sendfile64(int out, int in, long *off, size_t n) { NOSYS("sendfile64"); } // .NET falls back to read/write
int lx_shm_open(const char *n, int f, unsigned m) { NOSYS("shm_open"); }
int lx_shm_unlink(const char *n) { NOSYS("shm_unlink"); }

void *lx_realloc(void *p, size_t n) { return realloc(p, n); }
void lx_qsort(void *b, size_t n, size_t s, int (*c)(const void *, const void *)) { qsort(b, n, s, c); }
void *lx_memchr(const void *s, int c, size_t n) { return memchr(s, c, n); }
int lx_bcmp(const void *a, const void *b, size_t n) { return memcmp(a, b, n); }
int lx_strncasecmp(const char *a, const char *b, size_t n) { return strncasecmp(a, b, n); }
size_t lx_strnlen(const char *s, size_t n) { return strnlen(s, n); }
double lx_sqrt(double x) { return sqrt(x); }
time_t lx_mktime(struct tm *t) { return mktime(t); }
time_t lx_timegm(struct tm *t) { // UTC: days from the civil date (Howard Hinnant's algorithm)
    int y = t->tm_year + 1900, m = t->tm_mon + 1; y -= m <= 2;
    long era = (y >= 0 ? y : y - 399) / 400; unsigned yoe = y - era * 400;
    unsigned doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + t->tm_mday - 1, doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    long days = era * 146097 + (long)doe - 719468;
    return days * 86400 + t->tm_hour * 3600 + t->tm_min * 60 + t->tm_sec;
}
void lx__exit(int c) { _exit(c); }
void lx___clear_cache(void *a, void *b) {} // no JIT: nothing is ever written as code
char **lx_environ;
struct lx_utsname { char sysname[65], nodename[65], release[65], version[65], machine[65], domainname[65]; };
int lx_uname(struct lx_utsname *u) {
    memset(u, 0, sizeof *u); strcpy(u->sysname, "Horizon"); strcpy(u->nodename, "switch");
    strcpy(u->release, "1.0"); strcpy(u->version, "libnx"); strcpy(u->machine, "aarch64"); return 0;
}
int lx_gethostname(char *b, size_t n) { snprintf(b, n, "switch"); return 0; }
unsigned lx_getegid(void) { return 0; }
int lx_getgroups(int n, unsigned *g) { return 0; }
int lx_getgrouplist(const char *u, unsigned g, unsigned *gs, int *n) { *n = 0; return 0; }
int lx_setuid(unsigned u) { return 0; }
int lx_setgid(unsigned g) { return 0; }
int lx_setgroups(size_t n, const unsigned *g) { return 0; }
int lx_getpwnam_r(const char *n, void *pw, char *b, size_t s, void **res) { *res = NULL; return 0; }
int lx_execve(const char *p, char *const *a, char *const *e) { NOSYS("execve"); }
int lx_vfork(void) { NOSYS("vfork"); }
int lx_waitid(int t, int id, void *i, int o) { NOSYS("waitid"); }
int lx_sigfillset(uint64_t *m) { memset(m, 0xff, 128); return 0; }
int lx_pthread_setcancelstate(int s, int *o) { if (o) *o = 0; return 0; }
int lx_pthread_once(int *once, void (*fn)(void)) {
    static Mutex m; // ponytail: one lock for all once-controls (they run rarely)
    if (__atomic_load_n(once, __ATOMIC_ACQUIRE) == 2) return 0;
    mutexLock(&m);
    if (*once != 2) { fn(); __atomic_store_n(once, 2, __ATOMIC_RELEASE); }
    mutexUnlock(&m); return 0;
}

// dynamic loading: none on Horizon (DirectPInvoke binds Waddamburo's libs statically; ImGui/Realm stay unavailable)
void *lx_dlopen(const char *p, int f) { return NULL; }
void *lx_dlsym(void *h, const char *s) { return NULL; }
int lx_dlclose(void *h) { return 0; }
char *lx_dlerror(void) { return "dynamic loading is not available on Switch"; }

// inotify / epoll / sockets: not yet (networking off). ponytail: libnx has BSD sockets; needs sockaddr/constant translation.
int lx_inotify_init1(int f) { NOSYS("inotify_init1"); }
int lx_inotify_add_watch(int fd, const char *p, unsigned m) { NOSYS("inotify_add_watch"); }
int lx_inotify_rm_watch(int fd, int w) { NOSYS("inotify_rm_watch"); }
// epoll: .NET's socket engine needs one at startup even when every socket() fails (no networking yet). It
// accepts registrations and never reports events. ponytail: real readiness when sockets come (libnx poll()).
int lx_getnameinfo(const void *a, unsigned l, char *h, unsigned hl, char *s, unsigned sl, int f) { return -4; }

// SQLite is built without extension loading; SQLitePCLRaw still binds these two.
int lx_sqlite3_enable_load_extension(void *db, int on) { return 1; } // SQLITE_ERROR
int lx_sqlite3_load_extension(void *db, const char *f, const char *p, char **err) { return 1; }

// newlib declares these but libnx has no implementation (SQLite's unix VFS calls them): single user, no owners.
uid_t geteuid(void) { return 0; }
int fchown(int fd, uid_t u, gid_t g) { return 0; }

// SQLite (built with USE_PREAD) reads and writes through these. libnx's filesystem fails a read past the end of a
// file (SQLite then reports a corrupt database) and may refuse a write past it: clamp the one, extend for the other.
ssize_t pread(int fd, void *b, size_t n, off_t off) {
    struct stat s;
    if (!sqlite_fstat(fd, &s) && off >= s.st_size) return 0;
    if (lseek(fd, off, SEEK_SET) < 0) return -1;
    return read(fd, b, n);
}
ssize_t pwrite(int fd, const void *b, size_t n, off_t off) {
    struct stat s;
    if (!sqlite_fstat(fd, &s) && off > s.st_size && ftruncate(fd, off)) return -1;
    if (lseek(fd, off, SEEK_SET) < 0) return -1;
    return write(fd, b, n);
}


// ---------------------------------------------------------------- GPU buffer allocator
// libdrm_nouveau backs every buffer object with memalign (renamed to these in link-inner.sh). Mesa frees and
// reallocates big, page-aligned blocks every frame; newlib's malloc could not reuse the aligned holes and grew until
// it ran out (black frames). Here: first-fit with coalescing over a dedicated region. ponytail: O(n) lists.
typedef struct { uintptr_t at; size_t len; } Span;
#define GPU_SLOTS 16384 // a buffer per texture: 4096 ran out at ~5700 textures (textures came out broken)
static Span g_gfree[GPU_SLOTS], g_gused[GPU_SLOTS]; static int g_ngfree = -1, g_ngused; static Mutex g_glock;
static size_t g_gpu_in_use;
void *nx_bo_memalign(size_t align, size_t size) {
    if (align < 0x1000) align = 0x1000;
    size = (size + 0xfff) & ~0xfffull;
    mutexLock(&g_glock);
    if (g_ngfree < 0) { g_gfree[0] = (Span){ g_gpu_base, GPU_HEAP }; g_ngfree = 1; }
    for (int i = 0; i < g_ngfree; i++) {
        uintptr_t a = (g_gfree[i].at + align - 1) & ~(align - 1), end = g_gfree[i].at + g_gfree[i].len;
        if (a + size > end || g_ngused == GPU_SLOTS || g_ngfree >= GPU_SLOTS - 1) continue;
        Span f = g_gfree[i];
        g_gfree[i] = g_gfree[--g_ngfree];                                     // take the span out
        if (a > f.at) g_gfree[g_ngfree++] = (Span){ f.at, a - f.at };          // head left by alignment
        if (a + size < end) g_gfree[g_ngfree++] = (Span){ a + size, end - a - size };
        g_gused[g_ngused++] = (Span){ a, size }; g_gpu_in_use += size;
        mutexUnlock(&g_glock);
        return (void *)a;
    }
    mutexUnlock(&g_glock);
    static int once; if (!once++) fprintf(stderr, "[gpu] out of buffer memory (%zu MiB in use), falling back to malloc\n", g_gpu_in_use >> 20);
    return memalign(align, size);
}
void nx_bo_free(void *p) {
    uintptr_t a = (uintptr_t)p;
    if (a < g_gpu_base || a >= g_gpu_base + GPU_HEAP) { free(p); return; }
    mutexLock(&g_glock);
    for (int i = 0; i < g_ngused; i++) if (g_gused[i].at == a) {
        Span s = g_gused[i]; g_gused[i] = g_gused[--g_ngused]; g_gpu_in_use -= s.len;
        for (int j = 0; j < g_ngfree; ) { // coalesce with neighbours
            if (g_gfree[j].at + g_gfree[j].len == s.at) { s.at = g_gfree[j].at; s.len += g_gfree[j].len; g_gfree[j] = g_gfree[--g_ngfree]; continue; }
            if (s.at + s.len == g_gfree[j].at) { s.len += g_gfree[j].len; g_gfree[j] = g_gfree[--g_ngfree]; continue; }
            j++;
        }
        g_gfree[g_ngfree++] = s;
        break;
    }
    mutexUnlock(&g_glock);
}
size_t nx_gpu_in_use(void) { return g_gpu_in_use; }
int nx_gpu_free_spans(void) { return g_ngfree; }
int nx_gpu_buffers(void) { return g_ngused; }
// A limit of the kernel's own shows as these failing (nouveau then draws without the texture).
Result __real_nvMapCreate(NvMap *m, void *a, u32 size, u32 align, NvKind k, bool c);
Result __wrap_nvMapCreate(NvMap *m, void *a, u32 size, u32 align, NvKind k, bool c) {
    Result r = __real_nvMapCreate(m, a, size, align, k, c);
    static int n; if (R_FAILED(r) && n++ < 5) fprintf(stderr, "[gpu] nvMapCreate failed %x with %d buffers, %zu MiB\n", r, g_ngused, g_gpu_in_use >> 20);
    return r;
}
Result __real_nvAddressSpaceMap(NvAddressSpace *a, u32 h, bool g, NvKind k, iova_t *o);
Result __wrap_nvAddressSpaceMap(NvAddressSpace *a, u32 h, bool g, NvKind k, iova_t *o) {
    Result r = __real_nvAddressSpaceMap(a, h, g, k, o);
    static int n; if (R_FAILED(r) && n++ < 5) fprintf(stderr, "[gpu] nvAddressSpaceMap failed %x with %d buffers, %zu MiB\n", r, g_ngused, g_gpu_in_use >> 20);
    return r;
}


// FreeType streams faces from their file (FT_New_Face): every glyph becomes small freads, each an FS IPC on the
// SD card. Its fopen (renamed in link-inner.sh) loads font files once into memory instead.
FILE *nx_font_fopen(const char *path, const char *mode) {
    const char *dot = strrchr(path, '.');
    if (!dot || (strcasecmp(dot, ".ttf") && strcasecmp(dot, ".otf") && strcasecmp(dot, ".ttc"))) return fopen(path, mode);
    static struct { char *path; void *data; size_t size; } cache[16]; static Mutex m;
    mutexLock(&m);
    int i = 0;
    for (; i < 16 && cache[i].path; i++) if (!strcmp(cache[i].path, path)) break;
    if (i < 16 && !cache[i].path) {
        FILE *f = fopen(path, "rb");
        if (!f) { mutexUnlock(&m); return NULL; }
        fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
        void *d = malloc(n > 0 ? n : 1);
        if (fread(d, 1, n, f) != (size_t)n) { fclose(f); free(d); mutexUnlock(&m); return fopen(path, mode); }
        fclose(f);
        cache[i].path = strdup(path); cache[i].data = d; cache[i].size = n;
        fprintf(stderr, "[font] %s in memory (%ld KiB)\n", path, n >> 10);
    }
    mutexUnlock(&m);
    return i < 16 ? fmemopen(cache[i].data, cache[i].size, "rb") : fopen(path, mode);
}

size_t nx_malloc_extent(void) { extern char *fake_heap_start; return (char *)sbrk(0) - fake_heap_start; }
size_t nx_available(void) {
    return g_res_hi - g_res_lo - g_committed; // the pool is the GC's alone
}
