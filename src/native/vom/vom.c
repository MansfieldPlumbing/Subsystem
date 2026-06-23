/* vom.c — the VOM C kernel (pure C, MSVC). Mirrors src/runspace/Vom/Vom.cs behaviour-for-behaviour:
 * 256-aligned native alloc, generational handle table with free-list slot reuse, refcount Open/Close,
 * deterministic free-on-zero (aligned-free + generation bump => stale ids fail O(1)). No GC. */
#include "vom.h"
#include <stdlib.h>
#include <string.h>
#include <malloc.h>   /* _aligned_malloc / _aligned_free */

typedef struct {
    void*    ptr;
    uint32_t byte_count;
    int32_t  refcount;
    uint16_t generation;   /* starts at 1; bumped on free (ABA / use-after-free guard) */
    bool     live;
} VomEntry;

struct VomOwner {
    VomEntry* slots;
    uint32_t* free_list;
    uint32_t  capacity;
    uint32_t  high;        /* next fresh index; index 0 reserved => handle 0 is null */
    uint32_t  free_count;
    uint64_t  max_bytes;
    uint32_t  max_elements;
    uint64_t  current_bytes;
    uint32_t  current_elements;
};

static void grow(VomOwner* o, uint32_t needed) {
    uint32_t n = o->capacity ? o->capacity : 64;
    while (n <= needed) n <<= 1;
    o->slots     = (VomEntry*)realloc(o->slots, (size_t)n * sizeof(VomEntry));
    o->free_list = (uint32_t*)realloc(o->free_list, (size_t)n * sizeof(uint32_t));
    memset(o->slots + o->capacity, 0, (size_t)(n - o->capacity) * sizeof(VomEntry));
    o->capacity = n;
}

static inline uint32_t idx_of(VomHandle h) { return h & 0xFFFFu; }
static inline uint16_t gen_of(VomHandle h) { return (uint16_t)(h >> 16); }

static bool live_at(VomOwner* o, VomHandle h) {
    uint32_t idx = idx_of(h);
    return idx > 0 && idx < o->high && o->slots[idx].live && o->slots[idx].generation == gen_of(h);
}

VomOwner* vom_create_owner(uint64_t max_bytes, uint32_t max_elements) {
    VomOwner* o = (VomOwner*)calloc(1, sizeof(VomOwner));
    if (!o) return NULL;
    o->high = 1;                 /* index 0 is the null handle */
    o->max_bytes = max_bytes;
    o->max_elements = max_elements;
    grow(o, 64);
    return o;
}

static uint32_t take_index(VomOwner* o) {
    uint32_t idx = (o->free_count > 0) ? o->free_list[--o->free_count] : o->high++;
    if (idx >= o->capacity) grow(o, idx);
    if (o->slots[idx].generation == 0) o->slots[idx].generation = 1;  /* generations start at 1 */
    return idx;
}

VomHandle vom_alloc(VomOwner* o, uint32_t byte_count) {
    uint32_t padded = (byte_count + (VOM_ALIGN - 1)) & ~(VOM_ALIGN - 1);
    void* p = _aligned_malloc(padded, VOM_ALIGN);
    if (!p) return 0;
    uint32_t idx = take_index(o);
    VomEntry* e = &o->slots[idx];
    e->ptr = p; e->byte_count = padded; e->refcount = 1; e->live = true;
    o->current_bytes += padded; o->current_elements++;
    return ((uint32_t)e->generation << 16) | idx;
}

void* vom_resolve(VomOwner* o, VomHandle h, uint32_t* out_byte_count) {
    if (!live_at(o, h)) return NULL;
    VomEntry* e = &o->slots[idx_of(h)];
    if (out_byte_count) *out_byte_count = e->byte_count;
    return e->ptr;
}

bool vom_open(VomOwner* o, VomHandle h) {
    if (!live_at(o, h)) return false;
    o->slots[idx_of(h)].refcount++;
    return true;
}

static void free_entry(VomOwner* o, uint32_t idx) {
    VomEntry* e = &o->slots[idx];
    if (e->ptr) { _aligned_free(e->ptr); e->ptr = NULL; }
    o->current_bytes -= e->byte_count;
    o->current_elements--;
    e->live = false;
    e->generation++; if (e->generation == 0) e->generation = 1;   /* bump => every stale id now invalid */
    o->free_list[o->free_count++] = idx;
}

bool vom_close(VomOwner* o, VomHandle h) {
    if (!live_at(o, h)) return false;
    if (--o->slots[idx_of(h)].refcount > 0) return false;          /* still referenced */
    free_entry(o, idx_of(h));                                      /* free-on-zero */
    return true;
}

bool     vom_is_valid(VomOwner* o, VomHandle h) { return live_at(o, h); }
uint32_t vom_live_count(VomOwner* o) { return o->current_elements; }
uint64_t vom_current_bytes(VomOwner* o) { return o->current_bytes; }

void vom_terminate(VomOwner* o) {
    if (!o) return;
    for (uint32_t i = 1; i < o->high; i++) if (o->slots[i].live) free_entry(o, i);
    free(o->slots); free(o->free_list); free(o);
}
