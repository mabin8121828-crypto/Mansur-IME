// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <msctf.h>

namespace mansur::win::edit {
enum class ControlFallback : unsigned {
    NotAttempted, NoView, NoViewWindow, NoFocus, OutsideView, DifferentThread,
    UnknownClass, StyleUnavailable, Disabled, ReadOnly, PasswordStyle,
    PasswordQueryFailed, MaskedPassword, Changed, PlainEdit
};
struct ControlTrace {
    ControlFallback result=ControlFallback::NotAttempted;
    HRESULT view=E_PENDING,window=E_PENDING,password=E_PENDING;
    DWORD style=0;
    wchar_t class_name[32]{};
    HWND owner=nullptr,focused=nullptr; // Transient identity only; never logged.
};
// Small native metadata seam for isolated tests; implementations never read text.
struct ControlMetadataApi {
    virtual ~ControlMetadataApi()=default;
    virtual HWND focus() const noexcept=0;
    virtual bool belongs_to_thread(HWND window) const noexcept=0;
    virtual bool child_of(HWND parent,HWND child) const noexcept=0;
    virtual bool class_name(HWND window,wchar_t* buffer,int capacity) const noexcept=0;
    virtual bool style(HWND window,DWORD& value) const noexcept=0;
    virtual HRESULT password_character(HWND window,bool& masked) const noexcept=0;
};
ControlFallback inspect_standard_control(ITfContext* context,ControlTrace& trace) noexcept;
#ifdef MANSUR_METADATA_TEST
const ControlMetadataApi& metadata_test_api() noexcept;
#endif
}
