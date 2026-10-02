// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <string_view>
#include <vector>
#include <string>
namespace mansur::english {
// Small, independently curated common-word starter list. No network, inferred
// history, spelling replacement or proper-name correction runs while typing.
inline constexpr const wchar_t* words[]={
    L"hello",L"help",L"here",L"happy",L"have",L"home",L"hope",L"how",
    L"thank",L"thanks",L"there",L"their",L"these",L"think",L"this",L"those",L"through",L"today",L"tomorrow",L"together",
    L"about",L"after",L"again",L"also",L"always",L"another",L"answer",L"anything",L"around",L"available",
    L"back",L"because",L"before",L"better",L"between",L"both",L"bring",L"busy",
    L"call",L"can",L"change",L"check",L"come",L"could",L"copy",L"correct",
    L"different",L"done",L"down",L"during",L"each",L"early",L"email",L"english",L"enough",L"every",L"example",L"excuse",
    L"family",L"feel",L"find",L"first",L"follow",L"friend",L"from",L"give",L"good",L"great",
    L"important",L"information",L"just",L"keep",L"know",L"language",L"later",L"learn",L"learning",L"leave",L"little",L"look",L"love",
    L"make",L"many",L"maybe",L"meeting",L"message",L"more",L"morning",L"much",L"name",L"need",L"never",L"next",L"nice",L"night",L"nothing",
    L"okay",L"only",L"open",L"other",L"people",L"please",L"problem",L"question",L"quite",L"read",L"ready",L"really",L"remember",L"right",
    L"same",L"save",L"school",L"see",L"send",L"should",L"something",L"sorry",L"speak",L"start",L"still",L"sure",
    L"take",L"talk",L"tell",L"time",L"translate",L"understand",L"until",L"update",L"use",L"very",L"wait",L"want",L"welcome",L"well",L"what",L"when",L"where",L"which",L"while",L"will",L"with",L"work",L"world",L"would",L"write",L"wrong",L"yesterday",L"your"
};
inline bool letter(wchar_t c) noexcept {return (c>=L'a'&&c<=L'z')||(c>=L'A'&&c<=L'Z');}
inline wchar_t lower(wchar_t c) noexcept {return c>=L'A'&&c<=L'Z'?static_cast<wchar_t>(c-L'A'+L'a'):c;}
inline std::wstring_view prefix(std::wstring_view draft,std::size_t caret) noexcept {
    if(caret>draft.size()||(caret<draft.size()&&letter(draft[caret])))return {};
    auto start=caret;while(start&&letter(draft[start-1]))--start;
    if(caret-start<2||caret-start>24)return {};
    // Suppress identifiers, URLs and addresses rather than guessing a word.
    if(start&&draft[start-1]>=33&&draft[start-1]<=126&&draft[start-1]!=L'('&&draft[start-1]!=L'"')return {};
    return draft.substr(start,caret-start);
}
inline bool match(std::wstring_view value,std::wstring_view word) noexcept {
    if(value.empty()||value.size()>=word.size())return false;
    for(std::size_t i=0;i<value.size();++i)if(lower(value[i])!=word[i])return false;
    return true;
}
inline const wchar_t* at(std::wstring_view value,std::size_t index) noexcept {
    if(index>=3)return nullptr;
    for(const auto word:words)if(match(value,word)){if(!index)return word;--index;}
    return nullptr;
}
inline std::wstring complete(std::wstring_view value,const wchar_t* word) {
    std::wstring result(value);bool upper=true;for(auto c:value)if(c<L'A'||c>L'Z')upper=false;
    for(auto tail=word+value.size();*tail;++tail)result+=upper?static_cast<wchar_t>(*tail-L'a'+L'A'):*tail;
    return result;
}
}
