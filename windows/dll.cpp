// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "module.hpp"
#include "host_policy.hpp"
#include "ids.hpp"
#include "tsf_service.hpp"
#include <new>

namespace mansur::win {
HINSTANCE module_instance=nullptr;
std::atomic<long> live_objects{0};
std::atomic<long> server_locks{0};
namespace {
class Factory final:public IClassFactory,private ModuleObject {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) noexcept override {
        if(!out) return E_POINTER;*out=nullptr;
        if(iid!=IID_IUnknown&&iid!=IID_IClassFactory) return E_NOINTERFACE;
        *out=static_cast<IClassFactory*>(this);AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override{return ++references_;}
    ULONG STDMETHODCALLTYPE Release() noexcept override {const auto count=--references_;if(!count)delete this;return count;}
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID iid,void** out) noexcept override {
        if(!out) return E_POINTER;*out=nullptr;
        if(outer) return CLASS_E_NOAGGREGATION;
        return create_text_service(iid,out);
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) noexcept override {
        if(lock) ++server_locks;
        else {long count=server_locks.load();while(count>0&&!server_locks.compare_exchange_weak(count,count-1)) {}}
        return S_OK;
    }
private:std::atomic<ULONG> references_{1};
};
}
}
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid,REFIID iid,void** out) {
    using namespace mansur::win;
    if(!out) return E_POINTER;*out=nullptr;
    try {
        if(clsid!=kTextService||!host_can_create(current_host_policy())) return CLASS_E_CLASSNOTAVAILABLE;
        auto factory=new Factory();const HRESULT hr=factory->QueryInterface(iid,out);factory->Release();return hr;
    } catch(const std::bad_alloc&) {return E_OUTOFMEMORY;} catch(...) {return E_FAIL;}
}
extern "C" HRESULT __stdcall DllCanUnloadNow() {
    return mansur::win::live_objects==0&&mansur::win::server_locks==0?S_OK:S_FALSE;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID) {
    if(reason==DLL_PROCESS_ATTACH) mansur::win::module_instance=instance;
    return TRUE;
}
