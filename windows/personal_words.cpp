// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "personal_words.hpp"
#include "module.hpp"
#include <objbase.h>
#include <algorithm>
#include <atomic>
#include <deque>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <utility>

namespace mansur::win {
namespace {
constexpr std::size_t kMaximumWords=10000,kMaximumBytes=4*1024*1024,kMaximumBatches=256;
constexpr ULONGLONG kRefreshMilliseconds=1000;
constexpr DWORD kMutexWait=100;
constexpr unsigned kAttempts=3;
constexpr char kHeader[]="MansurNextPersonalWords=1\n";
struct Handle {
    HANDLE value=INVALID_HANDLE_VALUE;
    ~Handle(){if(value&&value!=INVALID_HANDLE_VALUE)CloseHandle(value);}
    bool valid()const noexcept{return value&&value!=INVALID_HANDLE_VALUE;}
    void close()noexcept{if(valid())CloseHandle(value);value=INVALID_HANDLE_VALUE;}
};
struct Batch {std::vector<PersonalWord> words;std::size_t bytes=0;};
struct alignas(MEMORY_ALLOCATION_ALIGNMENT) Incoming {SLIST_ENTRY link{};std::shared_ptr<const Batch> batch;};
struct FileStamp {
    bool exists=false;
    DWORD volume=0,id_high=0,id_low=0,size_high=0,size_low=0,time_high=0,time_low=0;
    bool operator==(const FileStamp& b)const noexcept{return exists==b.exists&&volume==b.volume&&id_high==b.id_high&&id_low==b.id_low&&size_high==b.size_high&&size_low==b.size_low&&time_high==b.time_high&&time_low==b.time_low;}
};
FileStamp stamp(const BY_HANDLE_FILE_INFORMATION& info) noexcept {
    return {true,info.dwVolumeSerialNumber,info.nFileIndexHigh,info.nFileIndexLow,info.nFileSizeHigh,info.nFileSizeLow,info.ftLastWriteTime.dwHighDateTime,info.ftLastWriteTime.dwLowDateTime};
}
struct Store {
    Store(){InitializeSListHead(&incoming);}
    ~Store() {
        // A running callback pins the DLL. Normal unload therefore has no
        // worker; release only memory, without file IO or waits in DllMain.
        auto entries=InterlockedFlushSList(&incoming);
        while(entries){auto next=entries->Next;delete reinterpret_cast<Incoming*>(entries);entries=next;}
    }
    std::mutex mutex;
    std::shared_ptr<const PersonalLexicon> snapshot;
    alignas(MEMORY_ALLOCATION_ALIGNMENT) SLIST_HEADER incoming;
    // Only the worker accesses pending/disk cache; producers use the SLIST.
    std::deque<std::shared_ptr<const Batch>> pending;
    std::atomic<std::size_t> pending_words{0},pending_bytes{0},pending_batches{0};
    std::atomic<bool> running{false};
    std::atomic<ULONGLONG> next_refresh{0};
    std::atomic<PersonalWordsState> status{PersonalWordsState::NotLoaded};
    std::atomic<DWORD> last_error{ERROR_SUCCESS};
    std::atomic<std::uint64_t> accepted{0},saved{0},skipped{0};
    bool disk_valid=false;
    FileStamp disk_stamp;
    std::shared_ptr<const PersonalLexicon> disk_snapshot;
#ifdef MANSUR_PERSONAL_WORDS_TEST
    std::filesystem::path test_file;
    std::wstring test_namespace;
    DWORD caller_thread=0;
#endif
};
Store& store(){static Store value;return value;}
std::atomic<std::uint64_t> rejected_batches{0};
#ifdef MANSUR_PERSONAL_WORDS_TEST
std::atomic<std::uint64_t> test_reads{0},test_writes{0},test_caller_io{0};
void count_io(bool write) noexcept {
    if(write)++test_writes;else ++test_reads;
    if(GetCurrentThreadId()==store().caller_thread)++test_caller_io;
}
#else
void count_io(bool) noexcept {}
#endif
struct Location {std::filesystem::path file;};
Location location() {
    Location result;
#ifdef MANSUR_PERSONAL_WORDS_TEST
    auto& state=store();
    // Configure can run only after all workers have exited.
    result.file=state.test_file;
    if(result.file.empty()||state.test_namespace.empty())return {};
#else
    const DWORD required=GetEnvironmentVariableW(L"USERPROFILE",nullptr,0);
    if(!required||required>32768)return {};
    std::vector<wchar_t> buffer(required);
    const DWORD count=GetEnvironmentVariableW(L"USERPROFILE",buffer.data(),required);
    if(!count||count>=required)return {};
    const std::filesystem::path profile(std::wstring(buffer.data(),count));
    if(!profile.is_absolute())return {};
    result.file=profile/L".mansur-next"/L"learned-words.tsv";
#endif
    return result;
}
struct Result {
    PersonalWordsState state=PersonalWordsState::Ready;
    DWORD error=ERROR_SUCCESS;
    std::shared_ptr<const PersonalLexicon> snapshot;
    std::uint64_t skipped_new_words=0;
};
Result failure(PersonalWordsState state,DWORD error){return {state,error,{}};}
bool uses_value(std::string_view text,std::uint32_t& out) noexcept {
    if(text.empty()||text.front()<'1'||text.front()>'9')return false;
    std::uint32_t value=0;
    for(char digit:text){if(digit<'0'||digit>'9')return false;const auto n=static_cast<unsigned>(digit-'0');if(value>(1000000-n)/10)return false;value=value*10+n;}
    out=value;return true;
}
bool parse(std::string_view data,std::vector<PersonalWord>& words) {
    if(data.empty()||data.size()>kMaximumBytes||data.find('\0')!=std::string_view::npos)return false;
    std::set<std::pair<std::wstring,std::string>> seen;
    std::size_t start=0;bool first=true;
    while(start<data.size()) {
        auto end=data.find('\n',start);if(end==std::string_view::npos)end=data.size();
        auto line=data.substr(start,end-start);if(!line.empty()&&line.back()=='\r')line.remove_suffix(1);
        if(line.find('\r')!=std::string_view::npos)return false;
        if(first){if(line!="MansurNextPersonalWords=1")return false;first=false;}
        else {
            if(line.empty()||words.size()>=kMaximumWords)return false;
            const auto a=line.find('\t'),b=a==std::string_view::npos?a:line.find('\t',a+1);
            if(a==std::string_view::npos||b==std::string_view::npos||line.find('\t',b+1)!=std::string_view::npos)return false;
            PersonalWord word;
            try {word.text=from_utf8(line.substr(0,a));}catch(...){return false;}
            word.syllables=std::string(line.substr(a+1,b-a-1));
            if(!uses_value(line.substr(b+1),word.uses)||!valid_personal_word(word)||!seen.emplace(word.text,word.syllables).second)return false;
            words.push_back(std::move(word));
        }
        if(end==data.size())break;start=end+1;
    }
    return !first;
}
Result read_file(const std::filesystem::path& file,std::vector<PersonalWord>& words,FileStamp& observed,const Store& state,bool use_cache) {
    count_io(false);
    Handle handle{CreateFileW(file.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_SEQUENTIAL_SCAN,nullptr)};
    if(!handle.valid()) {
        const auto error=GetLastError();
        if(error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND) {
            observed={};Result result;
            if(use_cache&&state.disk_valid&&!state.disk_stamp.exists)result.snapshot=state.disk_snapshot;
            return result;
        }
        return failure(PersonalWordsState::IoError,error);
    }
    BY_HANDLE_FILE_INFORMATION info{};LARGE_INTEGER size{};
    if(!GetFileInformationByHandle(handle.value,&info)||!GetFileSizeEx(handle.value,&size))return failure(PersonalWordsState::IoError,GetLastError());
    if(info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT))return failure(PersonalWordsState::IoError,ERROR_INVALID_DATA);
    if(size.QuadPart<=0||size.QuadPart>static_cast<LONGLONG>(kMaximumBytes))return failure(PersonalWordsState::InvalidData,ERROR_FILE_TOO_LARGE);
    observed=stamp(info);
    if(use_cache&&state.disk_valid&&observed==state.disk_stamp)return {PersonalWordsState::Ready,ERROR_SUCCESS,state.disk_snapshot};
    std::string bytes(static_cast<std::size_t>(size.QuadPart),'\0');std::size_t at=0;
    while(at<bytes.size()) {
        DWORD obtained=0;
        if(!ReadFile(handle.value,bytes.data()+at,static_cast<DWORD>(bytes.size()-at),&obtained,nullptr))return failure(PersonalWordsState::IoError,GetLastError());
        if(!obtained)return failure(PersonalWordsState::IoError,ERROR_HANDLE_EOF);at+=obtained;
    }
    if(!parse(bytes,words))return failure(PersonalWordsState::InvalidData,ERROR_INVALID_DATA);
    return {};
}
Result save_file(const std::filesystem::path& file,const std::vector<PersonalWord>& words) {
    std::string bytes=kHeader;
    for(const auto& word:words) {
        bytes+=to_utf8(word.text);bytes+='\t';bytes+=word.syllables;bytes+='\t';bytes+=std::to_string(word.uses);bytes+='\n';
        if(bytes.size()>kMaximumBytes)return failure(PersonalWordsState::Limit,ERROR_FILE_TOO_LARGE);
    }
    count_io(true);
    const auto directory=file.parent_path();
    if(!CreateDirectoryW(directory.c_str(),nullptr)&&GetLastError()!=ERROR_ALREADY_EXISTS)return failure(PersonalWordsState::IoError,GetLastError());
    const DWORD attributes=GetFileAttributesW(directory.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES||!(attributes&FILE_ATTRIBUTE_DIRECTORY)||(attributes&FILE_ATTRIBUTE_REPARSE_POINT))return failure(PersonalWordsState::IoError,ERROR_DIRECTORY);
    GUID id{};wchar_t token[40]{};
    if(FAILED(CoCreateGuid(&id))||!StringFromGUID2(id,token,40))return failure(PersonalWordsState::IoError,ERROR_GEN_FAILURE);
    const auto temporary=directory/(L".learned-words-"+std::wstring(token)+L".tmp");
    struct Temporary {const std::filesystem::path& path;~Temporary(){DeleteFileW(path.c_str());}} cleanup{temporary};
    Handle output{CreateFileW(temporary.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr)};
    if(!output.valid())return failure(PersonalWordsState::IoError,GetLastError());
    std::size_t at=0;
    while(at<bytes.size()) {
        DWORD written=0;
        if(!WriteFile(output.value,bytes.data()+at,static_cast<DWORD>(bytes.size()-at),&written,nullptr))return failure(PersonalWordsState::IoError,GetLastError());
        if(!written)return failure(PersonalWordsState::IoError,ERROR_WRITE_FAULT);at+=written;
    }
    if(!FlushFileBuffers(output.value))return failure(PersonalWordsState::IoError,GetLastError());
    output.close();
    if(!MoveFileExW(temporary.c_str(),file.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH))return failure(PersonalWordsState::IoError,GetLastError());
    return {};
}
Result merge(Store& state,const Location& where,const std::vector<std::shared_ptr<const Batch>>& batches) {
    if(where.file.empty())return failure(PersonalWordsState::Unavailable,ERROR_PATH_NOT_FOUND);
    // An ordinary file lock reaches the same physical profile directory from
    // packaged/unpackaged hosts and across x86/x64/sessions. A kernel namespace
    // fallback could accidentally produce two independent locks, so none exists.
    const auto directory=where.file.parent_path();
    if(!CreateDirectoryW(directory.c_str(),nullptr)&&GetLastError()!=ERROR_ALREADY_EXISTS)return failure(PersonalWordsState::IoError,GetLastError());
    const auto attributes=GetFileAttributesW(directory.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES||!(attributes&FILE_ATTRIBUTE_DIRECTORY)||(attributes&FILE_ATTRIBUTE_REPARSE_POINT))return failure(PersonalWordsState::IoError,ERROR_DIRECTORY);
    const auto lock_path=std::filesystem::path(where.file.native()+L".lock");
    Handle write_lock;const auto deadline=GetTickCount64()+kMutexWait;
    for(;;) {
        write_lock.value=CreateFileW(lock_path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
        if(write_lock.valid())break;
        const auto error=GetLastError();
        if(error!=ERROR_SHARING_VIOLATION&&error!=ERROR_LOCK_VIOLATION)return failure(PersonalWordsState::IoError,error);
        if(GetTickCount64()>=deadline)return failure(PersonalWordsState::Busy,ERROR_TIMEOUT);
        Sleep(10);
    }
    BY_HANDLE_FILE_INFORMATION lock_info{};
    if(!GetFileInformationByHandle(write_lock.value,&lock_info))return failure(PersonalWordsState::IoError,GetLastError());
    if((lock_info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT))||lock_info.nFileSizeHigh||lock_info.nFileSizeLow)return failure(PersonalWordsState::InvalidData,ERROR_INVALID_DATA);
    FileStamp observed;std::vector<PersonalWord> disk;auto result=read_file(where.file,disk,observed,state,batches.empty());
    if(result.state!=PersonalWordsState::Ready)return result;
    if(result.snapshot)return result;
    std::uint64_t skipped=0;
    if(!batches.empty()) {
        std::map<std::pair<std::wstring,std::string>,std::uint32_t> combined;
        for(const auto& word:disk)combined.emplace(std::make_pair(word.text,word.syllables),word.uses);
        for(const auto& batch:batches)for(const auto& word:batch->words) {
            const auto key=std::make_pair(word.text,word.syllables);
            auto at=combined.find(key);
            if(at==combined.end()) {
                if(combined.size()>=kMaximumWords){++skipped;continue;}
                at=combined.emplace(key,0u).first;
            }
            at->second=static_cast<std::uint32_t>(std::min<std::uint64_t>(1000000,static_cast<std::uint64_t>(at->second)+word.uses));
        }
        disk.clear();disk.reserve(combined.size());
        for(const auto& word:combined)disk.push_back({word.first.first,word.first.second,word.second});
    }
    // Construct before writing: validation/allocation failure cannot leave the
    // disk changed while the retained queue would later apply the delta twice.
    auto snapshot=std::make_shared<const PersonalLexicon>(disk);
    if(!batches.empty()) {
        result=save_file(where.file,disk);if(result.state!=PersonalWordsState::Ready)return result;
        // This metadata query is only an optimization. A failure AFTER rename
        // must never retain/replay an already persisted usage increment.
        Handle file{CreateFileW(where.file.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr)};
        BY_HANDLE_FILE_INFORMATION info{};
        state.disk_valid=file.valid()&&GetFileInformationByHandle(file.value,&info);
        if(state.disk_valid)observed=stamp(info);
    }else state.disk_valid=true;
    state.disk_stamp=observed;state.disk_snapshot=snapshot;
    result.snapshot=std::move(snapshot);result.skipped_new_words=skipped;return result;
}
bool same_words(const std::shared_ptr<const PersonalLexicon>& a,const std::shared_ptr<const PersonalLexicon>& b) noexcept {
    if(!a||!b)return a==b;const auto& left=a->words();const auto& right=b->words();
    if(left.size()!=right.size())return false;
    for(std::size_t i=0;i<left.size();++i)if(left[i].text!=right[i].text||left[i].syllables!=right[i].syllables||left[i].uses!=right[i].uses)return false;
    return true;
}
void drain(Store& state) {
    auto entries=InterlockedFlushSList(&state.incoming);
    // Reverse the detached LIFO chain without allocation. Failed deque growth
    // returns every not-yet-owned node to the SLIST, so it cannot lose a batch.
    PSLIST_ENTRY ordered=nullptr;
    while(entries){auto next=entries->Next;entries->Next=ordered;ordered=entries;entries=next;}
    try {
        while(ordered) {
            auto node=reinterpret_cast<Incoming*>(ordered);
            state.pending.push_back(node->batch);
            ordered=ordered->Next;delete node;
        }
    }catch(...) {
        while(ordered){auto next=ordered->Next;InterlockedPushEntrySList(&state.incoming,ordered);ordered=next;}
        throw;
    }
}
bool schedule(Store& state) noexcept;
void CALLBACK worker(PTP_CALLBACK_INSTANCE callback,void* module) noexcept {
    FreeLibraryWhenCallbackReturns(callback,static_cast<HMODULE>(module));
    ++live_objects;struct Lifetime {~Lifetime(){--live_objects;}} lifetime;
    auto& state=store();
    try {
        const auto where=location();
        // At most four groups per burst; later Get/Remember schedules leftovers.
        for(unsigned burst=0;burst<4;++burst) {
            std::vector<std::shared_ptr<const Batch>> batches;
            drain(state);batches.assign(state.pending.begin(),state.pending.end());
            Result result;
            for(unsigned attempt=0;attempt<kAttempts;++attempt) {
                result=merge(state,where,batches);
                if(result.state==PersonalWordsState::Ready||result.state==PersonalWordsState::InvalidData||result.state==PersonalWordsState::Limit||result.state==PersonalWordsState::Unavailable)break;
                if(attempt+1<kAttempts)Sleep(25);
            }
            bool again=false;
            if(result.state==PersonalWordsState::Ready) {
                std::shared_ptr<const PersonalLexicon> previous,retired;
                {std::lock_guard<std::mutex> lock(state.mutex);previous=state.snapshot;}
                const bool changed=!same_words(previous,result.snapshot);
                if(changed) {std::lock_guard<std::mutex> lock(state.mutex);retired=std::move(state.snapshot);state.snapshot=result.snapshot;}
                // previous/retired are released outside the cache mutex.
                for(const auto& batch:batches) {
                    state.pending_words.fetch_sub(batch->words.size());state.pending_bytes.fetch_sub(batch->bytes);--state.pending_batches;
                    state.pending.pop_front();++state.saved;
                }
                again=state.pending_batches.load()!=0;
                state.skipped.fetch_add(result.skipped_new_words);
                state.status=result.skipped_new_words?PersonalWordsState::Limit:(again?PersonalWordsState::Pending:PersonalWordsState::Ready);
            }else state.status=result.state;
            state.last_error=result.skipped_new_words?ERROR_TOO_MANY_NAMES:result.error;
            if(!again||burst==3) {
                state.next_refresh=GetTickCount64()+kRefreshMilliseconds;state.running=false;
                // Close the enqueue/worker-exit race without replaying a failed
                // retained batch on its own. A newly pushed node is fresh work.
                if(QueryDepthSList(&state.incoming)!=0)schedule(state);
                return;
            }
        }
    }catch(...) {
        // The queue was not removed until a successful atomic replacement. A
        // failed burst is visible, retains all deltas, and does not retry forever.
        state.next_refresh=GetTickCount64()+kRefreshMilliseconds;
        state.status=PersonalWordsState::IoError;state.last_error=ERROR_NOT_ENOUGH_MEMORY;state.running=false;
    }
}
bool schedule(Store& state) noexcept {
    bool expected=false;if(!state.running.compare_exchange_strong(expected,true))return true;
    HMODULE pin=nullptr;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,reinterpret_cast<LPCWSTR>(&GetPersonalLexicon),&pin)) {
        state.status=PersonalWordsState::Unavailable;state.last_error=GetLastError();state.next_refresh=GetTickCount64()+kRefreshMilliseconds;state.running=false;return false;
    }
    if(!TrySubmitThreadpoolCallback(worker,pin,nullptr)) {
        state.status=PersonalWordsState::Unavailable;state.last_error=GetLastError();state.next_refresh=GetTickCount64()+kRefreshMilliseconds;state.running=false;
        FreeLibrary(pin);return false;
    }
    return true;
}
}
std::shared_ptr<const PersonalLexicon> GetPersonalLexicon() noexcept {
    try {
        auto& state=store();std::shared_ptr<const PersonalLexicon> result;
        {std::unique_lock<std::mutex> lock(state.mutex,std::try_to_lock);if(lock.owns_lock())result=state.snapshot;}
        if(!state.running.load()&&GetTickCount64()>=state.next_refresh.load())schedule(state);
        return result;
    }catch(...){return {};}
}
bool RememberPersonalWords(const std::vector<PersonalWord>& words) noexcept {
    try {
        if(words.empty())return true;
        if(words.size()>kMaximumWords){++rejected_batches;return false;}
        auto batch=std::make_shared<Batch>();batch->words.reserve(words.size());
        for(const auto& word:words) {
            if(!valid_personal_word(word)){++rejected_batches;return false;}
            batch->bytes+=word.text.size()*3+word.syllables.size()+32;
            if(batch->bytes>kMaximumBytes){++rejected_batches;return false;}
            batch->words.push_back(word);
        }
        auto node=std::make_unique<Incoming>();node->batch=batch;auto& state=store();
        if(state.pending_words.fetch_add(words.size())+words.size()>kMaximumWords) {state.pending_words.fetch_sub(words.size());++rejected_batches;return false;}
        if(state.pending_bytes.fetch_add(batch->bytes)+batch->bytes>kMaximumBytes) {state.pending_bytes.fetch_sub(batch->bytes);state.pending_words.fetch_sub(words.size());++rejected_batches;return false;}
        if(state.pending_batches.fetch_add(1)>=kMaximumBatches) {--state.pending_batches;state.pending_bytes.fetch_sub(batch->bytes);state.pending_words.fetch_sub(words.size());++rejected_batches;return false;}
        InterlockedPushEntrySList(&state.incoming,&node.release()->link);
        ++state.accepted;state.status=PersonalWordsState::Pending;
        // Even failure to start a worker leaves the acknowledged queue intact.
        schedule(state);return true;
    }catch(...){++rejected_batches;return false;}
}
PersonalWordsDiagnostics GetPersonalWordsDiagnostics() noexcept {
    try {
        auto& state=store();return {state.status.load(),static_cast<std::uint32_t>(state.pending_words.load()),state.last_error.load(),state.accepted.load(),state.saved.load(),rejected_batches.load(),state.skipped.load()};
    }catch(...){return {PersonalWordsState::Unavailable,0,ERROR_NOT_ENOUGH_MEMORY,0,0,rejected_batches.load()};}
}
#ifdef MANSUR_PERSONAL_WORDS_TEST
bool PersonalWordsTestConfigure(const std::filesystem::path& file,const std::wstring& name) noexcept {
    try {
        auto& state=store();std::lock_guard<std::mutex> lock(state.mutex);
        if(state.running.load()||!file.is_absolute()||name.empty()||name.size()>128||name.find_first_not_of(L"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-.")!=std::wstring::npos)return false;
        state.test_file=file;state.test_namespace=name;state.caller_thread=GetCurrentThreadId();
        auto entries=InterlockedFlushSList(&state.incoming);while(entries){auto next=entries->Next;delete reinterpret_cast<Incoming*>(entries);entries=next;}
        state.snapshot.reset();state.pending.clear();state.pending_words=0;state.pending_bytes=0;state.pending_batches=0;state.next_refresh=0;
        state.status=PersonalWordsState::NotLoaded;state.last_error=0;state.accepted=0;state.saved=0;state.skipped=0;state.disk_valid=false;state.disk_snapshot.reset();
        rejected_batches=0;test_reads=0;test_writes=0;test_caller_io=0;return true;
    }catch(...){return false;}
}
bool PersonalWordsTestWait(DWORD milliseconds) noexcept {
    const auto deadline=GetTickCount64()+milliseconds;
    do {
        auto& state=store();{std::unique_lock<std::mutex> lock(state.mutex,std::try_to_lock);if(lock.owns_lock()&&!state.running&&live_objects.load()==0)return true;}
        Sleep(2);
    }while(GetTickCount64()<deadline);return false;
}
void PersonalWordsTestRefresh() noexcept {try{auto& state=store();std::lock_guard<std::mutex> lock(state.mutex);state.next_refresh=0;}catch(...) {}}
PersonalWordsTestIo PersonalWordsTestOperations() noexcept{return {test_reads.load(),test_writes.load(),test_caller_io.load()};}
#endif
}
