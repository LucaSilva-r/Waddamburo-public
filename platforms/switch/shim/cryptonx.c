// .NET's hashing/HMAC entry points (System.Security.Cryptography.Native) on mbedTLS 2.28 (devkitPro portlib).
// The OpenSSL-backed originals stay linked but weakened (link-inner.sh): these win. Everything else in that
// library (TLS, X.509, RSA, AES, ...) still needs OpenSSL, which cannot load here: those calls fail.
// ponytail: no SHA-3/SHAKE (mbedTLS 2.28 has none); EVP_MD* is a const mbedtls_md_info_t*.
#include <mbedtls/md.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef mbedtls_md_info_t EVP_MD;
typedef mbedtls_md_context_t EVP_MD_CTX;
typedef mbedtls_md_context_t HMAC_CTX;

int32_t CryptoNative_EnsureOpenSslInitialized(void) { return 0; }
int64_t CryptoNative_OpenSslVersionNumber(void) { return 0x30000000L; } // reported as 3.0: managed code gates features on it

void CryptoNative_ErrClearError(void) {}
uint64_t CryptoNative_ErrGetExceptionError(int32_t *isAllocFailure) { if (isAllocFailure) *isAllocFailure = 0; return 0; }
uint64_t CryptoNative_ErrPeekError(void) { return 0; }
uint64_t CryptoNative_ErrPeekLastError(void) { return 0; }
const char *CryptoNative_ErrReasonErrorString(uint64_t e) { return "unsupported on Switch (no OpenSSL)"; }
void CryptoNative_ErrErrorStringN(uint64_t e, char *buf, int32_t len) { if (len > 0) { strncpy(buf, "unsupported on Switch (no OpenSSL)", len - 1); buf[len - 1] = 0; } }

const EVP_MD *CryptoNative_EvpMd5(void) { return mbedtls_md_info_from_type(MBEDTLS_MD_MD5); }
const EVP_MD *CryptoNative_EvpSha1(void) { return mbedtls_md_info_from_type(MBEDTLS_MD_SHA1); }
const EVP_MD *CryptoNative_EvpSha256(void) { return mbedtls_md_info_from_type(MBEDTLS_MD_SHA256); }
const EVP_MD *CryptoNative_EvpSha384(void) { return mbedtls_md_info_from_type(MBEDTLS_MD_SHA384); }
const EVP_MD *CryptoNative_EvpSha512(void) { return mbedtls_md_info_from_type(MBEDTLS_MD_SHA512); }
const EVP_MD *CryptoNative_EvpSha3_256(void) { return NULL; }
const EVP_MD *CryptoNative_EvpSha3_384(void) { return NULL; }
const EVP_MD *CryptoNative_EvpSha3_512(void) { return NULL; }
const EVP_MD *CryptoNative_EvpShake128(void) { return NULL; }
const EVP_MD *CryptoNative_EvpShake256(void) { return NULL; }
int32_t CryptoNative_GetMaxMdSize(void) { return MBEDTLS_MD_MAX_SIZE; }
int32_t CryptoNative_EvpMdSize(const EVP_MD *md) { return md ? mbedtls_md_get_size(md) : 0; }

static mbedtls_md_context_t *ctx_new(const EVP_MD *md, int hmac) {
    if (!md) return NULL;
    mbedtls_md_context_t *c = malloc(sizeof *c);
    mbedtls_md_init(c);
    if (mbedtls_md_setup(c, md, hmac)) { mbedtls_md_free(c); free(c); return NULL; }
    return c;
}
static void ctx_free(mbedtls_md_context_t *c) { if (c) { mbedtls_md_free(c); free(c); } }
static size_t block_size(const mbedtls_md_info_t *md) { // mbedTLS 2.28 keeps md_info opaque
    mbedtls_md_type_t t = mbedtls_md_get_type(md);
    return t == MBEDTLS_MD_SHA384 || t == MBEDTLS_MD_SHA512 ? 128 : 64;
}
// mbedtls_md_clone copies the digest state but not HMAC's ipad/opad block.
static mbedtls_md_context_t *ctx_copy(const mbedtls_md_context_t *src) {
    mbedtls_md_context_t *c = ctx_new(src->md_info, src->hmac_ctx != NULL);
    if (!c) return NULL;
    mbedtls_md_clone(c, src);
    if (src->hmac_ctx) memcpy(c->hmac_ctx, src->hmac_ctx, 2 * block_size(src->md_info));
    return c;
}

EVP_MD_CTX *CryptoNative_EvpMdCtxCreate(const EVP_MD *type) {
    EVP_MD_CTX *c = ctx_new(type, 0);
    if (c && mbedtls_md_starts(c)) { ctx_free(c); return NULL; }
    return c;
}
void CryptoNative_EvpMdCtxDestroy(EVP_MD_CTX *c) { ctx_free(c); }
int32_t CryptoNative_EvpDigestReset(EVP_MD_CTX *c, const EVP_MD *type) { return c && mbedtls_md_starts(c) == 0; }
int32_t CryptoNative_EvpDigestUpdate(EVP_MD_CTX *c, const void *d, int32_t n) { return c && mbedtls_md_update(c, d, n) == 0; }
int32_t CryptoNative_EvpDigestFinalEx(EVP_MD_CTX *c, uint8_t *md, uint32_t *s) {
    if (!c || mbedtls_md_finish(c, md)) return 0;
    if (s) *s = mbedtls_md_get_size(c->md_info);
    return 1;
}
int32_t CryptoNative_EvpDigestCurrent(const EVP_MD_CTX *c, uint8_t *md, uint32_t *s) {
    EVP_MD_CTX *t = c ? ctx_copy(c) : NULL;
    int32_t r = t ? CryptoNative_EvpDigestFinalEx(t, md, s) : 0;
    ctx_free(t); return r;
}
EVP_MD_CTX *CryptoNative_EvpMdCtxCopyEx(const EVP_MD_CTX *c) { return c ? ctx_copy(c) : NULL; }
int32_t CryptoNative_EvpDigestOneShot(const EVP_MD *type, const void *src, int32_t n, uint8_t *md, uint32_t *size) {
    if (!type || mbedtls_md(type, src, n, md)) return 0;
    if (size) *size = mbedtls_md_get_size(type);
    return 1;
}
int32_t CryptoNative_EvpDigestFinalXOF(EVP_MD_CTX *c, uint8_t *md, uint32_t len) { return 0; }
int32_t CryptoNative_EvpDigestCurrentXOF(const EVP_MD_CTX *c, uint8_t *md, uint32_t len) { return 0; }
int32_t CryptoNative_EvpDigestXOFOneShot(const EVP_MD *t, const void *s, int32_t n, uint8_t *md, uint32_t len) { return 0; }
int32_t CryptoNative_EvpDigestSqueeze(EVP_MD_CTX *c, uint8_t *md, uint32_t len, int32_t *haveFeature) { if (haveFeature) *haveFeature = 0; return 0; }

HMAC_CTX *CryptoNative_HmacCreate(const uint8_t *key, int32_t keyLen, const EVP_MD *md) {
    HMAC_CTX *c = ctx_new(md, 1);
    static const uint8_t empty;
    if (c && mbedtls_md_hmac_starts(c, key ? key : &empty, keyLen)) { ctx_free(c); return NULL; }
    return c;
}
void CryptoNative_HmacDestroy(HMAC_CTX *c) { ctx_free(c); }
int32_t CryptoNative_HmacReset(HMAC_CTX *c) { return c && mbedtls_md_hmac_reset(c) == 0; }
int32_t CryptoNative_HmacUpdate(HMAC_CTX *c, const uint8_t *d, int32_t n) { return c && mbedtls_md_hmac_update(c, d, n) == 0; }
int32_t CryptoNative_HmacFinal(HMAC_CTX *c, uint8_t *md, int32_t *len) {
    if (!c || mbedtls_md_hmac_finish(c, md)) return 0;
    if (len) *len = mbedtls_md_get_size(c->md_info);
    return 1;
}
int32_t CryptoNative_HmacCurrent(const HMAC_CTX *c, uint8_t *md, int32_t *len) {
    HMAC_CTX *t = c ? ctx_copy(c) : NULL;
    int32_t r = t ? CryptoNative_HmacFinal(t, md, len) : 0;
    ctx_free(t); return r;
}
HMAC_CTX *CryptoNative_HmacCopy(const HMAC_CTX *c) { return c ? ctx_copy(c) : NULL; }
int32_t CryptoNative_HmacOneShot(const EVP_MD *type, const uint8_t *key, int32_t keySize, const uint8_t *src, int32_t n, uint8_t *md, int32_t *mdSize) {
    static const uint8_t empty;
    if (!type || mbedtls_md_hmac(type, key ? key : &empty, keySize, src ? src : &empty, n, md)) return 0;
    if (mdSize) *mdSize = mbedtls_md_get_size(type);
    return 1;
}
