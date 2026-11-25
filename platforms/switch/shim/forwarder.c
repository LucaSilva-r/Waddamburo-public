// Home-menu forwarder: an installable NSP whose program is nx-hbloader patched to start this NRO (patches/
// hbloader-forwarder.patch, built by scripts/forwarder.sh). Made on the console from the user's own prod.keys (Lockpick_RCM
// through hekate writes sdmc:/switch/prod.keys): header_key encrypts the NCA headers, key_area_key_application_00 the
// key areas. Signatures stay zero, so installing it needs sigpatches (as every homebrew forwarder does).
// The NCAs: program (ExeFS: main + main.npdm), control (RomFS: the NRO's own NACP and icon) and meta (the CNMT).
// Builds on a PC too (FORWARDER_TEST: OpenSSL, main below) so the output can be checked with hactool.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef uint8_t u8;
typedef uint32_t u32;
typedef uint64_t u64;

#define TITLE_ID 0x0500005744420000ull // "WDB": homebrew range, never a retail title

#ifdef FORWARDER_TEST
#include <openssl/aes.h>
#include <openssl/rand.h>
#include <openssl/sha.h>
typedef AES_KEY Aes;
static void aes_init(Aes *a, const u8 *key) { AES_set_encrypt_key(key, 128, a); }
static void aes_block(Aes *a, u8 *dst, const u8 *src) { AES_encrypt(src, dst, a); }
static void sha256(u8 *out, const void *p, size_t n) { SHA256(p, n, out); }
static void random_bytes(u8 *p, size_t n) { RAND_bytes(p, (int)n); }
#else
#include <switch.h>
typedef Aes128Context Aes;
static void aes_init(Aes *a, const u8 *key) { aes128ContextCreate(a, key, true); }
static void aes_block(Aes *a, u8 *dst, const u8 *src) { aes128EncryptBlock(a, dst, src); }
static void sha256(u8 *out, const void *p, size_t n) { sha256CalculateHash(out, p, n); }
static void random_bytes(u8 *p, size_t n) { randomGet(p, n); }
#endif

typedef struct { u8 *p; size_t n, cap; } Buf;
static void reserve(Buf *b, size_t n) {
    if (n <= b->cap) return;
    b->cap = n > b->cap * 2 ? n : b->cap * 2;
    b->p = realloc(b->p, b->cap);
}
static u8 *grow(Buf *b, size_t n) { // n zero bytes at the end
    reserve(b, b->n + n);
    u8 *at = b->p + b->n; memset(at, 0, n); b->n += n; return at;
}
static void put(Buf *b, const void *p, size_t n) { memcpy(grow(b, n), p, n); }
static void pad(Buf *b, size_t align) { if (b->n % align) grow(b, align - b->n % align); }
static void w32(u8 *p, u32 v) { memcpy(p, &v, 4); }
static void w64(u8 *p, u64 v) { memcpy(p, &v, 8); }
static u32 r32(const u8 *p) { u32 v; memcpy(&v, p, 4); return v; }
static u64 r64(const u8 *p) { u64 v; memcpy(&v, p, 8); return v; }
static size_t align(size_t v, size_t a) { return (v + a - 1) / a * a; }

// --- NSO: the embedded one is LZ4-compressed (elf2nso); the copy written has plain segments, the path patched in.
static int lz4_block(const u8 *src, size_t n, u8 *dst, size_t cap) {
    size_t i = 0, o = 0;
    while (i < n) {
        u8 token = src[i++];
        size_t lit = token >> 4;
        if (lit == 15) { u8 b; do { if (i >= n) return -1; b = src[i++]; lit += b; } while (b == 255); }
        if (i + lit > n || o + lit > cap) return -1;
        memcpy(dst + o, src + i, lit); i += lit; o += lit;
        if (i >= n) break; // the last sequence has literals only
        if (i + 2 > n) return -1;
        size_t off = src[i] | src[i + 1] << 8; i += 2;
        size_t len = token & 15;
        if (len == 15) { u8 b; do { if (i >= n) return -1; b = src[i++]; len += b; } while (b == 255); }
        len += 4;
        if (!off || off > o || o + len > cap) return -1;
        for (size_t k = 0; k < len; k++, o++) dst[o] = dst[o - off]; // byte by byte: copies may overlap
    }
    return o == cap ? 0 : -1;
}
static int nso_plain(const u8 *nso, size_t n, const char *target, Buf *out) {
    if (n < 0x100 || memcmp(nso, "NSO0", 4)) return -1;
    u32 flags = r32(nso + 0xC);
    u8 head[0x100]; memcpy(head, nso, 0x100);
    size_t first = r32(nso + 0x10); // module name etc. between the header and the first segment: kept as is
    if (first > n) return -1;
    put(out, nso, first);
    for (int s = 0; s < 3; s++) {
        u32 foff = r32(nso + 0x10 + s * 0x10), size = r32(nso + 0x18 + s * 0x10), csize = r32(nso + 0x60 + s * 4);
        int compressed = flags >> s & 1;
        if ((u64)foff + (compressed ? csize : size) > n) return -1;
        size_t at = out->n;
        u8 *seg = grow(out, size);
        if (compressed ? lz4_block(nso + foff, csize, seg, size) : (memcpy(seg, nso + foff, size), 0)) return -1;
        w32(head + 0x10 + s * 0x10, (u32)at);
        w32(head + 0x60 + s * 4, size);
    }
    static const char marker[] = "WADDAMBURO-FORWARDER-NRO-PATH:";
    u8 *m = NULL;
    for (size_t i = first; i + 0x320 <= out->n && !m; i++)
        if (!memcmp(out->p + i, marker, sizeof marker - 1)) m = out->p + i;
    if (!m || strlen(target) >= 0x320 - 32) return -1;
    memset(m + 32, 0, 0x320 - 32);
    memcpy(m + 32, target, strlen(target));
    for (int s = 0; s < 3; s++) sha256(head + 0xA0 + s * 0x20, out->p + r32(head + 0x10 + s * 0x10), r32(head + 0x18 + s * 0x10));
    w32(head + 0xC, (flags & ~7u) | 0x38); // plain segments, hashes checked
    memcpy(out->p, head, 0x100);
    return 0;
}

// --- PFS0
static void pfs0(Buf *out, int count, const char *const *names, const u8 *const *data, const size_t *sizes) {
    size_t strtab = 0;
    for (int i = 0; i < count; i++) strtab += strlen(names[i]) + 1;
    size_t head = align(0x10 + 0x18 * count + strtab, 0x20);
    strtab = head - 0x10 - 0x18 * count;
    u8 *h = grow(out, head);
    memcpy(h, "PFS0", 4); w32(h + 4, count); w32(h + 8, (u32)strtab);
    u64 off = 0; u32 name = 0;
    for (int i = 0; i < count; i++) {
        u8 *e = h + 0x10 + 0x18 * i;
        w64(e, off); w64(e + 8, sizes[i]); w32(e + 0x10, name);
        strcpy((char *)h + 0x10 + 0x18 * count + name, names[i]);
        name += strlen(names[i]) + 1; off += sizes[i];
    }
    for (int i = 0; i < count; i++) put(out, data[i], sizes[i]);
}

// --- RomFS with files in its root only
static u32 romfs_hash(u32 parent, const char *name) {
    u32 h = parent ^ 123456789;
    for (; *name; name++) { h = h >> 5 | h << 27; h ^= (u8)*name; }
    return h;
}
static u32 romfs_buckets(u32 n) {
    if (n < 3) return 3;
    if (n < 19) return n | 1;
    while (!(n % 2) || !(n % 3) || !(n % 5) || !(n % 7) || !(n % 11) || !(n % 13) || !(n % 17)) n++;
    return n;
}
static void romfs(Buf *out, int count, const char *const *names, const u8 *const *data, const size_t *sizes, const int *same_as) {
    u64 offs[32]; size_t datasize = 0;
    for (int i = 0; i < count; i++) {
        if (same_as[i] >= 0) { offs[i] = offs[same_as[i]]; continue; }
        datasize = align(datasize, 0x10); offs[i] = datasize; datasize += sizes[i];
    }
    grow(out, 0x200 + datasize);
    for (int i = 0; i < count; i++) if (same_as[i] < 0) memcpy(out->p + 0x200 + offs[i], data[i], sizes[i]);
    pad(out, 4);
    // directory hash table and the root's entry
    size_t dirhash = out->n; u32 dbuckets = romfs_buckets(1);
    for (u32 b = 0; b < dbuckets; b++) w32(grow(out, 4), b == romfs_hash(0, "") % dbuckets ? 0 : 0xFFFFFFFF);
    size_t dirmeta = out->n;
    u8 *root = grow(out, 0x18);
    w32(root, 0); w32(root + 4, 0xFFFFFFFF); w32(root + 8, 0xFFFFFFFF); w32(root + 12, 0); w32(root + 16, 0xFFFFFFFF); w32(root + 20, 0);
    // file hash table, then the entries (each chained to its bucket's previous head)
    size_t filehash = out->n; u32 fbuckets = romfs_buckets(count);
    for (u32 b = 0; b < fbuckets; b++) w32(grow(out, 4), 0xFFFFFFFF);
    size_t filemeta = out->n; u32 entry = 0;
    for (int i = 0; i < count; i++) {
        size_t len = strlen(names[i]), size = 0x20 + align(len, 4);
        u8 *e = grow(out, size);
        u32 *bucket = (u32 *)(out->p + filehash) + romfs_hash(0, names[i]) % fbuckets;
        w32(e, 0); w32(e + 4, i + 1 < count ? entry + (u32)size : 0xFFFFFFFF);
        w64(e + 8, offs[i]); w64(e + 16, sizes[i]); w32(e + 24, *bucket); w32(e + 28, (u32)len);
        memcpy(e + 0x20, names[i], len);
        *bucket = entry; entry += (u32)size;
    }
    u8 *h = out->p;
    w64(h, 0x50);
    w64(h + 0x08, dirhash); w64(h + 0x10, dirmeta - dirhash); w64(h + 0x18, dirmeta); w64(h + 0x20, filehash - dirmeta);
    w64(h + 0x28, filehash); w64(h + 0x30, filemeta - filehash); w64(h + 0x38, filemeta); w64(h + 0x40, out->n - filemeta);
    w64(h + 0x48, 0x200);
}

// --- NCA sections: hashed data plus the hash fields of its filesystem header
// PFS0 under HierarchicalSha256: one hash per block (the last one over what is left), then the data.
static void section_pfs0(Buf *sec, u8 *fs, const Buf *pfs, u32 block) {
    size_t count = (pfs->n + block - 1) / block;
    u8 *table = grow(sec, count * 32);
    for (size_t i = 0; i < count; i++) sha256(table + i * 32, pfs->p + i * block, pfs->n - i * block < block ? pfs->n - i * block : block);
    size_t data = align(count * 32, 0x200);
    pad(sec, 0x200); put(sec, pfs->p, pfs->n); pad(sec, 0x200);
    fs[2] = 1; fs[3] = 2; // PartitionFs, HierarchicalSha256
    u8 *hd = fs + 8;
    sha256(hd, sec->p, count * 32);
    w32(hd + 0x20, block); w32(hd + 0x24, 2);
    w64(hd + 0x28, 0); w64(hd + 0x30, count * 32); w64(hd + 0x38, data); w64(hd + 0x40, pfs->n);
}
// RomFS under HierarchicalIntegrity (IVFC): six levels, 0x4000-byte blocks zero-padded, the data last.
static void section_romfs(Buf *sec, u8 *fs, const Buf *rom) {
    enum { B = 0x4000 };
    Buf level[6] = { 0 }; // 0..4 hash levels (top first), 5 the data
    put(&level[5], rom->p, rom->n);
    for (int l = 4; l >= 0; l--) {
        const Buf *below = &level[l + 1];
        size_t count = (below->n + B - 1) / B;
        u8 *block = calloc(1, B);
        for (size_t i = 0; i < count; i++) {
            size_t n = below->n - i * B < B ? below->n - i * B : B;
            memset(block, 0, B); memcpy(block, below->p + i * B, n);
            sha256(grow(&level[l], 32), block, B);
        }
        free(block);
    }
    fs[2] = 0; fs[3] = 3; // RomFs, HierarchicalIntegrity
    u8 *hd = fs + 8;
    memcpy(hd, "IVFC", 4); w32(hd + 4, 0x20000); w32(hd + 8, 0x20); w32(hd + 0xC, 7);
    for (int l = 0; l < 6; l++) {
        size_t at = sec->n;
        put(sec, level[l].p, level[l].n);
        if (l < 5) pad(sec, B); else pad(sec, 0x200);
        u8 *info = hd + 0x10 + l * 0x18;
        w64(info, at); w64(info + 8, level[l].n); w32(info + 16, 14);
    }
    u8 *top = calloc(1, B); memcpy(top, level[0].p, level[0].n);
    sha256(hd + 0xC0, top, B);
    free(top);
    for (int l = 0; l < 6; l++) free(level[l].p);
}

typedef struct { u8 header_key[32]; u8 kak[16]; } Keys;

// One NCA with one section at 0xC00; returns its bytes in *out.
static void nca(Buf *out, const Keys *k, u8 type, Buf *sec, u8 *fs) {
    u8 h[0xC00] = { 0 };
    u8 key[16]; random_bytes(key, 16);
    fs[0] = 2; fs[1] = 0; // version 2 (u16); the filesystem and hash types come from the section
    fs[4] = 3;  // AesCtr
    memcpy(h + 0x200, "NCA3", 4);
    h[0x205] = type;
    w64(h + 0x208, 0xC00 + sec->n);
    w64(h + 0x210, TITLE_ID);
    w32(h + 0x21C, 0x000C1100); // SDK add-on version
    w32(h + 0x240, 0xC00 / 0x200); w32(h + 0x244, (u32)((0xC00 + sec->n) / 0x200)); h[0x248] = 1;
    memcpy(h + 0x400, fs, 0x200);
    sha256(h + 0x280, fs, 0x200);
    Aes kak; aes_init(&kak, k->kak);
    u8 area[0x40] = { 0 }; memcpy(area + 0x20, key, 16); // index 2: the AES-CTR key
    for (int i = 0; i < 4; i++) aes_block(&kak, h + 0x300 + i * 16, area + i * 16);
    // the section: AES-CTR, counter = 8 zero bytes (generation/secure value 0) + the absolute offset / 16, big-endian
    Aes ctr; aes_init(&ctr, key);
    for (size_t i = 0; i < sec->n; i += 16) {
        u8 iv[16] = { 0 }, ks[16]; u64 c = (0xC00 + i) >> 4;
        for (int b = 0; b < 8; b++) iv[15 - b] = (u8)(c >> (b * 8));
        aes_block(&ctr, ks, iv);
        for (int b = 0; b < 16; b++) sec->p[i + b] ^= ks[b];
    }
    // the header: AES-XTS over 0x200-byte sectors, the tweak the sector number big-endian (Nintendo's)
    Aes k1, k2; aes_init(&k1, k->header_key); aes_init(&k2, k->header_key + 16);
    for (int s = 0; s < 6; s++) {
        u8 t[16] = { 0 }, tw[16]; t[15] = (u8)s;
        aes_block(&k2, tw, t);
        for (int b = 0; b < 0x200; b += 16) {
            u8 x[16];
            for (int i = 0; i < 16; i++) x[i] = h[s * 0x200 + b + i] ^ tw[i];
            aes_block(&k1, x, x);
            for (int i = 0; i < 16; i++) h[s * 0x200 + b + i] = x[i] ^ tw[i];
            u8 carry = tw[15] >> 7; // multiply the tweak by x in GF(2^128), little-endian bytes
            for (int i = 15; i > 0; i--) tw[i] = (u8)(tw[i] << 1 | tw[i - 1] >> 7);
            tw[0] = (u8)(tw[0] << 1) ^ (carry ? 0x87 : 0);
        }
    }
    put(out, h, sizeof h);
    put(out, sec->p, sec->n);
}

static int hexkey(const char *text, const char *name, u8 *out, size_t n) {
    size_t len = strlen(name);
    for (const char *p = text; (p = strstr(p, name)); p += len) {
        if (p != text && p[-1] != '\n') continue;
        const char *q = p + len;
        while (*q == ' ' || *q == '\t') q++;
        if (*q++ != '=') continue;
        while (*q == ' ' || *q == '\t') q++;
        for (size_t i = 0; i < n; i++) {
            unsigned v;
            if (sscanf(q + i * 2, "%2x", &v) != 1) return 0;
            out[i] = (u8)v;
        }
        return 1;
    }
    return 0;
}

static void ncaid(char *name, const Buf *nca, const char *suffix) {
    u8 hash[32]; sha256(hash, nca->p, nca->n);
    for (int i = 0; i < 16; i++) sprintf(name + i * 2, "%02x", hash[i]);
    strcpy(name + 32, suffix);
}

static const char *const languages[16] = { "AmericanEnglish", "BritishEnglish", "Japanese", "French", "German",
    "LatinAmericanSpanish", "Spanish", "Italian", "Dutch", "CanadianFrench", "Portuguese", "Russian", "Korean",
    "TraditionalChinese", "SimplifiedChinese", "BrazilianPortuguese" };

// The NSP for the NRO at target (an sdmc: path), from the forwarder's NSO/NPDM, the NRO's bytes and prod.keys' text.
// 0 = done; 1 = prod.keys lacks header_key or key_area_key_application_00; 2 = the NRO has no icon/NACP; 3 = the NSO.
static int forwarder_nsp(Buf *nsp, const u8 *nso, size_t nso_n, const u8 *npdm, size_t npdm_n, const u8 *nro, size_t nro_n,
                         const char *keys_text, const char *target) {
    Keys k;
    if (!hexkey(keys_text, "header_key", k.header_key, 32) || !hexkey(keys_text, "key_area_key_application_00", k.kak, 16)) return 1;
    // the NRO's assets: icon (JPEG) and NACP
    if (nro_n < 0x80 || memcmp(nro + 0x10, "NRO0", 4)) return 2;
    size_t aset = r32(nro + 0x18);
    if (aset + 0x38 > nro_n || memcmp(nro + aset, "ASET", 4)) return 2;
    u64 icon_off = r64(nro + aset + 8), icon_n = r64(nro + aset + 16), nacp_off = r64(nro + aset + 24), nacp_n = r64(nro + aset + 32);
    if (!icon_n || nacp_n != 0x4000 || aset + icon_off + icon_n > nro_n || aset + nacp_off + nacp_n > nro_n) return 2;
    u8 nacp[0x4000]; memcpy(nacp, nro + aset + nacp_off, sizeof nacp);
    w32(nacp + 0x302C, 0xFFFF);            // supported languages: an icon for each below
    w64(nacp + 0x3038, TITLE_ID);          // presence group
    w64(nacp + 0x3070, TITLE_ID + 0x1000); // add-on content base
    w64(nacp + 0x3078, TITLE_ID);          // save data owner
    for (int i = 0; i < 8; i++) w64(nacp + 0x30B0 + i * 8, TITLE_ID); // local communication
    Buf plain = { 0 };
    if (nso_plain(nso, nso_n, target, &plain)) { free(plain.p); return 3; }

    Buf program = { 0 }, control = { 0 }, meta = { 0 }, part = { 0 }, sec = { 0 };
    u8 fs[0x200];
    { // program: ExeFS
        const char *names[] = { "main", "main.npdm" }; const u8 *data[] = { plain.p, npdm }; size_t sizes[] = { plain.n, npdm_n };
        pfs0(&part, 2, names, data, sizes);
        memset(fs, 0, sizeof fs); section_pfs0(&sec, fs, &part, 0x10000);
        nca(&program, &k, 0, &sec, fs);
    }
    part.n = sec.n = 0;
    { // control: RomFS with control.nacp and the icon under every language's name
        const char *names[17]; const u8 *data[17]; size_t sizes[17]; int same[17];
        names[0] = "control.nacp"; data[0] = nacp; sizes[0] = sizeof nacp; same[0] = -1;
        char icon_names[16][48];
        for (int i = 0; i < 16; i++) {
            snprintf(icon_names[i], sizeof icon_names[i], "icon_%s.dat", languages[i]);
            names[i + 1] = icon_names[i]; data[i + 1] = nro + aset + icon_off; sizes[i + 1] = icon_n; same[i + 1] = i ? 1 : -1;
        }
        romfs(&part, 17, names, data, sizes, same);
        memset(fs, 0, sizeof fs); section_romfs(&sec, fs, &part);
        nca(&control, &k, 2, &sec, fs);
    }
    part.n = sec.n = 0;
    char program_name[48], control_name[48], meta_name[48];
    ncaid(program_name, &program, ".nca"); ncaid(control_name, &control, ".nca");
    { // meta: the CNMT listing program and control
        u8 cnmt[0x20 + 0x10 + 2 * 0x38 + 0x20] = { 0 };
        w64(cnmt, TITLE_ID); cnmt[0xC] = 0x80; cnmt[0xE] = 0x10; cnmt[0x10] = 2;
        w64(cnmt + 0x20, TITLE_ID + 0x800);
        const Buf *contents[] = { &program, &control }; const u8 types[] = { 1, 3 };
        for (int i = 0; i < 2; i++) {
            u8 *r = cnmt + 0x30 + i * 0x38;
            sha256(r, contents[i]->p, contents[i]->n);
            memcpy(r + 0x20, r, 16);
            for (int b = 0; b < 6; b++) r[0x30 + b] = (u8)(contents[i]->n >> (b * 8));
            r[0x36] = types[i];
        }
        char name[64]; snprintf(name, sizeof name, "Application_%016llx.cnmt", (unsigned long long)TITLE_ID);
        const char *names[] = { name }; const u8 *data[] = { cnmt }; size_t sizes[] = { sizeof cnmt };
        pfs0(&part, 1, names, data, sizes);
        memset(fs, 0, sizeof fs); section_pfs0(&sec, fs, &part, 0x1000);
        nca(&meta, &k, 1, &sec, fs);
    }
    ncaid(meta_name, &meta, ".cnmt.nca");
    {
        const char *names[] = { program_name, control_name, meta_name };
        const u8 *data[] = { program.p, control.p, meta.p }; size_t sizes[] = { program.n, control.n, meta.n };
        pfs0(nsp, 3, names, data, sizes);
    }
    free(plain.p); free(program.p); free(control.p); free(meta.p); free(part.p); free(sec.p);
    return 0;
}

static int read_file(const char *path, Buf *b) {
    FILE *f = fopen(path, "rb");
    if (!f) return -1;
    u8 chunk[0x10000]; size_t n;
    while ((n = fread(chunk, 1, sizeof chunk, f)) > 0) put(b, chunk, n);
    fclose(f);
    return 0;
}

#ifdef FORWARDER_TEST
// cc -DFORWARDER_TEST forwarder.c -lcrypto && ./a.out forwarder.nso forwarder.npdm Waddamburo.nro prod.keys out.nsp
int main(int argc, char **argv) {
    if (argc != 6) { fprintf(stderr, "usage: %s nso npdm nro prod.keys out.nsp\n", argv[0]); return 2; }
    Buf nso = { 0 }, npdm = { 0 }, nro = { 0 }, keys = { 0 }, nsp = { 0 };
    if (read_file(argv[1], &nso) || read_file(argv[2], &npdm) || read_file(argv[3], &nro) || read_file(argv[4], &keys)) return 2;
    put(&keys, "", 1);
    int r = forwarder_nsp(&nsp, nso.p, nso.n, npdm.p, npdm.n, nro.p, nro.n, (char *)keys.p, "sdmc:/switch/Waddamburo.nro");
    if (r) { fprintf(stderr, "error %d\n", r); return 1; }
    FILE *f = fopen(argv[5], "wb"); fwrite(nsp.p, 1, nsp.n, f); fclose(f);
    return 0;
}
#else
#define INCBIN(name, file) \
    __asm__(".section .rodata\n.balign 16\n.global " #name "\n" #name ":\n.incbin \"" file "\"\n.global " #name "_end\n" #name "_end:\n.previous\n"); \
    extern const u8 name[], name##_end[];
INCBIN(fwd_nso, FORWARDER_NSO)
INCBIN(fwd_npdm, FORWARDER_NPDM)

const char *g_nro_path; // argv[0] as hbmenu or the forwarder gave it (main.c)

// Waddamburo's self-update (Hosting/SelfUpdate.cs): this NRO's path as .NET opens it (/switch/...), NULL when unknown.
const char *nxfwd_nro_path(void) {
    if (!g_nro_path || !*g_nro_path) return NULL;
    return strncmp(g_nro_path, "sdmc:", 5) ? g_nro_path : g_nro_path + 5;
}

// Ends the whole process (every thread with it) once the update is in place: returning to the loader would leave the
// runtime's threads running inside an unloaded NRO (they crashed it, 2026-11-25). The next start, from hbmenu or a
// HOME-menu forwarder, runs the new file at the same path. HOME then reports "the software was closed because an error
// occurred" (so did appletExit with exit mode 1): the game warns the player first (Hosting/SelfUpdate.cs).
void nxfwd_exit_process(void) {
    void log_flush(void);
    log_flush();
    svcExitProcess();
}

// Waddamburo's FORWARDER row (Hosting/SwitchForwarder.cs). 0 = written; 1 = no prod.keys; 2 = prod.keys lacks the keys;
// 3 = this NRO's path is unknown or unreadable; 4 = the NSP could not be written.
// Paths as Waddamburo writes them (/switch/...) or with the device (sdmc:/switch/...).
int nxfwd_make(const char *keys_in, const char *nsp_in) {
    char target[0x300], keys_path[0x300], nsp_path[0x300];
    if (!g_nro_path || !*g_nro_path) return 3;
#define SDMC(out, p) snprintf(out, sizeof out, "%s%s", strncmp(p, "sdmc:", 5) ? "sdmc:" : "", p)
    SDMC(target, g_nro_path); SDMC(keys_path, keys_in); SDMC(nsp_path, nsp_in);
    Buf keys = { 0 }, nro = { 0 }, nsp = { 0 };
    if (read_file(keys_path, &keys)) return 1;
    put(&keys, "", 1);
    if (read_file(target, &nro)) { free(keys.p); return 3; }
    int r = forwarder_nsp(&nsp, fwd_nso, fwd_nso_end - fwd_nso, fwd_npdm, fwd_npdm_end - fwd_npdm, nro.p, nro.n, (char *)keys.p, target);
    memset(keys.p, 0, keys.n); // the console's keys: not left in freed memory
    free(keys.p); free(nro.p);
    if (r) { free(nsp.p); return r == 1 ? 2 : 3; }
    FILE *f = fopen(nsp_path, "wb");
    int ok = f && fwrite(nsp.p, 1, nsp.n, f) == nsp.n;
    if (f && fclose(f)) ok = 0;
    free(nsp.p);
    if (!ok) { remove(nsp_path); return 4; }
    return 0;
}
#endif
