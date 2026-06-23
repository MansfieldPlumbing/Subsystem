/* vom.h — the VOM C ABI: the parity seam.
 *
 * The same kernel contract as the C# VOM (src/runspace/Vom/Vom.cs), distilled from the DirectPort
 * region model to its CPU core: 256-byte-aligned native regions, generational handles, refcount
 * Open/Close, deterministic free-on-zero. A flat C ABI (extern "C") so C++, C#, Rust, and AOT can all
 * drive the SAME heap over one base pointer — the interop-agnostic substrate.
 *
 * This is the kernel's data plane. Namespace paths, owners-tree, fences, and quota ENFORCEMENT are
 * projections/policies layered above; here a handle is an id, authority is the refcount, and reclaim
 * is deterministic at zero. Parity with the C# side is the contract below, behaviour-for-behaviour.
 */
#pragma once
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

#define VOM_ALIGN 256u            /* row-pitch / DMA / cache-line stride (VOM-SPEC §3) */

typedef struct VomOwner VomOwner; /* opaque */
typedef uint32_t VomHandle;       /* [16-bit generation | 16-bit index]; 0 == the null handle */

/* An owner is a handle table + advisory quota counters. Terminate frees every live handle + the owner. */
VomOwner* vom_create_owner(uint64_t max_bytes, uint32_t max_elements);
void      vom_terminate(VomOwner* o);

/* Take a handle: a 256-aligned native region, refcount 1. Returns 0 on OOM. */
VomHandle vom_alloc(VomOwner* o, uint32_t byte_count);

/* Resolve the backing pointer (kernel-internal use); NULL if the handle is stale. out_byte_count optional. */
void*     vom_resolve(VomOwner* o, VomHandle h, uint32_t* out_byte_count);

bool      vom_open (VomOwner* o, VomHandle h);   /* refcount++  (false if stale) */
bool      vom_close(VomOwner* o, VomHandle h);   /* refcount--; frees at zero. true == freed this call */
bool      vom_is_valid(VomOwner* o, VomHandle h);/* generational O(1) stale check */

uint32_t  vom_live_count(VomOwner* o);
uint64_t  vom_current_bytes(VomOwner* o);

#ifdef __cplusplus
}
#endif
