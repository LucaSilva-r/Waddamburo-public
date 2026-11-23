// TLS through Horizon's own service (libnx ssl): the system's trusted CAs, host name check, TLS up to 1.2. .NET's
// SslStream would need OpenSSL; on the Switch, Waddamburo.Services' ServerConnector hands its connected socket here
// (SwitchTls.cs) and speaks plain HTTP over the result.
//
// The handshake blocks; afterwards the connection is non-blocking and reads and writes wait in short sleeps: a blocking
// read holds the connection's IPC session, and a websocket's send would wait behind its pending receive.
#include <switch.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <errno.h>

#define SSL_WOULD_BLOCK MAKERESULT(123, 204) // 2123-0204

// active: calls in progress plus one for the owner; the last to leave frees it (a close can come while a websocket's
// receive waits in nxtls_read on another thread).
typedef struct { SslContext ctx; SslConnection conn; int fd; volatile int closing; int active; } Tls;

static int tls_init_once(void) {
    static int state; // 0 not tried, 1 ready, -1 failed
    if (!state) {
        Result rc = sslInitialize(3);
        state = R_SUCCEEDED(rc) ? 1 : -1;
        if (state < 0) fprintf(stderr, "[tls] sslInitialize failed %x\n", rc);
    }
    return state > 0;
}

static void tls_free(Tls *t) {
    if (t->fd >= 0) close(t->fd);
    sslConnectionClose(&t->conn);
    sslContextClose(&t->ctx);
    free(t);
}

// The connection over a connected socket (sockfd, a newlib descriptor), or NULL with *error set (a Result; a verify
// failure gives the certificate error). verify = 0: no certificate checks (a local server's self-signed certificate).
void *nxtls_open(int sockfd, const char *host, int verify, unsigned *error) {
    *error = 0;
    if (!tls_init_once()) { *error = 1; return NULL; }
    Tls *t = calloc(1, sizeof *t);
    t->fd = -1;
    Result rc = sslCreateContext(&t->ctx, SslVersion_Auto);
    if (R_FAILED(rc)) { *error = rc; free(t); return NULL; }
    if (R_FAILED(rc = sslContextCreateConnection(&t->ctx, &t->conn))) { *error = rc; sslContextClose(&t->ctx); free(t); return NULL; }
    t->fd = socketSslConnectionSetSocketDescriptor(&t->conn, sockfd);
    if (t->fd < 0 && errno != ENOENT) { *error = 2; tls_free(t); return NULL; }
    rc = sslConnectionSetHostName(&t->conn, host, strlen(host) + 1);
    if (R_SUCCEEDED(rc) && !verify) {
        sslConnectionSetOption(&t->conn, SslOptionType_SkipDefaultVerify, true);
        rc = sslConnectionSetVerifyOption(&t->conn, 0);
    } else if (R_SUCCEEDED(rc))
        rc = sslConnectionSetVerifyOption(&t->conn, SslVerifyOption_PeerCa | SslVerifyOption_HostName | SslVerifyOption_DateCheck);
    if (R_SUCCEEDED(rc)) rc = sslConnectionSetIoMode(&t->conn, SslIoMode_Blocking);
    if (R_SUCCEEDED(rc)) rc = sslConnectionDoHandshake(&t->conn, NULL, NULL, NULL, 0);
    if (R_FAILED(rc)) {
        Result verifyError = sslConnectionGetVerifyCertError(&t->conn);
        *error = R_FAILED(verifyError) ? verifyError : rc;
        fprintf(stderr, "[tls] handshake with %s failed %x (verify %x)\n", host, rc, verifyError);
        tls_free(t);
        return NULL;
    }
    sslConnectionSetIoMode(&t->conn, SslIoMode_NonBlocking);
    t->active = 1;
    return t;
}

static int enter(Tls *t) { __atomic_add_fetch(&t->active, 1, __ATOMIC_ACQ_REL); return !t->closing; }
static void leave(Tls *t) { if (__atomic_sub_fetch(&t->active, 1, __ATOMIC_ACQ_REL) == 0) tls_free(t); }

// Bytes read (0 = the peer closed), or -1 (also once closed). Blocks (in 2 ms sleeps) until something arrives.
int nxtls_read(void *h, void *buffer, int size) {
    Tls *t = h;
    int result = -1;
    if (enter(t))
        for (;;) {
            u32 n = 0;
            Result rc = sslConnectionRead(&t->conn, buffer, size, &n);
            if (R_SUCCEEDED(rc)) { result = n; break; }
            if (rc != SSL_WOULD_BLOCK) { fprintf(stderr, "[tls] read failed %x\n", rc); break; }
            if (t->closing) break;
            svcSleepThread(2000000);
        }
    leave(t);
    return result;
}

// Writes everything (in 2 ms sleeps while the service is full); 0, or -1.
int nxtls_write(void *h, const void *buffer, int size) {
    Tls *t = h;
    const char *p = buffer;
    int result = enter(t) ? 0 : -1;
    while (!result && size > 0) {
        u32 n = 0;
        Result rc = sslConnectionWrite(&t->conn, p, size, &n);
        if (R_SUCCEEDED(rc)) { p += n; size -= n; continue; }
        if (rc != SSL_WOULD_BLOCK || t->closing) { fprintf(stderr, "[tls] write failed %x\n", rc); result = -1; break; }
        svcSleepThread(2000000);
    }
    leave(t);
    return result;
}

// Ends the connection: calls in progress return -1, the last frees it.
void nxtls_close(void *h) {
    Tls *t = h;
    if (!t) return;
    t->closing = 1;
    leave(t);
}
