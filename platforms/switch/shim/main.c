#define _GNU_SOURCE
#include <switch.h>
#include <stdio.h>
#include <unistd.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
int lx_dotnet_main(int argc, char **argv); // the app: the NativeAOT exe bootstrapper (its main, renamed by scripts/link.sh)
int g_profile; // sdmc:/switch/taiko/profile exists: the sampling profiler and the present-pacing lines run
void shim_init(void);
// Handheld: 460.8 MHz GPU (a configuration games use; the default is 307.2), the AI battle stage missed the refresh in 7% of
// its frames waiting for the GPU (2026-11-22). Docked (the second): the system's own.
u32 __nx_applet_PerformanceConfiguration[2] = { 0x92220007, 0 };
// Log lines collect in memory; a low-priority thread appends them to the SD card every 0.5 s (an SD write per line
// would stall the game's own threads). log_flush() also runs on crash/exit paths.
// The SD write happens outside g_log_lock: holding it made every printing thread wait for the card (50-160 ms frame
// spikes every 0.5 s, 2026-11-22).
#define LOG_PATH "sdmc:/switch/taiko/shim.log"
static char g_logs[2][1 << 20], *g_log = g_logs[0]; static size_t g_log_len; static u32 g_log_lost; static Mutex g_log_lock, g_flush_lock;
void log_flush(void) {
    mutexLock(&g_flush_lock); // one writer at a time: the other buffer is free again when it is released
    mutexLock(&g_log_lock);
    char *full = g_log; size_t n = g_log_len; u32 lost = g_log_lost;
    g_log = full == g_logs[0] ? g_logs[1] : g_logs[0]; g_log_len = 0; g_log_lost = 0;
    mutexUnlock(&g_log_lock);
    if (n) { FILE *f = fopen(LOG_PATH, "a"); if (f) { fwrite(full, 1, n, f); if (lost) fprintf(f, "[log] %u writes lost (buffer full)\n", lost); fclose(f); } }
    mutexUnlock(&g_flush_lock);
}
static ssize_t log_write(void *c, const char *b, size_t n) {
    mutexLock(&g_log_lock);
    if (n <= sizeof g_logs[0] - g_log_len) { memcpy(g_log + g_log_len, b, n); g_log_len += n; }
    else g_log_lost++; // never wait for the card here
    mutexUnlock(&g_log_lock); return n;
}
// Stopped and joined before main returns: libnx then unloads this NRO (to start the next one), and a thread still
// sleeping here woke into freed code (Instruction Abort after the self-update's exit, 2026-11-25).
static Thread g_log_thread; static volatile bool g_log_running, g_log_stop;
static void log_thread(void *arg) { while (!g_log_stop) { svcSleepThread(500000000ll); log_flush(); } }
static void log_stop(void) {
    if (!g_log_running) return;
    g_log_stop = true; threadWaitForExit(&g_log_thread); threadClose(&g_log_thread); g_log_running = false;
    log_flush();
}

int main(int argc, char **argv) {
    extern const char *g_nro_path; g_nro_path = argc > 0 ? argv[0] : NULL; // forwarder.c: the NRO a forwarder starts
    mkdir("sdmc:/switch/waddamburo", 0777); // .NET ignores a HOME folder that does not exist
    setenv("HOME", "/switch/waddamburo", 1); // before shim_init snapshots environ for the runtime: .NET folders (cache) go here
    consoleInit(NULL);
    // No nxlink: its socket outlives a crashing process and once took bsdsocket (and Atmosphere) down.
    consoleDebugInit(debugDevice_SVC);
    // Crash-proof log on the SD card: stderr is unbuffered and goes only there (or to Ryujinx's log).
    // Each write opens, appends and closes: Horizon blocks other opens of a file held for writing, and FTP
    // (sys-ftpd) must be able to read the log while the app runs. ponytail: a few ms per line.
    if (!envIsNso()) {
        FILE *f = fopen(LOG_PATH, "w"); if (f) fclose(f);
        FILE *log = fopencookie(NULL, "w", (cookie_io_functions_t){ .write = log_write });
        if (log) { setvbuf(log, NULL, _IOLBF, 1024); stderr = log; } // threads created later inherit it
        if (R_SUCCEEDED(threadCreate(&g_log_thread, log_thread, NULL, NULL, 0x4000, 0x3B, -2)) && R_SUCCEEDED(threadStart(&g_log_thread)))
            g_log_running = true;
    }
    fprintf(stderr, "main: start\n");
    { u32 n = 0, b = 0; Result r = apmGetPerformanceConfiguration(ApmPerformanceMode_Normal, &n), r2 = apmGetPerformanceConfiguration(ApmPerformanceMode_Boost, &b);
      fprintf(stderr, "[apm] mode %d (0 handheld, 1 docked), configurations %x (rc %x) %x (rc %x)\n", appletGetPerformanceMode(), n, r, b, r2); }
    shim_init();
    fprintf(stderr, "main: shim ok\n");
    // The sampling profiler pauses every thread each millisecond: only on request (an empty file named profile beside USRDIR).
    g_profile = access("sdmc:/switch/taiko/profile", F_OK) == 0;
    if (g_profile) { void shim_start_watchdog(void); shim_start_watchdog(); }
    fprintf(stderr, "starting runtime\n");
    consoleUpdate(NULL);
    // ponytail: fixed game data folder; an args file on the SD card when other options are needed
    char *args[] = { "waddamburo", "--game-data=/switch/taiko/USRDIR", NULL };
    int r = lx_dotnet_main(2, args);
    fprintf(stderr, "runtime returned %d\n", r);
    log_stop();
    extern int g_no_console;
    if (g_no_console) return r; // graphics took the screen: no text console left to wait on
    padConfigureInput(1, HidNpadStyleSet_NpadStandard);
    PadState pad; padInitializeDefault(&pad);
    while (appletMainLoop()) {
        padUpdate(&pad);
        if (padGetButtonsDown(&pad) & HidNpadButton_Plus) break;
        consoleUpdate(NULL);
    }
    consoleExit(NULL);
    return 0;
}
