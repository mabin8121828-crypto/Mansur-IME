// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include <windows.h>
#include <msctf.h>
#include <filesystem>
#include <iostream>
#include <string>
#include <stdexcept>
#include "../windows/ids.hpp"
#include "../windows/com_ptr.hpp"

namespace {
using mansur::win::ComPtr;
using mansur::win::kTextService;
using mansur::win::kLanguageProfile;
using mansur::win::kProductName;
constexpr LANGID language=0x0804;
constexpr wchar_t registry_key[]=L"Software\\Classes\\CLSID\\{E17225F9-B37A-4A39-A6FA-6EC6971CB481}";
const GUID* categories[]={&GUID_TFCAT_TIP_KEYBOARD,&GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT};
void check(HRESULT hr,const char* stage) {
    if(FAILED(hr)) {
        std::cerr<<stage<<" HRESULT=0x"<<std::hex<<static_cast<unsigned long>(hr)<<'\n';
        throw std::runtime_error(stage);
    }
}
void wincheck(LSTATUS hr,const char* stage) {if(hr!=ERROR_SUCCESS)check(HRESULT_FROM_WIN32(hr),stage);}
struct RegKey {HKEY value=nullptr;~RegKey(){if(value)RegCloseKey(value);}};
void string_value(HKEY key,const wchar_t* name,const std::wstring& value) {
    wincheck(RegSetValueExW(key,name,0,REG_SZ,reinterpret_cast<const BYTE*>(value.c_str()),
        static_cast<DWORD>((value.size()+1)*sizeof(wchar_t))),"write product registry value");
}
ComPtr<ITfInputProcessorProfiles> profiles() {
    ComPtr<ITfInputProcessorProfiles> p;
    check(CoCreateInstance(CLSID_TF_InputProcessorProfiles,nullptr,CLSCTX_INPROC_SERVER,
        IID_ITfInputProcessorProfiles,reinterpret_cast<void**>(p.put())),"create profile manager");
    return p;
}
ComPtr<ITfCategoryMgr> category_manager() {
    ComPtr<ITfCategoryMgr> p;
    check(CoCreateInstance(CLSID_TF_CategoryMgr,nullptr,CLSCTX_INPROC_SERVER,
        IID_ITfCategoryMgr,reinterpret_cast<void**>(p.put())),"create category manager");
    return p;
}
void install(const std::filesystem::path& dll) {
    const auto full=std::filesystem::canonical(dll);
    if(!std::filesystem::is_regular_file(full)||full.extension()!=L".dll")throw std::runtime_error("invalid module path");
    const auto name=full.wstring();RegKey key,inproc;
    wincheck(RegCreateKeyExW(HKEY_LOCAL_MACHINE,registry_key,0,nullptr,0,KEY_WRITE,nullptr,&key.value,nullptr),"create product class");
    string_value(key.value,nullptr,kProductName);
    wincheck(RegCreateKeyExW(key.value,L"InprocServer32",0,nullptr,0,KEY_WRITE,nullptr,&inproc.value,nullptr),"create product module key");
    string_value(inproc.value,nullptr,name);string_value(inproc.value,L"ThreadingModel",L"Apartment");
    auto p=profiles();check(p->Register(kTextService),"register text service");
    check(p->AddLanguageProfile(kTextService,language,kLanguageProfile,kProductName,
        static_cast<ULONG>(wcslen(kProductName)),name.c_str(),static_cast<ULONG>(name.size()),0),"add Chinese language profile");
    auto manager=category_manager();
    for(const auto category:categories)check(manager->RegisterCategory(kTextService,*category,kTextService),"register capability");
    // Enabling is a separate unelevated step so it targets the original user.
    std::cout<<"registered="<<(sizeof(void*)==8?"x64":"x86")<<" default_changed=false\n";
}
void enable() {
    auto p=profiles();check(p->EnableLanguageProfile(kTextService,language,kLanguageProfile,TRUE),"enable current user profile");
    std::cout<<"enabled=true default_changed=false\n";
}
void uninstall() {
    auto p=profiles();auto manager=category_manager();
    for(const auto category:categories)manager->UnregisterCategory(kTextService,*category,kTextService);
    const HRESULT hr=p->Unregister(kTextService);
    check(hr,"unregister text service");
    const LSTATUS registry=RegDeleteTreeW(HKEY_LOCAL_MACHINE,registry_key);
    if(registry!=ERROR_SUCCESS&&registry!=ERROR_FILE_NOT_FOUND)wincheck(registry,"remove product class");
    std::cout<<"unregistered="<<(sizeof(void*)==8?"x64":"x86")<<" user_data_deleted=false\n";
}
bool inspect(const wchar_t* expected=nullptr) {
    auto p=profiles();BOOL enabled=FALSE;
    const HRESULT hr=p->IsEnabledLanguageProfile(kTextService,language,kLanguageProfile,&enabled);
    ComPtr<ITfInputProcessorProfileMgr> profile_manager;
    check(p->QueryInterface(IID_ITfInputProcessorProfileMgr,reinterpret_cast<void**>(profile_manager.put())),"query profile manager");
    ComPtr<IEnumTfInputProcessorProfiles> entries;
    check(profile_manager->EnumProfiles(language,entries.put()),"enumerate Chinese profiles");
    bool exists=false,listed_enabled=false,immersive=false;
    for(;;) {
        TF_INPUTPROCESSORPROFILE entry{};ULONG count=0;
        check(entries->Next(1,&entry,&count),"enumerate profile entry");
        if(!count)break;
        if(entry.dwProfileType==TF_PROFILETYPE_INPUTPROCESSOR && entry.langid==language &&
           IsEqualGUID(entry.clsid,kTextService) && IsEqualGUID(entry.guidProfile,kLanguageProfile) &&
           IsEqualGUID(entry.catid,GUID_TFCAT_TIP_KEYBOARD)) {
            exists=true;listed_enabled=(entry.dwFlags&TF_IPP_FLAG_ENABLED)!=0;
            immersive=(entry.dwCaps&TF_IPP_CAPS_IMMERSIVESUPPORT)!=0;
            break;
        }
    }
    RegKey key;
    const auto result=RegOpenKeyExW(HKEY_LOCAL_MACHINE,(std::wstring(registry_key)+L"\\InprocServer32").c_str(),0,KEY_READ,&key.value);
    std::wcout<<L"architecture="<<(sizeof(void*)==8?L"x64":L"x86")<<L" registered="<<(result==ERROR_SUCCESS)
        <<L" profile="<<exists<<L" enabled="<<listed_enabled<<L" immersive="<<immersive
        <<L" raw_enabled="<<(SUCCEEDED(hr)&&enabled)<<L'\n';
    bool module_matches=false;
    if(result==ERROR_SUCCESS) {
        wchar_t value[32768]{};DWORD bytes=sizeof(value),type=0;
        wincheck(RegQueryValueExW(key.value,nullptr,nullptr,&type,reinterpret_cast<BYTE*>(value),&bytes),"read product module");
        if(type==REG_SZ) {
            value[32767]=0;
            std::wcout<<L"module="<<value<<L'\n';
            module_matches=std::filesystem::is_regular_file(value) &&
                (!expected || _wcsicmp(std::filesystem::absolute(expected).c_str(),value)==0);
        }
    }
    return module_matches && exists && listed_enabled && immersive;
}
}
int wmain(int argc,wchar_t** argv) {
    if(argc<2){std::cerr<<"Usage: mansur_register --install module.dll | --enable | --uninstall | --inspect | --verify module.dll\n";return 2;}
    const auto initialized=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);if(FAILED(initialized))return 1;
    int code=0;
    try {
        const std::wstring action=argv[1];
        if(action==L"--install"&&argc==3)install(argv[2]);
        else if(action==L"--enable"&&argc==2)enable();
        else if(action==L"--uninstall"&&argc==2)uninstall();
        else if(action==L"--inspect"&&argc==2)inspect();
        else if(action==L"--verify"&&argc==3){if(!inspect(argv[2]))throw std::runtime_error("profile verification failed");}
        else throw std::runtime_error("invalid arguments");
    }catch(const std::exception& e){std::cerr<<e.what()<<'\n';code=1;}
    CoUninitialize();return code;
}
