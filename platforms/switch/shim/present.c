// Present pacing: the patched bgfx (glcontext_egl.cpp) calls nx_present_stats before every eglSwapBuffers.
#include <switch.h>
#include <stdio.h>

// Raises the render thread once; feeds the hitch profiler; with the profiler on, logs pacing every 2 s.
void nx_present_stats(void) {
    static u64 last, window_start, n, min_gap = ~0ull, max_gap, tids[4]; static int ntids;
    // Never bgfx_get_stats() here: it takes the resource lock, which the main thread holds in bgfx::frame() while it
    // waits for this thread (deadlock at boot, 2026-11-22). Waddamburo's own hitch line has the draw counts.
    static int boosted;
    if (!boosted) { // the render thread paces frames: above the main thread, on its own core
        boosted = 1; extern void hitch_render_thread(void); hitch_render_thread(); svcSetThreadPriority(CUR_THREAD_HANDLE, 0x2B); svcSetThreadCoreMask(CUR_THREAD_HANDLE, 1, 1 << 1);
        fprintf(stderr, "[present] render thread raised to 0x2B on core 1\n");
    }
    u64 now = armTicksToNs(armGetSystemTick()), tid = 0;
    svcGetThreadId(&tid, CUR_THREAD_HANDLE);
    int known = 0; for (int i = 0; i < ntids; i++) known |= tids[i] == tid;
    if (!known && ntids < 4) tids[ntids++] = tid;
    if (last) { u64 g = now - last; extern void hitch_present(u64); hitch_present(g); if (g < min_gap) min_gap = g; if (g > max_gap) max_gap = g; }
    last = now; n++;
    if (!window_start) window_start = now;
    extern int g_profile;
    if (g_profile && now - window_start >= 2000000000ull) {
        extern size_t nx_malloc_extent(void), nx_gpu_in_use(void); extern int nx_gpu_free_spans(void), nx_gpu_buffers(void);
        fprintf(stderr, "[present] %.1f/s, gaps %.2f-%.2f ms, %d thread(s), malloc heap %lu MiB, GPU buffers %zu MiB in %d (%d free spans)\n",
                n * 1e9 / (now - window_start), min_gap / 1e6, max_gap / 1e6, ntids,
                (unsigned long)nx_malloc_extent() >> 20, nx_gpu_in_use() >> 20, nx_gpu_buffers(), nx_gpu_free_spans());
        window_start = now; n = 0; min_gap = ~0ull; max_gap = 0;
    }
}
