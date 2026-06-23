/* vom_test.c — receipt for the C VOM kernel. Proves parity with the C# VOM's core behaviour:
 * 256-aligned regions, usable memory, refcount free-on-zero, generational stale-id rejection.
 * Prints a receipt; returns 0 iff all checks pass. Build: cl /nologo vom.c vom_test.c /Fe:vom_test.exe */
#include "vom.h"
#include <stdio.h>
#include <stdint.h>
#include <string.h>

static int fails = 0;
static void check(int cond, const char* msg) {
    printf(cond ? "  ok   %s\n" : "  FAIL %s\n", msg);
    if (!cond) fails++;
}

int main(void) {
    VomOwner* o = vom_create_owner(256ull * 1024 * 1024, 100000);
    check(o != NULL, "owner created");

    /* alloc: 256-aligned padded region, usable memory, accounted */
    uint32_t bc = 0;
    VomHandle h = vom_alloc(o, 1000);
    void* p = vom_resolve(o, h, &bc);
    check(p != NULL,                         "alloc returned a region");
    check(bc == 1024,                        "byte count padded to a 256 multiple (1000 -> 1024)");
    check(((uintptr_t)p % VOM_ALIGN) == 0,   "backing pointer is 256-aligned");
    memset(p, 0xAB, bc);
    check(((unsigned char*)p)[bc - 1] == 0xAB, "the region is real, writable memory");
    check(vom_current_bytes(o) == 1024,      "owner accounts the native bytes");
    check(vom_live_count(o) == 1,            "owner accounts one live handle");

    /* refcount free-on-zero */
    check(vom_open(o, h) == true,            "Open increments the refcount (1 -> 2)");
    check(vom_close(o, h) == false,          "Close at 2 -> 1 does NOT free");
    check(vom_is_valid(o, h) == true,        "handle still valid while referenced");
    check(vom_close(o, h) == true,           "Close at 1 -> 0 frees (free-on-zero)");
    check(vom_is_valid(o, h) == false,       "freed handle is invalid");
    check(vom_current_bytes(o) == 0,         "native bytes reclaimed to zero");

    /* generational stale-id rejection + slot reuse */
    VomHandle a = vom_alloc(o, 4096);
    vom_close(o, a);                          /* free -> bump generation */
    check(vom_is_valid(o, a) == false,       "freed id rejected O(1)");
    VomHandle b = vom_alloc(o, 4096);         /* reuses the slot, new generation */
    check(b != a,                            "reused slot carries a bumped (different) id");
    check(vom_is_valid(o, b) == true,        "new-generation id is valid");
    check(vom_is_valid(o, a) == false,       "old id stays invalid after reuse (ABA-proof)");

    vom_terminate(o);

    printf("\n%s\n", fails == 0
        ? "PASS - C VOM kernel: 256-aligned native regions, refcount free-on-zero, generational handles."
        : "FAIL - C VOM kernel has failures (see above).");
    printf("{\"test\":\"vom.c.kernel\",\"pass\":%s,\"fails\":%d}\n", fails == 0 ? "true" : "false", fails);
    return fails == 0 ? 0 : 1;
}
