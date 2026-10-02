// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include <windows.h>
#include <msctf.h>
#include "ids.hpp"
#include <iostream>
#include <stdexcept>
#include <cstdint>
void check(bool value,const char* label) {if(!value) throw std::runtime_error(label);}
int wmain(int argc,wchar_t** argv) {
    if(argc!=2)return 2;
    const HRESULT initialized=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    if(FAILED(initialized))return 3;
    HMODULE library=LoadLibraryW(argv[1]);
    if(!library) {CoUninitialize();return 4;}
    using Get=HRESULT(__stdcall*)(REFCLSID,REFIID,void**);
    using Can=HRESULT(__stdcall*)();
    auto get=reinterpret_cast<Get>(GetProcAddress(library,"DllGetClassObject"));
    auto can=reinterpret_cast<Can>(GetProcAddress(library,"DllCanUnloadNow"));
    int result=0;
    try {
        check(get&&can,"entry exports available");
        check(can()==S_OK,"empty DLL unloadable");
        check(get(mansur::win::kTextService,IID_IClassFactory,nullptr)==E_POINTER,"null output rejected");
        void* output=reinterpret_cast<void*>(static_cast<uintptr_t>(1));
        check(get(GUID_NULL,IID_IClassFactory,&output)==CLASS_E_CLASSNOTAVAILABLE&&output==nullptr,"unknown class clears output");
        check(get(mansur::win::kTextService,IID_IClassFactory,&output)==S_OK&&output,"class factory created without activation");
        auto factory=static_cast<IClassFactory*>(output);
        check(can()==S_FALSE,"factory pins DLL");
        output=reinterpret_cast<void*>(static_cast<uintptr_t>(1));
        check(factory->QueryInterface(GUID_NULL,&output)==E_NOINTERFACE&&output==nullptr,"unknown factory interface clears output");
        check(factory->CreateInstance(factory,IID_ITfTextInputProcessorEx,&output)==CLASS_E_NOAGGREGATION&&output==nullptr,"aggregation rejected");
        check(factory->CreateInstance(nullptr,IID_ITfTextInputProcessorEx,&output)==S_OK&&output,"independent service constructible");
        auto service=static_cast<ITfTextInputProcessorEx*>(output);
        IUnknown* identity=nullptr;ITfKeyEventSink* keys=nullptr;IUnknown* key_identity=nullptr;
        check(service->QueryInterface(IID_IUnknown,reinterpret_cast<void**>(&identity))==S_OK,"service identity");
        check(service->QueryInterface(IID_ITfKeyEventSink,reinterpret_cast<void**>(&keys))==S_OK,"key sink interface");
        check(keys->QueryInterface(IID_IUnknown,reinterpret_cast<void**>(&key_identity))==S_OK&&identity==key_identity,"canonical COM identity");
        BOOL eaten=TRUE;
        check(keys->OnTestKeyDown(nullptr,'N',0,&eaten)==S_OK&&!eaten,"inactive service passes keys");
        eaten=TRUE;
        check(keys->OnKeyDown(nullptr,'N',0,&eaten)==S_OK&&!eaten,"no context passes keys");
        check(keys->OnTestKeyDown(nullptr,'N',0,nullptr)==E_POINTER,"key output checked");
        check(service->ActivateEx(nullptr,1,0)==E_INVALIDARG,"null manager rejected");
        check(service->Deactivate()==S_OK&&service->Deactivate()==S_OK,"deactivation idempotent");
        key_identity->Release();keys->Release();identity->Release();service->Release();
        factory->LockServer(TRUE);factory->Release();
        check(can()==S_FALSE,"server lock pins DLL");
        check(get(mansur::win::kTextService,IID_IClassFactory,&output)==S_OK,"unlock factory");
        factory=static_cast<IClassFactory*>(output);factory->LockServer(FALSE);factory->Release();
        check(can()==S_OK,"all COM objects and lock released");
        std::cout<<"19 TSF entry checks passed (not activated, not registered, no UI).\n";
    } catch(const std::exception& e) {std::cerr<<e.what()<<'\n';result=1;}
    FreeLibrary(library);CoUninitialize();return result;
}
