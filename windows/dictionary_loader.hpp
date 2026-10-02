// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include "settings.hpp"
#include "mansur/core.hpp"
#include <memory>
#include <mutex>

namespace mansur::win {
class DictionaryCache {
public:
    std::shared_ptr<const Dictionary> load(const std::filesystem::path& base_directory,const NativeSettings& settings) noexcept;
private:
    std::mutex mutex_;
    std::filesystem::path base_path_,custom_path_;
    std::uint32_t custom_revision_=0;
    std::shared_ptr<const Dictionary> base_,custom_;
};
}
