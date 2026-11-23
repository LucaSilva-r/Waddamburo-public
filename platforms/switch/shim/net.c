// Sockets, DNS and epoll for the .NET runtime (glibc ABI) on libnx's BSD sockets.
//
// libnx's socket(), connect(), recv()... return newlib file descriptors, so read/write/close on a socket already reach
// them through shim.c's pass-throughs; this file translates what differs from glibc: sockaddr (glibc has no sa_len,
// AF_INET6 is 10, not 28), addrinfo (field order), msghdr, socket options and flags, AI_/EAI_ codes, fcntl's
// O_NONBLOCK. Errno values stay newlib's: lx___errno_location translates them (shim.c lx_errno).
// Horizon's BSD service is IPv4 only: IPv6 sockets fail with EAFNOSUPPORT and DNS answers IPv4.
//
// epoll is emulated on poll(): .NET's SocketAsyncEngine registers every socket edge-triggered (EPOLLET) for read and
// write, and a level-triggered report of an always-writable socket would spin it. A readiness bit is reported once and
// armed again when a read or write on that socket returns EAGAIN (exactly when real ET epoll would fire again), or when
// poll sees it drop. epoll_wait polls in short slices, so registrations made meanwhile are picked up.
#include <switch.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <netdb.h>
#include <poll.h>
#include <fcntl.h>
#include <errno.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <unistd.h>
#include <stdarg.h>

// glibc (aarch64 Linux) values.
#define G_AF_INET 2
#define G_AF_INET6 10
#define G_SOCK_NONBLOCK 04000
#define G_SOCK_CLOEXEC 02000000
#define G_O_NONBLOCK 04000
#define G_SOL_SOCKET 1
#define G_POLLRDHUP 0x2000
#define G_EPOLLIN 0x001
#define G_EPOLLOUT 0x004
#define G_EPOLLERR 0x008
#define G_EPOLLHUP 0x010
#define G_EPOLLRDHUP 0x2000
#define G_EPOLLET (1u << 31)

typedef struct { uint16_t family, port; uint32_t addr; uint8_t zero[8]; } g_sockaddr_in;
typedef struct { int flags, family, socktype, protocol; uint32_t addrlen; void *addr; char *canonname; void *next; } g_addrinfo;
typedef struct { void *name; uint32_t namelen; struct iovec *iov; size_t iovlen; void *control; size_t controllen; int flags; } g_msghdr;
typedef struct { uint32_t events; uint64_t data; } g_epoll_event; // aarch64: not packed (16 bytes)

// Which descriptors are sockets (fcntl, poll and epoll only handle those here).
#define MAX_FD 1024
static uint8_t g_sock[MAX_FD];
static int is_sock(int fd) { return fd >= 0 && fd < MAX_FD && g_sock[fd]; }

static void net_init_once(void) {
    static int done;
    if (done) return;
    done = 1;
    Result rc = socketInitializeDefault();
    if (R_FAILED(rc)) fprintf(stderr, "[net] socketInitializeDefault failed %x\n", rc);
}

// ---- addresses
static int to_bsd(const void *g, unsigned glen, struct sockaddr_in *out) {
    const g_sockaddr_in *in = g;
    if (glen < 2) { errno = EINVAL; return -1; }
    if (in->family != G_AF_INET || glen < sizeof *in) { errno = EAFNOSUPPORT; return -1; }
    memset(out, 0, sizeof *out);
    out->sin_len = sizeof *out; out->sin_family = AF_INET; out->sin_port = in->port; out->sin_addr.s_addr = in->addr;
    return 0;
}
static void from_bsd(const struct sockaddr_in *b, void *g, unsigned *glen) {
    if (!g || !glen) return;
    g_sockaddr_in out = { G_AF_INET, b->sin_port, b->sin_addr.s_addr, {0} };
    memcpy(g, &out, *glen < sizeof out ? *glen : sizeof out);
    *glen = sizeof out;
}

static int msg_flags(int g) { // send/recv flags
    int b = g & 7; // OOB, PEEK, DONTROUTE
    if (g & 0x40) b |= MSG_DONTWAIT;
    if (g & 0x100) b |= MSG_WAITALL;
    return b; // MSG_NOSIGNAL: no SIGPIPE on Horizon
}

static void set_nonblock(int fd, int on) {
    int f = fcntl(fd, F_GETFL, 0);
    fcntl(fd, F_SETFL, on ? f | O_NONBLOCK : f & ~O_NONBLOCK);
}

// ---- epoll emulation (declared early: reads and writes re-arm it)
static void rearm(int fd, uint32_t bits);
static void forget(int fd);

// ---- sockets
int lx_socket(int d, int t, int p) {
    net_init_once();
    if (d != G_AF_INET) { errno = EAFNOSUPPORT; return -1; }
    int fd = socket(AF_INET, t & ~(G_SOCK_NONBLOCK | G_SOCK_CLOEXEC), p);
    if (fd < 0) return -1;
    if (fd < MAX_FD) g_sock[fd] = 1;
    if (t & G_SOCK_NONBLOCK) set_nonblock(fd, 1);
    return fd;
}
int lx_bind(int s, const void *a, unsigned l) {
    struct sockaddr_in b; if (to_bsd(a, l, &b)) return -1;
    return bind(s, (struct sockaddr *)&b, sizeof b);
}
int lx_connect(int s, const void *a, unsigned l) {
    struct sockaddr_in b; if (to_bsd(a, l, &b)) return -1;
    return connect(s, (struct sockaddr *)&b, sizeof b);
}
int lx_listen(int s, int n) { return listen(s, n); }
int lx_accept4(int s, void *a, unsigned *l, int f) {
    struct sockaddr_in b; socklen_t bl = sizeof b;
    int fd = accept(s, (struct sockaddr *)&b, &bl);
    if (fd < 0) { if (errno == EAGAIN) rearm(s, G_EPOLLIN); return -1; }
    if (fd < MAX_FD) g_sock[fd] = 1;
    if (f & G_SOCK_NONBLOCK) set_nonblock(fd, 1);
    from_bsd(&b, a, l);
    return fd;
}
int lx_accept(int s, void *a, unsigned *l) { return lx_accept4(s, a, l, 0); }
ssize_t lx_send(int s, const void *buf, size_t n, int f) {
    ssize_t r = send(s, buf, n, msg_flags(f));
    if (r < 0 && errno == EAGAIN) rearm(s, G_EPOLLOUT);
    return r;
}
ssize_t lx_recv(int s, void *buf, size_t n, int f) {
    ssize_t r = recv(s, buf, n, msg_flags(f));
    if (r < 0 && errno == EAGAIN) rearm(s, G_EPOLLIN);
    return r;
}
ssize_t lx_sendto(int s, const void *buf, size_t n, int f, const void *a, unsigned l) {
    struct sockaddr_in b;
    if (a && to_bsd(a, l, &b)) return -1;
    ssize_t r = sendto(s, buf, n, msg_flags(f), a ? (struct sockaddr *)&b : NULL, a ? sizeof b : 0);
    if (r < 0 && errno == EAGAIN) rearm(s, G_EPOLLOUT);
    return r;
}
ssize_t lx_recvfrom(int s, void *buf, size_t n, int f, void *a, unsigned *l) {
    struct sockaddr_in b; socklen_t bl = sizeof b;
    ssize_t r = recvfrom(s, buf, n, msg_flags(f), (struct sockaddr *)&b, &bl);
    if (r < 0) { if (errno == EAGAIN) rearm(s, G_EPOLLIN); return -1; }
    if (a && l) { if (bl >= sizeof b) from_bsd(&b, a, l); else *l = 0; } // no address (a stream socket)
    return r;
}
// Messages through sendto/recvfrom: one buffer (gathered when there are several), no ancillary data.
ssize_t lx_sendmsg(int s, const void *m, int f) {
    const g_msghdr *g = m;
    size_t total = 0; for (size_t i = 0; i < g->iovlen; i++) total += g->iov[i].iov_len;
    char *buf = g->iovlen == 1 ? g->iov[0].iov_base : malloc(total ? total : 1);
    if (g->iovlen != 1) { size_t k = 0; for (size_t i = 0; i < g->iovlen; i++) { memcpy(buf + k, g->iov[i].iov_base, g->iov[i].iov_len); k += g->iov[i].iov_len; } }
    ssize_t r = lx_sendto(s, buf, total, f, g->name, g->namelen);
    if (g->iovlen != 1) free(buf);
    return r;
}
ssize_t lx_recvmsg(int s, void *m, int f) {
    g_msghdr *g = m;
    size_t total = 0; for (size_t i = 0; i < g->iovlen; i++) total += g->iov[i].iov_len;
    char *buf = g->iovlen == 1 ? g->iov[0].iov_base : malloc(total ? total : 1);
    unsigned namelen = g->namelen;
    ssize_t r = lx_recvfrom(s, buf, total, f, g->name, g->name ? &namelen : NULL);
    if (r >= 0) {
        if (g->iovlen != 1) { size_t k = 0; for (size_t i = 0; i < g->iovlen && k < (size_t)r; i++) { size_t c = g->iov[i].iov_len < (size_t)r - k ? g->iov[i].iov_len : (size_t)r - k; memcpy(g->iov[i].iov_base, buf + k, c); k += c; } }
        if (g->name) g->namelen = namelen;
        g->controllen = 0;
        g->flags = 0;
    }
    if (g->iovlen != 1) free(buf);
    return r;
}
int lx_shutdown(int s, int h) { return shutdown(s, h); }
int lx_getsockname(int s, void *a, unsigned *l) {
    struct sockaddr_in b; socklen_t bl = sizeof b;
    if (getsockname(s, (struct sockaddr *)&b, &bl)) return -1;
    from_bsd(&b, a, l); return 0;
}
int lx_getpeername(int s, void *a, unsigned *l) {
    struct sockaddr_in b; socklen_t bl = sizeof b;
    if (getpeername(s, (struct sockaddr *)&b, &bl)) return -1;
    from_bsd(&b, a, l); return 0;
}

// Option (level, name) from glibc to BSD; 0 = unknown.
static int sockopt(int level, int name, int *bl, int *bn) {
    switch (level) {
    case G_SOL_SOCKET:
        *bl = SOL_SOCKET;
        switch (name) {
        case 2: *bn = SO_REUSEADDR; return 1;
        case 3: *bn = SO_TYPE; return 1;
        case 4: *bn = SO_ERROR; return 1;
        case 5: *bn = SO_DONTROUTE; return 1;
        case 6: *bn = SO_BROADCAST; return 1;
        case 7: *bn = SO_SNDBUF; return 1;
        case 8: *bn = SO_RCVBUF; return 1;
        case 9: *bn = SO_KEEPALIVE; return 1;
        case 10: *bn = SO_OOBINLINE; return 1;
        case 13: *bn = SO_LINGER; return 1;
        case 15: *bn = SO_REUSEPORT; return 1;
        case 20: case 66: *bn = SO_RCVTIMEO; return 1;
        case 21: case 67: *bn = SO_SNDTIMEO; return 1;
        }
        return 0;
    case IPPROTO_IP:
        *bl = IPPROTO_IP;
        switch (name) {
        case 1: *bn = IP_TOS; return 1;
        case 2: *bn = IP_TTL; return 1;
        case 33: *bn = IP_MULTICAST_TTL; return 1;
        case 34: *bn = IP_MULTICAST_LOOP; return 1;
        case 35: *bn = IP_ADD_MEMBERSHIP; return 1;
        case 36: *bn = IP_DROP_MEMBERSHIP; return 1;
        }
        return 0;
    case IPPROTO_TCP:
        *bl = IPPROTO_TCP;
        switch (name) {
        case 1: *bn = TCP_NODELAY; return 1;
        case 4: *bn = TCP_KEEPIDLE; return 1;
        case 5: *bn = TCP_KEEPINTVL; return 1;
        case 6: *bn = TCP_KEEPCNT; return 1;
        }
        return 0;
    }
    return 0;
}
int lx_setsockopt(int s, int l, int o, const void *v, unsigned n) {
    int bl, bn;
    if (!sockopt(l, o, &bl, &bn)) return 0; // ponytail: an option Horizon lacks (IPv6, ...) is quietly accepted
    return setsockopt(s, bl, bn, v, n);
}
int lx_getsockopt(int s, int l, int o, void *v, unsigned *n) {
    int bl, bn;
    if (!sockopt(l, o, &bl, &bn)) { errno = ENOPROTOOPT; return -1; }
    socklen_t bnl = *n;
    if (getsockopt(s, bl, bn, v, &bnl)) return -1;
    *n = bnl;
    if (bl == SOL_SOCKET && bn == SO_ERROR && *n >= sizeof(int)) { // a newlib errno: glibc's (lx___errno_location's table)
        extern int *lx___errno_location(void);
        int saved = errno; errno = *(int *)v; *(int *)v = *lx___errno_location(); errno = saved;
    }
    return 0;
}

// ---- fcntl and poll (sockets; everything else as before)
int lx_fcntl(int fd, int cmd, ...) {
    va_list ap; va_start(ap, cmd); long arg = va_arg(ap, long); va_end(ap);
    if (!is_sock(fd)) return cmd == F_GETFL ? 2 : 0; // files: O_RDWR, the rest ignored (as it always was)
    if (cmd == F_GETFL) { int f = fcntl(fd, F_GETFL, 0); return f < 0 ? f : (f & O_NONBLOCK ? G_O_NONBLOCK : 0) | 2; }
    if (cmd == F_SETFL) { set_nonblock(fd, (arg & G_O_NONBLOCK) != 0); return 0; }
    return 0; // F_SETFD (close-on-exec) and the rest: nothing to do
}
typedef struct { int fd; short events, revents; } g_pollfd; // same layout and bits as BSD's, but POLLRDHUP
int lx_poll(void *f, unsigned long n, int t) {
    g_pollfd *p = f;
    struct pollfd b[64];
    if (n > 64) n = 64;
    for (unsigned long i = 0; i < n; i++) {
        b[i].fd = is_sock(p[i].fd) ? p[i].fd : -1; // ponytail: only sockets are polled (pipes and files read as idle)
        b[i].events = p[i].events & ~G_POLLRDHUP; b[i].revents = 0;
    }
    int r = poll(b, n, t);
    for (unsigned long i = 0; i < n; i++) p[i].revents = b[i].fd < 0 ? 0 : b[i].revents;
    return r;
}

// ---- epoll
typedef struct { int fd; uint32_t events, reported; uint64_t data; } Watch;
#define NEPOLL 8
#define EPOLL_FD 0x7000
#define NWATCH 512
typedef struct { int used; Mutex m; Watch w[NWATCH]; int n; } Epoll;
static Epoll g_ep[NEPOLL];
static Epoll *ep_of(int fd) { int i = fd - EPOLL_FD; return i >= 0 && i < NEPOLL && g_ep[i].used ? &g_ep[i] : NULL; }
int net_is_epoll(int fd) { return ep_of(fd) != NULL; }

int lx_epoll_create1(int f) {
    for (int i = 0; i < NEPOLL; i++) if (!g_ep[i].used) { memset(&g_ep[i], 0, sizeof g_ep[i]); g_ep[i].used = 1; return EPOLL_FD + i; }
    errno = EMFILE; return -1;
}
int lx_epoll_ctl(int e, int op, int fd, void *ev) {
    Epoll *ep = ep_of(e); if (!ep) { errno = EBADF; return -1; }
    const g_epoll_event *g = ev;
    mutexLock(&ep->m);
    int i = 0; while (i < ep->n && ep->w[i].fd != fd) i++;
    int r = 0;
    if (op == 1 /*ADD*/ || op == 3 /*MOD*/) {
        if (i == ep->n) { if (ep->n == NWATCH) { errno = ENOMEM; r = -1; goto out; } ep->n++; }
        ep->w[i] = (Watch){ fd, g->events, 0, g->data };
    } else if (op == 2 /*DEL*/ && i < ep->n) ep->w[i] = ep->w[--ep->n];
out:
    mutexUnlock(&ep->m);
    return r;
}
static void rearm(int fd, uint32_t bits) {
    for (int k = 0; k < NEPOLL; k++) {
        Epoll *ep = &g_ep[k]; if (!ep->used) continue;
        mutexLock(&ep->m);
        for (int i = 0; i < ep->n; i++) if (ep->w[i].fd == fd) ep->w[i].reported &= ~bits;
        mutexUnlock(&ep->m);
    }
}
static void forget(int fd) { // a closed socket leaves every epoll set (as on Linux)
    for (int k = 0; k < NEPOLL; k++) {
        Epoll *ep = &g_ep[k]; if (!ep->used) continue;
        mutexLock(&ep->m);
        for (int i = 0; i < ep->n; i++) if (ep->w[i].fd == fd) ep->w[i--] = ep->w[--ep->n];
        mutexUnlock(&ep->m);
    }
}
int lx_epoll_wait(int e, void *events, int max, int timeout) {
    Epoll *ep = ep_of(e); if (!ep) { errno = EBADF; return -1; }
    g_epoll_event *out = events;
    u64 start = armTicksToNs(armGetSystemTick());
    for (;;) {
        struct pollfd p[NWATCH]; Watch w[NWATCH]; int n;
        mutexLock(&ep->m); n = ep->n; memcpy(w, ep->w, n * sizeof *w); mutexUnlock(&ep->m);
        for (int i = 0; i < n; i++) {
            p[i].fd = is_sock(w[i].fd) ? w[i].fd : -1;
            p[i].events = (w[i].events & G_EPOLLIN ? POLLIN : 0) | (w[i].events & G_EPOLLOUT ? POLLOUT : 0);
            p[i].revents = 0;
        }
        u64 now = armTicksToNs(armGetSystemTick());
        int left = timeout < 0 ? 20 : (int)(timeout - (int)((now - start) / 1000000));
        int slice = left < 0 ? 0 : left > 20 ? 20 : left; // registrations made meanwhile are seen at the next slice
        int r = n ? poll(p, n, slice) : (svcSleepThread((s64)slice * 1000000), 0);
        int count = 0;
        if (r > 0) {
            mutexLock(&ep->m);
            for (int i = 0; i < n && count < max; i++) {
                if (!p[i].revents || p[i].fd < 0) continue;
                uint32_t ready = (p[i].revents & POLLIN ? G_EPOLLIN : 0) | (p[i].revents & POLLOUT ? G_EPOLLOUT : 0)
                    | (p[i].revents & POLLERR ? G_EPOLLERR : 0) | (p[i].revents & POLLHUP ? G_EPOLLHUP | G_EPOLLRDHUP : 0);
                // The live entry (it may have been re-registered or removed meanwhile).
                int k = 0; while (k < ep->n && ep->w[k].fd != w[i].fd) k++;
                if (k == ep->n) continue;
                Watch *live = &ep->w[k];
                uint32_t fire = live->events & G_EPOLLET ? ready & ~live->reported : ready;
                live->reported = (live->reported & ready) | fire;
                if (!fire) continue;
                out[count].events = fire; out[count].data = live->data; count++;
            }
            mutexUnlock(&ep->m);
        } else if (r == 0 && n) {
            // Nothing ready: every bit dropped, so the next readiness fires again.
            mutexLock(&ep->m);
            for (int i = 0; i < ep->n; i++) ep->w[i].reported = 0;
            mutexUnlock(&ep->m);
        }
        if (count) return count;
        if (timeout >= 0 && left <= 0) return 0;
    }
}

// shim.c calls these from read/write/close on any descriptor.
void net_io(int fd, int writing, ssize_t r) { if (r < 0 && errno == EAGAIN && is_sock(fd)) rearm(fd, writing ? G_EPOLLOUT : G_EPOLLIN); }
int net_close(int fd) {
    if (ep_of(fd)) { g_ep[fd - EPOLL_FD].used = 0; return 0; }
    if (is_sock(fd)) { g_sock[fd] = 0; forget(fd); }
    return -1; // not ours to close: shim.c goes on
}

// ---- DNS
static int gai_error(int b) {
    switch (b) {
    case EAI_AGAIN: return -3; case EAI_BADFLAGS: return -1; case EAI_FAIL: return -4; case EAI_FAMILY: return -6;
    case EAI_MEMORY: return -10; case EAI_NONAME: return -2; case EAI_SERVICE: return -8; case EAI_SYSTEM: return -11;
    default: return -4;
    }
}
int lx_getaddrinfo(const char *node, const char *service, const void *hints, void **res) {
    net_init_once();
    const g_addrinfo *g = hints;
    struct addrinfo h = {0};
    h.ai_family = AF_INET;
    if (g) {
        if (g->family != 0 && g->family != G_AF_INET) return -6; // EAI_FAMILY: IPv4 only
        h.ai_socktype = g->socktype; h.ai_protocol = g->protocol;
        if (g->flags & 1) h.ai_flags |= AI_PASSIVE;
        if (g->flags & 2) h.ai_flags |= AI_CANONNAME;
        if (g->flags & 4) h.ai_flags |= AI_NUMERICHOST;
        if (g->flags & 0x400) h.ai_flags |= AI_NUMERICSERV;
    }
    struct addrinfo *r = NULL;
    int rc = getaddrinfo(node, service, &h, &r);
    if (rc) return gai_error(rc);
    g_addrinfo *first = NULL, **link = &first;
    for (struct addrinfo *a = r; a; a = a->ai_next) {
        if (a->ai_family != AF_INET || !a->ai_addr) continue;
        g_addrinfo *o = calloc(1, sizeof *o + sizeof(g_sockaddr_in));
        o->flags = 0; o->family = G_AF_INET; o->socktype = a->ai_socktype; o->protocol = a->ai_protocol;
        o->addr = o + 1; unsigned len = sizeof(g_sockaddr_in);
        from_bsd((struct sockaddr_in *)a->ai_addr, o->addr, &len); o->addrlen = len;
        o->canonname = a->ai_canonname ? strdup(a->ai_canonname) : NULL;
        *link = o; link = (g_addrinfo **)&o->next;
    }
    freeaddrinfo(r);
    if (!first) return -2; // EAI_NONAME
    *res = first;
    return 0;
}
void lx_freeaddrinfo(void *r) {
    for (g_addrinfo *a = r, *next; a; a = next) { next = a->next; free(a->canonname); free(a); }
}
