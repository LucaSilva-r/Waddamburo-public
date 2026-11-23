// libnx has no dynamic loader. bx includes this header but its NX branches never call these.
#pragma once
#define RTLD_LAZY 1
#define RTLD_NOW 2
#define RTLD_LOCAL 0
#define RTLD_GLOBAL 0x100
#ifdef __cplusplus
extern "C" {
#endif
void *dlopen(const char *, int);
int dlclose(void *);
void *dlsym(void *, const char *);
char *dlerror(void);
#ifdef __cplusplus
}
#endif
