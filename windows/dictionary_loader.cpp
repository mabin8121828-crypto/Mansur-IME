// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "dictionary_loader.hpp"
#include <windows.h>

namespace mansur::win {
namespace {
std::shared_ptr<const Dictionary> read_dictionary(const std::filesystem::path& path) noexcept {
    try {
        auto dictionary=std::make_shared<Dictionary>();
        const auto report=dictionary->load(path);
        return report.loaded&&dictionary->size()?dictionary:std::shared_ptr<const Dictionary>{};
    }catch(...){return {};}
}
bool local_binary_path(const std::filesystem::path& path) {
    if(!path.is_absolute()||path.extension()!=L".mlex")return false;
    const auto root=path.root_name().wstring();
    if(root.size()!=2||root[1]!=L':'||!((root[0]>=L'A'&&root[0]<=L'Z')||(root[0]>=L'a'&&root[0]<=L'z')))return false;
    const UINT type=GetDriveTypeW(path.root_path().c_str());
    return type==DRIVE_FIXED||type==DRIVE_REMOVABLE||type==DRIVE_RAMDISK;
}
}
std::shared_ptr<const Dictionary> DictionaryCache::load(const std::filesystem::path& base_directory,const NativeSettings& settings) noexcept {
    try {
        // Data-only mutex, never held while invoking host COM.
        std::lock_guard<std::mutex> lock(mutex_);
        if(base_path_!=base_directory) {base_path_=base_directory;base_.reset();custom_.reset();custom_path_.clear();}
        if(!base_) {
            base_=read_dictionary(base_directory/L"base.mlex");
            if(!base_)base_=read_dictionary(base_directory/L"base.tsv");
        }
        if(settings.user_lexicon.empty()||!local_binary_path(settings.user_lexicon))return base_;
        const auto path=settings.user_lexicon.lexically_normal();
        if(custom_&&custom_path_==path&&custom_revision_==settings.lexicon_revision)return custom_;
        auto replacement=read_dictionary(path);
        if(!replacement)return base_;
        custom_path_=path;custom_revision_=settings.lexicon_revision;custom_=std::move(replacement);
        return custom_;
    }catch(...){return {};}
}
}
