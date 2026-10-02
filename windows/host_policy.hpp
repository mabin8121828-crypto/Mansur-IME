// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <string_view>

namespace mansur::win {
enum class HostDecision { Allowed, ExcludedSystemHost, UnknownPath, StandardEditOnly };
constexpr bool host_can_create(HostDecision value) noexcept {
    return value==HostDecision::Allowed||value==HostDecision::StandardEditOnly;
}
HostDecision classify_host(std::wstring_view executable, std::wstring_view windows_directory);
HostDecision current_host_policy() noexcept;
}
