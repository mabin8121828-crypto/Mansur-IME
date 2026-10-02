// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "host_policy.hpp"
#include <iostream>
#include <stdexcept>
using mansur::win::classify_host;
using mansur::win::HostDecision;
void check(bool value,const char* label) {if(!value)throw std::runtime_error(label);}
int main() {
    try {
        check(classify_host(L"C:\\Windows\\explorer.exe",L"C:\\Windows")==HostDecision::StandardEditOnly,"Explorer permits verified standard editors only");
        check(classify_host(L"d:/WINROOT/explorer.exe",L"D:\\WinRoot\\")==HostDecision::StandardEditOnly,"Explorer matches actual Windows root with normalized case");
        check(classify_host(L"C:\\Windows\\Other\\explorer.exe",L"C:\\Windows")==HostDecision::ExcludedSystemHost,"another system-path executable cannot gain Explorer capability by name");
        check(mansur::win::host_can_create(HostDecision::StandardEditOnly)&&!mansur::win::host_can_create(HostDecision::ExcludedSystemHost)&&!mansur::win::host_can_create(HostDecision::UnknownPath),"factory gates distinguish restricted host from excluded host");
        check(classify_host(L"c:/WINDOWS/SystemApps/package/SearchHost.exe",L"C:\\Windows\\")==HostDecision::ExcludedSystemHost,"nested system host case insensitive");
        check(classify_host(L"D:\\WinRoot\\ImmersiveControlPanel\\SystemSettings.exe",L"D:\\WinRoot")==HostDecision::ExcludedSystemHost,"nonstandard Windows root");
        check(classify_host(L"C:\\Windows.old\\explorer.exe",L"C:\\Windows")==HostDecision::Allowed,"root boundary");
        check(classify_host(L"D:\\Tools\\explorer.exe",L"C:\\Windows")==HostDecision::Allowed,"name alone not excluded");
        check(classify_host(L"C:\\Windows\\System32\\notepad.exe",L"C:\\Windows")==HostDecision::Allowed,"ordinary app supported");
        check(classify_host(L"C:\\Windows\\System32\\ctfmon.exe",L"C:\\Windows")==HostDecision::Allowed,"shared text broker not excluded");
        check(classify_host(L"C:\\Windows\\System32\\RuntimeBroker.exe",L"C:\\Windows")==HostDecision::Allowed,"shared runtime broker not excluded");
        check(classify_host(L"C:\\Apps\\BrandNewBBY.exe",L"C:\\Windows")==HostDecision::Allowed,"new app no special case");
        check(classify_host(L"C:\\Apps\\Chrome.exe",L"C:\\Windows")==HostDecision::Allowed,"browser same route");
        check(classify_host(L"",L"C:\\Windows")==HostDecision::UnknownPath,"unknown executable refused");
        check(classify_host(L"C:\\Apps\\app.exe",L"")==HostDecision::UnknownPath,"unknown Windows root refused");
        check(classify_host(L"explorer.exe",L"C:\\Windows")==HostDecision::UnknownPath,"relative executable refused");
        check(classify_host(L"C:\\Windows\\..\\Apps\\app.exe",L"C:\\Windows")==HostDecision::UnknownPath,"noncanonical executable refused");
        check(mansur::win::current_host_policy()==HostDecision::Allowed,"test executable normal host");
        std::cout<<"18 host policy checks passed (no UI or registration).\n";return 0;
    } catch(const std::exception& e) {std::cerr<<e.what()<<'\n';return 1;}
}
