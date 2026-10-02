// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <guiddef.h>

namespace mansur::win {
// Independent product identity; never reuse the previous installation's GUIDs.
inline constexpr GUID kTextService{0xe17225f9,0xb37a,0x4a39,{0xa6,0xfa,0x6e,0xc6,0x97,0x1c,0xb4,0x81}};
inline constexpr GUID kLanguageProfile{0x78e7761a,0x33de,0x4b91,{0xb7,0x51,0x82,0xc2,0xd9,0xcb,0x28,0x35}};
inline constexpr wchar_t kProductName[]=L"Mansur Next";
}
