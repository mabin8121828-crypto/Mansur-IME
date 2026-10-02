// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "control_metadata.hpp"
#include "com_ptr.hpp"
#include <richedit.h>
#include <cwchar>

namespace mansur::win::edit {
namespace {
class NativeControlMetadata final:public ControlMetadataApi {
public:
    HWND focus() const noexcept override{return GetFocus();}
    bool belongs_to_thread(HWND window) const noexcept override {
        DWORD process=0;const DWORD thread=GetWindowThreadProcessId(window,&process);
        return thread==GetCurrentThreadId()&&process==GetCurrentProcessId();
    }
    bool child_of(HWND parent,HWND child) const noexcept override{return IsChild(parent,child)!=FALSE;}
    bool class_name(HWND window,wchar_t* buffer,int capacity) const noexcept override {
        const int count=GetClassNameW(window,buffer,capacity);return count>0&&count<capacity-1;
    }
    bool style(HWND window,DWORD& value) const noexcept override {
        SetLastError(ERROR_SUCCESS);const LONG_PTR style=GetWindowLongPtrW(window,GWL_STYLE);
        if(!style&&GetLastError()!=ERROR_SUCCESS)return false;
        value=static_cast<DWORD>(style);return true;
    }
    HRESULT password_character(HWND window,bool& masked) const noexcept override {
        masked=false;
        // Never send to another input thread while a TSF read lock is held.
        // Same-thread messages call the standard control procedure directly;
        // Windows ignores the timeout in that case. This is not a hard deadline.
        if(!belongs_to_thread(window))return E_ACCESSDENIED;
        DWORD_PTR value=0;SetLastError(ERROR_SUCCESS);
        const LRESULT sent=SendMessageTimeoutW(window,EM_GETPASSWORDCHAR,0,0,
            SMTO_ABORTIFHUNG|SMTO_BLOCK|SMTO_ERRORONEXIT,30,&value);
        if(!sent) {const DWORD error=GetLastError();return HRESULT_FROM_WIN32(error?error:ERROR_TIMEOUT);}
        masked=value!=0;return S_OK;
    }
};
bool standard_class(const wchar_t* name) noexcept {
    // Exact SDK/Microsoft control classes, never executable or application names.
    constexpr const wchar_t* classes[]={L"Edit",L"RichEdit20A",L"RichEdit20W",L"RICHEDIT50W",
        L"RichEdit20WPT",L"RichEditD2D",L"RichEditD2DPT"};
    for(const auto candidate:classes)if(_wcsicmp(name,candidate)==0)return true;
    // RichEdit 1.0 is deliberately excluded: EM_GETPASSWORDCHAR is a 2.0+ API.
    return false;
}
}
ControlFallback inspect_standard_control(ITfContext* context,ControlTrace& trace) noexcept {
    trace={};
    auto finish=[&](ControlFallback result){trace.result=result;return result;};
#ifdef MANSUR_METADATA_TEST
    const auto& api=metadata_test_api();
#else
    const NativeControlMetadata api;
#endif
    ComPtr<ITfContextView> view;
    trace.view=context->GetActiveView(view.put());
    if(FAILED(trace.view)||!view)return finish(ControlFallback::NoView);
    HWND owner=nullptr;trace.window=view->GetWnd(&owner);
    if(FAILED(trace.window)||!owner)return finish(ControlFallback::NoViewWindow);
    const HWND focus=api.focus();
    trace.owner=owner;trace.focused=focus;
    if(!focus)return finish(ControlFallback::NoFocus);
    if(focus!=owner&&!api.child_of(owner,focus))return finish(ControlFallback::OutsideView);
    if(!api.belongs_to_thread(focus))return finish(ControlFallback::DifferentThread);
    wchar_t name[64]{};
    if(!api.class_name(focus,name,64)||!standard_class(name))return finish(ControlFallback::UnknownClass);
    wcscpy_s(trace.class_name,name); // Only one of the fixed classes above is retained.
    if(!api.style(focus,trace.style))return finish(ControlFallback::StyleUnavailable);
    if(trace.style&WS_DISABLED)return finish(ControlFallback::Disabled);
    if(_wcsicmp(name,L"Edit")!=0&&(trace.style&ES_NOIME))return finish(ControlFallback::Disabled);
    if(trace.style&ES_READONLY)return finish(ControlFallback::ReadOnly);
    if(trace.style&ES_PASSWORD)return finish(ControlFallback::PasswordStyle);
    bool masked=false;trace.password=api.password_character(focus,masked);
    if(FAILED(trace.password))return finish(ControlFallback::PasswordQueryFailed);
    if(masked)return finish(ControlFallback::MaskedPassword);
    ComPtr<ITfContextView> after_view;HWND after_owner=nullptr;
    if(FAILED(context->GetActiveView(after_view.put()))||!after_view||
        FAILED(after_view->GetWnd(&after_owner))||after_owner!=owner)
        return finish(ControlFallback::Changed);
    DWORD after=0;wchar_t after_class[64]{};
    if(api.focus()!=focus||(focus!=owner&&!api.child_of(owner,focus))||!api.belongs_to_thread(focus)||
        !api.style(focus,after)||after!=trace.style||!api.class_name(focus,after_class,64)||_wcsicmp(after_class,name)!=0)
        return finish(ControlFallback::Changed);
    return finish(ControlFallback::PlainEdit);
}
}
