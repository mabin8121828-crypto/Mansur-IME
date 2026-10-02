// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "tsf_service.hpp"
#include "com_ptr.hpp"
#include "host_policy.hpp"
#include "ids.hpp"
#include "module.hpp"
#include "preview_window.hpp"
#include "edit_sessions.hpp"
#include "dictionary_loader.hpp"
#include "personal_words.hpp"
#include "mode_endpoint.hpp"
#include "writeback_endpoint.hpp"
#include "mansur/core.hpp"
#include <msctf.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace mansur::win {
#ifdef MANSUR_KEY_ROUTING_TEST
std::shared_ptr<const Dictionary> key_routing_test_dictionary();
NativeSettings key_routing_test_settings();
HWND key_routing_test_foreground();
SHORT key_routing_test_key_state(int key);
ULONGLONG key_routing_test_time();
wchar_t key_routing_test_character(WPARAM key,LPARAM flags);
#endif
namespace {
using edit::ReadMetadata;
using edit::WriteCommit;
SHORT key_state(int key) noexcept {
#ifdef MANSUR_KEY_ROUTING_TEST
    return key_routing_test_key_state(key);
#else
    return GetKeyState(key);
#endif
}
ULONGLONG key_time() noexcept {
#ifdef MANSUR_KEY_ROUTING_TEST
    return key_routing_test_time();
#else
    return GetTickCount64();
#endif
}
bool shift_key(WPARAM key) noexcept {return key==VK_SHIFT||key==VK_LSHIFT||key==VK_RSHIFT;}
unsigned shift_identity(WPARAM key,LPARAM flags) noexcept {
    const auto scan=static_cast<unsigned>((static_cast<ULONG_PTR>(flags)>>16)&255u);
    if(scan)return scan;
    return key==VK_LSHIFT?0x2au:key==VK_RSHIFT?0x36u:VK_SHIFT;
}
bool other_key_held() noexcept {
    if((key_state(VK_LSHIFT)&0x8000)&&(key_state(VK_RSHIFT)&0x8000))return true;
    // Check only current down states, not toggle bits (e.g. Caps Lock). This
    // also excludes Shift started while a letter or mouse button is held.
    for(int key=1;key<256;++key)if(!shift_key(key)&&(key_state(key)&0x8000))return true;
    return false;
}
std::atomic<std::uint64_t> next_context_id{0};
std::uint32_t next_mode_token() noexcept {
    static std::atomic<std::uint32_t> sequence{[] {GUID id{};CoCreateGuid(&id);return id.Data1&0x7fffffffu;}()};
    std::uint32_t value=0;while(!value)value=(++sequence)&0x7fffffffu;return value;
}
HWND mode_foreground() noexcept {
#ifdef MANSUR_KEY_ROUTING_TEST
    return key_routing_test_foreground();
#else
    const HWND window=GetForegroundWindow();DWORD process=0;
    if(window)GetWindowThreadProcessId(window,&process);
    return process==GetCurrentProcessId()?window:nullptr;
#endif
}
template<class F> HRESULT boundary(F&& fn) noexcept {
    try {return fn();} catch(const std::bad_alloc&) {return E_OUTOFMEMORY;} catch(...) {return E_FAIL;}
}
bool same_identity(IUnknown* left,IUnknown* right) {
    if(!left||!right) return left==right;
    ComPtr<IUnknown> a,b;
    return SUCCEEDED(left->QueryInterface(IID_IUnknown,reinterpret_cast<void**>(a.put())))&&
        SUCCEEDED(right->QueryInterface(IID_IUnknown,reinterpret_cast<void**>(b.put())))&&a.get()==b.get();
}
std::shared_ptr<const Dictionary> load_dictionary() {
#ifdef MANSUR_KEY_ROUTING_TEST
    return key_routing_test_dictionary();
#else
    // This cache contains data only. Its mutex is never held while calling host COM.
    static DictionaryCache cache;
    std::vector<wchar_t> path(32768);
    const DWORD n=GetModuleFileNameW(module_instance,path.data(),static_cast<DWORD>(path.size()));
    if(!n||n>=path.size()) return {};
    const auto directory=std::filesystem::path(std::wstring(path.data(),n)).parent_path()/L"data";
    return cache.load(directory,ReadNativeSettings(true));
#endif
}
NativeSettings input_settings() noexcept {
#ifdef MANSUR_KEY_ROUTING_TEST
    return key_routing_test_settings();
#else
    return ReadNativeSettings();
#endif
}
class ActionSession final:public ITfEditSession,private ModuleObject {
public:
    explicit ActionSession(std::function<HRESULT(TfEditCookie)> action):action_(std::move(action)){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) noexcept override {
        if(!out)return E_POINTER;*out=nullptr;
        if(iid!=IID_IUnknown&&iid!=IID_ITfEditSession)return E_NOINTERFACE;
        *out=static_cast<ITfEditSession*>(this);AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override{return ++refs_;}
    ULONG STDMETHODCALLTYPE Release() noexcept override{auto n=--refs_;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE DoEditSession(TfEditCookie cookie) noexcept override {
        return boundary([&] {if(called_)return E_UNEXPECTED;called_=true;return action_(cookie);});
    }
private:
    std::atomic<ULONG> refs_{1};bool called_=false;
    std::function<HRESULT(TfEditCookie)> action_;
};
struct ContextState {
    ContextState(ITfContext* value,std::shared_ptr<const Dictionary> dictionary,std::uint64_t number)
        :context(value),draft(std::move(dictionary)),id(number) {
        value->QueryInterface(IID_IUnknown,reinterpret_cast<void**>(identity.put()));
        value->GetDocumentMgr(document.put());
    }
    ComPtr<ITfContext> context;
    ComPtr<IUnknown> identity;
    ComPtr<ITfDocumentMgr> document;
    DraftSession draft;
    const std::uint64_t id;
    CaretLocation caret;
    HWND edit_owner=nullptr,edit_window=nullptr;
};
struct WritebackLease {
    std::shared_ptr<ContextState> state;
    ComPtr<ITfRange> range;
    std::wstring original;
    std::string token;
    std::uint64_t epoch=0,focus=0,revision=0;
    ULONGLONG deadline=0;
    HWND foreground=nullptr;
    bool valid=true,claimed=false;
    std::uint32_t invalidation_reason=0;
    std::uint32_t notification_stage=0,own_notifications=0;
    HRESULT notification_hr=S_OK;
    WritebackResult result=WritebackResult::Ready;
    ComPtr<ITfSource> source;
    ComPtr<WritebackEditSink> sink;
    DWORD cookie=TF_INVALID_COOKIE;
    void invalidate(std::uint32_t reason=9) noexcept {if(valid)invalidation_reason=reason;valid=false;if(claimed&&result==WritebackResult::Ready)result=WritebackResult::Rejected;}
    void detach(std::uint32_t reason=3) noexcept {
        invalidate(reason);if(sink)sink->Detach();
        auto held=std::move(source);auto subscribed=std::exchange(cookie,TF_INVALID_COOKIE);
        if(held&&subscribed!=TF_INVALID_COOKIE)held->UnadviseSink(subscribed);
        sink={};
    }
    ~WritebackLease(){detach();}
};
wchar_t ascii_character(WPARAM key,LPARAM flags) noexcept {
#ifdef MANSUR_KEY_ROUTING_TEST
    return key_routing_test_character(key,flags);
#else
    BYTE states[256]{};if(!GetKeyboardState(states))return 0;
    const auto layout=GetKeyboardLayout(0);
    auto scan=static_cast<UINT>((static_cast<ULONG_PTR>(flags)>>16)&255u);
    if(!scan)scan=MapVirtualKeyExW(static_cast<UINT>(key),MAPVK_VK_TO_VSC,layout);
    wchar_t text[8]{};
    // Windows 10 1607+: bit 2 prevents this routing query from changing the
    // keyboard's dead-key state. Never call TranslateMessage or inject WM_CHAR.
    const int count=ToUnicodeEx(static_cast<UINT>(key),scan,states,text,8,4,layout);
    return count==1&&text[0]>=32&&text[0]<=126?text[0]:0;
#endif
}
std::optional<Key> decode_key(WPARAM value,LPARAM flags,bool chinese) {
    if((key_state(VK_CONTROL)&0x8000)||(key_state(VK_MENU)&0x8000)||
       (key_state(VK_LWIN)&0x8000)||(key_state(VK_RWIN)&0x8000)) return {};
    const bool shift=(key_state(VK_SHIFT)&0x8000)!=0;
    const bool caps=(key_state(VK_CAPITAL)&1)!=0;
    Key key{KeyKind::Letter,0,key_time(),(static_cast<ULONG_PTR>(flags)&(1ull<<30))!=0};
    if(!chinese) {
        switch(value) {
        case VK_SPACE:key.kind=KeyKind::EnglishSpace;break;
        case VK_TAB:if(shift)return {};key.kind=KeyKind::CompleteEnglish;break;
        case VK_BACK:key.kind=KeyKind::Backspace;break;
        case VK_DELETE:key.kind=KeyKind::Delete;break;
        case VK_LEFT:if(shift)return {};key.kind=KeyKind::Left;break;
        case VK_RIGHT:if(shift)return {};key.kind=KeyKind::Right;break;
        case VK_RETURN:key.kind=KeyKind::Enter;break;
        case VK_ESCAPE:key.kind=KeyKind::Escape;break;
        default:
            key.character=ascii_character(value,flags);if(!key.character)return {};
            key.kind=KeyKind::Literal;break;
        }
        return key;
    }
    if(value>='A'&&value<='Z') {
        key.character=static_cast<wchar_t>((shift!=caps?L'A':L'a')+value-'A');return key;
    }
    if(value>='0'&&value<='9'&&!shift) {key.kind=KeyKind::Digit;key.character=static_cast<wchar_t>(value);return key;}
    switch(value) {
    case VK_SPACE:if(shift) return {};key.kind=KeyKind::Space;break;
    case VK_BACK:key.kind=KeyKind::Backspace;break;
    case VK_DELETE:key.kind=KeyKind::Delete;break;
    case VK_LEFT:if(shift) return {};key.kind=KeyKind::Left;break;
    case VK_RIGHT:if(shift) return {};key.kind=KeyKind::Right;break;
    case VK_UP:if(shift) return {};key.kind=KeyKind::Up;break;
    case VK_DOWN:if(shift) return {};key.kind=KeyKind::Down;break;
    case VK_PRIOR:if(shift) return {};key.kind=KeyKind::PageUp;break;
    case VK_NEXT:if(shift) return {};key.kind=KeyKind::PageDown;break;
    case VK_OEM_MINUS:if(shift) return {};key.kind=KeyKind::PageUp;break;
    case VK_OEM_PLUS:case VK_ADD:key.kind=KeyKind::PageDown;break;
    case VK_RETURN:key.kind=KeyKind::Enter;break;
    case VK_ESCAPE:key.kind=KeyKind::Escape;break;
    case VK_OEM_COMMA:key.kind=KeyKind::Punctuation;key.character=shift?L'《':L'，';break;
    case VK_OEM_PERIOD:key.kind=KeyKind::Punctuation;key.character=shift?L'》':L'。';break;
    case VK_OEM_1:key.kind=KeyKind::Punctuation;key.character=shift?L'：':L'；';break;
    case VK_OEM_2:key.kind=KeyKind::Punctuation;key.character=shift?L'？':L'、';break;
    case VK_OEM_7:key.kind=shift?KeyKind::Punctuation:KeyKind::Letter;key.character=shift?L'“':L'\'';break;
    case VK_OEM_4:key.kind=KeyKind::Punctuation;key.character=shift?L'｛':L'【';break;
    case VK_OEM_6:key.kind=KeyKind::Punctuation;key.character=shift?L'｝':L'】';break;
    case '1':if(!shift)return {};key.kind=KeyKind::Punctuation;key.character=L'！';break;
    case '9':if(!shift)return {};key.kind=KeyKind::Punctuation;key.character=L'（';break;
    case '0':if(!shift)return {};key.kind=KeyKind::Punctuation;key.character=L'）';break;
    default:return {};
    }
    return key;
}
class TextService final:public ITfTextInputProcessorEx,public ITfKeyEventSink,
    public ITfThreadMgrEventSink,public ITfCompartmentEventSink,private ModuleObject {
public:
    TextService(){preview_.set_action_handler(this,preview_action);}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) noexcept override {
        if(!out) return E_POINTER;*out=nullptr;
        if(iid==IID_IUnknown||iid==IID_ITfTextInputProcessor||iid==IID_ITfTextInputProcessorEx)
            *out=static_cast<ITfTextInputProcessorEx*>(this);
        else if(iid==IID_ITfKeyEventSink) *out=static_cast<ITfKeyEventSink*>(this);
        else if(iid==IID_ITfThreadMgrEventSink) *out=static_cast<ITfThreadMgrEventSink*>(this);
        else if(iid==IID_ITfCompartmentEventSink) *out=static_cast<ITfCompartmentEventSink*>(this);
        else return E_NOINTERFACE;
        AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override{return ++references_;}
    ULONG STDMETHODCALLTYPE Release() noexcept override {const auto count=--references_;if(!count) delete this;return count;}
    HRESULT STDMETHODCALLTYPE Activate(ITfThreadMgr* manager,TfClientId client) noexcept override {
        return ActivateEx(manager,client,0);
    }
    HRESULT STDMETHODCALLTYPE ActivateEx(ITfThreadMgr* manager,TfClientId client,DWORD flags) noexcept override {
        return boundary([&]() -> HRESULT {
            if(!manager) return E_INVALIDARG;
            if(manager_) return E_UNEXPECTED;
            const auto host=current_host_policy();
            if(!host_can_create(host) || (flags&TF_TMAE_SECUREMODE)) return E_ACCESSDENIED;
            auto dictionary=load_dictionary();
            if(!dictionary) return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
            ComPtr<ITfKeystrokeMgr> keys;ComPtr<ITfSource> source;
            HRESULT hr=manager->QueryInterface(IID_ITfKeystrokeMgr,reinterpret_cast<void**>(keys.put()));
            if(FAILED(hr)) return hr;
            hr=manager->QueryInterface(IID_ITfSource,reinterpret_cast<void**>(source.put()));
            if(FAILED(hr)) return hr;
            manager_=ComPtr<ITfThreadMgr>(manager);client_=client;dictionary_=std::move(dictionary);
            standard_edit_only_=host==HostDecision::StandardEditOnly;
            active_=true;thread_=GetCurrentThreadId();++epoch_;++activation_count_;
            chinese_mode_=true;rotate_mode_token();
            const auto activation=epoch_;
            DWORD cookie=TF_INVALID_COOKIE;
            hr=source->AdviseSink(IID_ITfThreadMgrEventSink,static_cast<ITfThreadMgrEventSink*>(this),&cookie);
            if(!active_||epoch_!=activation) {
                if(SUCCEEDED(hr)&&cookie!=TF_INVALID_COOKIE)source->UnadviseSink(cookie);
                return E_ABORT;
            }
            if(SUCCEEDED(hr)) {
                thread_cookie_=cookie;
                hr=keys->AdviseKeyEventSink(client,static_cast<ITfKeyEventSink*>(this),TRUE);
                if(!active_||epoch_!=activation) {
                    if(SUCCEEDED(hr))keys->UnadviseKeyEventSink(client);
                    return E_ABORT;
                }
                key_advised_=SUCCEEDED(hr);
            }
            if(FAILED(hr)) deactivate();
            else {
                initialize_mode(activation);
                if(!active_||epoch_!=activation)return E_ABORT;
                mode_endpoint_.open(this,mode_callback);
                if(active_&&epoch_==activation)writeback_endpoint_.open(this,writeback_callback);
            }
            return hr;
        });
    }
    HRESULT STDMETHODCALLTYPE Deactivate() noexcept override {return boundary([&]{deactivate();return S_OK;});}
    HRESULT STDMETHODCALLTYPE OnSetFocus(BOOL foreground) noexcept override {
        return boundary([&]{if(!foreground)hide_reset();++focus_epoch_;rotate_mode_token();return S_OK;});
    }
    HRESULT STDMETHODCALLTYPE OnTestKeyDown(ITfContext* context,WPARAM key,LPARAM flags,BOOL* eaten) noexcept override {
        return handle(context,key,flags,eaten,true);
    }
    HRESULT STDMETHODCALLTYPE OnKeyDown(ITfContext* context,WPARAM key,LPARAM flags,BOOL* eaten) noexcept override {
        return handle(context,key,flags,eaten,false);
    }
    HRESULT STDMETHODCALLTYPE OnTestKeyUp(ITfContext* context,WPARAM key,LPARAM flags,BOOL* eaten) noexcept override {
        return handle_up(context,key,flags,eaten,true);
    }
    HRESULT STDMETHODCALLTYPE OnKeyUp(ITfContext* context,WPARAM key,LPARAM flags,BOOL* eaten) noexcept override {
        return handle_up(context,key,flags,eaten,false);
    }
    HRESULT STDMETHODCALLTYPE OnPreservedKey(ITfContext*,REFGUID,BOOL* eaten) noexcept override {
        if(!eaten) return E_POINTER;*eaten=FALSE;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnInitDocumentMgr(ITfDocumentMgr*) noexcept override {return S_OK;}
    HRESULT STDMETHODCALLTYPE OnUninitDocumentMgr(ITfDocumentMgr* document) noexcept override {
        return boundary([&]{
            const auto snapshot=contexts_;
            for(const auto& state:snapshot) if(same_identity(state->document.get(),document)) remove_state(state);
            hide_reset();++focus_epoch_;rotate_mode_token();return S_OK;
        });
    }
    HRESULT STDMETHODCALLTYPE OnSetFocus(ITfDocumentMgr*,ITfDocumentMgr*) noexcept override {
        return boundary([&]{hide_reset();++focus_epoch_;rotate_mode_token();return S_OK;});
    }
    HRESULT STDMETHODCALLTYPE OnPushContext(ITfContext*) noexcept override {
        return boundary([&]{hide_reset();++focus_epoch_;rotate_mode_token();return S_OK;});
    }
    HRESULT STDMETHODCALLTYPE OnPopContext(ITfContext* context) noexcept override {
        return boundary([&]{
            auto state=find_context(context);if(state)remove_state(state);
            hide_reset();++focus_epoch_;rotate_mode_token();return S_OK;
        });
    }
    HRESULT STDMETHODCALLTYPE OnChange(REFGUID key) noexcept override {
        return boundary([&]{if(active_&&!initializing_mode_&&key==GUID_COMPARTMENT_KEYBOARD_OPENCLOSE)read_mode();return S_OK;});
    }
private:
    ~TextService() {deactivate();}
    void rotate_mode_token() noexcept {
        mode_token_=next_mode_token();query_snapshot_=0;query_window_=nullptr;query_time_=0;
    }
    std::uint32_t mode_snapshot() const noexcept {return (mode_token_<<1)|(chinese_mode_?1u:0u);}
    void accept_mode(bool chinese) {
        if(chinese_mode_==chinese)return;
        chinese_mode_=chinese;++mode_changes_;++focus_epoch_;rotate_mode_token();hide_reset();
    }
    void read_mode() {
        if(!keyboard_mode_)return;
        auto compartment=keyboard_mode_;const auto epoch=epoch_;
        VARIANT value;VariantInit(&value);last_mode_get_=compartment->GetValue(&value);
        const bool valid=SUCCEEDED(last_mode_get_)&&value.vt==VT_I4;
        const bool chinese=valid&&value.lVal!=0;VariantClear(&value);
        if(valid&&active_&&epoch==epoch_)accept_mode(chinese);
        // Missing/failed optional mode metadata retains the known in-memory
        // state. It never silently turns a newly activated service to English.
    }
    void initialize_mode(std::uint64_t activation) {
        ComPtr<ITfCompartmentMgr> compartments;ComPtr<ITfCompartment> value;ComPtr<ITfSource> source;
        auto manager=manager_;
        if(FAILED(manager->QueryInterface(IID_ITfCompartmentMgr,reinterpret_cast<void**>(compartments.put())))||!compartments)return;
        if(FAILED(compartments->GetCompartment(GUID_COMPARTMENT_KEYBOARD_OPENCLOSE,value.put()))||!value)return;
        if(!active_||activation!=epoch_)return;
        keyboard_mode_=value;initializing_mode_=true;
        if(SUCCEEDED(value->QueryInterface(IID_ITfSource,reinterpret_cast<void**>(source.put())))&&source) {
            DWORD cookie=TF_INVALID_COOKIE;
            const auto hr=source->AdviseSink(IID_ITfCompartmentEventSink,static_cast<ITfCompartmentEventSink*>(this),&cookie);
            if(!active_||activation!=epoch_) {
                if(SUCCEEDED(hr)&&cookie!=TF_INVALID_COOKIE)source->UnadviseSink(cookie);
                initializing_mode_=false;return;
            }
            if(SUCCEEDED(hr)){mode_source_=source;mode_cookie_=cookie;}
        }
        VARIANT initial;VariantInit(&initial);initial.vt=VT_I4;initial.lVal=1;
        last_mode_set_=value->SetValue(client_,&initial);initializing_mode_=false;
        if(active_&&activation==epoch_&&SUCCEEDED(last_mode_set_))read_mode();
    }
    bool mode_is_target(bool reject_busy=true) {
        if(!active_||(reject_busy&&busy_)||!manager_||GetCurrentThreadId()!=thread_||!mode_foreground())return false;
        auto manager=manager_;BOOL focus=FALSE;
        if(FAILED(manager->IsThreadFocus(&focus))||!focus)return false;
        ComPtr<ITfKeystrokeMgr> keys;CLSID tip{};
        if(FAILED(manager->QueryInterface(IID_ITfKeystrokeMgr,reinterpret_cast<void**>(keys.put())))||!keys||
            FAILED(keys->GetForeground(&tip))||tip!=kTextService)return false;
        ComPtr<ITfDocumentMgr> document;ComPtr<ITfContext> context;
        return SUCCEEDED(manager->GetFocus(document.put()))&&document&&
            SUCCEEDED(document->GetTop(context.put()))&&context;
    }
    std::uint32_t diagnostic(std::uint32_t field) const noexcept {
        switch(field) {
        case 0:return 1;
        case 1:return (active_?1u:0u)|(chinese_mode_?2u:0u)|(keyboard_mode_?4u:0u)|(busy_?16u:0u);
        case 2:return activation_count_;
        case 3:return static_cast<std::uint32_t>(contexts_.size());
        case 4:return reclaimed_contexts_;
        case 5:return context_pressure_;
        case 6:return metadata_failures_;
        case 7:return last_metadata_stage_;
        case 8:return static_cast<std::uint32_t>(last_metadata_request_);
        case 9:return static_cast<std::uint32_t>(last_metadata_session_);
        case 10:return static_cast<std::uint32_t>(last_metadata_property_);
        case 11:return last_metadata_fallback_;
        case 12:return static_cast<std::uint32_t>(last_mode_get_);
        case 13:return static_cast<std::uint32_t>(last_mode_set_);
        case 14:return 22;
        case 15:return static_cast<std::uint32_t>(focus_epoch_);
        case 16:return mode_changes_;
        case 17:return static_cast<std::uint32_t>(GetPersonalWordsDiagnostics().state);
        case 18:return GetPersonalWordsDiagnostics().pending_words;
        case 19:return GetPersonalWordsDiagnostics().last_error;
        case 20:return static_cast<std::uint32_t>(GetPersonalWordsDiagnostics().accepted_batches);
        case 21:return static_cast<std::uint32_t>(GetPersonalWordsDiagnostics().saved_batches);
        case 22:return static_cast<std::uint32_t>(GetPersonalWordsDiagnostics().rejected_batches);
        case 23:return static_cast<std::uint32_t>(GetPersonalWordsDiagnostics().skipped_new_words);
        case 24:return writeback_?(writeback_->valid?1u:0u)|(writeback_->claimed?2u:0u):0;
        case 25:return writeback_?writeback_->invalidation_reason:0;
        case 26:return last_writeback_guard_;
        case 27:return writeback_?writeback_->notification_stage:0;
        case 28:return writeback_?static_cast<std::uint32_t>(writeback_->notification_hr):0;
        case 29:return writeback_?writeback_->own_notifications:0;
        default:return 0;
        }
    }
    static std::uint32_t mode_callback(void* owner,ModeOperation operation,std::uint32_t snapshot,std::uint32_t desired) noexcept {
        auto self=static_cast<TextService*>(owner);self->AddRef();std::uint32_t result=0;
        boundary([&]{result=self->handle_mode(operation,snapshot,desired);return S_OK;});self->Release();return result;
    }
    std::uint32_t handle_mode(ModeOperation operation,std::uint32_t snapshot,std::uint32_t desired) {
        if(operation==ModeOperation::Diagnostic)return diagnostic(snapshot);
        const auto epoch=epoch_,focus=focus_epoch_;const auto window=mode_foreground();
        if(!window||!mode_is_target()||!active_||epoch!=epoch_||focus!=focus_epoch_)return 0;
        if(operation==ModeOperation::Query) {
            read_mode();
            if(!active_||epoch!=epoch_||mode_foreground()!=window||!mode_is_target())return 0;
            query_snapshot_=mode_snapshot();query_window_=window;query_time_=GetTickCount64();return query_snapshot_;
        }
        if(!snapshot||snapshot!=query_snapshot_||snapshot!=mode_snapshot()||window!=query_window_||
            GetTickCount64()-query_time_>2000||desired>1)return 0;
        query_snapshot_=0; // A request is single-use, even when the optional compartment fails.
        if(!set_mode(desired!=0))return 0;
        if(!active_||epoch!=epoch_||mode_foreground()!=window||!mode_is_target())return 0;
        return mode_snapshot();
    }
    void hide_reset() {
        invalidate_writeback();
        reset_shift();
        if(last_learning_context_) {CancelLearning(last_learning_context_);last_learning_context_=0;}
        preview_.hide();downs_.fill(false);
        for(const auto& state:contexts_) state->draft.reset_gesture();
    }
    void deactivate() noexcept {
        active_=false;++epoch_;rotate_mode_token();mode_endpoint_.close();writeback_endpoint_.close();invalidate_writeback();preview_.close();
        reset_shift();
        if(last_learning_context_) {CancelLearning(last_learning_context_);last_learning_context_=0;}
        // Move state out before host calls, since Unadvise can synchronously reenter.
        auto manager=std::move(manager_);const auto client=std::exchange(client_,TF_CLIENTID_NULL);
        const auto cookie=std::exchange(thread_cookie_,TF_INVALID_COOKIE);
        const bool keys=std::exchange(key_advised_,false);
        auto mode_source=std::move(mode_source_);auto mode_value=std::move(keyboard_mode_);
        const auto mode_cookie=std::exchange(mode_cookie_,TF_INVALID_COOKIE);initializing_mode_=false;
        auto states=std::move(contexts_);dictionary_.reset();downs_.fill(false);
        if(mode_source&&mode_cookie!=TF_INVALID_COOKIE)mode_source->UnadviseSink(mode_cookie);
        if(manager) {
            if(keys) {ComPtr<ITfKeystrokeMgr> sink;
                if(SUCCEEDED(manager->QueryInterface(IID_ITfKeystrokeMgr,reinterpret_cast<void**>(sink.put())))) sink->UnadviseKeyEventSink(client);}
            if(cookie!=TF_INVALID_COOKIE) {ComPtr<ITfSource> source;
                if(SUCCEEDED(manager->QueryInterface(IID_ITfSource,reinterpret_cast<void**>(source.put())))) source->UnadviseSink(cookie);}
        }
    }
    std::shared_ptr<ContextState> find_context(ITfContext* context) {
        if(!context) return {};
        ComPtr<IUnknown> identity;
        if(FAILED(context->QueryInterface(IID_IUnknown,reinterpret_cast<void**>(identity.put())))) return {};
        // No vector reference/iterator survives the host QueryInterface call above.
        for(const auto& state:contexts_) if(state->identity.get()==identity.get()) return state;
        return {};
    }
    std::shared_ptr<ContextState> get_context(ITfContext* context) {
        if(auto existing=find_context(context)) return existing;
        if(!active_||!dictionary_)return {};
        if(contexts_.size()>=64) {
            // Remove only empty idle states. Destruction (and host Release) is
            // delayed until the vector mutation is complete, so reentry never
            // observes an invalid iterator. Pending mouse sessions hold their
            // state through shared_ptr and are not eligible for reclamation.
            const auto before=epoch_;
            std::vector<std::shared_ptr<ContextState>> retired;
            for(auto it=contexts_.begin();it!=contexts_.end();) {
                const auto& state=*it;
                if(state.use_count()==1&&state->draft.empty()&&!state->draft.commit_pending()&&state->draft.recovery_draft().empty()&&state->id!=last_learning_context_) {
                    retired.push_back(std::move(*it));it=contexts_.erase(it);++reclaimed_contexts_;
                }else ++it;
            }
            retired.clear();
            if(!active_||before!=epoch_)return {};
            if(contexts_.size()>=64){++context_pressure_;return {};}
        }
        const auto before=epoch_;
        auto state=std::make_shared<ContextState>(context,dictionary_,++next_context_id);
        if(!active_||before!=epoch_||!state->identity) return {};
        contexts_.push_back(state);return state;
    }
    void remove_state(const std::shared_ptr<ContextState>& state) {
        if(writeback_&&writeback_->state==state)invalidate_writeback();
        const auto it=std::find(contexts_.begin(),contexts_.end(),state);
        if(it!=contexts_.end()) contexts_.erase(it);
    }
    void render(const std::shared_ptr<ContextState>& state,std::wstring notice={}) {
        if(state->draft.empty()&&notice.empty()) {preview_.hide();return;}
        PreviewContent content;content.title.clear();
        const auto& draft=state->draft.draft();const auto position=state->draft.caret();
        content.draft=draft.substr(0,position)+from_utf8(state->draft.pinyin())+L"│"+draft.substr(position);
        content.context=state->id;content.revision=state->draft.revision();
        if(chinese_mode_) {
            const auto& candidates=state->draft.candidates();
            content.page=state->draft.selected()/9;content.pages=(candidates.size()+8)/9;
            const auto start=content.page*9;
            for(std::size_t i=start;i<std::min(start+9,candidates.size());++i)
                content.candidates.push_back({candidates[i].text,i,i==state->draft.selected()});
        } else {
            content.english_suggestions=true;
            const auto candidates=state->draft.english_completions();
            for(std::size_t i=0;i<candidates.size();++i)content.candidates.push_back({candidates[i],i,i==0});
        }
        content.notice=std::move(notice);
        if(state->draft.uncertain()&&!state->draft.recovery_draft().empty())
            content.notice+=L"\n待核对原句："+state->draft.recovery_draft();
        preview_.show(std::move(content),state->caret);
    }
    bool focused_context(ITfContext* context) {
        if(!manager_)return false;
        auto manager=manager_;ComPtr<ITfDocumentMgr> document;ComPtr<ITfContext> top;
        return SUCCEEDED(manager->GetFocus(document.put()))&&document&&
            SUCCEEDED(document->GetTop(top.put()))&&top&&same_identity(top.get(),context);
    }
    bool focused(const std::shared_ptr<ContextState>& state) {return focused_context(state->context.get());}
    void invalidate_writeback(std::uint32_t reason=3) noexcept {if(writeback_)writeback_->detach(reason);}
    bool writeback_current(const std::shared_ptr<WritebackLease>& lease,bool permit_busy=false) {
        if(!lease||!lease->valid||writeback_!=lease||!active_||(!permit_busy&&busy_)||GetCurrentThreadId()!=thread_||
            lease->epoch!=epoch_||lease->focus!=focus_epoch_||lease->foreground!=mode_foreground()||
            !lease->state||!lease->state->draft.empty()||lease->revision!=lease->state->draft.revision()){last_writeback_guard_=1;return false;}
        // The captured foreground HWND, focused document/context, lifecycle,
        // explicit mode-change invalidation and final safe edit checks bind this
        // operation. Keyboard routing's transient foreground-TIP metadata is not
        // an additional prerequisite for a mouse-initiated edit.
        const bool current=focused(lease->state)&&lease->valid&&active_&&writeback_==lease&&
            lease->epoch==epoch_&&lease->focus==focus_epoch_&&lease->foreground==mode_foreground();
        last_writeback_guard_=current?0:2;return current;
    }
    void arm_writeback(const std::shared_ptr<ContextState>& state,ITfRange* range,const Commit& commit) {
        invalidate_writeback();
        if(!range||!writeback_endpoint_.window())return;
        auto lease=std::make_shared<WritebackLease>();
        lease->state=state;lease->range=ComPtr<ITfRange>(range);lease->original=commit.text;
        lease->token=NewWritebackToken();lease->epoch=epoch_;lease->focus=focus_epoch_;
        lease->revision=state->draft.revision();lease->foreground=mode_foreground();
        if(!ValidWritebackToken(lease->token)||!lease->foreground)return;
        writeback_=lease;
        if(!writeback_current(lease,true)) {invalidate_writeback();return;}
        ComPtr<ITfSource> source;
        if(FAILED(state->context->QueryInterface(IID_ITfSource,reinterpret_cast<void**>(source.put())))||!source||!writeback_current(lease,true)) {invalidate_writeback();return;}
        const std::weak_ptr<WritebackLease> weak=lease;
        const bool restricted=standard_edit_only_;
        lease->sink.attach(new WritebackEditSink([weak]{if(auto value=weak.lock())value->invalidate(1);},
            [weak,restricted](ITfContext* context,TfEditCookie cookie) {
                auto value=weak.lock();
                if(!value||!value->valid)return false;
                if(!same_identity(context,value->state->context.get())){value->notification_stage=12;return false;}
                WritebackProbe probe;
                const bool same=OwnWritebackRangeUnchanged(context,cookie,value->range.get(),value->original,restricted,
                    value->state->edit_owner,value->state->edit_window,&probe);
                if(value->valid){value->notification_stage=probe.stage;value->notification_hr=probe.result;}
                return same&&value->valid;
            },client_,[weak](std::uint32_t stage,HRESULT hr) {
                if(auto value=weak.lock();value&&value->valid) {
                    if(stage==20)++value->own_notifications;
                    else {value->notification_stage=stage;value->notification_hr=hr;}
                }
            }));
        DWORD cookie=TF_INVALID_COOKIE;
        const auto hr=source->AdviseSink(IID_ITfTextEditSink,lease->sink.get(),&cookie);
        if(SUCCEEDED(hr)&&cookie!=TF_INVALID_COOKIE) {lease->source=std::move(source);lease->cookie=cookie;}
        if(FAILED(hr)||cookie==TF_INVALID_COOKIE||!writeback_current(lease,true))invalidate_writeback();
    }
    static WritebackResult writeback_callback(void* owner,const WritebackCommand& command) noexcept {
        auto self=static_cast<TextService*>(owner);self->AddRef();auto result=WritebackResult::Unavailable;
        boundary([&]{result=self->handle_writeback(command);return S_OK;});self->Release();return result;
    }
    WritebackResult handle_writeback(const WritebackCommand& command) {
        auto lease=writeback_;
        if(!lease||lease->token!=command.token)return WritebackResult::Unavailable;
        if(command.operation==WritebackOperation::Status)return lease->claimed?lease->result:
            (writeback_current(lease)?WritebackResult::Ready:WritebackResult::Unavailable);
        if(command.operation==WritebackOperation::Query)return !lease->claimed&&writeback_current(lease)?WritebackResult::Ready:WritebackResult::Unavailable;
        if(lease->claimed)return lease->result; // A repeated click/timeout never writes twice.
        if(!writeback_current(lease))return WritebackResult::Rejected;
        lease->claimed=true;lease->deadline=key_time()+1000;
        const auto english=command.text;const auto mode=command.mode;
        ComPtr<ITfTextInputProcessorEx> lifetime(static_cast<ITfTextInputProcessorEx*>(this));
        ComPtr<ActionSession> session;
        session.attach(new ActionSession([this,lifetime,lease,english,mode](TfEditCookie cookie)->HRESULT {
            if(key_time()>lease->deadline||!writeback_current(lease)){lease->invalidate();return S_OK;}
            struct Guard {bool& flag;Guard(bool& v):flag(v){flag=true;}~Guard(){flag=false;}} busy(busy_);
            auto valid=[&]{return key_time()<=lease->deadline&&writeback_current(lease,true);};
            CommitResult result=CommitResult::NotWritten;
            try {result=ApplyWriteback(lease->state->context.get(),manager_.get(),cookie,lease->range.get(),
                lease->original,english,mode,standard_edit_only_,lease->state->edit_owner,lease->state->edit_window,valid);}
            catch(...) {lease->result=WritebackResult::Unknown;lease->valid=false;return E_FAIL;}
            lease->result=result==CommitResult::Written?WritebackResult::Written:
                result==CommitResult::Unknown?WritebackResult::Unknown:WritebackResult::Rejected;
            lease->valid=false;return S_OK;
        }));
        HRESULT result=E_PENDING;
        // A click is not a keystroke callback. Force an asynchronous TSF write
        // session; revalidate the exact context, range and deadline when granted.
        const auto requested=lease->state->context->RequestEditSession(client_,session.get(),TF_ES_ASYNC|TF_ES_READWRITE,&result);
        if((FAILED(requested)||FAILED(result))&&lease->result==WritebackResult::Ready)lease->invalidate();
        return lease->result;
    }
    bool set_mode(bool chinese) {
        const auto epoch=epoch_,focus=focus_epoch_;const auto window=mode_foreground();
        const bool changed=chinese_mode_!=chinese;
        std::shared_ptr<ContextState> staged_state;
        std::optional<DraftSession> staged;
        std::uint64_t revision=0;
        if(!chinese&&manager_) {
            auto manager=manager_;ComPtr<ITfDocumentMgr> document;ComPtr<ITfContext> context;
            if(SUCCEEDED(manager->GetFocus(document.put()))&&document&&SUCCEEDED(document->GetTop(context.put()))&&context) {
                staged_state=find_context(context.get());
                if(staged_state&&!staged_state->draft.pinyin().empty()) {
                    revision=staged_state->draft.revision();staged=staged_state->draft;
                    if(!staged->accept_raw())return false;
                }
            }
            if(!active_||epoch!=epoch_||focus!=focus_epoch_||window!=mode_foreground())return false;
        }
        if(keyboard_mode_) {
            auto compartment=keyboard_mode_;VARIANT value;VariantInit(&value);value.vt=VT_I4;value.lVal=chinese?1:0;
            last_mode_set_=compartment->SetValue(client_,&value);
            if(FAILED(last_mode_set_)||!active_||epoch!=epoch_)return false;
            read_mode();
        }else accept_mode(chinese);
        if(!active_||epoch!=epoch_||chinese_mode_!=chinese)return false;
        if(staged) {
            const auto expected_focus=focus+(changed?1u:0u);
            if(focus_epoch_!=expected_focus||window!=mode_foreground()||
               find_context(staged_state->context.get())!=staged_state||!focused(staged_state)||
               !active_||epoch!=epoch_||focus_epoch_!=expected_focus||window!=mode_foreground()||staged_state->draft.revision()!=revision)return false;
            staged_state->draft=std::move(*staged);
        }
        return true;
    }
    void reset_shift() noexcept {
        shift_key_=0;shift_started_=0;shift_window_=nullptr;
        shift_context_={};
    }
    void observe_shift_down(ITfContext* context,WPARAM key,LPARAM flags) {
        if(!shift_key(key)){reset_shift();return;}
        for(const auto& state:contexts_)state->draft.reset_gesture();
        if((static_cast<ULONG_PTR>(flags)&((1ull<<30)|(1ull<<29)))||other_key_held()) {reset_shift();return;}
        const unsigned identity=shift_identity(key,flags);
        const auto epoch=epoch_,focus=focus_epoch_;
        if(shift_key_) {
            // Test may be called repeatedly, followed by a real callback. Do
            // not rearm or extend the time limit for the same physical press.
            if(shift_key_!=identity||!same_identity(shift_context_.get(),context))reset_shift();
            return;
        }
        if(!mode_is_target(false)||!focused_context(context)||!edit::can_route_key(context))return;
        ComPtr<ITfContext> held(context);
        if(!active_||epoch!=epoch_||focus!=focus_epoch_)return;
        shift_context_=std::move(held);shift_key_=identity;
        shift_started_=key_time();shift_focus_=focus;shift_window_=mode_foreground();
    }
    bool shift_release_matches(ITfContext* context,WPARAM key,LPARAM flags) {
        if(!shift_key_||shift_key_!=shift_identity(key,flags)||!shift_context_||
           shift_focus_!=focus_epoch_||key_time()<shift_started_||key_time()-shift_started_>800||
           (static_cast<ULONG_PTR>(flags)&(1ull<<29))||other_key_held())return false;
        const auto epoch=epoch_,focus=focus_epoch_;const auto window=shift_window_;
        return window&&window==mode_foreground()&&same_identity(shift_context_.get(),context)&&
            mode_is_target(false)&&focused_context(context)&&edit::can_route_key(context)&&
            active_&&epoch==epoch_&&focus==focus_epoch_&&shift_key_!=0&&window==mode_foreground();
    }
    HRESULT handle_up(ITfContext* context,WPARAM key,LPARAM flags,BOOL* eaten,bool test) noexcept {
        if(!eaten)return E_POINTER;*eaten=FALSE;
        return boundary([&]() -> HRESULT {
            if(!active_||busy_||GetCurrentThreadId()!=thread_)return S_OK;
            struct Guard {bool& flag;explicit Guard(bool& x):flag(x){flag=true;}~Guard(){flag=false;}} guard(busy_);
            if(!shift_key(key)) {
                reset_shift();
                if(key<downs_.size()){*eaten=downs_[key]?TRUE:FALSE;if(!test)downs_[key]=false;}
                return S_OK;
            }
            if(!context||!shift_release_matches(context,key,flags)){reset_shift();return S_OK;}
            if(test){*eaten=TRUE;return S_OK;}
            // Consume the gesture before any host callback (SetValue can call
            // OnChange synchronously). Both physical Shift edges still reach
            // the app: TestUp routes here, real Up finally returns FALSE.
            const auto epoch=epoch_,focus=focus_epoch_;const auto window=mode_foreground();
            const bool chinese=!chinese_mode_;
            reset_shift();
            if(!active_||epoch!=epoch_||focus!=focus_epoch_||window!=mode_foreground())return S_OK;
            ComPtr<ReadMetadata> read;read.attach(new ReadMetadata(context,standard_edit_only_));
            HRESULT session=E_FAIL;
            const HRESULT request=context->RequestEditSession(client_,read.get(),TF_ES_SYNC|TF_ES_READ,&session);
            if(FAILED(request)||FAILED(session)||!read->metadata.inspected||!read->metadata.allowed||
               !active_||epoch!=epoch_||focus!=focus_epoch_||window!=mode_foreground()||!focused_context(context)||
               !active_||epoch!=epoch_||focus!=focus_epoch_)return S_OK;
            if(!set_mode(chinese)||!active_||epoch!=epoch_||window!=mode_foreground()||
               focus_epoch_!=focus+1||!mode_is_target(false)||!active_||epoch!=epoch_||focus_epoch_!=focus+1)return S_OK;
            {
                const auto mode_focus=focus_epoch_;
                auto state=find_context(context);
                if(state&&!state->draft.empty()&&focused(state)&&active_&&epoch==epoch_&&mode_focus==focus_epoch_) {
                    const auto& control=read->metadata.trace.control;
                    if(standard_edit_only_&&(state->edit_owner!=control.owner||state->edit_window!=control.focused))return S_OK;
                    state->caret=read->metadata.caret;render(state);
                }
            }
            return S_OK;
        });
    }
    static void preview_action(void* owner,const PreviewAction& action) noexcept {
        auto self=static_cast<TextService*>(owner);self->AddRef();
        boundary([&]{self->request_action(action);return S_OK;});self->Release();
    }
    void request_action(const PreviewAction& action) {
        if(!active_||busy_||GetCurrentThreadId()!=thread_)return;
        std::shared_ptr<ContextState> state;
        for(const auto& item:contexts_)if(item->id==action.context){state=item;break;}
        if(!state||state->draft.revision()!=action.revision)return;
        const auto epoch=epoch_,focus=focus_epoch_;
        if(!focused(state)||!active_||epoch!=epoch_||focus!=focus_epoch_)return;
        // Mouse input is not a TSF keystroke callback. Allow the manager to defer
        // a read lock; the request keeps its service, context and revision alive.
        // This callback only selects or pages a draft and can never submit text.
        ComPtr<ITfTextInputProcessorEx> lifetime(static_cast<ITfTextInputProcessorEx*>(this));
        ComPtr<ActionSession> session;
        session.attach(new ActionSession([this,lifetime,state,action,epoch,focus](TfEditCookie cookie) -> HRESULT {
            if(!active_||busy_||epoch!=epoch_||focus!=focus_epoch_||GetCurrentThreadId()!=thread_)return S_OK;
            struct Guard {bool& flag;Guard(bool& v):flag(v){flag=true;}~Guard(){flag=false;}} guard(busy_);
            const auto settings=input_settings();
            if(!focused(state))return S_OK;
            state->draft.set_options({settings.abbreviation,settings.fuzzy_mask});
            state->draft.set_personalization_enabled(settings.auto_remember);
            state->draft.set_english_suggestions(settings.english_suggestions&&!chinese_mode_);
            if(state->draft.revision()!=action.revision)return S_OK;
            if(chinese_mode_&&state->draft.candidates().empty())return S_OK;
            if(!chinese_mode_&&(action.kind!=PreviewActionKind::EnglishCompletion||action.index>=state->draft.english_completions().size()))return S_OK;
            if(chinese_mode_&&action.kind==PreviewActionKind::EnglishCompletion)return S_OK;
            edit::Metadata metadata;
            HRESULT hr=edit::inspect_context(state->context.get(),cookie,metadata,standard_edit_only_);
            if(FAILED(hr)||!metadata.inspected||!metadata.allowed)return S_OK;
            if(standard_edit_only_&&(state->edit_owner!=metadata.trace.control.owner||state->edit_window!=metadata.trace.control.focused))return S_OK;
            if(!focused(state)||!active_||epoch!=epoch_||focus!=focus_epoch_||state->draft.revision()!=action.revision)return S_OK;
            Key key{KeyKind::PageUp,0,GetTickCount64(),false};
            if(action.kind==PreviewActionKind::EnglishCompletion){key.kind=KeyKind::CompleteEnglish;key.character=static_cast<wchar_t>(action.index);}
            else if(action.kind==PreviewActionKind::Choose) {
                const auto start=(state->draft.selected()/9)*9;
                if(action.index<start||action.index>=std::min(start+9,state->draft.candidates().size()))return S_OK;
                key.kind=KeyKind::Digit;key.character=static_cast<wchar_t>(L'1'+action.index-start);
            }else if(action.kind==PreviewActionKind::NextPage)key.kind=KeyKind::PageDown;
            auto result=state->draft.key(key);
            state->caret=metadata.caret;if(result.consumed)render(state);return S_OK;
        }));
        HRESULT result=E_PENDING;
        state->context->RequestEditSession(client_,session.get(),TF_ES_ASYNCDONTCARE|TF_ES_READ,&result);
    }
    HRESULT handle(ITfContext* context,WPARAM virtual_key,LPARAM flags,BOOL* eaten,bool test) noexcept {
        if(!eaten) return E_POINTER;*eaten=FALSE;
        return boundary([&]() -> HRESULT {
            if(!active_||busy_||!context||GetCurrentThreadId()!=thread_) return S_OK;
            // A new physical key may be handled by the application instead of
            // this IME. Invalidate even shortcut/navigation Test callbacks.
            if(writeback_&&writeback_->valid)invalidate_writeback(2);
            struct Guard {bool& flag;explicit Guard(bool& x):flag(x){flag=true;}~Guard(){flag=false;}} guard(busy_);
            const auto epoch=epoch_,focus=focus_epoch_;
            observe_shift_down(context,virtual_key,flags);
            if(!active_||epoch!=epoch_||focus!=focus_epoch_||shift_key(virtual_key))return S_OK;
            const auto settings=input_settings();
            const auto decoded=decode_key(virtual_key,flags,chinese_mode_);
            if(!decoded) {
                // Shortcut routing may have no subsequent real callback. End
                // only the confirmation gesture; never submit or discard text.
                auto state=find_context(context);if(state)state->draft.reset_gesture();return S_OK;
            }
            auto state=get_context(context);
            if(!state){if(!test&&context_pressure_)preview_.show(L"仍有较多未提交草稿，本次按键已交还软件。请先完成或取消原输入框中的草稿。",{});return S_OK;}
            if(!test)state->draft.set_options({settings.abbreviation,settings.fuzzy_mask});
            if(decoded->kind==KeyKind::CompleteEnglish&&!settings.english_suggestions){state->draft.reset_gesture();return S_OK;}
            if(!state->draft.wants(*decoded)){state->draft.reset_gesture();return S_OK;}
            if(test) {
                // Routing only. Never acquire an edit lock, change a draft, or
                // show UI here. OnKeyDown checks the complete safety metadata
                // under its own lock before it accepts any character.
                const bool route=edit::can_route_key(context);
                if(active_&&epoch==epoch_&&focus==focus_epoch_&&route) *eaten=TRUE;
                return S_OK;
            }
            ComPtr<ReadMetadata> read;read.attach(new ReadMetadata(context,standard_edit_only_));
            HRESULT session=E_FAIL;
            const HRESULT request=context->RequestEditSession(client_,read.get(),TF_ES_SYNC|TF_ES_READ,&session);
            last_metadata_request_=request;last_metadata_session_=session;
            last_metadata_stage_=static_cast<std::uint32_t>(read->metadata.trace.stage);
            last_metadata_property_=read->metadata.trace.property_value;
            last_metadata_fallback_=static_cast<std::uint32_t>(read->metadata.trace.control.result);
            if(!active_||epoch!=epoch_||focus!=focus_epoch_) return S_OK;
            if(FAILED(request)||FAILED(session)||!read->metadata.inspected) {
                ++metadata_failures_;
                state->draft.reset_gesture();
                preview_.show(edit::metadata_failure_notice(test,request,session,read->called,read->metadata),{});
                return S_OK;
            }
            if(!read->metadata.allowed) {
                preview_.hide();state->draft.reset_gesture();return S_OK;
            }
            if(standard_edit_only_) {
                const auto& control=read->metadata.trace.control;
                if(state->edit_window&&(state->edit_owner!=control.owner||state->edit_window!=control.focused)) {
                    // A shell can reuse a TSF context for a different transient
                    // label editor. Its previous draft must not cross that HWND
                    // boundary, even if no context-pop notification was sent.
                    auto replacement=std::make_shared<ContextState>(context,dictionary_,++next_context_id);
                    if(!active_||epoch!=epoch_||focus!=focus_epoch_||!replacement->identity)return S_OK;
                    contexts_.push_back(replacement);remove_state(state);
                    if(last_learning_context_==state->id){CancelLearning(state->id);last_learning_context_=0;}
                    state=std::move(replacement);
                    if(!active_||epoch!=epoch_||focus!=focus_epoch_)return S_OK;
                    state->draft.set_options({settings.abbreviation,settings.fuzzy_mask});
                }
                state->edit_owner=control.owner;state->edit_window=control.focused;
            }
            state->caret=read->metadata.caret;
            state->draft.set_personalization_enabled(settings.auto_remember);
            state->draft.set_english_suggestions(settings.english_suggestions&&!chinese_mode_);
            // Data-only snapshot; file reads and saves happen on a worker. A
            // busy cache returns null, so retain this context's last snapshot.
            if(settings.auto_remember) {
                if(auto personal=GetPersonalLexicon())state->draft.set_personal_lexicon(std::move(personal));
            }
            if(!preview_.ensure_ready()) return S_OK;
            // Literal/EnglishSpace already reserve raw+new character together
            // in the core transaction. Do not pre-seal and split that operation.
            // External OPENCLOSE changes can leave raw spelling for navigation;
            // seal it in a staged copy so arrows cannot choose a Chinese word.
            KeyResult result;
            if(!chinese_mode_&&!state->draft.pinyin().empty()&&
               decoded->kind!=KeyKind::Literal&&decoded->kind!=KeyKind::EnglishSpace&&
               decoded->kind!=KeyKind::Enter&&decoded->kind!=KeyKind::Backspace&&decoded->kind!=KeyKind::Escape) {
                auto staged=state->draft;
                if(!staged.accept_raw()) {
                    *eaten=TRUE;if(virtual_key<downs_.size())downs_[virtual_key]=true;
                    render(state,L"草稿已达到长度上限，请先提交或删减。");return S_OK;
                }
                result=staged.key(*decoded);state->draft=std::move(staged);
            }else result=state->draft.key(*decoded);
            *eaten=result.consumed?TRUE:FALSE;
            if(result.consumed&&virtual_key<downs_.size()) downs_[virtual_key]=true;
            if(!result.commit) {if(result.consumed)render(state);return S_OK;}
            // The copy belongs to this exact context and request. No global pending text.
            const Commit& commit=*result.commit;
            ComPtr<WriteCommit> write;
            try {
                write.attach(new WriteCommit(context,manager_.get(),commit,standard_edit_only_,state->edit_owner,state->edit_window));
                session=E_FAIL;
                const HRESULT write_request=context->RequestEditSession(client_,write.get(),TF_ES_SYNC|TF_ES_READWRITE,&session);
                (void)write_request; (void)session;
            } catch(...) {
                // Allocation/callback failure cannot strand the core pending bit.
                state->draft.acknowledge(commit.id,write?write->outcome:CommitResult::NotWritten);
                if(write&&write->outcome==CommitResult::Written&&!commit.personal_words.empty())
                    RememberPersonalWords(commit.personal_words);
                throw;
            }
            // outcome is authoritative: after mutation succeeds, later caret errors
            // must not cause the already-written text to be resubmitted.
            state->draft.acknowledge(commit.id,write->outcome);
            // Once the host confirms insertion, remember the selected words
            // even when a later focus notification hides the learning window.
            // Enter and triple-space both count; translation is independent.
            if(write->outcome==CommitResult::Written&&!commit.personal_words.empty())
                RememberPersonalWords(commit.personal_words);
            if(!active_||epoch!=epoch_||focus!=focus_epoch_) return S_OK;
            if(standard_edit_only_&&!write->focus_retained){preview_.hide();return S_OK;}
            state->caret=write->caret.valid?write->caret:state->caret;
            if(write->outcome==CommitResult::Written) {
                preview_.hide();
                if(commit.learn) {
                    arm_writeback(state,write->committed_range.get(),commit);
                    // Optional sink registration calls the host and can reenter
                    // focus/deactivation just like the preceding text mutation.
                    if(!active_||epoch!=epoch_||focus!=focus_epoch_||!focused(state)||
                        !active_||epoch!=epoch_||focus!=focus_epoch_)return S_OK;
                    const bool can_writeback=writeback_&&writeback_->valid;
                    const auto dispatched=LearningDispatch({commit,state->id,state->draft.revision(),state->caret,
                        can_writeback?writeback_endpoint_.window():nullptr,can_writeback?writeback_->token:std::string{}});
                    if(dispatched==DispatchResult::Queued) {
                        last_learning_context_=state->id;
                        preview_.watch_delivery(state->id,state->caret);
                    } else render(state,L"文字已提交；伴读请求未能排队。可继续输入。");
                }
            } else if(write->outcome==CommitResult::Unknown) {
                render(state,L"提交结果不明确：请核对输入框。原句已保留恢复副本，不会自动重试；可继续输入。");
            } else {
                render(state,L"本次未提交，草稿已保留。可再次按 Enter 或三空格；Esc 取消。");
            }
            return S_OK;
        });
    }
    std::atomic<ULONG> references_{1};
    ComPtr<ITfThreadMgr> manager_;
    std::shared_ptr<const Dictionary> dictionary_;
    std::vector<std::shared_ptr<ContextState>> contexts_;
    PreviewWindow preview_;
    ModeEndpoint mode_endpoint_;
    WritebackEndpoint writeback_endpoint_;
    std::shared_ptr<WritebackLease> writeback_;
    std::uint32_t last_writeback_guard_=0;
    ComPtr<ITfCompartment> keyboard_mode_;
    ComPtr<ITfSource> mode_source_;
    std::array<bool,256> downs_{};
    ComPtr<ITfContext> shift_context_;
    unsigned shift_key_=0;
    ULONGLONG shift_started_=0;
    std::uint64_t shift_focus_=0;
    HWND shift_window_=nullptr;
    TfClientId client_=TF_CLIENTID_NULL;DWORD thread_cookie_=TF_INVALID_COOKIE;DWORD thread_=0;
    std::uint64_t epoch_=0,focus_epoch_=0,last_learning_context_=0;
    bool key_advised_=false,active_=false,busy_=false,standard_edit_only_=false;
    bool chinese_mode_=true,initializing_mode_=false;
    DWORD mode_cookie_=TF_INVALID_COOKIE;
    std::uint32_t mode_token_=0,query_snapshot_=0;
    HWND query_window_=nullptr;ULONGLONG query_time_=0;
    std::uint32_t activation_count_=0,reclaimed_contexts_=0,context_pressure_=0,metadata_failures_=0,mode_changes_=0;
    std::uint32_t last_metadata_stage_=0,last_metadata_fallback_=0;
    HRESULT last_metadata_request_=E_PENDING,last_metadata_session_=E_PENDING,last_metadata_property_=E_PENDING;
    HRESULT last_mode_get_=E_PENDING,last_mode_set_=E_PENDING;
};
}
HRESULT create_text_service(REFIID iid,void** out) noexcept {
    if(!out) return E_POINTER;*out=nullptr;
    return boundary([&]{
        if(!host_can_create(current_host_policy())) return CLASS_E_CLASSNOTAVAILABLE;
        auto service=new TextService();const HRESULT hr=service->QueryInterface(iid,out);service->Release();return hr;
    });
}
}
