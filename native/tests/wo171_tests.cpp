// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// WO-171 M: engine-free checks of the marker picture's name table and the literal swap that gives a mark its picture.
// Linked into KCDMP_NativeTests; wo171_tests() returns the number of failures.
#include <cstdio>
#include <cstring>

#include "wo171_rules.h"

using namespace kcdmp::wo171rules;

namespace {
int g_fail = 0, g_pass = 0;
#define DCHECK(cond, ...) do { if (cond) ++g_pass; else { ++g_fail; std::printf("FAIL  %s:%d  ", __FILE__, __LINE__); std::printf(__VA_ARGS__); std::printf("\n"); } } while (0)
} // namespace

int wo171_tests(int* passed) {
    g_fail = g_pass = 0;
    // the 32 names: colour letter x frame digit, unique, all fit a general-poi slot
    {
        char seen[kPictures][8]{};
        const char* want = "rpbg";
        for (unsigned code = 1; code <= kPictures; ++code) {
            char n[8]{};
            DCHECK(picture_name(code, n, sizeof n), "code %u has a name", code);
            const unsigned c = (code - 1) / 8, f = (code - 1) % 8;
            char exp[8]{}; std::snprintf(exp, sizeof exp, "kct_%c%u", want[c], f);
            DCHECK(std::strcmp(n, exp) == 0, "code %u is %s (got %s)", code, exp, n);
            DCHECK(std::strlen(n) + 1 <= 11, "%s fits the 12-byte GeneralPoi slot", n);
            std::memcpy(seen[code - 1], n, 8);
            for (unsigned k = 1; k < code; ++k) DCHECK(std::strcmp(seen[k - 1], n) != 0, "%s is unique", n);
        }
        char n[8];
        DCHECK(!picture_name(0, n, sizeof n), "code 0 = no picture");
        DCHECK(!picture_name(33, n, sizeof n), "code 33 is out of range");
        DCHECK(!picture_name(1, n, 6), "a buffer too small is refused");
        DCHECK(picture_code_ok(0) && picture_code_ok(32) && !picture_code_ok(33), "the pipe's code range");
    }
    // the literal swap, on a copy of the game's rdata around "GeneralPoi" (12-byte slot) and its neighbours
    {
        char rdata[40];
        std::memset(rdata, 0, sizeof rdata);
        std::memcpy(rdata + 0, "GeneralPoi", 11);     // 10 chars + NUL + one NUL of padding = 12 bytes before "Bed"
        std::memcpy(rdata + 12, "Bed", 4);
        std::memcpy(rdata + 20, "Butchery", 9);
        DCHECK(literal_slot(rdata) == 12, "the slot of GeneralPoi is 12 bytes (got %zu)", literal_slot(rdata));
        DCHECK(literal_slot(rdata + 12) == 8, "Bed + NUL + padding up to Butchery is 8 bytes (got %zu)", literal_slot(rdata + 12));
        char saved[16];
        DCHECK(literal_swap(rdata, 12, "GeneralPoi", "kct_r3", saved), "the swap succeeds");
        DCHECK(std::strcmp(rdata, "kct_r3") == 0, "the literal now reads the picture's name");
        DCHECK(std::strcmp(rdata + 12, "Bed") == 0 && std::strcmp(rdata + 20, "Butchery") == 0, "the neighbours are untouched");
        literal_restore(rdata, 12, saved);
        DCHECK(std::memcmp(rdata, "GeneralPoi\0\0", 12) == 0, "restored byte for byte, padding included");
        DCHECK(std::strcmp(rdata + 12, "Bed") == 0, "the neighbour is still intact after the restore");
        // not what is expected: nothing written
        char other[12] = "SomethingEl";
        DCHECK(!literal_swap(other, 12, "GeneralPoi", "kct_r3", saved) && std::strcmp(other, "SomethingEl") == 0, "an unexpected literal is left alone");
        // a name too long for the slot
        char small[8] = "Dog";
        DCHECK(!literal_swap(small, 4, "Dog", "kct_r3", saved) && std::strcmp(small, "Dog") == 0, "a name longer than the slot is refused, nothing written");
    }
    if (passed) *passed = g_pass;
    return g_fail;
}
