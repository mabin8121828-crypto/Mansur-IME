// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "settings.hpp"
#include <windows.h>
#include <algorithm>
#include <array>
#include <string>
#include <string_view>
#include <vector>

namespace mansur::win {
namespace {
constexpr DWORD kMaximumBytes=32768;
constexpr ULONGLONG kCacheMilliseconds=200;
struct Key {HKEY value=nullptr;~Key(){if(value)RegCloseKey(value);}};
struct Handle {HANDLE value=INVALID_HANDLE_VALUE;~Handle(){if(value!=INVALID_HANDLE_VALUE)CloseHandle(value);}};
struct Cache {
    std::filesystem::path path;
    NativeSettings value;
    ULONGLONG checked_at=0;
    bool checked=false;
};
thread_local Cache cache;
#ifdef MANSUR_SETTINGS_TEST
thread_local std::filesystem::path test_path;
thread_local ULONGLONG test_tick=0;
thread_local unsigned test_reads=0;
#endif
DWORD dword(HKEY key,const wchar_t* name,DWORD fallback) noexcept {
    DWORD value=0,bytes=sizeof(value),kind=0;
    if(!key||RegQueryValueExW(key,name,nullptr,&kind,reinterpret_cast<BYTE*>(&value),&bytes)!=ERROR_SUCCESS||
        kind!=REG_DWORD||bytes!=sizeof(value))return fallback;
    return value;
}
bool system_dark() noexcept {
    Key personal;
    if(RegOpenKeyExW(HKEY_CURRENT_USER,L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",0,KEY_QUERY_VALUE,&personal.value)==ERROR_SUCCESS)
        return dword(personal.value,L"AppsUseLightTheme",1)==0;
    return false;
}
std::filesystem::path preference_path() {
#ifdef MANSUR_SETTINGS_TEST
    return test_path; // Test targets cannot read or write a real user's settings.
#else
    const DWORD required=GetEnvironmentVariableW(L"USERPROFILE",nullptr,0);
    if(!required||required>32768)return {};
    std::vector<wchar_t> buffer(required);
    const DWORD count=GetEnvironmentVariableW(L"USERPROFILE",buffer.data(),required);
    if(!count||count>=required)return {};
    std::filesystem::path root(std::wstring(buffer.data(),count));
    if(!root.is_absolute())return {};
    return root/L".mansur-next"/L"preferences.ini";
#endif
}
bool integer(std::wstring_view text,std::int32_t& out) noexcept {
    if(text.empty())return false;
    bool negative=text.front()==L'-';if(negative)text.remove_prefix(1);
    if(text.empty())return false;
    const std::uint64_t maximum=negative?2147483648ull:2147483647ull;
    std::uint64_t value=0;
    for(wchar_t digit:text){if(digit<L'0'||digit>L'9')return false;const auto n=static_cast<unsigned>(digit-L'0');if(value>(maximum-n)/10)return false;value=value*10+n;}
    out=negative?static_cast<std::int32_t>(-static_cast<std::int64_t>(value)):static_cast<std::int32_t>(value);return true;
}
bool absolute_lexicon(std::wstring_view value) {
    if(value.empty())return true;
    // No drive-relative (C:foo), root-relative (\foo), device namespace or
    // incomplete UNC share can become a dictionary pointer. Both separators
    // match the desktop writer's AbsolutePath grammar without normalization.
    const auto slash=[](wchar_t c){return c==L'\\'||c==L'/';};
    if(value.size()>=4&&slash(value[0])&&slash(value[1])&&(value[2]==L'?'||value[2]==L'.')&&slash(value[3]))return false;
    if(value.size()>=3&&((value[0]>=L'A'&&value[0]<=L'Z')||(value[0]>=L'a'&&value[0]<=L'z'))&&value[1]==L':'&&slash(value[2]))return true;
    if(value.size()<5||!slash(value[0])||!slash(value[1])||slash(value[2]))return false;
    std::size_t server_end=2;while(server_end<value.size()&&!slash(value[server_end]))++server_end;
    return server_end+1<value.size()&&!slash(value[server_end+1]);
}
bool parse(std::string_view bytes,NativeSettings& result) {
    if(bytes.empty()||bytes.size()>kMaximumBytes||bytes.find('\0')!=std::string_view::npos||bytes.substr(0,3)=="\xEF\xBB\xBF")return false;
    const int count=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,bytes.data(),static_cast<int>(bytes.size()),nullptr,0);
    if(count<=0)return false;std::wstring decoded(static_cast<std::size_t>(count),L'\0');
    if(MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,bytes.data(),static_cast<int>(bytes.size()),decoded.data(),count)!=count)return false;
    static constexpr const wchar_t* keys[]={L"Theme",L"CandidateLayout",L"FontSize",L"Abbreviation",L"FuzzyMask",L"ToolbarVisible",L"Voice",L"SpeedPercent",L"UserLexiconPath",L"LexiconRevision",L"ToolbarX",L"ToolbarY",L"AutoRemember"};
    std::array<bool,13> seen{};NativeSettings parsed;std::size_t start=0;bool first=true;
    while(start<=decoded.size()) {
        auto end=decoded.find(L'\n',start);if(end==std::wstring::npos)end=decoded.size();
        std::wstring_view line(decoded.data()+start,end-start);
        if(!line.empty()&&line.back()==L'\r')line.remove_suffix(1);
        if(line.find(L'\r')!=std::wstring_view::npos)return false;
        if(first) {if(line!=L"MansurNextSettings=1")return false;first=false;}
        else if(!line.empty()) {
            const auto separator=line.find(L'=');if(separator==std::wstring_view::npos)return false;
            const auto name=line.substr(0,separator),value=line.substr(separator+1);
            std::size_t index=0;while(index<seen.size()&&name!=keys[index])++index;
            if(index==seen.size()||seen[index])return false;seen[index]=true;
            if(index==6) {
                if(value!=L"af_heart"&&value!=L"af_bella"&&value!=L"am_michael"&&value!=L"bf_emma")return false;
            } else if(index==8) {
                if(!absolute_lexicon(value))return false;
                parsed.user_lexicon=std::wstring(value);
            } else {
                std::int32_t number=0;if(!integer(value,number))return false;
                const auto within=[&](std::int32_t low,std::int32_t high){return number>=low&&number<=high;};
                switch(index) {
                case 0:if(!within(0,2))return false;parsed.theme=static_cast<UiTheme>(number);break;
                case 1:if(!within(0,1))return false;parsed.layout=static_cast<CandidateLayout>(number);break;
                case 2:if(!within(10,20))return false;parsed.font_points=static_cast<unsigned>(number);break;
                case 3:if(!within(0,1))return false;parsed.abbreviation=number!=0;break;
                case 4:if(!within(0,255))return false;parsed.fuzzy_mask=static_cast<std::uint32_t>(number);break;
                case 5:if(!within(0,1))return false;break;
                case 7:if(!within(75,125))return false;break;
                case 9:if(number<0)return false;parsed.lexicon_revision=static_cast<std::uint32_t>(number);break;
                case 10:case 11:break; // All signed Int32 toolbar positions are valid.
                case 12:if(!within(0,1))return false;parsed.auto_remember=number!=0;break;
                default:return false;
                }
            }
        }
        if(end==decoded.size())break;start=end+1;
    }
    result=std::move(parsed);return true;
}
enum class Load { Valid,Missing,Invalid };
bool english_preference(const std::filesystem::path& path,bool previous) {
    if(path.empty())return false;
    const auto learning=path.parent_path()/L"learning-preferences.ini";
    Handle file{CreateFileW(learning.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr)};
    if(file.value==INVALID_HANDLE_VALUE){const auto error=GetLastError();return error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND?false:previous;}
    LARGE_INTEGER size{};if(!GetFileSizeEx(file.value,&size)||size.QuadPart<=0||size.QuadPart>kMaximumBytes)return previous;
    std::string bytes(static_cast<std::size_t>(size.QuadPart),0);DWORD read=0;
    if(!ReadFile(file.value,bytes.data(),static_cast<DWORD>(bytes.size()),&read,nullptr)||read!=bytes.size())return previous;
    if(bytes.find('\0')!=std::string::npos)return previous;
    bool value=false,first=true;std::array<bool,3> seen{};
    const std::string_view keys[]={"EnglishWritebackMode","SystemSpeechFallback","EnglishSuggestions"};
    std::size_t start=0;
    while(start<=bytes.size()){
        auto end=bytes.find('\n',start);if(end==std::string::npos)end=bytes.size();
        auto line=std::string_view(bytes).substr(start,end-start);if(!line.empty()&&line.back()=='\r')line.remove_suffix(1);
        if(first){if(line!="MansurNextLearningSettings=1")return previous;first=false;}
        else if(!line.empty()){
            const auto at=line.find('=');if(at==std::string_view::npos)return previous;
            const auto name=line.substr(0,at),number=line.substr(at+1);std::size_t i=0;while(i<3&&keys[i]!=name)++i;
            if(i==3||seen[i]||(number!="0"&&number!="1"))return previous;seen[i]=true;
            if(i==2)value=number=="1";
        }
        if(end==bytes.size())break;start=end+1;
    }
    return value;
}
Load load(const std::filesystem::path& path,NativeSettings& value) {
#ifdef MANSUR_SETTINGS_TEST
    ++test_reads;
#endif
    if(path.empty())return Load::Missing;
    Handle file{CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr)};
    if(file.value==INVALID_HANDLE_VALUE) {const auto error=GetLastError();return error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND?Load::Missing:Load::Invalid;}
    LARGE_INTEGER size{};if(!GetFileSizeEx(file.value,&size)||size.QuadPart<=0||size.QuadPart>kMaximumBytes)return Load::Invalid;
    std::string bytes(static_cast<std::size_t>(size.QuadPart),0);DWORD read=0;
    if(!ReadFile(file.value,bytes.data(),static_cast<DWORD>(bytes.size()),&read,nullptr)||read!=bytes.size())return Load::Invalid;
    char extra=0;DWORD remaining=0;if(!ReadFile(file.value,&extra,1,&remaining,nullptr)||remaining)return Load::Invalid;
    return parse(bytes,value)?Load::Valid:Load::Invalid;
}
}
NativeSettings SanitizeSettings(NativeSettings value) noexcept {
    if(static_cast<unsigned>(value.theme)>2)value.theme=UiTheme::System;
    if(static_cast<unsigned>(value.layout)>1)value.layout=CandidateLayout::Horizontal;
    value.font_points=std::clamp(value.font_points,10u,20u);
    value.fuzzy_mask&=0xff;
    return value;
}
NativeSettings ReadNativeSettings(bool include_lexicon) noexcept {
    try {
        const auto path=preference_path();
        if(cache.path!=path){cache=Cache{};cache.path=path;}
#ifdef MANSUR_SETTINGS_TEST
        const auto now=test_tick;
#else
        const auto now=GetTickCount64();
#endif
        if(include_lexicon||!cache.checked||now<cache.checked_at||now-cache.checked_at>=kCacheMilliseconds) {
            NativeSettings fresh;
            const auto english=english_preference(path,cache.value.english_suggestions);
            const auto outcome=load(path,fresh);
            if(outcome==Load::Valid)cache.value=std::move(fresh);
            else if(outcome==Load::Missing)cache.value=NativeSettings{};
            cache.value.english_suggestions=english;
            // Invalid/inaccessible files keep the last valid snapshot. A new
            // thread starts from defaults, never a virtualized product HKCU key.
            cache.value.system_dark=system_dark();cache.checked_at=now;cache.checked=true;
        }
        NativeSettings result=cache.value;
        if(!include_lexicon){result.user_lexicon.clear();result.lexicon_revision=0;}
        return result;
    }catch(...){return NativeSettings{};}
}
#ifdef MANSUR_SETTINGS_TEST
void SettingsTestPath(std::filesystem::path path){test_path=std::move(path);cache=Cache{};test_reads=0;}
void SettingsTestTick(std::uint64_t tick) noexcept {test_tick=tick;}
unsigned SettingsTestReads() noexcept {return test_reads;}
#endif
}
