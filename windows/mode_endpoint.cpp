// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "mode_endpoint.hpp"
#include "module.hpp"
#include <limits>
namespace mansur::win {
namespace {
constexpr wchar_t window_class[]=L"Mansur.Next.Mode.v1";
struct Messages {
    UINT query=RegisterWindowMessageW(L"Mansur.Next.Mode.Query.v1");
    UINT set=RegisterWindowMessageW(L"Mansur.Next.Mode.Set.v1");
    UINT diagnostic=RegisterWindowMessageW(L"Mansur.Next.Mode.Diagnostic.v1");
};
const Messages& messages(){static const Messages value;return value;}
}
ModeEndpoint::~ModeEndpoint(){close();}
bool ModeEndpoint::open(void* owner,Handler handler) noexcept {
    try {
        if(window_)return thread_==GetCurrentThreadId()&&IsWindow(window_)!=FALSE;
        const auto& ids=messages();if(!ids.query||!ids.set||!ids.diagnostic)return false;
        WNDCLASSEXW type{};type.cbSize=sizeof(type);type.hInstance=module_instance;
        type.lpfnWndProc=procedure;type.lpszClassName=window_class;
        if(!RegisterClassExW(&type)&&GetLastError()!=ERROR_CLASS_ALREADY_EXISTS)return false;
        owner_=owner;handler_=handler;thread_=GetCurrentThreadId();
        window_=CreateWindowExW(0,window_class,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,module_instance,this);
        return window_!=nullptr;
    }catch(...){return false;}
}
void ModeEndpoint::close() noexcept {
    HWND window=window_;window_=nullptr;owner_=nullptr;handler_=nullptr;
    // TSF activation/deactivation and the message endpoint belong to one STA.
    if(window&&thread_==GetCurrentThreadId())DestroyWindow(window);
    UnregisterClassW(window_class,module_instance);thread_=0;
}
LRESULT CALLBACK ModeEndpoint::procedure(HWND window,UINT message,WPARAM w,LPARAM l) noexcept {
    if(message==WM_NCCREATE){auto cs=reinterpret_cast<CREATESTRUCTW*>(l);SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(cs->lpCreateParams));}
    auto self=reinterpret_cast<ModeEndpoint*>(GetWindowLongPtrW(window,GWLP_USERDATA));
    if(self&&self->handler_&&self->thread_==GetCurrentThreadId()) {
        const auto& ids=messages();ModeOperation operation=ModeOperation::Query;bool handled=true;
        if(message==ids.query){if(w||l)return 0;operation=ModeOperation::Query;}
        else if(message==ids.set){if(static_cast<ULONG_PTR>(w)>std::numeric_limits<std::uint32_t>::max()||(l!=0&&l!=1))return 0;operation=ModeOperation::Set;}
        else if(message==ids.diagnostic){if(w>29||l)return 0;operation=ModeOperation::Diagnostic;}
        else handled=false;
        if(handled){const auto handler=self->handler_;void* owner=self->owner_;
            return static_cast<LRESULT>(static_cast<ULONG_PTR>(handler(owner,operation,static_cast<std::uint32_t>(w),static_cast<std::uint32_t>(l))));}
    }
    if(message==WM_NCDESTROY){if(self&&self->window_==window)self->window_=nullptr;SetWindowLongPtrW(window,GWLP_USERDATA,0);}
    return DefWindowProcW(window,message,w,l);
}
}
