// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <unknwn.h>
namespace mansur::win {
HRESULT create_text_service(REFIID iid,void** result) noexcept;
}
