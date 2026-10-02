// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include "mansur/core.hpp"
#include <windows.h>
#include <cstdint>
#include <memory>
#include <vector>

namespace mansur::win {
// These entry points never read/write files or wait for a worker/file lock. A null
// snapshot means no snapshot is available at this instant; retain the previous
// one. Calling Get schedules a refresh at most once per second.
std::shared_ptr<const PersonalLexicon> GetPersonalLexicon() noexcept;
// true acknowledges an in-memory queue only. Call exclusively after Written;
// persistence is reported separately, and is independent of English learning.
bool RememberPersonalWords(const std::vector<PersonalWord>& words) noexcept;
enum class PersonalWordsState : std::uint32_t {
    NotLoaded, Ready, Pending, Busy, InvalidData, IoError, Limit, Unavailable
};
struct PersonalWordsDiagnostics {
    PersonalWordsState state=PersonalWordsState::NotLoaded;
    std::uint32_t pending_words=0;
    DWORD last_error=ERROR_SUCCESS;
    std::uint64_t accepted_batches=0,saved_batches=0,rejected_batches=0;
    // New entries skipped after the persistent 10,000-entry limit. Existing
    // entries still receive their usage increments; no stored entry is evicted.
    std::uint64_t skipped_new_words=0;
};
PersonalWordsDiagnostics GetPersonalWordsDiagnostics() noexcept;

#ifdef MANSUR_PERSONAL_WORDS_TEST
// Test builds have no production path fallback. These hooks operate only on
// an explicitly configured absolute temporary path and private test identity.
bool PersonalWordsTestConfigure(const std::filesystem::path& file,const std::wstring& name) noexcept;
bool PersonalWordsTestWait(DWORD milliseconds=5000) noexcept;
void PersonalWordsTestRefresh() noexcept;
struct PersonalWordsTestIo {std::uint64_t reads=0,writes=0,caller_thread_io=0;};
PersonalWordsTestIo PersonalWordsTestOperations() noexcept;
#endif
}
