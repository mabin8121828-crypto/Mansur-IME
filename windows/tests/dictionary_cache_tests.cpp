// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "dictionary_loader.hpp"
#include <windows.h>
#include <fstream>
#include <iostream>
#include <stdexcept>
using namespace mansur;using namespace mansur::win;
void check(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
void create(const std::filesystem::path& root,const std::wstring& filename,const char* chinese){
    auto tsv=root/L"fixture.tsv";{std::ofstream f(tsv,std::ios::binary);f<<"nihao\t"<<chinese<<"\t800\tni hao\n";}
    Dictionary dictionary;check(dictionary.load(tsv).loaded==1,"temporary fixture dictionary");dictionary.save_binary(root/filename);std::filesystem::remove(tsv);
}
int main(){auto root=std::filesystem::temp_directory_path()/(L"mansur-lexicon-test-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64()));
    try{std::filesystem::create_directory(root);create(root,L"base.mlex","你好");create(root,L"first.mlex","您好");create(root,L"second.mlex","你们好");
        DictionaryCache cache;NativeSettings settings;auto base=cache.load(root,settings);check(base&&base->suggest("nihao")[0].text==L"你好","base loads");
        settings.user_lexicon=root/L"first.mlex";settings.lexicon_revision=1;auto first=cache.load(root,settings);check(first&&first->suggest("nihao")[0].text==L"您好","first custom snapshot loads");
        DraftSession draft(first);for(char ch:std::string("nihao"))draft.key({KeyKind::Letter,static_cast<wchar_t>(ch)});
        settings.user_lexicon=root/L"second.mlex";settings.lexicon_revision=2;auto second=cache.load(root,settings);check(second!=first&&second->suggest("nihao")[0].text==L"你们好","new activation uses new path revision");
        check(draft.candidates()[0].text==L"您好","existing draft keeps previous snapshot");
        check(cache.load(root,settings)==second,"same path revision reuses dictionary");
        settings.lexicon_revision=3;check(cache.load(root,settings)!=second,"revision change reloads same path");
        settings.user_lexicon=root/L"missing.mlex";check(cache.load(root,settings)==base,"missing custom leaves base usable");
        settings.user_lexicon=root/L"broken.mlex";{std::ofstream f(settings.user_lexicon);f<<"bad";}check(cache.load(root,settings)==base,"invalid binary leaves base usable");
        settings.user_lexicon=L"..\\bad.mlex";check(cache.load(root,settings)==base,"relative pointer rejected");
        settings.user_lexicon=L"\\\\server\\share\\file.mlex";check(cache.load(root,settings)==base,"network pointer rejected");
        settings.user_lexicon.clear();check(cache.load(root,settings)==base,"clear custom restores base");
        for(auto name:{L"base.mlex",L"first.mlex",L"second.mlex",L"broken.mlex"})std::filesystem::remove(root/name);std::filesystem::remove(root);
        std::cout<<"10 activation snapshot/cache/fallback checks passed; no installed dictionary or registry modified.\n";return 0;
    }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}

