// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "learning_dispatch.hpp"
#include "module.hpp"
#include <sddl.h>
#include <objbase.h>
#include <atomic>
#include <algorithm>
#include <memory>
#include <limits>
#include <mutex>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace mansur::win {
namespace {
constexpr std::size_t kMaxPacket=64*1024;
constexpr ULONGLONG kConnectBudgetMs=1200;
constexpr DWORD kConnectPauseMs=20;
constexpr ULONGLONG kAcknowledgeBudgetMs=750;
constexpr unsigned char kReceivedAck=0x06;
struct Handle {
    HANDLE value=INVALID_HANDLE_VALUE;
    ~Handle(){if(value!=INVALID_HANDLE_VALUE&&value)CloseHandle(value);}
};
struct EventStamp {std::int64_t ticks=0;std::uint64_t sequence=0;};
struct Packet {std::string json;std::uint64_t context=0;std::uint64_t sequence=0;EventStamp sent;};
std::atomic<std::uint64_t> next_event_sequence{0};
#ifdef MANSUR_TRANSPORT_TEST
std::atomic<bool> test_counter_failure{false};
std::atomic<unsigned> test_connect_attempts{0};
std::atomic<HANDLE> test_before_write_reached{nullptr},test_before_write_continue{nullptr};
std::atomic<bool> test_partial_write{false};
#endif
bool capture_stamp(EventStamp& stamp) noexcept {
#ifdef MANSUR_TRANSPORT_TEST
    if(test_counter_failure.load())return false;
#endif
    LARGE_INTEGER counter{};
    if(!QueryPerformanceCounter(&counter)||counter.QuadPart<=0)return false;
    stamp.ticks=counter.QuadPart;
    stamp.sequence=++next_event_sequence;
    return stamp.sequence!=0&&stamp.sequence<=static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max());
}
struct Transport {
    std::mutex mutex;
    std::optional<Packet> latest;
    std::wstring pipe;
    std::string process_token;
    std::atomic<std::uint64_t> sequence{0};
    bool running=false;
    DeliverySnapshot status;
    EventStamp newest_sent;
    Transport() {
        HANDLE raw=nullptr;
        if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&raw))return;
        Handle token{raw};DWORD bytes=0;
        GetTokenInformation(token.value,TokenUser,nullptr,0,&bytes);
        if(!bytes)return;
        std::vector<unsigned char> data(bytes);
        if(!GetTokenInformation(token.value,TokenUser,data.data(),bytes,&bytes))return;
        LPWSTR sid=nullptr;
        if(!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid,&sid))return;
        try {pipe=L"\\\\.\\pipe\\MansurNext.Learning."+std::wstring(sid);} catch(...) {LocalFree(sid);throw;}
        LocalFree(sid);
#ifdef MANSUR_TRANSPORT_TEST
        pipe+=L".SelfTest."+std::to_wstring(GetCurrentProcessId());
#endif
        GUID token_id{};wchar_t guid[40]{};
        if(FAILED(CoCreateGuid(&token_id))||!StringFromGUID2(token_id,guid,40)) {pipe.clear();return;}
        process_token=std::to_string(GetCurrentProcessId())+":"+to_utf8(guid);
    }
};
Transport& transport() {static Transport value;return value;}
std::string escaped(std::string_view text) {
    std::string result;result.reserve(text.size()+16);result+='"';
    constexpr char digits[]="0123456789abcdef";
    for(const unsigned char c:text) {
        if(c=='"'||c=='\\') {result+='\\';result+=static_cast<char>(c);}
        else if(c<0x20) {result+="\\u00";result+=digits[c>>4];result+=digits[c&15];}
        else result+=static_cast<char>(c);
    }
    result+='"';return result;
}
std::string stamp_json(const EventStamp& stamp,const std::string& sender) {
    return ",\"sent_ticks\":"+std::to_string(stamp.ticks)+",\"sender\":"+escaped(sender)+
        ",\"sent_sequence\":"+std::to_string(stamp.sequence);
}
DeliveryStatus deliver(Transport& state,const Packet& packet) noexcept {
    Handle handle;
    const auto deadline=GetTickCount64()+kConnectBudgetMs;
    // Only this background worker retries. No byte has been submitted while
    // acquiring a connection; a busy listener or brief startup gap is retryable.
    for(;;) {
        if(packet.sequence!=state.sequence.load())return DeliveryStatus::Rejected;
        if(GetTickCount64()>=deadline)return DeliveryStatus::Unavailable;
#ifdef MANSUR_TRANSPORT_TEST
        ++test_connect_attempts;
#endif
        handle.value=CreateFileW(state.pipe.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED|SECURITY_SQOS_PRESENT|SECURITY_IDENTIFICATION,nullptr);
        if(handle.value!=INVALID_HANDLE_VALUE)break;
        const auto error=GetLastError();
        if(error!=ERROR_PIPE_BUSY&&error!=ERROR_FILE_NOT_FOUND&&error!=ERROR_PATH_NOT_FOUND)
            return DeliveryStatus::Unavailable;
        if(GetTickCount64()>=deadline)return DeliveryStatus::Unavailable;
        // A small bounded pause also makes a new cancel/learn supersede this
        // packet promptly. Never wait on the input thread or an unbounded queue.
        Sleep(kConnectPauseMs);
    }
#ifdef MANSUR_TRANSPORT_TEST
    const auto reached=test_before_write_reached.load();const auto proceed=test_before_write_continue.load();
    if(reached&&proceed){SetEvent(reached);WaitForSingleObject(proceed,1000);}
#endif
    // Acquiring a pipe can race a later user event. Drop stale data even after a
    // connection was obtained; the broker will observe only an empty connection.
    if(packet.sequence!=state.sequence.load())return DeliveryStatus::Rejected;
    Handle event{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    if(!event.value)return DeliveryStatus::Rejected;
    if(packet.sequence!=state.sequence.load())return DeliveryStatus::Rejected;
    const auto& json=packet.json;
    OVERLAPPED overlap{};overlap.hEvent=event.value;DWORD written=0;
    DWORD requested=static_cast<DWORD>(json.size());
#ifdef MANSUR_TRANSPORT_TEST
    if(test_partial_write.load())requested/=2;
#endif
    // After entering WriteFile the outcome may be partial/ambiguous. There is
    // deliberately no retry path for any write result, including timeout.
    BOOL completed=WriteFile(handle.value,json.data(),requested,&written,&overlap);
    if(!completed&&GetLastError()!=ERROR_IO_PENDING) return DeliveryStatus::Unavailable;
    bool timed_out=false;
    if(!completed) {
        const DWORD wait=WaitForSingleObject(event.value,200);
        if(wait!=WAIT_OBJECT_0) {
            timed_out=true;CancelIoEx(handle.value,&overlap);
            // Only the worker waits. OVERLAPPED and buffer remain alive until the
            // cancelled operation completes; never free a pending kernel buffer.
            WaitForSingleObject(event.value,INFINITE);
        }
        completed=GetOverlappedResult(handle.value,&overlap,&written,FALSE);
    }
    if(timed_out)return DeliveryStatus::TimedOut;
    if(!completed||written!=json.size())return DeliveryStatus::Unavailable;
    // Keeping this endpoint alive until the reader acknowledges the full line
    // prevents client-close from overtaking the server's ConnectNamedPipe call.
    // This confirms transport receipt only, never translation/audio success.
    ResetEvent(event.value);overlap={};overlap.hEvent=event.value;
    unsigned char ack=0;DWORD received=0;
    completed=ReadFile(handle.value,&ack,1,&received,&overlap);
    if(!completed&&GetLastError()!=ERROR_IO_PENDING)return DeliveryStatus::Unavailable;
    if(!completed) {
        const auto ack_deadline=GetTickCount64()+kAcknowledgeBudgetMs;
        DeliveryStatus interrupted=DeliveryStatus::Idle;
        for(;;) {
            const auto wait=WaitForSingleObject(event.value,kConnectPauseMs);
            if(wait==WAIT_OBJECT_0)break;
            if(packet.sequence!=state.sequence.load()){interrupted=DeliveryStatus::Rejected;break;}
            if(wait!=WAIT_TIMEOUT||GetTickCount64()>=ack_deadline){interrupted=DeliveryStatus::TimedOut;break;}
        }
        if(interrupted!=DeliveryStatus::Idle) {
            CancelIoEx(handle.value,&overlap);
            // The outstanding read owns this stack byte until cancellation has
            // completed. Only the worker waits; the written packet is never replayed.
            GetOverlappedResult(handle.value,&overlap,&received,TRUE);
            return interrupted;
        }
        completed=GetOverlappedResult(handle.value,&overlap,&received,FALSE);
    }
    return completed&&received==1&&ack==kReceivedAck?DeliveryStatus::Delivered:DeliveryStatus::Unavailable;
}
void CALLBACK worker(PTP_CALLBACK_INSTANCE callback,void* module_pointer) noexcept {
    FreeLibraryWhenCallbackReturns(callback,static_cast<HMODULE>(module_pointer));
    ++live_objects;
    struct Count {~Count(){--live_objects;}} count;
    try {
        auto& state=transport();
        for(;;) {
            std::optional<Packet> packet;
            {
                std::lock_guard<std::mutex> lock(state.mutex);
                if(!state.latest) {state.running=false;return;}
                packet=std::move(state.latest);state.latest.reset();
            }
            const auto status=deliver(state,*packet);
            {
                std::lock_guard<std::mutex> lock(state.mutex);
                if(packet->sequence==state.sequence.load())state.status={status,packet->context};
            }
        }
    } catch(...) {
        try {auto& state=transport();std::lock_guard<std::mutex> lock(state.mutex);
            state.running=false;state.latest.reset();state.status.status=DeliveryStatus::Rejected;
        } catch(...) {}
    }
}
DispatchResult enqueue(Packet packet) {
    auto& state=transport();
    if(state.pipe.empty())return DispatchResult::NotConnected;
    if(packet.json.size()>kMaxPacket)return DispatchResult::Rejected;
    std::unique_lock<std::mutex> lock(state.mutex,std::try_to_lock);
    if(!lock.owns_lock())return DispatchResult::Rejected;
    if(packet.sent.ticks<state.newest_sent.ticks ||
       (packet.sent.ticks==state.newest_sent.ticks&&packet.sent.sequence<=state.newest_sent.sequence))
        return DispatchResult::Rejected;
    state.newest_sent=packet.sent;
    packet.sequence=++state.sequence;state.status={DeliveryStatus::Queued,packet.context};
    state.latest=std::move(packet);
    if(state.running)return DispatchResult::Queued;
    HMODULE pin=nullptr;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(&LearningDispatch),&pin)) {
        state.latest.reset();state.status.status=DeliveryStatus::Rejected;return DispatchResult::Rejected;
    }
    state.running=true;
    if(!TrySubmitThreadpoolCallback(worker,pin,nullptr)) {
        state.running=false;state.latest.reset();state.status.status=DeliveryStatus::Rejected;
        lock.unlock();FreeLibrary(pin);return DispatchResult::Rejected;
    }
    return DispatchResult::Queued;
}
}
DispatchResult LearningDispatch(const LearningRequest& request) noexcept {
    try {
        EventStamp sent;
        // Capture user-event order before serialization/queue contention. This
        // clock is shared across Windows processes; never substitute wall time.
        if(!capture_stamp(sent))return DispatchResult::Rejected;
        if(!request.commit.learn||request.commit.text.empty()||request.commit.text.size()>2048) return DispatchResult::Rejected;
        const auto& state=transport();
        const auto context=state.process_token+":"+std::to_string(request.context_id);
        std::string json="{\"op\":\"learn\",\"text\":"+escaped(to_utf8(request.commit.text))+
            ",\"context\":"+escaped(context)+",\"revision\":"+std::to_string(request.source_revision)+
            stamp_json(sent,state.process_token);
        if(request.caret.valid) {
            const auto& r=request.caret.rect;
            json+=",\"anchor\":{\"left\":"+std::to_string(r.left)+",\"top\":"+std::to_string(r.top)+
                ",\"right\":"+std::to_string(r.right)+",\"bottom\":"+std::to_string(r.bottom)+"}";
        } else json+=",\"anchor\":null";
        if(request.writeback_endpoint&&request.writeback_token.size()==32&&
            std::all_of(request.writeback_token.begin(),request.writeback_token.end(),[](char c){return (c>='0'&&c<='9')||(c>='a'&&c<='f');}))
            json+=",\"writeback\":{\"endpoint\":\""+std::to_string(reinterpret_cast<std::uintptr_t>(request.writeback_endpoint))+
                "\",\"token\":"+escaped(request.writeback_token)+"}";
        json+="}\n";
        return enqueue({std::move(json),request.context_id,0,sent});
    } catch(...) {return DispatchResult::Rejected;}
}
DispatchResult CancelLearning(std::uint64_t context_id) noexcept {
    try {
        EventStamp sent;
        if(!capture_stamp(sent))return DispatchResult::Rejected;
        const auto& state=transport();
        const auto context=state.process_token+":"+std::to_string(context_id);
        return enqueue({"{\"op\":\"cancel\",\"context\":"+escaped(context)+
            stamp_json(sent,state.process_token)+"}\n",context_id,0,sent});
    } catch(...) {return DispatchResult::Rejected;}
}
DeliverySnapshot GetLastDeliveryStatus() noexcept {
    try {
        auto& state=transport();std::unique_lock<std::mutex> lock(state.mutex,std::try_to_lock);
        if(lock.owns_lock())return state.status;
    } catch(...) {}
    return {};
}
#ifdef MANSUR_TRANSPORT_TEST
std::wstring LearningTestPipeName() {return transport().pipe;}
void LearningTestFailCounter(bool fail) noexcept {test_counter_failure=fail;}
unsigned LearningTestConnectAttempts() noexcept {return test_connect_attempts.load();}
void LearningTestPauseBeforeWrite(HANDLE reached,HANDLE proceed) noexcept {
    test_before_write_reached=reached;test_before_write_continue=proceed;
}
void LearningTestPartialWrite(bool partial) noexcept {test_partial_write=partial;}
void LearningTestSetPipeName(std::wstring name) {transport().pipe=std::move(name);}
#endif
}
