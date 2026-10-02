// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
// Fixed input in an isolated core session. No user input, registry or UI access.
#include "mansur/core.hpp"
#include <fstream>
#include <iostream>
int wmain(int argc,wchar_t** argv) {
    if(argc!=3)return 2;
    try {
        auto dictionary=std::make_shared<mansur::Dictionary>();
        if(!dictionary->load(argv[1]).loaded)throw std::runtime_error("dictionary empty");
        mansur::DraftSession draft(dictionary);std::uint64_t now=1000;
        for(wchar_t ch:std::wstring(L"nihao"))draft.key({mansur::KeyKind::Letter,ch,now+=20});
        auto chosen=draft.key({mansur::KeyKind::Space,0,now+=20});
        if(chosen.commit||draft.draft()!=L"你好")throw std::runtime_error("Chinese selection failed");
        for(int i=0;i<2;++i)if(draft.key({mansur::KeyKind::Space,0,now+=60}).commit)
            throw std::runtime_error("early confirmation");
        auto confirmed=draft.key({mansur::KeyKind::Space,0,now+=60});
        if(!confirmed.commit||confirmed.commit->text!=L"你好"||!confirmed.commit->learn)
            throw std::runtime_error("Chinese learning confirmation failed");
        if(!draft.acknowledge(confirmed.commit->id,mansur::CommitResult::Written)||!draft.draft().empty())
            throw std::runtime_error("Chinese completion failed");
        for(wchar_t ch:std::wstring(L"Hello"))draft.key({mansur::KeyKind::Literal,ch,now+=20});
        for(int i=0;i<2;++i)if(draft.key({mansur::KeyKind::EnglishSpace,L' ',now+=60}).commit)
            throw std::runtime_error("early English confirmation");
        auto english=draft.key({mansur::KeyKind::EnglishSpace,L' ',now+=60});
        if(!english.commit||english.commit->text!=L"Hello"||!english.commit->learn)
            throw std::runtime_error("English learning confirmation failed");
        if(!draft.acknowledge(english.commit->id,mansur::CommitResult::Written))
            throw std::runtime_error("English completion failed");
        std::ofstream file(argv[2],std::ios::binary);
        file<<"{\"chinese\":\""<<mansur::to_utf8(confirmed.commit->text)<<"\",\"english\":\"Hello\",\"core_confirmed\":true}";
        if(!file)throw std::runtime_error("fixture output failed");
        std::cout<<"CORE_CONFIRMATION_PASS: nihao selection, three-space single confirmation, literal Hello.\n";
        return 0;
    }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
