// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "host_policy.hpp"
#include <windows.h>
#include <algorithm>
#include <array>
#include <cwctype>
#include <string>
#include <vector>

namespace mansur::win {
namespace {
std::wstring normalize(std::wstring_view value) {
    std::wstring out(value);
    for(auto& c:out) {if(c==L'/') c=L'\\'; c=static_cast<wchar_t>(std::towlower(c));}
    while(out.size()>3 && out.back()==L'\\') out.pop_back();
    return out;
}
bool absolute_regular(const std::wstring& value) {
    return value.size()>3 && value[1]==L':' && value[2]==L'\\' &&
        value.find(L"\\..") == std::wstring::npos && value.find(L"\\.") == std::wstring::npos;
}
}
HostDecision classify_host(std::wstring_view executable,std::wstring_view windows_directory) {
    const auto exe=normalize(executable), root=normalize(windows_directory);
    if(!absolute_regular(exe)||!absolute_regular(root)) return HostDecision::UnknownPath;
    if(exe.size()<=root.size() || exe.compare(0,root.size(),root)!=0 || exe[root.size()]!=L'\\')
        return HostDecision::Allowed;
    const auto name=std::wstring_view(exe).substr(exe.find_last_of(L'\\')+1);
    // Explorer's genuine Windows location can host ordinary label-edit fields.
    // Service creation is allowed; every key/write still requires a verified
    // standard editable control. Other shell surfaces remain outside this step.
    if(exe==root+L"\\explorer.exe")return HostDecision::StandardEditOnly;
    constexpr std::array<std::wstring_view,9> excluded={L"explorer.exe",L"systemsettings.exe",
        L"searchhost.exe",L"searchapp.exe",L"startmenuexperiencehost.exe",L"shellexperiencehost.exe",
        L"lockapp.exe",L"credentialuibroker.exe",L"logonui.exe"};
    return std::find(excluded.begin(),excluded.end(),name)!=excluded.end()
        ?HostDecision::ExcludedSystemHost:HostDecision::Allowed;
}
HostDecision current_host_policy() noexcept {
    try {
        std::vector<wchar_t> exe(32768), root(32768);
        const DWORD n=GetModuleFileNameW(nullptr,exe.data(),static_cast<DWORD>(exe.size()));
        const UINT r=GetWindowsDirectoryW(root.data(),static_cast<UINT>(root.size()));
        if(!n||n>=exe.size()||!r||r>=root.size()) return HostDecision::UnknownPath;
        return classify_host({exe.data(),n},{root.data(),r});
    } catch(...) {return HostDecision::UnknownPath;}
}
}
