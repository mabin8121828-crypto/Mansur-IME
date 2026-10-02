// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <filesystem>

namespace mansur::win {
enum class UiTheme : unsigned { System=0, Light=1, Dark=2 };
enum class CandidateLayout : unsigned { Horizontal=0, Vertical=1 };
struct NativeSettings {
    UiTheme theme=UiTheme::System;
    CandidateLayout layout=CandidateLayout::Horizontal;
    unsigned font_points=12;
    bool abbreviation=true;
    bool auto_remember=true;
    bool english_suggestions=false;
    std::uint32_t fuzzy_mask=0;
    bool chinese_mode=true; // Legacy data shape only; native routing ignores it.
    bool system_dark=false;
    std::filesystem::path user_lexicon;
    std::uint32_t lexicon_revision=0;
};
// Product preferences share %USERPROFILE%\.mansur-next\preferences.ini across
// packaged and normal hosts. Per-thread reads cache for 200 ms. Activation
// (include_lexicon=true) always refreshes; dictionaries are never read per key.
NativeSettings ReadNativeSettings(bool include_lexicon=false) noexcept;
NativeSettings SanitizeSettings(NativeSettings value) noexcept;
#ifdef MANSUR_SETTINGS_TEST
// An isolated test build can only read this explicit fixture path.
void SettingsTestPath(std::filesystem::path path);
void SettingsTestTick(std::uint64_t tick) noexcept;
unsigned SettingsTestReads() noexcept;
#endif
}
