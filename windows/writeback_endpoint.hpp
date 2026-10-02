// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include "writeback.hpp"
namespace mansur::win {
// WM_COPYDATA performs the cross-process copy, including x64/x86 callers. No
// pointer is ever passed through a private registered message.
class WritebackEndpoint {
public:
    using Handler=WritebackResult(*)(void*,const WritebackCommand&) noexcept;
    ~WritebackEndpoint();
    bool open(void*,Handler) noexcept;
    void close() noexcept;
    HWND window() const noexcept{return window_;}
private:
    static LRESULT CALLBACK procedure(HWND,UINT,WPARAM,LPARAM) noexcept;
    HWND window_=nullptr;DWORD thread_=0;void* owner_=nullptr;Handler handler_=nullptr;
};
}
