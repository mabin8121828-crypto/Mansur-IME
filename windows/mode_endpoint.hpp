// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <cstdint>
namespace mansur::win {
enum class ModeOperation { Query, Set, Diagnostic };
// Message-only native endpoint. No keyboard capture, foreground changes or text.
class ModeEndpoint {
public:
    using Handler=std::uint32_t(*)(void*,ModeOperation,std::uint32_t,std::uint32_t) noexcept;
    ~ModeEndpoint();
    bool open(void*,Handler) noexcept;
    void close() noexcept;
private:
    static LRESULT CALLBACK procedure(HWND,UINT,WPARAM,LPARAM) noexcept;
    HWND window_=nullptr;DWORD thread_=0;
    void* owner_=nullptr;Handler handler_=nullptr;
};
}
