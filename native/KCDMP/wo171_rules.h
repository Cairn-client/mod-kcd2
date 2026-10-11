// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
// WO-171 M: the pure rules of the partner marker's picture (unit-tested in native/tests/wo171_tests.cpp). No engine access here.
//
// How a mark gets its picture: C_UIMap's mark copies the game's own name for its type (GUIModule's type -> name function: the
// 0x30 "GeneralPoi" literal) into itself when it is made, and the map draws Libs/UI/Textures/Icons/Map/<name>_icon.dds. For the
// one call that makes OUR pin's mark, the literal's bytes are swapped for our picture's name (kct_r3 ...) and put back at once:
// the game's own copy routine does the copying, no game code is patched, and every other mark is made from the original bytes.

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace kcdmp::wo171rules {

constexpr int kColours = 4;     // red, purple, blue, green (players 1-4)
constexpr int kFrames = 8;      // the art's rotation frames
constexpr int kPictures = kColours * kFrames;
constexpr size_t kNameMax = 16;   // "kct_r3" is 6; the literal's own slot is the real limit (see literal_slot)

// A picture code on the pipe: 0 = none (the game's own companion / general icon), 1..32 = colour * 8 + frame + 1.
inline bool picture_code_ok(unsigned code) { return code <= static_cast<unsigned>(kPictures); }

// "kct_r3": colour letter (r p b g), frame digit 0-7. False for code 0 or out of range.
inline bool picture_name(unsigned code, char* out, size_t cap) {
    if (code == 0 || code > static_cast<unsigned>(kPictures) || cap < 7) return false;
    const unsigned c = (code - 1) / kFrames, f = (code - 1) % kFrames;
    static const char kLetter[kColours] = {'r', 'p', 'b', 'g'};
    out[0] = 'k'; out[1] = 'c'; out[2] = 't'; out[3] = '_'; out[4] = kLetter[c]; out[5] = static_cast<char>('0' + f); out[6] = 0;
    return true;
}

// The bytes a literal owns: its characters, its NUL, and the NUL padding after it (the next literal starts at the first
// non-NUL byte). Reads at most `probe` bytes past the characters; the caller has checked the range is readable.
inline size_t literal_slot(const char* lit, size_t probe = 8) {
    const size_t n = std::strlen(lit);
    size_t slot = n + 1;
    while (slot - n - 1 < probe && lit[slot] == 0) ++slot;
    return slot;
}

// Swap the literal `lit` (currently exactly `expect`) for `name`, saving its slot's bytes into `saved` (slot bytes).
// False -- and nothing written -- when the literal is not what is expected or the name does not fit its slot.
inline bool literal_swap(char* lit, size_t slot, const char* expect, const char* name, char* saved) {
    if (std::strcmp(lit, expect) != 0) return false;
    const size_t need = std::strlen(name) + 1;
    if (need > slot || slot > kNameMax) return false;
    std::memcpy(saved, lit, slot);
    std::memset(lit, 0, slot);
    std::memcpy(lit, name, need);
    return true;
}

inline void literal_restore(char* lit, size_t slot, const char* saved) { std::memcpy(lit, saved, slot); }

} // namespace kcdmp::wo171rules
