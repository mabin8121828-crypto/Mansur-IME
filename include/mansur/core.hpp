// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <filesystem>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace mansur {
enum class CandidateKind { Exact, Composed, Completion, Abbreviated, Fuzzy, Interjection, OtherCompletion, Partial, Raw };
struct Candidate {
    std::wstring text;
    std::size_t consumed=0;
    double score=0;
    // Canonical source syllables, never the possibly abbreviated/fuzzy keystrokes.
    // Empty when the source cannot provide one valid syllable per Unicode character.
    std::string syllables;
    CandidateKind kind=CandidateKind::Exact;
    bool whole_word=false;
};
struct PersonalWord { std::wstring text; std::string syllables; std::uint32_t uses=1; };
bool valid_personal_word(const PersonalWord& word) noexcept;
class PersonalLexicon;
struct LoadReport { std::size_t loaded=0; std::size_t rejected=0; std::size_t first_rejected_line=0; };
struct InputOptions { bool abbreviation=true; std::uint32_t fuzzy_mask=0; };
struct DictionaryFormatError:std::runtime_error {
    explicit DictionaryFormatError(std::size_t line):std::runtime_error("invalid dictionary data"),line(line){}
    std::size_t line;
};
class Dictionary {
public:
    Dictionary();
    ~Dictionary();
    Dictionary(const Dictionary&)=delete;
    Dictionary& operator=(const Dictionary&)=delete;
    LoadReport load(const std::filesystem::path& path, bool use_syllable_sidecar=true);
    void save_binary(const std::filesystem::path& path) const;
    std::vector<Candidate> suggest(std::string_view pinyin, std::size_t limit=9, InputOptions options={}) const;
    std::size_t size() const noexcept;
private:
    friend class PersonalLexicon;
    struct Impl;
    std::vector<Candidate> suggest_internal(std::string_view pinyin,std::size_t limit,InputOptions options,bool only_entries) const;
    LoadReport load_binary(const std::filesystem::path& path);
    std::unique_ptr<Impl> impl_;
};
// Immutable, bounded in-memory personal index. Construction performs no file I/O.
class PersonalLexicon {
public:
    explicit PersonalLexicon(std::vector<PersonalWord> words);
    ~PersonalLexicon();
    PersonalLexicon(const PersonalLexicon&)=delete;
    PersonalLexicon& operator=(const PersonalLexicon&)=delete;
    const std::vector<PersonalWord>& words() const noexcept;
private:
    friend class DraftSession;
    std::vector<Candidate> suggest(std::string_view pinyin,InputOptions options) const;
    std::uint32_t uses(const Candidate& candidate) const noexcept;
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
struct Commit {
    std::uint64_t id=0;
    std::wstring text;
    bool learn=false;
    // Usage increments for this commit only. Persist exclusively after Written.
    std::vector<PersonalWord> personal_words;
};
enum class CommitResult { Written, NotWritten, Unknown };
enum class KeyKind { Letter, Space, Digit, Backspace, Delete, Left, Right, Up, Down, Enter, Escape, Punctuation, PageUp, PageDown, Literal, EnglishSpace, CompleteEnglish };
struct Key { KeyKind kind; wchar_t character=0; std::uint64_t timestamp_ms=0; bool repeat=false; };
struct KeyResult { bool consumed=false; bool changed=false; std::optional<Commit> commit; };
// Per context and input thread; no model or IPC dependency.
class DraftSession {
public:
    explicit DraftSession(std::shared_ptr<const Dictionary> dictionary);
    KeyResult key(const Key& key);
    bool wants(const Key& key) const noexcept;
    bool acknowledge(std::uint64_t commit_id, CommitResult result);
    // Confirm pending letters into our own draft without converting or committing.
    // Empty pending letters are already confirmed; failure preserves all state.
    bool accept_raw();
    void set_options(InputOptions options);
    void set_personal_lexicon(std::shared_ptr<const PersonalLexicon> lexicon);
    void set_personalization_enabled(bool enabled);
    void set_english_suggestions(bool enabled) noexcept;
    std::vector<std::wstring> english_completions() const;
    bool personalization_enabled() const noexcept { return personalization_enabled_; }
    InputOptions options() const noexcept { return options_; }
    void reset_gesture() noexcept;
    const std::string& pinyin() const noexcept { return pinyin_; }
    const std::wstring& draft() const noexcept { return draft_; }
    const std::vector<Candidate>& candidates() const noexcept { return candidates_; }
    std::size_t selected() const noexcept { return selected_; }
    std::size_t caret() const noexcept { return caret_; }
    std::uint64_t revision() const noexcept { return revision_; }
    bool empty() const noexcept { return draft_.empty() && pinyin_.empty(); }
    bool commit_pending() const noexcept { return pending_.has_value(); }
    bool uncertain() const noexcept { return uncertain_; }
    const std::wstring& recovery_draft() const noexcept { return recovery_; }
    std::wstring preview() const;
private:
    KeyResult apply_key(const Key& key);
    void refresh();
    bool choose(std::size_t index);
    bool accept_raw_internal();
    void observe(const Candidate& candidate);
    void clear_observations() noexcept;
    KeyResult prepare(bool learn);
    std::shared_ptr<const Dictionary> dictionary_;
    std::shared_ptr<const PersonalLexicon> personal_;
    std::shared_ptr<const PersonalLexicon> temporary_personal_;
    std::vector<PersonalWord> observations_;
    std::vector<PersonalWord> selection_run_;
    bool personalization_enabled_=true;
    bool english_suggestions_enabled_=false;
    InputOptions options_;
    std::wstring draft_;
    std::string pinyin_;
    std::vector<Candidate> candidates_;
    std::size_t selected_=0;
    std::size_t caret_=0;
    std::uint64_t revision_=0;
    std::uint64_t next_commit_=1;
    std::uint64_t last_space_=0;
    unsigned spaces_=0;
    // English spaces are actual owned text. Only these recorded consecutive
    // insertions may be removed when the third space confirms the sentence.
    enum class SpaceGesture { None, Chinese, English };
    SpaceGesture space_gesture_=SpaceGesture::None;
    std::size_t space_anchor_=0;
    std::optional<Commit> pending_;
    bool uncertain_=false;
    std::wstring recovery_;
};
std::wstring from_utf8(std::string_view value);
std::string to_utf8(std::wstring_view value);
}
