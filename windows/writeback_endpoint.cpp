// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "writeback_endpoint.hpp"
#include "module.hpp"
namespace mansur::win {
namespace {constexpr wchar_t window_class[]=L"Mansur.Next.Writeback.v1";}
WritebackEndpoint::~WritebackEndpoint(){close();}
bool WritebackEndpoint::open(void* owner,Handler handler) noexcept {
    try {
        if(window_)return thread_==GetCurrentThreadId()&&IsWindow(window_)!=FALSE;
        WNDCLASSEXW type{};type.cbSize=sizeof(type);type.hInstance=module_instance;
        type.lpfnWndProc=procedure;type.lpszClassName=window_class;
        if(!RegisterClassExW(&type)&&GetLastError()!=ERROR_CLASS_ALREADY_EXISTS)return false;
        owner_=owner;handler_=handler;thread_=GetCurrentThreadId();
        window_=CreateWindowExW(0,window_class,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,module_instance,this);
        return window_!=nullptr;
    }catch(...){return false;}
}
void WritebackEndpoint::close() noexcept {
    HWND window=window_;window_=nullptr;owner_=nullptr;handler_=nullptr;
    if(window&&thread_==GetCurrentThreadId())DestroyWindow(window);
    UnregisterClassW(window_class,module_instance);thread_=0;
}
LRESULT CALLBACK WritebackEndpoint::procedure(HWND window,UINT message,WPARAM w,LPARAM l) noexcept {
    if(message==WM_NCCREATE){auto cs=reinterpret_cast<CREATESTRUCTW*>(l);SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(cs->lpCreateParams));}
    auto self=reinterpret_cast<WritebackEndpoint*>(GetWindowLongPtrW(window,GWLP_USERDATA));
    if(message==WM_COPYDATA&&self&&self->handler_&&self->thread_==GetCurrentThreadId()) {
        auto packet=reinterpret_cast<const COPYDATASTRUCT*>(l);WritebackCommand command;
        if(!packet||packet->dwData!=kWritebackPacketTag||!ParseWritebackPacket(packet->lpData,packet->cbData,command))return 0;
        const auto handler=self->handler_;void* owner=self->owner_;
        return static_cast<LRESULT>(handler(owner,command));
    }
    if(message==WM_NCDESTROY){if(self&&self->window_==window)self->window_=nullptr;SetWindowLongPtrW(window,GWLP_USERDATA,0);}
    return DefWindowProcW(window,message,w,l);
}
}
