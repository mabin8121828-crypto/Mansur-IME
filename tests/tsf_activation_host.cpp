// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
// An explicitly invoked, nonvisual process testing the already-installed TIP.
// Profile activation flags are zero: current test thread only, no registry,
// session or process-wide default changes; no key injection or external UI.
#include <windows.h>
#include <msctf.h>
#include "../windows/com_ptr.hpp"
#include "../windows/ids.hpp"
#include <iostream>
#include <stdexcept>
#include <string_view>
#include <vector>
using mansur::win::ComPtr;
static constexpr LANGID targetLanguage=0x0804;
static LANGID language(HKL layout) {return LOWORD(reinterpret_cast<ULONG_PTR>(layout));}
static HKL loadedChineseLayout() {
    const int count=GetKeyboardLayoutList(0,nullptr);
    if(count<=0||count>256)return nullptr;
    std::vector<HKL> layouts(static_cast<std::size_t>(count));
    const int read=GetKeyboardLayoutList(count,layouts.data());
    if(read<=0||read>count)return nullptr;
    for(int i=0;i<read;++i)if(language(layouts[static_cast<std::size_t>(i)])==targetLanguage)return layouts[static_cast<std::size_t>(i)];
    return nullptr;
}
struct Precondition : std::runtime_error {using std::runtime_error::runtime_error;};
static void selectPrivateThreadLanguage() {
    if(language(GetKeyboardLayout(0))==targetLanguage)return;
    const HKL layout=loadedChineseLayout();
    if(!layout)throw Precondition("Chinese input locale is not loaded in the verification environment");
    // Zero flags affect only this thread. Never load/install a layout, reorder
    // defaults, set a session-wide profile, or change an external window.
    if(!ActivateKeyboardLayout(layout,0)||language(GetKeyboardLayout(0))!=targetLanguage)
        throw Precondition("Could not establish Chinese locale in the private verification thread");
}
static void checked(HRESULT hr,const char* label) {
    if(hr!=S_OK){std::cerr<<label<<" hr=0x"<<std::hex<<static_cast<unsigned long>(hr)<<'\n';throw std::runtime_error(label);}
}
struct NativeState {bool present=false;ULONG_PTR handle=0;std::uint32_t flags=0,activations=0;};
static NativeState ownNativeState() {
    const UINT message=RegisterWindowMessageW(L"Mansur.Next.Mode.Diagnostic.v1");
    HWND after=nullptr;
    for(unsigned inspected=0;message&&inspected<128;++inspected) {
        after=FindWindowExW(HWND_MESSAGE,after,L"Mansur.Next.Mode.v1",nullptr);
        if(!after)break;
        DWORD process=0;const DWORD thread=GetWindowThreadProcessId(after,&process);
        if(process!=GetCurrentProcessId()||thread!=GetCurrentThreadId())continue;
        if(SendMessageW(after,message,0,0)!=1)continue;
        return {true,reinterpret_cast<ULONG_PTR>(after),static_cast<std::uint32_t>(SendMessageW(after,message,1,0)),
            static_cast<std::uint32_t>(SendMessageW(after,message,2,0))};
    }
    return {};
}
static void diagnose(const wchar_t* stage,ITfInputProcessorProfileMgr* profiles,ITfThreadMgr* manager,
    int host,int cycle,const wchar_t* expectedPath) {
    TF_INPUTPROCESSORPROFILE active{};
    const HRESULT profileHr=profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD,&active);
    wchar_t clsid[40]{},profile[40]{};
    StringFromGUID2(active.clsid,clsid,40);StringFromGUID2(active.guidProfile,profile,40);
    ComPtr<ITfDocumentMgr> document;BOOL focus=FALSE;
    const HRESULT documentHr=manager->GetFocus(document.put()),focusHr=manager->IsThreadFocus(&focus);
    const HMODULE module=GetModuleHandleW(L"mansur_next_tsf.dll");
    wchar_t path[32768]{};DWORD pathError=ERROR_SUCCESS;
    const DWORD length=module?GetModuleFileNameW(module,path,32768):0;
    if(module&&!length)pathError=GetLastError();
    const auto native=ownNativeState();
    ComPtr<ITfCompartmentMgr> compartments;ComPtr<ITfCompartment> mode;VARIANT value;VariantInit(&value);
    HRESULT modeHr=manager->QueryInterface(IID_ITfCompartmentMgr,reinterpret_cast<void**>(compartments.put()));
    if(SUCCEEDED(modeHr))modeHr=compartments->GetCompartment(GUID_COMPARTMENT_KEYBOARD_OPENCLOSE,mode.put());
    if(SUCCEEDED(modeHr))modeHr=mode->GetValue(&value);
    std::wcerr<<L"DIAG stage="<<stage<<L" host="<<std::dec<<host<<L" cycle="<<cycle
        <<L" locale=0x"<<std::hex<<language(GetKeyboardLayout(0))
        <<L" active_hr=0x"<<static_cast<unsigned long>(profileHr)
        <<L" active_type="<<std::dec<<active.dwProfileType<<L" active_lang=0x"<<std::hex<<active.langid
        <<L" active_clsid="<<clsid<<L" active_profile="<<profile
        <<L" document_hr=0x"<<static_cast<unsigned long>(documentHr)<<L" has_document="<<(document?1:0)
        <<L" thread_focus_hr=0x"<<static_cast<unsigned long>(focusHr)<<L" thread_focus="<<focus
        <<L" native_present="<<native.present<<L" native_hwnd=0x"<<native.handle<<L" native_flags=0x"<<native.flags
        <<L" activation_count="<<std::dec<<native.activations<<L" mode_hr=0x"<<std::hex<<static_cast<unsigned long>(modeHr)
        <<L" mode_type="<<std::dec<<value.vt<<L" mode_i4="<<(value.vt==VT_I4?value.lVal:0)
        <<L" module_present="<<(module?1:0)<<L" module_path_error="<<pathError
        <<L" actual_module=\""<<(length?path:L"<not loaded>")<<L"\" expected_module=\""<<expectedPath<<L"\"\n";
    VariantClear(&value);
}
static void pumpPrivateMessages(DWORD milliseconds,bool stopWhenModulePresent=false) {
    const ULONGLONG start=GetTickCount64();unsigned processed=0;
    do {
        MSG message{};
        while(processed<256&&GetTickCount64()-start<milliseconds&&PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) {
            ++processed;
            if(message.message==WM_QUIT)throw Precondition("Private verification message loop received quit");
            TranslateMessage(&message);DispatchMessageW(&message);
        }
        if(processed==256||(stopWhenModulePresent&&GetModuleHandleW(L"mansur_next_tsf.dll")))return;
        const ULONGLONG elapsed=GetTickCount64()-start;
        if(elapsed>=milliseconds)return;
        MsgWaitForMultipleObjectsEx(0,nullptr,static_cast<DWORD>((milliseconds-elapsed)<20?(milliseconds-elapsed):20),QS_ALLINPUT,MWMO_INPUTAVAILABLE);
    }while(GetTickCount64()-start<milliseconds);
}
int wmain(int argc,wchar_t** argv) {
    if(argc==2&&std::wstring_view(argv[1])==L"--inspect-language") {
        const bool available=language(GetKeyboardLayout(0))==targetLanguage||loadedChineseLayout()!=nullptr;
        std::cout<<"READ_ONLY: current_language=0x"<<std::hex<<language(GetKeyboardLayout(0))<<"; loaded_chinese="<<available<<"; no locale/profile activation.\n";
        return available?0:10;
    }
    if(argc<2||argc>3)return 2;
    const bool modes=argc==3&&std::wstring_view(argv[2])==L"--mode-reset";
    if(argc==3&&!modes)return 2;
    if(FAILED(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED)))return 3;
    int result=0;
    try {
        ComPtr<ITfInputProcessorProfileMgr> profiles;
        checked(CoCreateInstance(CLSID_TF_InputProcessorProfiles,nullptr,CLSCTX_INPROC_SERVER,
            IID_ITfInputProcessorProfileMgr,reinterpret_cast<void**>(profiles.put())),"profile manager");
        TF_INPUTPROCESSORPROFILE original{};
        const bool had=profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD,&original)==S_OK;
        const HKL originalLocale=GetKeyboardLayout(0);
        if(!originalLocale)throw Precondition("Could not identify the private verification thread locale");
        struct Restore {
            ITfInputProcessorProfileMgr* profiles;TF_INPUTPROCESSORPROFILE original;bool had;HKL locale;bool attempted=false;
            bool apply() {
                attempted=true;
                const bool localeOk=GetKeyboardLayout(0)==locale||(ActivateKeyboardLayout(locale,0)&&GetKeyboardLayout(0)==locale);
                const bool profileOk=!had||profiles->ActivateProfile(original.dwProfileType,original.langid,original.clsid,original.guidProfile,original.hkl,0)==S_OK;
                return localeOk&&profileOk;
            }
            ~Restore(){if(!attempted)apply();}
        } restore{profiles.get(),original,had,originalLocale};
        selectPrivateThreadLanguage();
        for(int host=0;host<3;++host) {
            ComPtr<ITfThreadMgr> manager;
            checked(CoCreateInstance(CLSID_TF_ThreadMgr,nullptr,CLSCTX_INPROC_SERVER,IID_ITfThreadMgr,
                reinterpret_cast<void**>(manager.put())),"thread manager");
            TfClientId client=TF_CLIENTID_NULL;
            checked(manager->Activate(&client),"activate private manager");
            struct Scope {ITfThreadMgr* manager;~Scope(){manager->Deactivate();}} scope{manager.get()};
            for(int cycle=0;cycle<20;++cycle) {
                if(cycle==0)diagnose(L"before-profile",profiles.get(),manager.get(),host,cycle,argv[1]);
                checked(profiles->ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR,targetLanguage,mansur::win::kTextService,
                    mansur::win::kLanguageProfile,nullptr,0),"activate profile in test thread");
                // ActivateProfile confirms profile selection, not a synchronous
                // DLL load in a nonfocused process with no document. Give only
                // this thread's queued work a limited opportunity to complete.
                pumpPrivateMessages(250);
                if(cycle==0)diagnose(L"after-profile",profiles.get(),manager.get(),host,cycle,argv[1]);
                auto module=GetModuleHandleW(L"mansur_next_tsf.dll");wchar_t path[32768]{};
                if(!module) {
                    diagnose(L"module-not-loaded",profiles.get(),manager.get(),host,cycle,argv[1]);
                    throw Precondition("PROFILE_SELECTED_BUT_TIP_NOT_LOADED: nonvisual verification could not establish a loaded text service; no lifecycle or mode-reset pass claimed");
                }
                const DWORD length=GetModuleFileNameW(module,path,32768);
                if(!length||length>=32768) {
                    diagnose(L"module-path-unavailable",profiles.get(),manager.get(),host,cycle,argv[1]);
                    throw Precondition("Loaded module identity could not be read");
                }
                if(_wcsicmp(path,argv[1])!=0) {
                    diagnose(L"module-path-mismatch",profiles.get(),manager.get(),host,cycle,argv[1]);
                    throw std::runtime_error("INSTALLED_MODULE_PATH_MISMATCH: TSF loaded a different module path");
                }
                TF_INPUTPROCESSORPROFILE active{};
                if(profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD,&active)!=S_OK||
                    active.dwProfileType!=TF_PROFILETYPE_INPUTPROCESSOR||active.langid!=targetLanguage||
                    active.clsid!=mansur::win::kTextService||active.guidProfile!=mansur::win::kLanguageProfile) {
                    diagnose(L"active-profile-unconfirmed",profiles.get(),manager.get(),host,cycle,argv[1]);
                    throw Precondition("Current private-thread active profile was not confirmed");
                }
                const auto beforeMode=ownNativeState();
                if(!beforeMode.present||(beforeMode.flags&1u)==0) {
                    diagnose(L"tip-activation-unconfirmed",profiles.get(),manager.get(),host,cycle,argv[1]);
                    ComPtr<ITfDocumentMgr> focusedDocument;BOOL threadFocus=FALSE;
                    const HRESULT documentHr=manager->GetFocus(focusedDocument.put()),focusHr=manager->IsThreadFocus(&threadFocus);
                    if(FAILED(documentHr)||FAILED(focusHr))throw Precondition("Private thread focus prerequisites could not be established");
                    if(!focusedDocument&&!threadFocus)throw Precondition("TIP activation was not observed in the private nonfocused thread with no document");
                    throw std::runtime_error("TIP_ACTIVATION_NOT_OBSERVED: profile activation did not activate the service");
                }
                if(modes) {
                    ComPtr<ITfCompartmentMgr> compartments;ComPtr<ITfCompartment> mode;
                    checked(manager->QueryInterface(IID_ITfCompartmentMgr,reinterpret_cast<void**>(compartments.put())),"compartment manager");
                    checked(compartments->GetCompartment(GUID_COMPARTMENT_KEYBOARD_OPENCLOSE,mode.put()),"mode compartment");
                    VARIANT v;VariantInit(&v);const HRESULT hr=mode->GetValue(&v);
                    const bool chinese=SUCCEEDED(hr)&&v.vt==VT_I4&&v.lVal!=0;VariantClear(&v);
                    if(!chinese) {
                        diagnose(L"mode-not-chinese",profiles.get(),manager.get(),host,cycle,argv[1]);
                        throw std::runtime_error("fresh activation must default to Chinese");
                    }
                    VariantInit(&v);v.vt=VT_I4;v.lVal=0;
                    checked(mode->SetValue(client,&v),"temporary English in test thread");
                }
                pumpPrivateMessages(20);
                checked(profiles->DeactivateProfile(TF_PROFILETYPE_INPUTPROCESSOR,targetLanguage,mansur::win::kTextService,
                    mansur::win::kLanguageProfile,nullptr,0),"deactivate profile in test thread");
                pumpPrivateMessages(100);
                const auto afterDeactivate=ownNativeState();
                if(cycle==0)diagnose(L"after-deactivate",profiles.get(),manager.get(),host,cycle,argv[1]);
                if(afterDeactivate.present&&(afterDeactivate.flags&1u)!=0) {
                    diagnose(L"tip-deactivation-unconfirmed",profiles.get(),manager.get(),host,cycle,argv[1]);
                    ComPtr<ITfDocumentMgr> focusedDocument;BOOL threadFocus=FALSE;
                    const HRESULT documentHr=manager->GetFocus(focusedDocument.put()),focusHr=manager->IsThreadFocus(&threadFocus);
                    if(SUCCEEDED(documentHr)&&!focusedDocument&&SUCCEEDED(focusHr)&&!threadFocus)
                        throw Precondition("PROFILE_CYCLING_DID_NOT_DEACTIVATE_TIP: a nonfocused thread with no document cannot establish fresh TIP reactivation; initial module/mode evidence is preserved, no 60-cycle pass claimed");
                    throw std::runtime_error("TIP_DEACTIVATION_NOT_OBSERVED: profile deactivation did not deactivate the loaded service");
                }
            }
        }
        if(!restore.apply())throw std::runtime_error("Private verification thread locale/profile restore failed");
        std::cout<<"PASS: 60 installed-profile reactivation cycles using real Windows TSF in private test thread; mode_reset="<<modes<<". Original private-thread locale/profile restored. No external UI, input or default change; not typing acceptance.\n";
    }catch(const Precondition& e){std::cerr<<"PRECONDITION: "<<e.what()<<'\n';result=10;}
    catch(const std::exception& e){std::cerr<<e.what()<<'\n';result=1;}
    CoUninitialize();return result;
}
