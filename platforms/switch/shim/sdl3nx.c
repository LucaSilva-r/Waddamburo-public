// The subset of SDL3 (3.5.0 ABI, headers in ext/sdl3) that Waddamburo.Platform.Sdl calls, on libnx.
// Text input opens the system keyboard (swkbd). ponytail: no keyboard/mouse/touch, dialogs or PNG loading; stubs fail softly.
//
// - Window: the default NWindow at 1280x720. bgfx's Linux branch reads SDL_PROP_WINDOW_X11_WINDOW_NUMBER,
//   which returns nwindowGetDefault(), so Waddamburo needs no Switch branch for the swap chain.
// - Gamepads: a 1 kHz HID thread turns pad changes into SDL gamepad events stamped with the time they were
//   seen (SDL_GetTicksNS clock), so judgement does not depend on when the frame loop polls.
// - Audio: one audout device (48 kHz stereo s16, 3 x 256-frame buffers); bound F32 streams are mixed on a thread.
#include <switch.h>
#include <SDL3/SDL.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <malloc.h>

static char g_err[256] = "";
static void set_error(const char *s) { snprintf(g_err, sizeof g_err, "%s", s); }
const char *SDL_GetError(void) { return g_err; }
bool SDL_SetHint(const char *n, const char *v) { return true; }
bool SDL_SetAppMetadata(const char *a, const char *b, const char *c) { return true; }
Uint64 SDL_GetTicksNS(void) { return armTicksToNs(armGetSystemTick()); }
void SDL_free(void *p) { free(p); }

// ---------------------------------------------------------------- events
#define NEVENTS 1024
static SDL_Event g_events[NEVENTS];
static unsigned g_ev_head, g_ev_len;
static Mutex g_ev_lock;

static void push(const SDL_Event *e) {
    mutexLock(&g_ev_lock);
    if (g_ev_len < NEVENTS) { g_events[(g_ev_head + g_ev_len++) % NEVENTS] = *e; }
    mutexUnlock(&g_ev_lock);
}

static bool g_focused = true, g_quit_sent;
static AppletHookCookie g_hook;
static void applet_hook(AppletHookType hook, void *param) {
    if (hook != AppletHookType_OnFocusState) return;
    bool focused = appletGetFocusState() == AppletFocusState_InFocus;
    if (focused == g_focused) return;
    g_focused = focused;
    SDL_Event e = { 0 };
    e.window.type = focused ? SDL_EVENT_WINDOW_FOCUS_GAINED : SDL_EVENT_WINDOW_FOCUS_LOST;
    e.window.timestamp = SDL_GetTicksNS(); e.window.windowID = 1;
    push(&e);
}

bool SDL_PollEvent(SDL_Event *out) {
    if (!appletMainLoop() && !g_quit_sent) { // HOME menu "close" or the system asks us to exit
        g_quit_sent = true;
        SDL_Event e = { 0 }; e.type = SDL_EVENT_QUIT; e.quit.timestamp = SDL_GetTicksNS(); push(&e);
    }
    mutexLock(&g_ev_lock);
    bool any = g_ev_len > 0;
    if (any) { if (out) *out = g_events[g_ev_head]; g_ev_head = (g_ev_head + 1) % NEVENTS; g_ev_len--; }
    mutexUnlock(&g_ev_lock);
    return any;
}

// ---------------------------------------------------------------- gamepads
#define NPADS 8
struct SDL_Gamepad { int slot; };
static struct SDL_Gamepad g_gamepads[NPADS];
static PadState g_pads[NPADS];
static bool g_connected[NPADS];
static u64 g_buttons[NPADS];
static s16 g_axes[NPADS][SDL_GAMEPAD_AXIS_COUNT];
static Thread g_hid_thread;
static volatile bool g_hid_run;

static const struct { u64 nx; int sdl; } k_buttons[] = {
    // By label, not position: the game confirms with SOUTH and backs out with EAST (Xbox's A and B), so the Switch's A
    // (on the right) confirms and its B goes back, as every Switch game does.
    { HidNpadButton_A, SDL_GAMEPAD_BUTTON_SOUTH }, { HidNpadButton_B, SDL_GAMEPAD_BUTTON_EAST },
    { HidNpadButton_X, SDL_GAMEPAD_BUTTON_WEST }, { HidNpadButton_Y, SDL_GAMEPAD_BUTTON_NORTH },
    { HidNpadButton_StickL, SDL_GAMEPAD_BUTTON_LEFT_STICK }, { HidNpadButton_StickR, SDL_GAMEPAD_BUTTON_RIGHT_STICK },
    { HidNpadButton_L, SDL_GAMEPAD_BUTTON_LEFT_SHOULDER }, { HidNpadButton_R, SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER },
    { HidNpadButton_Plus, SDL_GAMEPAD_BUTTON_START }, { HidNpadButton_Minus, SDL_GAMEPAD_BUTTON_BACK },
    { HidNpadButton_Up, SDL_GAMEPAD_BUTTON_DPAD_UP }, { HidNpadButton_Down, SDL_GAMEPAD_BUTTON_DPAD_DOWN },
    { HidNpadButton_Left, SDL_GAMEPAD_BUTTON_DPAD_LEFT }, { HidNpadButton_Right, SDL_GAMEPAD_BUTTON_DPAD_RIGHT },
};

static void axis(int slot, int a, s16 v, Uint64 now) {
    if (g_axes[slot][a] == v) return;
    g_axes[slot][a] = v;
    SDL_Event e = { 0 };
    e.gaxis.type = SDL_EVENT_GAMEPAD_AXIS_MOTION; e.gaxis.timestamp = now; e.gaxis.which = slot + 1;
    e.gaxis.axis = a; e.gaxis.value = v; push(&e);
}

static s16 stick(s32 v) { return v > 32767 ? 32767 : v < -32768 ? -32768 : v; }

static void hid_thread(void *arg) {
    while (g_hid_run) {
        Uint64 now = SDL_GetTicksNS();
        for (int i = 0; i < NPADS; i++) {
            padUpdate(&g_pads[i]);
            bool c = padIsConnected(&g_pads[i]);
            if (c != g_connected[i]) {
                g_connected[i] = c;
                SDL_Event e = { 0 };
                e.gdevice.type = c ? SDL_EVENT_GAMEPAD_ADDED : SDL_EVENT_GAMEPAD_REMOVED;
                e.gdevice.timestamp = now; e.gdevice.which = i + 1; push(&e);
                if (!c) g_buttons[i] = 0;
            }
            if (!c) continue;
            u64 b = padGetButtons(&g_pads[i]), changed = b ^ g_buttons[i];
            if (changed) {
                for (size_t k = 0; k < sizeof k_buttons / sizeof *k_buttons; k++) if (changed & k_buttons[k].nx) {
                    SDL_Event e = { 0 };
                    bool down = b & k_buttons[k].nx;
                    e.gbutton.type = down ? SDL_EVENT_GAMEPAD_BUTTON_DOWN : SDL_EVENT_GAMEPAD_BUTTON_UP;
                    e.gbutton.timestamp = now; e.gbutton.which = i + 1; e.gbutton.button = k_buttons[k].sdl; e.gbutton.down = down;
                    push(&e);
                }
                g_buttons[i] = b;
            }
            HidAnalogStickState l = padGetStickPos(&g_pads[i], 0), r = padGetStickPos(&g_pads[i], 1);
            axis(i, SDL_GAMEPAD_AXIS_LEFTX, stick(l.x), now); axis(i, SDL_GAMEPAD_AXIS_LEFTY, stick(-l.y), now);
            axis(i, SDL_GAMEPAD_AXIS_RIGHTX, stick(r.x), now); axis(i, SDL_GAMEPAD_AXIS_RIGHTY, stick(-r.y), now);
            axis(i, SDL_GAMEPAD_AXIS_LEFT_TRIGGER, b & HidNpadButton_ZL ? 32767 : 0, now);
            axis(i, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER, b & HidNpadButton_ZR ? 32767 : 0, now);
        }
        svcSleepThread(1000000); // 1 ms
    }
}

static void hid_start(void) {
    if (g_hid_run) return;
    // From scratch after an SDL_Quit (the in-process restart): pads connected then are announced again.
    memset(g_connected, 0, sizeof g_connected); memset(g_buttons, 0, sizeof g_buttons); memset(g_axes, 0, sizeof g_axes);
    mutexLock(&g_ev_lock); g_ev_len = 0; mutexUnlock(&g_ev_lock);
    padConfigureInput(NPADS, HidNpadStyleSet_NpadStandard);
    padInitialize(&g_pads[0], HidNpadIdType_No1, HidNpadIdType_Handheld); // handheld Joy-Con count as player 1
    for (int i = 1; i < NPADS; i++) padInitialize(&g_pads[i], (HidNpadIdType)(HidNpadIdType_No1 + i));
    for (int i = 0; i < NPADS; i++) g_gamepads[i].slot = i;
    appletHook(&g_hook, applet_hook, NULL);
    g_hid_run = true;
    threadCreate(&g_hid_thread, hid_thread, NULL, NULL, 0x8000, 0x2B, 2); // above the game's threads
    threadStart(&g_hid_thread);
}

SDL_Gamepad *SDL_OpenGamepad(SDL_JoystickID id) { return id >= 1 && id <= NPADS ? &g_gamepads[id - 1] : NULL; }
void SDL_CloseGamepad(SDL_Gamepad *g) {}
const char *SDL_GetGamepadName(SDL_Gamepad *g) {
    if (!g) return NULL;
    u32 s = padGetStyleSet(&g_pads[g->slot]);
    return s & HidNpadStyleTag_NpadFullKey ? "Nintendo Switch Pro Controller"
         : s & HidNpadStyleTag_NpadHandheld ? "Nintendo Switch Joy-Con (handheld)" : "Nintendo Switch Joy-Con";
}
bool SDL_RumbleGamepad(SDL_Gamepad *g, Uint16 lo, Uint16 hi, Uint32 ms) { return true; } // ponytail: no rumble

// ---------------------------------------------------------------- init
bool SDL_Init(SDL_InitFlags f) { return SDL_InitSubSystem(f); }
bool SDL_InitSubSystem(SDL_InitFlags f) {
    if (f & (SDL_INIT_VIDEO | SDL_INIT_GAMEPAD | SDL_INIT_JOYSTICK | SDL_INIT_EVENTS)) hid_start();
    return true;
}
void SDL_QuitSubSystem(SDL_InitFlags f) {}
static void audio_stop(void);
void SDL_Quit(void) {
    if (g_hid_run) { g_hid_run = false; threadWaitForExit(&g_hid_thread); threadClose(&g_hid_thread); appletUnhook(&g_hook); }
    audio_stop();
}
void sdlnx_shutdown(void) { SDL_Quit(); }

// ---------------------------------------------------------------- window / display
struct SDL_Window { int w, h; };
static struct SDL_Window g_window = { 1280, 720 };
SDL_Window *SDL_CreateWindow(const char *t, int w, int h, SDL_WindowFlags f) {
    // The libnx text console (main.c) owns the default NWindow until now; bgfx's EGL surface needs it.
    extern int g_no_console;
    if (!g_no_console) { g_no_console = 1; consoleExit(NULL); }
    return &g_window;
}
void SDL_DestroyWindow(SDL_Window *w) {}
bool SDL_GetWindowSize(SDL_Window *w, int *x, int *y) { if (x) *x = g_window.w; if (y) *y = g_window.h; return true; }
bool SDL_GetWindowSizeInPixels(SDL_Window *w, int *x, int *y) { return SDL_GetWindowSize(w, x, y); }
float SDL_GetWindowPixelDensity(SDL_Window *w) { return 1.0f; }
SDL_WindowFlags SDL_GetWindowFlags(SDL_Window *w) { return SDL_WINDOW_FULLSCREEN | (g_focused ? SDL_WINDOW_INPUT_FOCUS : 0); }
bool SDL_SetWindowFullscreen(SDL_Window *w, bool f) { return true; }
bool SDL_SetWindowFullscreenMode(SDL_Window *w, const SDL_DisplayMode *m) { return true; }
bool SDL_SetWindowIcon(SDL_Window *w, SDL_Surface *s) { return true; }
SDL_PropertiesID SDL_GetWindowProperties(SDL_Window *w) { return 1; }
void *SDL_GetPointerProperty(SDL_PropertiesID p, const char *n, void *d) { return d; }
Sint64 SDL_GetNumberProperty(SDL_PropertiesID p, const char *n, Sint64 d) {
    return !strcmp(n, SDL_PROP_WINDOW_X11_WINDOW_NUMBER) ? (Sint64)(uintptr_t)nwindowGetDefault() : d;
}
const char *SDL_GetCurrentVideoDriver(void) { return "switch"; }
SDL_DisplayID SDL_GetDisplayForWindow(SDL_Window *w) { return 1; }
static const SDL_DisplayMode g_mode = { .displayID = 1, .format = SDL_PIXELFORMAT_XRGB8888, .w = 1280, .h = 720,
                                        .pixel_density = 1.0f, .refresh_rate = 60.0f, .refresh_rate_numerator = 60,
                                        .refresh_rate_denominator = 1 };
const SDL_DisplayMode *SDL_GetCurrentDisplayMode(SDL_DisplayID d) { return &g_mode; }
const SDL_DisplayMode *SDL_GetDesktopDisplayMode(SDL_DisplayID d) { return &g_mode; }
SDL_DisplayMode **SDL_GetFullscreenDisplayModes(SDL_DisplayID d, int *count) {
    // one allocation, freed with SDL_free: [ptr, NULL, mode]
    SDL_DisplayMode **list = malloc(2 * sizeof *list + sizeof g_mode);
    SDL_DisplayMode *m = (SDL_DisplayMode *)(list + 2); *m = g_mode;
    list[0] = m; list[1] = NULL; if (count) *count = 1; return list;
}
bool SDL_GetClosestFullscreenDisplayMode(SDL_DisplayID d, int w, int h, float r, bool hd, SDL_DisplayMode *out) { *out = g_mode; return true; }

// cursor, mouse, text input, keyboard names
bool SDL_ShowCursor(void) { return true; }
bool SDL_HideCursor(void) { return true; }
bool SDL_CursorVisible(void) { return false; }
bool SDL_CaptureMouse(bool e) { return true; }
SDL_MouseButtonFlags SDL_GetMouseState(float *x, float *y) { if (x) *x = 0; if (y) *y = 0; return 0; }
SDL_Window *SDL_GetMouseFocus(void) { return NULL; }
bool SDL_StartTextInput(SDL_Window *w) { return true; }
bool SDL_StopTextInput(SDL_Window *w) { return true; }
bool SDL_TextInputActive(SDL_Window *w) { return false; }
bool SDL_ScreenKeyboardShown(SDL_Window *w) { return false; }
const char *SDL_GetScancodeName(SDL_Scancode s) { return ""; }
const char *SDL_GetKeyName(SDL_Keycode k) { return ""; }
SDL_Keycode SDL_GetKeyFromScancode(SDL_Scancode s, SDL_Keymod m, bool ev) {
    if (s >= SDL_SCANCODE_A && s <= SDL_SCANCODE_Z) return 'a' + (s - SDL_SCANCODE_A);
    if (s >= SDL_SCANCODE_1 && s <= SDL_SCANCODE_9) return '1' + (s - SDL_SCANCODE_1);
    if (s == SDL_SCANCODE_0) return '0';
    return SDL_SCANCODE_TO_KEYCODE(s);
}

// dialogs, URLs, message boxes, images
void SDL_ShowOpenFileDialog(SDL_DialogFileCallback cb, void *u, SDL_Window *w, const SDL_DialogFileFilter *f, int n, const char *d, bool many) { set_error("no file dialogs on Switch"); cb(u, NULL, -1); }
void SDL_ShowSaveFileDialog(SDL_DialogFileCallback cb, void *u, SDL_Window *w, const SDL_DialogFileFilter *f, int n, const char *d) { set_error("no file dialogs on Switch"); cb(u, NULL, -1); }
void SDL_ShowOpenFolderDialog(SDL_DialogFileCallback cb, void *u, SDL_Window *w, const char *d, bool many) { set_error("no file dialogs on Switch"); cb(u, NULL, -1); }
bool SDL_OpenURL(const char *url) { set_error("no browser"); return false; }
bool SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags f, const char *t, const char *m, SDL_Window *w) { fprintf(stderr, "[sdl] %s: %s\n", t, m); return true; }
SDL_IOStream *SDL_IOFromConstMem(const void *m, size_t n) { set_error("SDL_IOStream unsupported"); return NULL; }
SDL_Surface *SDL_LoadPNG_IO(SDL_IOStream *s, bool c) { set_error("PNG loading unsupported"); return NULL; }
SDL_Surface *SDL_ConvertSurface(SDL_Surface *s, SDL_PixelFormat f) { set_error("surfaces unsupported"); return NULL; }
void SDL_DestroySurface(SDL_Surface *s) {}

// ---------------------------------------------------------------- audio
#define AUDIO_RATE 48000
#define AUDIO_FRAMES 256
#define AUDIO_BUFS 3
#define DEVICE_ID 2
struct SDL_AudioStream {
    RMutex lock; float *buf; size_t cap, head, len; // len/cap in samples
    int channels; bool bound; float gain; struct SDL_AudioStream *next;
};
static SDL_AudioStream *g_streams; static Mutex g_streams_lock;
static bool g_audio_open, g_audio_paused = true; static volatile bool g_audio_run;
static Thread g_audio_thread;
static AudioOutBuffer g_out[AUDIO_BUFS];
static s16 *g_out_mem;
static float g_mix[AUDIO_FRAMES * 2];

static void mix_into(s16 *dst) {
    memset(g_mix, 0, sizeof g_mix);
    mutexLock(&g_streams_lock);
    if (!g_audio_paused) for (SDL_AudioStream *s = g_streams; s; s = s->next) {
        if (!s->bound) continue;
        rmutexLock(&s->lock);
        size_t frames = s->len / s->channels; if (frames > AUDIO_FRAMES) frames = AUDIO_FRAMES;
        for (size_t f = 0; f < frames; f++) {
            float l = s->buf[s->head], r = s->channels > 1 ? s->buf[(s->head + 1) % s->cap] : l;
            g_mix[f * 2] += l * s->gain; g_mix[f * 2 + 1] += r * s->gain;
            s->head = (s->head + s->channels) % s->cap; s->len -= s->channels;
        }
        rmutexUnlock(&s->lock);
    }
    mutexUnlock(&g_streams_lock);
    for (int i = 0; i < AUDIO_FRAMES * 2; i++) {
        float v = g_mix[i] * 32767.0f;
        dst[i] = v > 32767.0f ? 32767 : v < -32768.0f ? -32768 : (s16)v;
    }
}

static void audio_thread(void *arg) {
    while (g_audio_run) {
        AudioOutBuffer *released = NULL; u32 count = 0;
        if (R_FAILED(audoutWaitPlayFinish(&released, &count, 100000000ull)) || !released) continue;
        mix_into(released->buffer);
        audoutAppendAudioOutBuffer(released);
    }
}

static bool audio_start(void) {
    if (g_audio_open) return true;
    Result rc = audoutInitialize();
    if (R_FAILED(rc)) { set_error("audoutInitialize failed"); return false; }
    rc = audoutStartAudioOut();
    if (R_FAILED(rc)) { audoutExit(); set_error("audoutStartAudioOut failed"); return false; }
    size_t bytes = (AUDIO_FRAMES * 2 * sizeof(s16) + 0xfff) & ~0xfffull;
    g_out_mem = memalign(0x1000, bytes * AUDIO_BUFS);
    memset(g_out_mem, 0, bytes * AUDIO_BUFS);
    for (int i = 0; i < AUDIO_BUFS; i++) {
        g_out[i].next = NULL; g_out[i].buffer = (char *)g_out_mem + i * bytes; g_out[i].buffer_size = bytes;
        g_out[i].data_size = AUDIO_FRAMES * 2 * sizeof(s16); g_out[i].data_offset = 0;
        audoutAppendAudioOutBuffer(&g_out[i]);
    }
    g_audio_run = true; g_audio_open = true;
    threadCreate(&g_audio_thread, audio_thread, NULL, NULL, 0x8000, 0x2A, 2);
    threadStart(&g_audio_thread);
    fprintf(stderr, "[sdl] audout %u Hz x%u\n", audoutGetSampleRate(), audoutGetChannelCount());
    return true;
}
static void audio_stop(void) {
    if (!g_audio_open) return;
    g_audio_run = false; threadWaitForExit(&g_audio_thread); threadClose(&g_audio_thread);
    audoutStopAudioOut(); audoutExit(); free(g_out_mem); g_audio_open = false;
}

const char *SDL_GetCurrentAudioDriver(void) { return "audout"; }
SDL_AudioDeviceID *SDL_GetAudioPlaybackDevices(int *count) {
    SDL_AudioDeviceID *ids = malloc(2 * sizeof *ids); ids[0] = DEVICE_ID; ids[1] = 0; if (count) *count = 1; return ids;
}
const char *SDL_GetAudioDeviceName(SDL_AudioDeviceID d) { return "Nintendo Switch"; }
SDL_AudioDeviceID SDL_OpenAudioDevice(SDL_AudioDeviceID d, const SDL_AudioSpec *spec) {
    if (spec && (spec->freq != AUDIO_RATE || spec->format != SDL_AUDIO_F32LE)) { set_error("only 48 kHz F32 audio"); return 0; }
    return audio_start() ? DEVICE_ID : 0;
}
void SDL_CloseAudioDevice(SDL_AudioDeviceID d) { g_audio_paused = true; } // ponytail: device stays open until SDL_Quit
bool SDL_PauseAudioDevice(SDL_AudioDeviceID d) { g_audio_paused = true; return true; }
bool SDL_ResumeAudioDevice(SDL_AudioDeviceID d) { g_audio_paused = false; return true; }
bool SDL_GetAudioDeviceFormat(SDL_AudioDeviceID d, SDL_AudioSpec *spec, int *frames) {
    if (spec) { spec->format = SDL_AUDIO_F32LE; spec->channels = 2; spec->freq = AUDIO_RATE; }
    if (frames) *frames = AUDIO_FRAMES;
    return true;
}

SDL_AudioStream *SDL_CreateAudioStream(const SDL_AudioSpec *src, const SDL_AudioSpec *dst) {
    if (!src || src->format != SDL_AUDIO_F32LE || src->freq != AUDIO_RATE || src->channels < 1 || src->channels > 2) {
        set_error("only 48 kHz F32 mono/stereo streams"); return NULL;
    }
    SDL_AudioStream *s = calloc(1, sizeof *s);
    rmutexInit(&s->lock); s->channels = src->channels; s->gain = 1.0f;
    // A stream a sound now (the game's mixer plays each voice on its own stream): 0.25 s to start, grows.
    s->cap = AUDIO_RATE / 4 * src->channels; s->buf = malloc(s->cap * sizeof(float));
    mutexLock(&g_streams_lock); s->next = g_streams; g_streams = s; mutexUnlock(&g_streams_lock);
    return s;
}
void SDL_DestroyAudioStream(SDL_AudioStream *s) {
    if (!s) return;
    mutexLock(&g_streams_lock);
    for (SDL_AudioStream **p = &g_streams; *p; p = &(*p)->next) if (*p == s) { *p = s->next; break; }
    mutexUnlock(&g_streams_lock);
    free(s->buf); free(s);
}
bool SDL_BindAudioStream(SDL_AudioDeviceID d, SDL_AudioStream *s) { if (!s) return false; s->bound = true; return true; }
void SDL_UnbindAudioStream(SDL_AudioStream *s) { if (s) s->bound = false; } // keeps what is queued (a paused song)
bool SDL_SetAudioStreamGain(SDL_AudioStream *s, float gain) { if (!s) return false; s->gain = gain; return true; }
float SDL_GetAudioStreamGain(SDL_AudioStream *s) { return s ? s->gain : -1.0f; }
bool SDL_LockAudioStream(SDL_AudioStream *s) { rmutexLock(&s->lock); return true; }
bool SDL_UnlockAudioStream(SDL_AudioStream *s) { rmutexUnlock(&s->lock); return true; }
bool SDL_FlushAudioStream(SDL_AudioStream *s) { return true; }
bool SDL_ClearAudioStream(SDL_AudioStream *s) { rmutexLock(&s->lock); s->len = 0; rmutexUnlock(&s->lock); return true; }
int SDL_GetAudioStreamQueued(SDL_AudioStream *s) { rmutexLock(&s->lock); int n = (int)(s->len * sizeof(float)); rmutexUnlock(&s->lock); return n; }
bool SDL_PutAudioStreamData(SDL_AudioStream *s, const void *data, int bytes) {
    size_t n = bytes / sizeof(float);
    const float *src = data;
    rmutexLock(&s->lock);
    if (s->len + n > s->cap) { // grow, unwrapping the ring
        size_t cap = s->cap; while (s->len + n > cap) cap *= 2;
        float *b = malloc(cap * sizeof(float));
        for (size_t i = 0; i < s->len; i++) b[i] = s->buf[(s->head + i) % s->cap];
        free(s->buf); s->buf = b; s->cap = cap; s->head = 0;
    }
    for (size_t i = 0; i < n; i++) s->buf[(s->head + s->len + i) % s->cap] = src[i];
    s->len += n;
    rmutexUnlock(&s->lock);
    return true;
}

// The console's keyboard (swkbd) for Waddamburo's text fields (SdlApplication.EditWithSystemKeyboard): edits the whole
// text at once, blocking while the applet shows (the game's audio plays on). 1 = confirmed (out holds UTF-8), 0 = cancelled,
// -1 = the applet is unavailable.
int sdlnx_keyboard(const char *initial, const char *guide, int max_length, char *out, int out_size) {
    SwkbdConfig kbd;
    if (R_FAILED(swkbdCreate(&kbd, 0))) return -1;
    swkbdConfigMakePresetDefault(&kbd);
    if (initial && *initial) swkbdConfigSetInitialText(&kbd, initial);
    if (guide && *guide) swkbdConfigSetGuideText(&kbd, guide);
    if (max_length > 0) swkbdConfigSetStringLenMax(&kbd, max_length);
    Result rc = swkbdShow(&kbd, out, out_size);
    swkbdClose(&kbd);
    return R_SUCCEEDED(rc) ? 1 : 0;
}
