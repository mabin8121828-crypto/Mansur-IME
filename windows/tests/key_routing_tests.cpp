// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
// Exercise the production TextService callbacks with an isolated COM host.
// All preview/learning dependencies below are inert; no window or model starts.
#include "edit_session_tests.cpp"
#include "tsf_service.hpp"
#include "host_policy.hpp"
#include "preview_window.hpp"
#include "mode_endpoint.hpp"
#include "writeback_endpoint.hpp"
#include "personal_words.hpp"
#include <fstream>
#include <filesystem>
#include <array>
#include <cstring>

namespace {
std::shared_ptr<const mansur::Dictionary> fixture_dictionary;
std::wstring shown;
unsigned shows=0;
unsigned learning_dispatches=0;
unsigned word_remembers=0,word_snapshots=0;
bool word_queue_accepts=true;
std::vector<mansur::PersonalWord> last_remembered;
std::shared_ptr<const mansur::PersonalLexicon> fixture_personal;
std::wstring last_learning_text;
mansur::win::NativeSettings fixture_settings;
mansur::win::PreviewContent last_content;
mansur::win::PreviewWindow::ActionHandler click_handler=nullptr;
void* click_owner=nullptr;
mansur::win::ModeEndpoint::Handler native_mode_handler=nullptr;
void* native_mode_owner=nullptr;
HWND test_foreground=reinterpret_cast<HWND>(1);
unsigned mode_windows=0;
mansur::win::WritebackEndpoint::Handler native_writeback_handler=nullptr;
void* native_writeback_owner=nullptr;
std::string last_writeback_token;
HWND last_writeback_endpoint=nullptr;
mansur::win::HostDecision fixture_host=mansur::win::HostDecision::Allowed;
mansur::win::DispatchResult fixture_dispatch=mansur::win::DispatchResult::Queued;
std::array<SHORT,256> fixture_key_states{};
ULONGLONG fixture_time=1000;
}
namespace mansur::win {
std::shared_ptr<const Dictionary> key_routing_test_dictionary(){return fixture_dictionary;}
NativeSettings key_routing_test_settings(){return fixture_settings;}
HWND key_routing_test_foreground(){return test_foreground;}
SHORT key_routing_test_key_state(int key){return key>=0&&key<256?fixture_key_states[static_cast<std::size_t>(key)]:0;}
ULONGLONG key_routing_test_time(){return fixture_time;}
wchar_t key_routing_test_character(WPARAM key,LPARAM){
    const bool shift=(fixture_key_states[VK_SHIFT]&0x8000)!=0;
    const bool caps=(fixture_key_states[VK_CAPITAL]&1)!=0;
    if(key>='A'&&key<='Z')return static_cast<wchar_t>((shift!=caps?L'A':L'a')+key-'A');
    if(key>='0'&&key<='9')return shift?L")!@#$%^&*("[key-'0']:static_cast<wchar_t>(key);
    if(key>=VK_NUMPAD0&&key<=VK_NUMPAD9)return static_cast<wchar_t>(L'0'+key-VK_NUMPAD0);
    switch(key){
    case VK_SPACE:return L' ';case VK_OEM_MINUS:return shift?L'_':L'-';
    case VK_OEM_PLUS:return shift?L'+':L'=';case VK_ADD:return L'+';
    case VK_OEM_COMMA:return shift?L'<':L',';case VK_OEM_PERIOD:return shift?L'>':L'.';
    case VK_OEM_1:return shift?L':':L';';case VK_OEM_2:return shift?L'?':L'/';
    case VK_OEM_7:return shift?L'"':L'\'';case VK_OEM_4:return shift?L'{':L'[';
    case VK_OEM_6:return shift?L'}':L']';case VK_OEM_5:return shift?L'|':L'\\';
    case VK_OEM_3:return shift?L'~':L'`';default:return 0;
    }
}
ModeEndpoint::~ModeEndpoint(){close();}
bool ModeEndpoint::open(void* owner,Handler callback) noexcept{if(!window_){window_=reinterpret_cast<HWND>(1);++mode_windows;}native_mode_handler=callback;native_mode_owner=owner;return true;}
void ModeEndpoint::close() noexcept{if(window_){window_=nullptr;--mode_windows;}}
WritebackEndpoint::~WritebackEndpoint(){close();}
bool WritebackEndpoint::open(void* owner,Handler callback) noexcept{window_=reinterpret_cast<HWND>(2);native_writeback_handler=callback;native_writeback_owner=owner;return true;}
void WritebackEndpoint::close() noexcept{window_=nullptr;owner_=nullptr;handler_=nullptr;}
HostDecision current_host_policy() noexcept{return fixture_host;}
PreviewWindow::~PreviewWindow()=default;
void PreviewWindow::set_action_handler(void* owner,ActionHandler handler) noexcept{click_owner=owner;click_handler=handler;}
bool PreviewWindow::ensure_ready() noexcept{return true;}
bool PreviewWindow::show(std::wstring text,const CaretLocation&) noexcept{shown=std::move(text);++shows;return true;}
bool PreviewWindow::show(PreviewContent content,const CaretLocation&) noexcept{last_content=std::move(content);shown=last_content.draft+last_content.notice;++shows;return true;}
void PreviewWindow::hide() noexcept{shown.clear();}
void PreviewWindow::close() noexcept{shown.clear();}
void PreviewWindow::watch_delivery(std::uint64_t,const CaretLocation&) noexcept{}
DispatchResult LearningDispatch(const LearningRequest& request) noexcept{++learning_dispatches;last_learning_text=request.commit.text;last_writeback_token=request.writeback_token;last_writeback_endpoint=request.writeback_endpoint;return fixture_dispatch;}
DispatchResult CancelLearning(std::uint64_t) noexcept{return DispatchResult::NotConnected;}
DeliverySnapshot GetLastDeliveryStatus() noexcept{return {};}
std::shared_ptr<const PersonalLexicon> GetPersonalLexicon() noexcept{++word_snapshots;return fixture_personal;}
bool RememberPersonalWords(const std::vector<PersonalWord>& words) noexcept{++word_remembers;last_remembered=words;return word_queue_accepts;}
PersonalWordsDiagnostics GetPersonalWordsDiagnostics() noexcept{return {};}
}
struct Fixture {
    Context context;Manager manager;ComPtr<ITfTextInputProcessorEx> service;ComPtr<ITfKeyEventSink> keys;
    ModeEndpoint::Handler mode_handler=nullptr;void* mode_owner=nullptr;
    WritebackEndpoint::Handler writeback_handler=nullptr;void* writeback_owner=nullptr;
    explicit Fixture(HostDecision host=HostDecision::Allowed) {
        fixture_host=host;fixture_dispatch=DispatchResult::Queued;fake_control_api=FakeControlApi{};
        manager.document.context=&context;shown.clear();shows=0;learning_dispatches=0;last_learning_text.clear();fixture_settings={};last_content={};test_foreground=reinterpret_cast<HWND>(1);
        fixture_key_states.fill(0);fixture_time=1000;
        word_remembers=word_snapshots=0;last_remembered.clear();fixture_personal.reset();word_queue_accepts=true;
        check(SUCCEEDED(create_text_service(IID_ITfTextInputProcessorEx,reinterpret_cast<void**>(service.put()))),"create isolated service");
        check(SUCCEEDED(service->QueryInterface(IID_ITfKeyEventSink,reinterpret_cast<void**>(keys.put()))),"query key sink");
        check(SUCCEEDED(service->ActivateEx(&manager,1,0)),"activate simulated host");
        mode_handler=native_mode_handler;mode_owner=native_mode_owner;
        writeback_handler=native_writeback_handler;writeback_owner=native_writeback_owner;last_writeback_token.clear();last_writeback_endpoint=nullptr;
    }
    ~Fixture(){service->Deactivate();fixture_host=HostDecision::Allowed;}
    std::uint32_t mode(){return mode_handler(mode_owner,ModeOperation::Query,0,0);}
    std::uint32_t diagnostic(std::uint32_t field){return mode_handler(mode_owner,ModeOperation::Diagnostic,field,0);}
    bool set_mode(bool chinese){auto snapshot=mode();return snapshot&&mode_handler(mode_owner,ModeOperation::Set,snapshot,chinese?1:0)!=0;}
    bool test(WPARAM key,LPARAM flags=0,Context* target=nullptr) {
        auto& current=target?*target:context;current.test_phase=true;BOOL eaten=FALSE;
        const HRESULT result=keys->OnTestKeyDown(&current,key,flags,&eaten);
        current.test_phase=false;check(result==S_OK,"test callback success");return eaten!=FALSE;
    }
    bool down(WPARAM key,LPARAM flags=0,Context* target=nullptr) {
        BOOL eaten=FALSE;check(keys->OnKeyDown(target?target:&context,key,flags,&eaten)==S_OK,"key callback success");return eaten!=FALSE;
    }
    bool key(WPARAM key,LPARAM flags=0,Context* target=nullptr) {
        return test(key,flags,target)&&down(key,flags,target);
    }
    bool test_up(WPARAM key,LPARAM flags=0,Context* target=nullptr) {
        auto& current=target?*target:context;current.test_phase=true;BOOL eaten=FALSE;
        const HRESULT result=keys->OnTestKeyUp(&current,key,flags,&eaten);
        current.test_phase=false;check(result==S_OK,"test up callback success");return eaten!=FALSE;
    }
    bool release(WPARAM key,LPARAM flags=0,Context* target=nullptr) {
        BOOL eaten=FALSE;check(keys->OnKeyUp(target?target:&context,key,flags,&eaten)==S_OK,"key up callback success");return eaten!=FALSE;
    }
    bool up(WPARAM key,LPARAM flags=0,Context* target=nullptr) {
        return test_up(key,flags,target)&&release(key,flags,target);
    }
};
LPARAM shift_flags(bool up=false,bool repeat=false,unsigned scan=0x2a) {
    return static_cast<LPARAM>(1u|(scan<<16)|(repeat?0x40000000u:0u)|(up?0xc0000000u:0u));
}
void hold_key(int key,bool down=true) {fixture_key_states[static_cast<std::size_t>(key)]=down?static_cast<SHORT>(0x8000):0;}
void hold_shift(bool down,unsigned scan=0x2a) {
    hold_key(scan==0x36?VK_RSHIFT:VK_LSHIFT,down);
    hold_key(VK_SHIFT,(fixture_key_states[VK_LSHIFT]&0x8000)||(fixture_key_states[VK_RSHIFT]&0x8000));
}
void begin_shift(Fixture& f,unsigned scan=0x2a) {
    hold_shift(true,scan);check(!f.test(VK_SHIFT,shift_flags(false,false,scan)),"Shift down stays available to application");
}
void end_shift(Fixture& f,ULONGLONG elapsed=50,unsigned scan=0x2a) {
    fixture_time+=elapsed;hold_shift(false,scan);
    check(!f.up(VK_SHIFT,shift_flags(true,false,scan)),"Shift release stays available to application");
}
void tap_shift(Fixture& f,ULONGLONG elapsed=50,unsigned scan=0x2a) {begin_shift(f,scan);end_shift(f,elapsed,scan);}
void type_ascii(Fixture& f,std::wstring_view text) {
    for(const wchar_t character:text) {
        WPARAM key=0;bool shift=false;
        if(character>=L'a'&&character<=L'z')key='A'+character-L'a';
        else if(character>=L'A'&&character<=L'Z'){key=character;shift=true;}
        else if(character>=L'0'&&character<=L'9')key=character;
        else if(character==L' ')key=VK_SPACE;
        else {
            const std::wstring ordinary=L"-=[]\\;',./`";
            const std::wstring shifted=L"_+{}|:\"<>?~";
            const std::array<WPARAM,11> keys={VK_OEM_MINUS,VK_OEM_PLUS,VK_OEM_4,VK_OEM_6,VK_OEM_5,VK_OEM_1,VK_OEM_7,VK_OEM_COMMA,VK_OEM_PERIOD,VK_OEM_2,VK_OEM_3};
            auto index=ordinary.find(character);
            if(index==std::wstring::npos){index=shifted.find(character);shift=true;}
            if(index!=std::wstring::npos)key=keys[index];
            else {index=std::wstring(L")!@#$%^&*(").find(character);check(index!=std::wstring::npos,"test text is supported ASCII");key='0'+index;shift=true;}
        }
        hold_shift(shift);fixture_time+=10;check(f.key(key),"ASCII key reaches owned spelling");hold_shift(false);
    }
}
void choose_hello(Fixture& f) {
    type_ascii(f,L"nihao");check(f.key(VK_SPACE),"choose fixture Chinese word");
}
struct ReentrantModeSink final:ITfCompartmentEventSink {
    std::atomic<ULONG> references{1};ComPtr<ITfCompartmentEventSink> previous;
    std::function<void()> callback;
    ReentrantModeSink(ITfCompartmentEventSink* value,std::function<void()> action):previous(value),callback(std::move(action)){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out)override {
        if(!out)return E_POINTER;*out=nullptr;
        if(iid!=IID_IUnknown&&iid!=IID_ITfCompartmentEventSink)return E_NOINTERFACE;
        *out=static_cast<ITfCompartmentEventSink*>(this);AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef()override{return ++references;}
    ULONG STDMETHODCALLTYPE Release()override{auto n=--references;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE OnChange(REFGUID guid)override {
        AddRef();const auto result=previous->OnChange(guid);auto action=callback;if(action)action();Release();return result;
    }
};
void on_mode_change(Fixture& f,std::function<void()> action) {
    if(auto current=dynamic_cast<ReentrantModeSink*>(f.manager.keyboard.sink.get())){current->callback=std::move(action);return;}
    auto sink=new ReentrantModeSink(f.manager.keyboard.sink.get(),std::move(action));
    f.manager.keyboard.sink.attach(sink);
}
void initialize_dictionary() {
    const auto path=std::filesystem::temp_directory_path()/(L"mansur-key-routing-"+std::to_wstring(GetCurrentProcessId())+L".tsv");
    try {
        {std::ofstream file(path,std::ios::binary);file<<"ni\t你\t200\tni\nhao\t好\t200\thao\nnihao\t你好\t500\tni hao\n";
            for(int i=0;i<20;++i)file<<"ni\t测试"<<i<<'\t'<<100-i<<"\tni\n";
            check(static_cast<bool>(file),"write synthetic dictionary");}
        auto value=std::make_shared<mansur::Dictionary>();check(value->load(path).loaded==23,"load synthetic dictionary");fixture_dictionary=std::move(value);
    }catch(...){std::error_code error;std::filesystem::remove(path,error);throw;}
    std::error_code error;std::filesystem::remove(path,error);
}
void routing_tests() {
    {
        Fixture f;
        check(f.test('N')&&f.test('N'),"test routes supported key without edit session");
        check(f.context.edit_requests==0&&shows==0,"test has no edit lock or preview side effect");
        check(!f.test(VK_RETURN),"test did not mutate empty draft");
        for(auto key:{'N','I','H','A','O'})check(f.key(key),"key gets metadata lock and accepts pinyin");
        check(shown.find(L"nihao│")!=std::wstring::npos,"production draft holds original pinyin");
        check(last_content.title.empty()&&last_content.notice.empty(),"normal input has no product title or help text");
        check(f.key(VK_SPACE)&&f.key(VK_RETURN),"choose and commit");
        check(f.context.writes==1&&f.context.written==L"你好","Chinese written exactly once after routing");
        check(f.context.range.text_reads==0,"routing never reads host text");
    }
    {
        Fixture f;f.context.blocked_session=TF_E_SYNCHRONOUS;
        check(!f.key('N'),"real key denied read lock passes to application");
        check(shown.find(L"phase=key callback=0 stage=0")!=std::wstring::npos,"unentered session precisely diagnosed");
        check(shown.find(L"session=80040208")!=std::wstring::npos,"sync failure HRESULT displayed");
        f.context.blocked_session=S_OK;
        check(f.key('N')&&shown.find(L"n│")!=std::wstring::npos,"next key recovers without replaying failed key");
    }
    {
        Fixture f;f.context.request_result=TF_E_LOCKED;
        check(!f.key('N'),"outer request failure passes key");
        check(shown.find(L"callback=0 stage=0")!=std::wstring::npos&&shown.find(L"session=8000000A")!=std::wstring::npos,"outer failure distinguished from callback result");
    }
    {
        Fixture f;f.context.property.failed=true;
        check(!f.key('N'),"scope value failure passes key");
        check(shown.find(L"phase=key callback=1 stage=6")!=std::wstring::npos&&shown.find(L"value=80004005")!=std::wstring::npos,"property failure has own stage and HRESULT");
        f.context.property.failed=false;
        check(!f.test(VK_RETURN),"failed metadata never changed draft");
    }
    {
        Fixture f;f.context.property.scope.kind=IS_PASSWORD;
        check(f.test('N')&&!f.key('N'),"routing is not acceptance; password rejected in real key");
        check(shown.empty()&&f.context.writes==0,"no password preview or write");
        f.context.property.scope.kind=IS_DEFAULT;
        check(!f.test(VK_RETURN),"sensitive key never entered draft");
    }
    {
        Fixture f;f.context.read_only=true;
        check(!f.test('N')&&f.context.edit_requests==0,"explicit readonly stops at routing");
    }
    {
        Fixture f;f.context.status_failure=true;
        check(f.test('N')&&!f.key('N'),"status failure diagnosed in real key, not accepted");
        check(shown.find(L"callback=1 stage=1")!=std::wstring::npos&&shown.find(L"status=80070005")!=std::wstring::npos,"status failure retains original HRESULT");
    }
    {
        Fixture f;f.context.app_property_failure=true;
        check(!f.key('N')&&shown.find(L"stage=5")!=std::wstring::npos&&shown.find(L"property=80070005")!=std::wstring::npos,"app property failure diagnosed");
    }
    {
        Fixture f;f.context.property.scope.failed=true;
        check(!f.key('N')&&shown.find(L"stage=8")!=std::wstring::npos&&shown.find(L"scopes=80070005")!=std::wstring::npos,"scope enumeration failure diagnosed");
    }
    {
        Fixture f;fake_control_api=FakeControlApi{};
        f.context.property.failed=true;f.context.view.window=reinterpret_cast<HWND>(1);
        for(auto key:{'N','I','H','A','O'})check(f.key(key),"ordinary RichEdit supports pinyin when optional TSF property fails");
        check(f.key(VK_SPACE),"ordinary RichEdit chooses candidate despite property E_FAIL");
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.context.writes==0&&learning_dispatches==0,"first two gesture spaces do not submit or learn");
        check(f.key(VK_SPACE),"third gesture space confirms the sentence");
        check(f.context.written==L"你好"&&f.context.writes==1,"fallback preserves once-only Chinese commit");
        check(learning_dispatches==1&&last_learning_text==L"你好","confirmed sentence handed to learning exactly once");
        check(fake_control_api.sends>0&&f.context.range.text_reads==0,"fallback only queried control metadata");
    }
    {
        Fixture f;
        for(auto key:{VK_PRIOR,VK_NEXT,VK_OEM_MINUS,VK_OEM_PLUS,VK_ADD})
            check(!f.key(key),"paging punctuation passes through without candidates");
        check(f.key('N')&&f.key('I'),"paging fixture pinyin");
        check(last_content.candidates.size()==9&&last_content.page==0,"first nine candidates rendered");
        check(f.key(VK_OEM_PLUS)&&last_content.page==1,"equals moves one complete page");
        check(f.key(VK_NEXT)&&last_content.page==2,"PageDown moves to final page");
        check(f.key(VK_ADD)&&last_content.page==2,"plus clamps on final page");
        check(f.key(VK_OEM_MINUS)&&last_content.page==1,"minus previous page");
        check(f.key(VK_PRIOR)&&last_content.page==0,"PageUp first page");
        check(f.context.writes==0&&learning_dispatches==0,"paging never commits or learns");
    }
    {
        Fixture f;check(f.key('N')&&f.key('I'),"mouse fixture pinyin");
        auto original=last_content;
        click_handler(click_owner,{PreviewActionKind::NextPage,0,original.context,original.revision});
        check(last_content.page==1&&f.context.writes==0,"mouse page changes draft only");
        auto current=last_content;
        click_handler(click_owner,{PreviewActionKind::Choose,0,original.context,original.revision});
        check(last_content.revision==current.revision,"stale click does not select new page");
        auto selected=current.candidates.front();
        click_handler(click_owner,{PreviewActionKind::Choose,selected.index,current.context,current.revision});
        check(last_content.candidates.empty()&&shown==selected.text+L"│","mouse chooses current candidate into draft");
        check(f.context.writes==0&&learning_dispatches==0,"mouse choice does not submit or learn");
    }
    {
        Fixture f;check(f.key('N')&&f.key('I'),"focused action fixture");auto original=last_content;
        Context other;f.manager.document.context=&other;
        click_handler(click_owner,{PreviewActionKind::Choose,0,original.context,original.revision});
        check(last_content.revision==original.revision&&f.context.writes==0,"other focused context rejects click");
        f.manager.document.context=&f.context;f.context.property.scope.kind=IS_PASSWORD;
        click_handler(click_owner,{PreviewActionKind::Choose,0,original.context,original.revision});
        check(last_content.revision==original.revision,"sensitive metadata rejects candidate click");
    }
    {
        Fixture f;check(f.key('N')&&f.key('I'),"mode fixture pinyin");auto original=last_content;
        check(f.set_mode(false),"toolbar explicitly selects temporary English");
        check(f.key('A')&&f.test(VK_RETURN)&&shown==L"nia│","English mode seals spelling and appends literal to owned draft");
        const auto current=last_content;
        click_handler(click_owner,{PreviewActionKind::Choose,0,original.context,original.revision});
        check(last_content.revision==current.revision,"English mode rejects old candidate click");
        check(f.set_mode(true),"toolbar explicitly selects temporary Chinese");check(f.key('H')&&f.key('A')&&f.key('O'),"Chinese mode resumes retained draft");
        check(shown==L"niahao│","mode switch preserves raw spelling order");
        check(f.key(VK_SPACE)&&f.key(VK_RETURN)&&f.context.written==L"nia好","mode resume commits literal and chosen Chinese in order");
    }
    {
        Fixture f;check(f.key('N')&&f.key('I'),"deferred action fixture");auto original=last_content;
        f.context.defer_action=true;
        click_handler(click_owner,{PreviewActionKind::NextPage,0,original.context,original.revision});
        check(static_cast<bool>(f.context.queued_action)&&last_content.page==0,"mouse can wait for a read lock without mutation");
        check(f.key(VK_OEM_PLUS)&&last_content.page==1,"keyboard continues while mouse read waits");auto current=last_content;
        check(SUCCEEDED(f.context.queued_action->DoEditSession(7)),"stale deferred callback exits normally");
        check(last_content.revision==current.revision&&last_content.page==1,"deferred stale revision cannot double-page");
        f.context.queued_action={};
    }
    {
        Fixture f;check(f.key('N')&&f.key('I'),"deactivate action fixture");auto original=last_content;
        f.context.defer_action=true;
        click_handler(click_owner,{PreviewActionKind::Choose,0,original.context,original.revision});
        f.service->Deactivate();
        check(SUCCEEDED(f.context.queued_action->DoEditSession(7)),"deactivated deferred action exits normally");
        check(f.context.writes==0&&learning_dispatches==0&&shown.empty(),"deferred action cannot revive old service UI or commit");
        f.context.queued_action={};
    }
}
void shift_routing_tests() {
    {
        Fixture f;const auto requests=f.context.edit_requests;
        begin_shift(f);fixture_time+=100;
        check(!f.test(VK_SHIFT,shift_flags()),"duplicate Shift test down remains passthrough");
        check((f.mode()&1)&&f.context.edit_requests==requests&&shows==0,"test down neither changes mode nor acquires edit lock");
        hold_shift(false);fixture_time+=50;
        check(f.test_up(VK_SHIFT,shift_flags(true))&&f.test_up(VK_SHIFT,shift_flags(true)),"repeated test up routes the same release");
        check((f.mode()&1)&&f.context.edit_requests==requests&&shows==0,"test up does not toggle or read metadata");
        check(!f.release(VK_SHIFT,shift_flags(true))&&!(f.mode()&1),"real Shift up toggles English and passes modifier release");
        check(f.diagnostic(16)==1,"synchronous compartment notification cannot double-toggle");
        check(!f.test_up(VK_SHIFT,shift_flags(true))&&!f.release(VK_SHIFT,shift_flags(true))&&f.diagnostic(16)==1,"duplicate release cannot toggle again");
        for(auto key:{int('A'),int('B'),int('C')})check(f.key(key),"English letters remain owned until confirmation");
        check(f.context.writes==0&&learning_dispatches==0,"English spelling and mode switch do not submit");
        tap_shift(f,50,0x36);
        check((f.mode()&1)&&f.diagnostic(16)==2&&f.key('N'),"right Shift restores Chinese while keyboard compartment was closed");
        check(f.context.writes==0&&learning_dispatches==0,"mode change itself never commits or learns");
    }
    {
        Fixture f;hold_shift(true);
        check(!f.down(VK_SHIFT,shift_flags()),"host directly invoking real Shift down still passes modifier");
        end_shift(f);check(!(f.mode()&1),"direct down plus routed release supports clean tap");
    }
    {
        Fixture f;begin_shift(f);fixture_time+=799;
        check(!f.test(VK_SHIFT,shift_flags()),"duplicate test does not restart tap timer");
        end_shift(f,2);check((f.mode()&1)&&f.diagnostic(16)==0,"elapsed 801ms is a hold even after repeated test");
        tap_shift(f,800);check(!(f.mode()&1),"800ms boundary remains a permitted tap");
    }
    {
        Fixture f;begin_shift(f);
        check(!f.test(VK_SHIFT,shift_flags(false,true))&&!f.down(VK_SHIFT,shift_flags(false,true)),"autorepeat remains passthrough");
        end_shift(f);check((f.mode()&1)&&f.diagnostic(16)==0,"autorepeat disarms the tap until release");
        hold_shift(false);check(!f.up(VK_SHIFT,shift_flags(true))&&(f.mode()&1),"unpaired release never changes mode");
    }
    for(auto key:{int('A'),VK_TAB,VK_SPACE,VK_LEFT}) {
        Fixture f;begin_shift(f);
        check(f.test(key)==(key=='A'),"Shift letter routes as uppercase while navigation remains offered to host");
        end_shift(f);check((f.mode()&1)&&f.diagnostic(16)==0,"test-only non-Shift event cancels pending toggle");
    }
    for(auto key:{VK_CONTROL,VK_MENU,VK_LWIN,VK_RWIN,VK_LBUTTON,int('A')}) {
        Fixture f;hold_key(key);begin_shift(f);hold_key(key,false);end_shift(f);
        check((f.mode()&1)&&f.diagnostic(16)==0,"key held before Shift prevents tap even after it is released");
    }
    for(auto key:{VK_CONTROL,VK_MENU,VK_LWIN,VK_RWIN}) {
        Fixture f;begin_shift(f);hold_key(key);
        check(!f.test(key)&&!f.test('A'),"shortcut modifiers and letters remain passthrough");
        hold_key(key,false);check(!f.up(key),"shortcut modifier release remains passthrough");
        end_shift(f);check((f.mode()&1),"shortcut modifier pressed after Shift cancels tap");
    }
    {
        Fixture f;begin_shift(f);check(!f.test_up('A'),"unrelated key release is not eaten");end_shift(f);
        check((f.mode()&1)&&f.diagnostic(16)==0,"test-only other key up cancels tap");
    }
    {
        Fixture f;begin_shift(f);hold_shift(true,0x36);
        check(!f.test(VK_SHIFT,shift_flags(false,false,0x36)),"second Shift down passes through");
        end_shift(f,20,0x36);end_shift(f,20);
        check((f.mode()&1)&&f.diagnostic(16)==0,"overlapping left and right Shift cannot toggle either release");
    }
    {
        Fixture f;check(f.set_mode(false),"combination fixture begins in English");begin_shift(f);
        check(f.test('A'),"English Shift+A is routed as owned uppercase and cancels mode gesture");end_shift(f);
        check(!(f.mode()&1)&&f.diagnostic(16)==1,"English Shift+A does not accidentally restore Chinese");
        tap_shift(f);check((f.mode()&1),"next clean tap can restore Chinese");
    }
    {
        Fixture f;fixture_key_states[VK_CAPITAL]=1;
        check(f.key('A')&&shown==L"A│","Caps Lock preserves original uppercase spelling");
        fixture_key_states[VK_CAPITAL]=0;check(f.key('N'),"caps-off Chinese decoder resumes");
    }
    {
        Fixture f;begin_shift(f);f.keys->OnSetFocus(FALSE);f.manager.thread_focus=false;end_shift(f);
        check((f.manager.keyboard.value!=0)&&f.diagnostic(16)==0,"focus loss cannot toggle target on release");
        f.manager.thread_focus=true;f.keys->OnSetFocus(TRUE);tap_shift(f);check(!(f.mode()&1),"new focused tap works after canceled gesture");
    }
    {
        Fixture f;Context other;begin_shift(f);f.manager.document.context=&other;fixture_time+=50;hold_shift(false);
        check(!f.up(VK_SHIFT,shift_flags(true),&other)&&(f.manager.keyboard.value!=0),"release in another context cannot toggle or move draft");
        f.service->Deactivate();
    }
    {
        Fixture f;ComPtr<ITfThreadMgrEventSink> events;
        check(SUCCEEDED(f.service->QueryInterface(IID_ITfThreadMgrEventSink,reinterpret_cast<void**>(events.put()))),"query shift focus event sink");
        begin_shift(f);events->OnPushContext(&f.context);end_shift(f);
        check((f.mode()&1)&&f.diagnostic(16)==0,"same-context focus epoch change disarms Shift");
    }
    {
        Fixture f;begin_shift(f);test_foreground=reinterpret_cast<HWND>(2);end_shift(f);
        check((f.manager.keyboard.value!=0)&&f.diagnostic(16)==0,"foreground window change without notification cannot toggle");
    }
    {
        Fixture f;begin_shift(f);f.manager.foreground_service=GUID_NULL;end_shift(f);
        check((f.manager.keyboard.value!=0)&&f.diagnostic(16)==0,"another active TIP cannot be changed by stale Shift release");
    }
    {
        Fixture f;begin_shift(f);f.service->Deactivate();
        check(SUCCEEDED(f.service->ActivateEx(&f.manager,1,0)),"reactivate while physical Shift remains held");end_shift(f);
        check((f.mode()&1)&&f.diagnostic(16)==0,"reactivation invalidates old Shift and starts Chinese");
        tap_shift(f);check(!(f.mode()&1),"fresh tap after reactivation works");
    }
    {
        Fixture f;f.context.read_only=true;tap_shift(f);
        check((f.mode()&1)&&f.diagnostic(16)==0&&shows==0,"readonly context cannot toggle or show preview");
    }
    for(auto scope:{IS_PASSWORD,IS_PRIVATE,IS_NUMERIC_PIN}) {
        Fixture f;check(f.key('N')&&f.key('I'),"sensitive transition fixture retains original pinyin");
        begin_shift(f);f.context.property.scope.kind=scope;const auto count=shows;end_shift(f);
        check((f.mode()&1)&&f.diagnostic(16)==0&&shows==count&&f.context.writes==0&&learning_dispatches==0,"sensitive real-up metadata rejects mode change without redisplaying draft");
        f.context.property.scope.kind=IS_DEFAULT;check(f.key('H')&&shown==L"nih│","rejected sensitive toggle did not lose draft");
    }
    for(auto failure:{TF_E_LOCKED,TF_E_SYNCHRONOUS}) {
        Fixture f;begin_shift(f);
        if(failure==TF_E_LOCKED)f.context.request_result=failure;else f.context.blocked_session=failure;
        end_shift(f);check((f.mode()&1)&&f.diagnostic(16)==0&&f.context.writes==0,"unavailable metadata read passes release without changing mode");
        f.context.request_result=S_OK;f.context.blocked_session=S_OK;tap_shift(f);
        check(!(f.mode()&1),"clean tap recovers after metadata read becomes available");
    }
    {
        Fixture f;begin_shift(f);f.context.property.failed=true;end_shift(f);
        check((f.mode()&1)&&f.diagnostic(16)==0&&shows==0,"unknown scope metadata cannot toggle or expose draft");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);f.context.view.window=reinterpret_cast<HWND>(1);
        fake_control_api.window_class=L"Edit";fake_control_api.window_style|=WS_DISABLED;tap_shift(f);
        check((f.mode()&1)&&f.diagnostic(16)==0&&shows==0,"disabled standard editor cannot toggle mode");
    }
    {
        Fixture f;f.service->Deactivate();check(f.service->ActivateEx(&f.manager,1,TF_TMAE_SECUREMODE)==E_ACCESSDENIED,"secure activation stays denied");
        tap_shift(f);check(f.diagnostic(16)==0&&shows==0,"inactive secure service never handles Shift toggle");
    }
    {
        Fixture f;check(f.key('N')&&f.key('I'),"failed mode write fixture has draft");const auto original=shown;
        f.manager.keyboard.set_result=E_ACCESSDENIED;tap_shift(f);
        check((f.mode()&1)&&f.diagnostic(16)==0&&shown==original&&f.context.writes==0,"compartment write failure preserves mode and draft");
        f.manager.keyboard.set_result=S_OK;tap_shift(f);check(!(f.mode()&1),"new tap retries only new explicit gesture after failed mode write");
        tap_shift(f);check((f.mode()&1)&&shown==original,"successful return immediately restores preserved preview");
    }
    {
        Fixture f;f.service->Deactivate();f.manager.compartment_supported=false;
        check(SUCCEEDED(f.service->ActivateEx(&f.manager,1,0)),"activate optional-compartment fallback");
        tap_shift(f);check(!(f.mode()&1)&&f.key('A'),"memory mode fallback supports owned English");
        tap_shift(f);check((f.mode()&1)&&f.key('N'),"memory mode fallback supports Shift to Chinese");
    }
    {
        Fixture f;check(f.key('N')&&f.key('I'),"retained pinyin fixture");
        tap_shift(f);check(!(f.mode()&1)&&shown==L"ni│"&&last_content.candidates.empty(),"English keeps pending spelling visible as literal");
        check(f.key('A')&&f.key('B'),"English characters append to the same owned draft");
        tap_shift(f);check((f.mode()&1)&&shown==L"niab│","Chinese restores literal draft preview immediately");
        for(auto key:{'N','I','H','A','O'})check(f.key(key),"new Chinese spelling follows literal draft");
        check(f.key(VK_SPACE),"mixed draft chooses next Chinese word");
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.context.writes==0,"partial three-space confirmation begins without submit");
        tap_shift(f);tap_shift(f);
        check(shown==L"niab你好│"&&f.context.writes==0&&learning_dispatches==0,"mode switches preserve chosen Chinese without writing or learning");
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.context.writes==0,"mode switches reset earlier two-space gesture");
        check(f.key(VK_SPACE)&&f.context.writes==1&&f.context.written==L"niab你好"&&learning_dispatches==1,"new complete three-space gesture submits mixed text exactly once");
        check(f.context.range.text_reads==0,"Shift mode path never reads host text");
    }
}
void mixed_language_tests() {
    {
        Fixture f;tap_shift(f);type_ascii(f,L"Hello");
        check(shown==L"Hello│"&&last_content.candidates.empty()&&f.context.writes==0,"pure English stays visible and preserves case before confirmation");
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&shown==L"Hello  │"&&f.context.writes==0,"first two English spaces are owned literal spaces");
        check(f.key(VK_SPACE)&&f.context.writes==1&&f.context.written==L"Hello","third English space removes only gesture spaces and writes once");
        check(learning_dispatches==1&&last_learning_text==L"Hello"&&word_remembers==0,"pure English reaches learning exactly once without Chinese vocabulary observation");
        check(f.context.range.text_reads==0&&!f.key(VK_RETURN),"empty draft Enter stays native and no host body was read");
    }
    {
        Fixture f;check(f.set_mode(false),"English sentence fixture mode");type_ascii(f,L"Hello world");
        check(shown==L"Hello world│"&&f.context.writes==0,"normal word-separating English space is preserved");
        check(f.key(VK_RETURN)&&f.context.written==L"Hello world"&&f.context.writes==1&&learning_dispatches==0,"Enter submits English without sending or learning");
        check(!f.key(VK_RETURN)&&f.context.writes==1,"second Enter is available to native editor");
    }
    {
        Fixture f;f.set_mode(false);type_ascii(f,L"a");check(f.key(VK_SPACE),"intentional space before pause");
        fixture_time+=2000;check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.key(VK_SPACE),"fresh gesture after a paused space");
        check(f.context.written==L"a "&&learning_dispatches==1,"gesture removes only its own two spaces, preserving earlier intended space");
    }
    {
        Fixture f;f.set_mode(false);
        check(f.key(VK_SPACE)&&shown==L" │"&&f.context.writes==0,"English empty draft accepts one literal space");
        check(f.key(VK_BACK)&&shown.empty()&&!f.key(VK_RETURN),"editing an English space draft cannot force empty commit");
    }
    {
        Fixture f;choose_hello(f);tap_shift(f);type_ascii(f,L"GitHub_42 + https://a.b?q=1!");tap_shift(f);choose_hello(f);
        const std::wstring expected=L"你好GitHub_42 + https://a.b?q=1!你好";
        check(shown==expected+L"│"&&f.context.writes==0,"mixed case, digits, ASCII punctuation and Chinese stay in exact input order");
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.context.written==expected,"mixed language uses one final commit");
        check(f.context.writes==1&&learning_dispatches==1&&last_learning_text==expected,"learning receives exact mixed sentence once");
        for(const auto& word:last_remembered)check(word.text==L"你好","ASCII boundary cannot create a cross-English Chinese personal word");
    }
    {
        Fixture f;type_ascii(f,L"mansur");const auto original=last_content;
        tap_shift(f);check(shown==L"mansur│"&&last_content.candidates.empty()&&last_content.revision>original.revision,"Shift seals unchosen pinyin as original Latin, never a Chinese candidate");
        type_ascii(f,L"GitHub");check(f.key(VK_RETURN)&&f.context.written==L"mansurGitHub"&&word_remembers==0,"unselected raw and literal spelling submit in order");
    }
    {
        Fixture f;type_ascii(f,L"GitHub");
        check(f.key(VK_RETURN)&&shown==L"GitHub│"&&f.context.writes==0&&last_content.candidates.empty(),"first Enter seals original raw case without Chinese selection");
        check(f.key(VK_RETURN)&&f.context.written==L"GitHub"&&f.context.writes==1&&word_remembers==0,"second Enter submits sealed spelling only once");
    }
    {
        Fixture f;type_ascii(f,L"NIHAO");check(f.key(VK_SPACE)&&f.key(VK_RETURN)&&f.context.written==L"你好","uppercase Chinese lookup still finds lowercase full-pinyin candidate");
    }
    {
        Fixture f;f.set_mode(false);fixture_key_states[VK_CAPITAL]=1;type_ascii(f,L"hello");
        check(shown==L"HELLO│","Caps Lock English casing matches keyboard state");
        hold_shift(true);check(f.key('A'),"Shift with Caps Lock produces lowercase");hold_shift(false);
        check(f.key(VK_RETURN)&&f.context.written==L"HELLOa","Caps Lock XOR Shift is preserved at commit");
    }
    for(const auto modifier:{VK_CONTROL,VK_MENU,VK_LWIN,VK_RWIN}) {
        Fixture f;f.set_mode(false);type_ascii(f,L"abc");check(f.key(VK_SPACE)&&f.key(VK_SPACE),"partial English gesture fixture");
        const auto current=shown;hold_key(modifier);check(!f.test('A')&&!f.down('A'),"application shortcut never inserts literal");hold_key(modifier,false);
        check(shown==current&&f.context.writes==0,"shortcut preserves draft and does not commit");
        check(f.key(VK_SPACE)&&f.context.writes==0&&shown==L"abc   │","test-only shortcut breaks earlier confirmation gesture");
        check(f.key(VK_RETURN)&&f.context.written==L"abc   "&&learning_dispatches==0,"broken confirmation retains all intended spaces");
    }
    {
        Fixture f;f.set_mode(false);fixture_settings.english_suggestions=true;type_ascii(f,L"hel");
        check(last_content.english_suggestions&&!last_content.candidates.empty()&&last_content.candidates[0].text==L"hello","English opt-in suggestions rendered by production callbacks");
        check(f.test(VK_TAB)&&f.key(VK_TAB)&&shown==L"hello│","Tab explicitly completes English under metadata checks");
        check(f.key(VK_RETURN)&&f.context.written==L"hello"&&word_remembers==0,"English completion commits once without learning Chinese personal words");
    }
    {
        Fixture f;f.set_mode(false);fixture_settings.english_suggestions=true;type_ascii(f,L"hel");
        check(f.key('1')&&f.key(VK_RETURN)&&f.context.written==L"hel1","English suggestion numbers remain literal input");
    }
    {
        Fixture f;f.set_mode(false);fixture_settings.english_suggestions=true;type_ascii(f,L"hel");
        const auto c=last_content;
        click_handler(click_owner,{PreviewActionKind::EnglishCompletion,1,c.context,c.revision});
        check(shown==L"help│"&&f.context.writes==0,"English suggestion click changes only owned draft");
        click_handler(click_owner,{PreviewActionKind::EnglishCompletion,0,c.context,c.revision});
        check(shown==L"help│","stale English click cannot change newer draft");
        check(f.key(VK_RETURN)&&f.context.written==L"help","explicit English suggestion is committed exactly");
    }
    {
        Fixture f;f.set_mode(false);type_ascii(f,L"hel");f.key(VK_SPACE);f.key(VK_SPACE);
        check(!f.test(VK_TAB)&&!f.key(VK_TAB),"default English mode preserves application Tab navigation");
        check(f.key(VK_SPACE)&&f.context.writes==0,"unhandled Tab ends earlier three-space gesture");
    }
    {
        Fixture f;f.set_mode(false);type_ascii(f,L"ac");check(f.key(VK_LEFT),"move inside literal draft");type_ascii(f,L"b");
        check(f.key(VK_DELETE)&&f.key(VK_BACK)&&shown==L"a│","backspace/delete operate on owned Latin draft");
        tap_shift(f);choose_hello(f);check(f.key(VK_RETURN)&&f.context.written==L"a你好","editing before switching keeps complete final order");
    }
    {
        Fixture f;f.set_mode(false);type_ascii(f,L"Hello");Context other;f.manager.document.context=&other;
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.context.writes==0&&learning_dispatches==0,"lost focus cannot redirect English commit");
        check(shown.find(L"Hello│")!=std::wstring::npos,"NotWritten retains exact sentence without swallowed confirmation spaces");
        f.manager.document.context=&f.context;
        check(f.key(VK_RETURN)&&f.context.written==L"Hello"&&f.context.writes==1&&learning_dispatches==0,"explicit retry writes preserved English exactly once");
    }
    {
        Fixture f;f.set_mode(false);type_ascii(f,L"Hello");f.context.write_failure=true;
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.context.writes==1&&learning_dispatches==0,"unknown insertion cannot dispatch English learning");
        check(shown.find(L"Hello")!=std::wstring::npos&&!f.key(VK_RETURN),"uncertain English remains recovery-only without automatic resubmission");
        f.context.write_failure=false;type_ascii(f,L"Next");check(f.key(VK_RETURN)&&f.context.writes==2&&f.context.written==L"Next","unknown previous outcome does not strand new English input");
    }
    for(const auto scope:{IS_PASSWORD,IS_PRIVATE,IS_NUMERIC_PIN}) {
        Fixture f;f.set_mode(false);f.context.property.scope.kind=scope;
        check(f.test('A')&&!f.down('A')&&shown.empty()&&f.context.writes==0,"English uses same sensitive input rejection as Chinese");
        f.context.property.scope.kind=IS_DEFAULT;check(!f.test(VK_RETURN),"sensitive Latin never enters draft");
    }
    {
        Fixture f;f.set_mode(false);f.context.read_only=true;check(!f.test('A'),"English readonly routing is denied");
        f.context.read_only=false;type_ascii(f,L"ab");const auto original=last_content;
        f.context.property.failed=true;check(!f.key('C')&&last_content.revision==original.revision,"English metadata failure does not alter owned draft");
        f.context.property.failed=false;check(f.key(VK_RETURN)&&f.context.written==L"ab","metadata recovery submits only accepted English characters");
    }
    {
        Fixture f;type_ascii(f,L"ni");const auto original=last_content;
        f.manager.keyboard.set_result=E_ACCESSDENIED;
        check(!f.set_mode(false)&&last_content.revision==original.revision&&(f.mode()&1),"failed toolbar mode write never publishes staged literal conversion");
        f.manager.keyboard.set_result=S_OK;
        check(f.key(VK_SPACE)&&f.key(VK_RETURN)&&f.context.written==L"你","failed English switch retains selectable original Chinese");
    }
    {
        Fixture f;type_ascii(f,L"ni");const auto revision=last_content.revision;
        VARIANT value;VariantInit(&value);value.vt=VT_I4;value.lVal=0;
        check(SUCCEEDED(f.manager.keyboard.SetValue(1,&value)),"external standard compartment enters English");
        const auto requests=f.context.edit_requests;
        check(f.test('A')&&last_content.revision==revision&&f.context.edit_requests==requests,"external notification and Test do not seal draft or request a lock");
        check(f.down('A')&&shown==L"nia│"&&last_content.candidates.empty(),"real English callback safely seals original raw before appending");
        check(f.key(VK_RETURN)&&f.context.written==L"nia","external mode source cannot reorder raw text");
    }
    {
        Fixture f;type_ascii(f,L"ni");Context other;
        on_mode_change(f,[&]{f.keys->OnSetFocus(FALSE);f.manager.document.context=&other;});
        check(!f.set_mode(false)&&f.context.writes==0&&other.writes==0,"mode notification focus reentry rejects stale staged publication");
        f.manager.document.context=&f.context;on_mode_change(f,{});
        f.keys->OnSetFocus(TRUE);check(f.set_mode(true),"restore mode after isolated focus reentry");
        check(f.key(VK_SPACE)&&f.key(VK_RETURN)&&f.context.written==L"你","focus-rejected staging did not overwrite original pinyin");
    }
    {
        Fixture f;type_ascii(f,L"ni");VARIANT value;VariantInit(&value);value.vt=VT_I4;value.lVal=0;
        check(SUCCEEDED(f.manager.keyboard.SetValue(1,&value)),"external English navigation fixture");
        check(f.key(VK_LEFT)&&shown==L"n│i"&&last_content.candidates.empty(),"English navigation seals Latin without choosing Chinese");
        check(f.key(VK_DELETE)&&f.key(VK_RETURN)&&f.context.written==L"n","English staged navigation and deletion retain Latin order");
    }
    {
        Fixture f;f.set_mode(false);type_ascii(f,std::wstring(2047,L'x'));tap_shift(f);type_ascii(f,L"G");
        VARIANT value;VariantInit(&value);value.vt=VT_I4;value.lVal=0;f.manager.keyboard.SetValue(1,&value);
        const auto original=last_content;
        check(f.key('A')&&f.context.writes==0&&last_content.revision==original.revision&&last_content.draft==original.draft,"full raw+literal operation consumes without partial sealing or host passthrough");
        value.lVal=1;f.manager.keyboard.SetValue(1,&value);type_ascii(f,L"H");
        value.lVal=0;f.manager.keyboard.SetValue(1,&value);const auto overflow=last_content;
        check(f.key(VK_SPACE)&&last_content.revision==overflow.revision&&last_content.draft==overflow.draft,"over-capacity external English space preserves entire raw spelling");
        check(f.key(VK_LEFT)&&last_content.revision==overflow.revision&&f.context.writes==0,"unsealable English navigation is consumed without choosing or sending raw");
        check(f.key(VK_BACK)&&f.key(VK_BACK)&&f.key(VK_RETURN)&&f.context.written==std::wstring(2047,L'x'),"backspace can recover capacity without losing accepted draft");
    }
    {
        Fixture f;type_ascii(f,L"ni");bool called=false;
        on_mode_change(f,[&]{if(!called){called=true;check(f.key('X'),"reentrant real key accepted in new mode");}});
        check(!f.set_mode(false)&&shown==L"nix│","new real input revision prevents stale staged copy from replacing it");
        check(f.key(VK_RETURN)&&f.context.written==L"nix"&&f.context.writes==1,"reentrant accepted literal survives subsequent commit");
    }
    {
        Fixture f;f.set_mode(false);type_ascii(f,L"Hello");f.context.after_insert=[&]{f.service->Deactivate();};
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.context.writes==1&&learning_dispatches==0,"deactivation during English insertion never duplicates text or revives learning");
    }
}
void stability_tests() {
    {
        Fixture first;fixture_settings.chinese_mode=false;
        check(first.key('N'),"legacy persistent English preference is ignored");
        check(first.set_mode(false)&&first.test('I'),"current thread accepts explicit owned English");
        Fixture second;
        check((second.mode()&1)&&second.key('N'),"new native service starts Chinese independently");
        check(!(first.mode()&1)&&first.test('I'),"new service does not change other thread temporary mode");
    }
    {
        Fixture f;
        for(int i=0;i<24;++i) {
            check(f.set_mode(false),"temporary English before lifecycle restart");
            f.service->Deactivate();check(!f.manager.keyboard.sink,"mode sink detached on deactivate");
            check(SUCCEEDED(f.service->ActivateEx(&f.manager,1,0)),"same service reactivation succeeds");
            check((f.mode()&1)&&f.manager.keyboard.value==1,"reactivation defaults to Chinese once");
        }
        check(f.diagnostic(2)==25&&f.manager.keyboard.advises==25&&f.manager.keyboard.unadvises==24,"activation subscriptions remain balanced");
    }
    {
        Fixture f;f.manager.keyboard.get_result=E_FAIL;
        check((f.mode()&1)&&f.key('N'),"mode read failure preserves default Chinese");
        check(f.diagnostic(12)==static_cast<std::uint32_t>(E_FAIL),"mode failure exposed without text");
        f.manager.keyboard.get_result=S_OK;check(f.set_mode(false),"set English when mode property recovers");
        f.manager.keyboard.get_result=E_FAIL;check(!(f.mode()&1)&&f.test('I'),"mode read failure preserves explicit English too");
    }
    {
        Fixture f;f.service->Deactivate();f.manager.compartment_supported=false;
        check(SUCCEEDED(f.service->ActivateEx(&f.manager,1,0))&&(f.mode()&1),"missing optional compartment does not block activation");
        check(f.set_mode(false)&&f.test('N'),"memory mode fallback supports explicit English");
        check(f.set_mode(true)&&f.key('N'),"memory mode fallback restores explicit Chinese");
    }
    {
        Fixture f;auto snapshot=f.mode();check(snapshot!=0,"focused native endpoint available");
        f.keys->OnSetFocus(FALSE);
        check(f.mode_handler(f.mode_owner,ModeOperation::Set,snapshot,0)==0,"focus epoch invalidates old toolbar request");
        snapshot=f.mode();test_foreground=reinterpret_cast<HWND>(2);
        check(f.mode_handler(f.mode_owner,ModeOperation::Set,snapshot,0)==0,"foreground window change rejects old request");
        test_foreground=reinterpret_cast<HWND>(1);snapshot=f.mode();
        check(f.mode_handler(f.mode_owner,ModeOperation::Set,snapshot,0)!=0,"fresh toolbar request succeeds");
        check(f.mode_handler(f.mode_owner,ModeOperation::Set,snapshot,1)==0,"accepted request cannot be replayed");
        f.manager.foreground_service=GUID_NULL;check(f.mode()==0,"another selected TIP disables native toolbar target");
        f.manager.foreground_service=kTextService;f.manager.thread_focus=false;check(f.mode()==0,"inactive thread does not publish active mode");
    }
    {
        Fixture f;std::vector<std::unique_ptr<Context>> contexts;
        for(int i=0;i<96;++i) {
            contexts.push_back(std::make_unique<Context>());BOOL eaten=FALSE;
            check(SUCCEEDED(f.keys->OnTestKeyDown(contexts.back().get(),'N',0,&eaten))&&eaten,"more than 64 empty contexts remain usable");
        }
        check(f.diagnostic(4)>=64&&f.diagnostic(3)<=64&&f.diagnostic(5)==0,"empty context reclamation is bounded and observable");
        f.service->Deactivate(); // Release COM references while mock contexts still exist.
    }
    {
        Fixture f;std::vector<std::unique_ptr<Context>> contexts;
        for(int i=0;i<65;++i)contexts.push_back(std::make_unique<Context>());
        for(int i=0;i<64;++i) {
            f.manager.document.context=contexts[i].get();BOOL eaten=FALSE;
            check(SUCCEEDED(f.keys->OnTestKeyDown(contexts[i].get(),'N',0,&eaten))&&eaten,"allocate nonempty isolated draft");
            check(SUCCEEDED(f.keys->OnKeyDown(contexts[i].get(),'N',0,&eaten))&&eaten,"retain isolated draft content");
        }
        BOOL eaten=FALSE;
        check(SUCCEEDED(f.keys->OnTestKeyDown(contexts[64].get(),'N',0,&eaten))&&!eaten,"pool pressure never discards a pending draft");
        check(f.diagnostic(3)==64&&f.diagnostic(5)>0,"full draft pool has fixed metadata diagnosis");
        f.manager.document.context=contexts[0].get();f.keys->OnKeyDown(contexts[0].get(),VK_ESCAPE,0,&eaten);
        check(SUCCEEDED(f.keys->OnTestKeyDown(contexts[64].get(),'N',0,&eaten))&&eaten,"cancelled draft frees capacity for later context");
        f.service->Deactivate();
    }
    {
        Fixture f;f.context.property.failed=true;check(!f.key('N'),"diagnostic fixture rejects unknown property");
        check(f.diagnostic(0)==1&&f.diagnostic(14)==22&&f.diagnostic(6)==1&&f.diagnostic(7)==6,"version and metadata stage exposed without body");
        check(f.diagnostic(10)==static_cast<std::uint32_t>(E_FAIL),"exact property HRESULT exposed");
    }
    {
        Fixture f;f.service->Deactivate();f.manager.keyboard.advise_result=E_NOTIMPL;
        check(SUCCEEDED(f.service->ActivateEx(&f.manager,1,0))&&f.key('N'),"optional mode notifications unavailable does not block Chinese");
        check(f.set_mode(false)&&f.test('I'),"explicit toolbar request refreshes mode without notifications");
    }
}
void shell_editor_tests() {
    auto editor=[](Fixture& f,const wchar_t* class_name=L"Edit") {
        f.context.view.window=reinterpret_cast<HWND>(1);fake_control_api.window_class=class_name;
    };
    auto type=[](Fixture& f){for(auto key:{'N','I','H','A','O'})check(f.key(key),"restricted editor accepts pinyin");check(f.key(VK_SPACE),"restricted editor chooses Chinese");};
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);type(f);
        check(f.context.writes==0&&learning_dispatches==0,"Explorer uses same whole-sentence draft until confirmation");
        check(f.key(VK_RETURN)&&f.context.writes==1&&f.context.written==L"你好","Enter submits Chinese through standard edit session once");
        check(!f.key(VK_RETURN)&&learning_dispatches==0,"next Enter passes to native rename without forced learning");
        check(f.context.range.text_reads==0,"rename scenario never reads existing label text");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f,L"RICHEDIT50W");type(f);
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.context.writes==0,"first two confirmation spaces retain draft");
        check(f.key(VK_SPACE)&&f.context.writes==1&&learning_dispatches==1&&last_learning_text==L"你好","third space writes Chinese and dispatches learning once");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);fixture_dispatch=DispatchResult::NotConnected;type(f);
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.key(VK_SPACE),"confirmation remains usable without learning backend");
        check(f.context.writes==1&&f.context.written==L"你好"&&learning_dispatches==1,"missing backend cannot undo or duplicate Chinese");
        check(f.key('N'),"new Chinese can start immediately after unavailable learning");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);f.context.property.failed=true;type(f);
        check(f.key(VK_RETURN)&&f.context.writes==1,"existing standard-control scope fallback also works in restricted host");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f,L"SysListView32");
        check(f.test('N')&&!f.key('N')&&f.context.writes==0&&shown.empty(),"Explorer navigation surface cannot become an input target");
        editor(f);check(f.key('N'),"entering an actual standard editor restores Chinese without restart");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);fake_control_api.same_thread=false;
        check(!f.key('N')&&fake_control_api.sends==0,"restricted host cannot query another thread's edit control");
        fake_control_api.same_thread=true;fake_control_api.window_style|=ES_READONLY;
        check(!f.key('N'),"readonly standard editor remains excluded");
        fake_control_api.window_style&=~ES_READONLY;fake_control_api.window_style|=ES_PASSWORD;
        check(!f.key('N'),"password style remains excluded even with ordinary input scope");
    }
    for(auto scope:{IS_PASSWORD,IS_PRIVATE,IS_NUMERIC_PIN}) {
        Fixture f(HostDecision::StandardEditOnly);editor(f);f.context.property.scope.kind=scope;
        check(!f.key('N')&&fake_control_api.sends==0&&f.context.writes==0,"explicit sensitive scope wins before standard control queries");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);type(f);
        Context other;f.manager.document.context=&other;
        check(f.key(VK_RETURN)&&f.context.writes==0&&other.writes==0,"focus moving before commit cannot redirect draft to another context");
        f.manager.document.context=&f.context;check(f.key(VK_RETURN)&&f.context.writes==1,"failed nonmutation commit preserves draft for explicit retry");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);type(f);
        f.context.after_insertion_query=[&]{f.context.view.window=reinterpret_cast<HWND>(2);fake_control_api.focused=reinterpret_cast<HWND>(2);};
        check(f.key(VK_RETURN)&&f.context.writes==0&&learning_dispatches==0,"editor replaced while obtaining insertion interface cannot receive the previous draft");
        f.context.after_insertion_query={};check(f.key('N')&&last_content.draft==L"n│","replacement editor starts fresh after prewrite reentrancy");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);type(f);
        unsigned extents=0;f.context.after_insert=[&]{extents=f.context.view.extents;f.context.range.after_collapse=[&]{fake_control_api.focused=nullptr;};};
        check(f.key(VK_RETURN)&&f.context.writes==1&&!f.context.selection_called&&f.context.view.extents==extents,"editor removed during returned-range collapse gets no selection or caret mutation");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);type(f);
        unsigned extents=0;f.context.after_insert=[&]{extents=f.context.view.extents;fake_control_api.focused=nullptr;};
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.key(VK_SPACE),"label destruction during successful insert returns safely");
        check(f.context.writes==1&&!f.context.selection_called&&f.context.view.extents==extents&&learning_dispatches==0,"destroyed editor gets no post-write selection/caret/learning calls");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);type(f);
        ComPtr<ITfThreadMgrEventSink> events;check(SUCCEEDED(f.service->QueryInterface(IID_ITfThreadMgrEventSink,reinterpret_cast<void**>(events.put()))),"query lifecycle sink");
        unsigned extents=0;f.context.after_insert=[&]{extents=f.context.view.extents;f.manager.document.context=nullptr;events->OnPopContext(&f.context);};
        check(f.key(VK_RETURN)&&f.context.writes==1&&!f.context.selection_called&&f.context.view.extents==extents,"context popped reentrantly during insert cannot receive caret writes");
        check(f.diagnostic(3)==0&&learning_dispatches==0,"popped editor is released without reviving learning");
        f.context.after_insert={};f.manager.document.context=&f.context;check(f.key('N'),"next editing context remains usable after reentrant pop");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);type(f);
        f.context.after_insert=[&]{f.manager.document.context=nullptr;f.service->Deactivate();};
        check(f.key(VK_RETURN)&&f.context.writes==1&&!f.context.selection_called&&learning_dispatches==0,"deactivation during write retains one insertion and unwinds safely");
        f.context.after_insert={};f.manager.document.context=&f.context;
        check(SUCCEEDED(f.service->ActivateEx(&f.manager,1,0))&&f.key('N'),"same service can reactivate after destroyed edit lifecycle");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);type(f);
        unsigned extents=0;f.context.after_selection=[&]{extents=f.context.view.extents;f.manager.document.context=nullptr;};
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.key(VK_SPACE),"selection notification can remove a label editor");
        check(f.context.writes==1&&f.context.selection_called&&f.context.view.extents==extents&&learning_dispatches==0,"post-selection context change never queries stale caret or starts learning");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);check(f.key('N')&&f.key('I'),"mouse candidate fixture");
        const auto selected=last_content.candidates.front();
        click_handler(click_owner,{PreviewActionKind::Choose,selected.index,last_content.context,last_content.revision});
        check(f.context.writes==0&&learning_dispatches==0,"restricted mouse selection cannot submit or learn by itself");
        check(f.key(VK_RETURN)&&f.context.written==selected.text,"mouse choice is committed only by explicit confirmation");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);editor(f);type(f);
        const auto old_context=last_content.context;
        f.context.view.window=reinterpret_cast<HWND>(2);fake_control_api.focused=reinterpret_cast<HWND>(2);
        check(f.key('N')&&last_content.draft==L"n│"&&last_content.context!=old_context,"same TSF context reused by a new editor receives a fresh isolated draft");
        for(auto key:{'I','H','A','O'})check(f.key(key),"type only into replacement editor draft");
        check(f.key(VK_SPACE)&&f.key(VK_RETURN)&&f.context.writes==1&&f.context.written==L"你好","previous label draft cannot leak into new editor commit");
    }
    {
        Fixture f(HostDecision::StandardEditOnly);f.service->Deactivate();
        check(f.service->ActivateEx(&f.manager,1,TF_TMAE_SECUREMODE)==E_ACCESSDENIED&&!f.key('N'),"restricted host never activates in secure mode");
    }
}
void personal_word_routing_tests() {
    auto type=[](Fixture& f){for(auto key:{'N','I','H','A','O'})check(f.key(key),"personal word pinyin");check(f.key(VK_SPACE),"choose personal word");};
    auto remembered=[](){for(const auto& w:last_remembered)if(w.text==L"你好"&&w.syllables=="ni hao"&&w.uses==1)return true;return false;};
    {
        Fixture f;check(f.test('N')&&f.test('N')&&word_snapshots==0,"test callbacks never load personal snapshot");
        type(f);check(word_remembers==0,"choosing alone never persists a draft");
        check(f.key(VK_RETURN)&&f.context.writes==1&&word_remembers==1&&remembered(),"Enter confirmed insertion remembers canonical words once");
        check(learning_dispatches==0&&!f.key(VK_RETURN)&&word_remembers==1,"ordinary Enter learning is independent of translation and cannot repeat");
    }
    {
        Fixture f;fixture_dispatch=DispatchResult::NotConnected;type(f);
        check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&word_remembers==0,"incomplete confirmation does not persist");
        check(f.key(VK_SPACE)&&word_remembers==1&&remembered()&&f.context.writes==1,"missing translation backend cannot disable personal vocabulary");
    }
    {
        Fixture f;type(f);Context other;f.manager.document.context=&other;
        check(f.key(VK_RETURN)&&f.context.writes==0&&word_remembers==0,"NotWritten cannot train vocabulary");
        f.manager.document.context=&f.context;check(f.key(VK_RETURN)&&word_remembers==1&&remembered(),"explicit successful retry counts once");
    }
    {
        Fixture f;type(f);f.context.write_failure=true;
        check(f.key(VK_RETURN)&&f.context.writes==1&&word_remembers==0,"Unknown write cannot train vocabulary");
    }
    {
        Fixture f;type(f);check(f.key(VK_ESCAPE)&&word_remembers==0&&!f.key(VK_RETURN),"cancelled draft never persists");
    }
    {
        Fixture f;type(f);check(f.key(VK_BACK)&&f.key(VK_RETURN)&&f.context.written==L"你"&&word_remembers==0,"edited choice does not persist stale original word");
    }
    {
        Fixture f;type(f);fixture_settings.auto_remember=false;
        check(f.key(VK_RETURN)&&word_remembers==0&&f.context.written==L"你好","disabling before submit clears pending observations without losing Chinese");
    }
    {
        Fixture f;fixture_settings.auto_remember=false;type(f);
        check(f.key(VK_RETURN)&&word_remembers==0&&word_snapshots==0,"disabled preference avoids store and suggestions");
    }
    {
        Fixture f;word_queue_accepts=false;type(f);
        check(f.key(VK_RETURN)&&f.context.writes==1&&f.key('N'),"full/unavailable storage queue cannot interrupt Chinese");
    }
    {
        Fixture f;f.context.property.scope.kind=IS_PASSWORD;
        check(!f.key('N')&&word_snapshots==0&&word_remembers==0,"sensitive field does not access personal vocabulary");
    }
    {
        Fixture f;fixture_personal=std::make_shared<const mansur::PersonalLexicon>(std::vector<mansur::PersonalWord>{{L"拟好","ni hao",8}});
        for(auto key:{'N','I','H','A','O'})check(f.key(key),"personal snapshot routed pinyin");
        check(!last_content.candidates.empty()&&last_content.candidates.front().text==L"拟好","shared personal snapshot reaches production candidate rendering");
        fixture_personal.reset();check(f.key(VK_SPACE)&&f.key(VK_RETURN)&&f.context.written==L"拟好","busy snapshot cache retains known vocabulary");
    }
    {
        Fixture f;fixture_personal=std::make_shared<const mansur::PersonalLexicon>(std::vector<mansur::PersonalWord>{{L"拟好","ni hao",8}});
        for(auto key:{'N','I','H','A','O'})check(f.key(key),"switch-off snapshot fixture");
        check(last_content.candidates.front().text==L"拟好","personal candidate displayed before preference changes");
        fixture_settings.auto_remember=false;
        check(f.key(VK_SPACE)&&f.key(VK_RETURN)&&f.context.written==L"拟好"&&word_remembers==0,"disabling learning cannot silently change the displayed selected word");
    }
    {
        Fixture f;type(f);f.context.after_insert=[&]{f.service->Deactivate();};
        check(f.key(VK_RETURN)&&f.context.writes==1&&word_remembers==1&&remembered(),"confirmed insert survives reentrant deactivation with one data-only enqueue");
    }
}
struct ChangedRanges final:IEnumTfRanges {
    std::atomic<ULONG> references{1};ComPtr<ITfRange> value;bool consumed=false;
    explicit ChangedRanges(ITfRange* range):value(range){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out)override{*out=nullptr;if(iid!=IID_IUnknown&&iid!=IID_IEnumTfRanges)return E_NOINTERFACE;*out=this;AddRef();return S_OK;}
    ULONG STDMETHODCALLTYPE AddRef()override{return ++references;}
    ULONG STDMETHODCALLTYPE Release()override{auto n=--references;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE Clone(IEnumTfRanges** out)override{auto copy=new ChangedRanges(value.get());copy->consumed=consumed;*out=copy;return S_OK;}
    HRESULT STDMETHODCALLTYPE Next(ULONG count,ITfRange** out,ULONG* fetched)override{*fetched=0;if(count&&value&&!consumed){*out=value.get();(*out)->AddRef();*fetched=1;consumed=true;return S_OK;}return S_FALSE;}
    HRESULT STDMETHODCALLTYPE Reset()override{consumed=false;return S_OK;}
    HRESULT STDMETHODCALLTYPE Skip(ULONG count)override{if(count&&value&&!consumed){consumed=true;return count==1?S_OK:S_FALSE;}return count?S_FALSE:S_OK;}
};
struct EditRecord final:ITfEditRecord {
    UNKNOWN(ITfEditRecord)
    bool selection=true;
    ITfRange* text_change=nullptr;
    HRESULT STDMETHODCALLTYPE GetSelectionStatus(BOOL* changed)override{*changed=selection;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetTextAndPropertyUpdates(DWORD,const GUID**,ULONG,IEnumTfRanges** out)override{*out=new ChangedRanges(text_change);return S_OK;}
};
WritebackCommand prepare_writeback(Fixture& f) {
    f.context.text_source_supported=true;f.context.range.model=std::make_shared<RangeText>();
    f.context.range.model->context=&f.context;
    choose_hello(f);check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.key(VK_SPACE),"writeback fixture triple-space commits Chinese");
    check(ValidWritebackToken(last_writeback_token)&&last_writeback_endpoint!=nullptr,"only tracked own range publishes writeback token");
    WritebackCommand command;command.token=last_writeback_token;
    check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Ready,"current committed range can be queried without text read");
    check(f.context.range.model->reads==0,"availability query reads no application text");
    command.operation=WritebackOperation::Apply;command.text=L"Hello";
    f.context.defer_action=true;return command;
}
void complete_writeback(Fixture& f) {
    auto action=std::move(f.context.queued_action);check(static_cast<bool>(action),"explicit apply queues asynchronous write session");
    action->DoEditSession(7);
}
WritebackResult writeback_status(Fixture& f,WritebackCommand command) {
    command.operation=WritebackOperation::Status;command.text.clear();return f.writeback_handler(f.writeback_owner,command);
}
void writeback_tests() {
    const std::string token(32,'a');
    auto packet=[&](std::uint32_t op,std::uint32_t mode,const std::wstring& text){
        std::vector<unsigned char> value(48+text.size()*2);std::uint32_t fields[]={1,op,mode,static_cast<std::uint32_t>(text.size())};
        std::memcpy(value.data(),fields,16);std::memcpy(value.data()+16,token.data(),32);
        if(!text.empty())std::memcpy(value.data()+48,text.data(),text.size()*2);return value;
    };
    WritebackCommand parsed;auto bytes=packet(2,1,L"Hello");
    check(ParseWritebackPacket(bytes.data(),bytes.size(),parsed)&&parsed.token==token&&parsed.text==L"Hello"&&parsed.mode==WritebackMode::Append,"cross-architecture packet fixed 48-byte header and UTF16 payload");
    for(const auto bad:std::vector<std::vector<unsigned char>>{packet(4,0,L""),packet(2,2,L"x"),packet(1,0,L"x"),packet(2,0,L""),packet(2,0,std::wstring(4097,L'a')),packet(2,0,std::wstring(1,0xd800)),packet(2,0,std::wstring(1,L'\0'))})
        check(!ParseWritebackPacket(bad.data(),bad.size(),parsed),"invalid packet operation/size/text rejected");
    bytes[16]='A';check(!ParseWritebackPacket(bytes.data(),bytes.size(),parsed),"token must be exact lowercase hex");
    check(!ParseWritebackPacket(nullptr,48,parsed)&&!ParseWritebackPacket(bytes.data(),47,parsed),"null or partial packet rejected");
    {
        Fixture f;auto command=prepare_writeback(f);auto model=f.context.range.model;
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Ready&&model->writes==0,"apply only queues, never writes inside IPC callback");
        complete_writeback(f);check(model->text==L"Hello"&&model->writes==1&&model->reads==1&&model->maximum_read==3,"replace reads only own two-character range plus one bounded sentinel");
        check(writeback_status(f,command)==WritebackResult::Written&&f.writeback_handler(f.writeback_owner,command)==WritebackResult::Written&&model->writes==1,"duplicate apply returns written without a second write");
    }
    {
        Fixture f;auto command=prepare_writeback(f);command.mode=WritebackMode::Append;
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(f.context.range.model->text==L"你好 Hello","append targets own saved END and separates English with one space");
    }
    {
        Fixture f;auto command=prepare_writeback(f);auto old=command;
        choose_hello(f);check(f.key(VK_SPACE)&&f.key(VK_SPACE)&&f.key(VK_SPACE),"next learning sentence commits");
        check(last_writeback_token!=old.token&&f.writeback_handler(f.writeback_owner,old)==WritebackResult::Unavailable,"new request cannot reuse previous range token");
    }
    {
        Fixture f;auto command=prepare_writeback(f);command.token[0]=command.token[0]=='a'?'b':'a';
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Unavailable&&f.context.range.model->writes==0,"foreign token cannot edit current field");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.key('N');
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Rejected&&f.context.range.model->writes==0,"new draft key invalidates previous committed target");
    }
    {
        Fixture f;auto command=prepare_writeback(f);hold_key(VK_CONTROL);f.test('V');hold_key(VK_CONTROL,false);
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Rejected,"application shortcut routing invalidates target before paste");
    }
    {
        Fixture f;auto command=prepare_writeback(f);EditRecord edit;f.context.range.start=0;f.context.range.end=1;
        auto sink=f.context.text_sink;sink->OnEndEdit(&f.context,7,&edit);
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Rejected&&f.context.range.model->reads==0,"selection change abandons target without reading host text");
    }
    {
        Fixture f;auto command=prepare_writeback(f);auto sink=f.context.text_sink;sink->OnEndEdit(&f.context,7,nullptr);
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Rejected,"uninspectable external edit abandons target");
    }
    {
        Fixture f;auto command=prepare_writeback(f);EditRecord edit;edit.selection=false;
        auto sink=f.context.text_sink;sink->OnEndEdit(&f.context,7,&edit);
        auto query=command;query.operation=WritebackOperation::Query;query.text.clear();
        check(f.writeback_handler(f.writeback_owner,query)==WritebackResult::Ready,"unchanged edit notification does not immediately invalidate newly committed text");
        edit.text_change=&f.context.range;f.context.range.model->text=L"再见";sink->OnEndEdit(&f.context,7,&edit);
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Rejected&&f.context.range.model->reads==1,
            "actual own-range text update abandons writeback after one bounded check");
    }
    {
        Fixture f;auto command=prepare_writeback(f);EditRecord edit;
        auto sink=f.context.text_sink;sink->OnEndEdit(&f.context,7,&edit);
        auto query=command;query.operation=WritebackOperation::Query;query.text.clear();
        check(f.writeback_handler(f.writeback_owner,query)==WritebackResult::Ready&&f.context.range.model->reads==1&&
            f.context.range.model->maximum_read==3,"redundant caret synchronisation preserves exact original range and bounded read");
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Written&&f.context.range.model->text==L"Hello",
            "replace still works after redundant selection notification");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.own_write_session=true;
        auto sink=f.context.text_sink;sink->OnEndEdit(&f.context,7,nullptr);
        auto query=command;query.operation=WritebackOperation::Query;query.text.clear();
        check(f.writeback_handler(f.writeback_owner,query)==WritebackResult::Ready&&f.context.range.model->reads==0&&
            f.diagnostic(29)==1,"own successful commit notification is distinct from subsequent external edit");
        f.context.own_write_session=false;command.mode=WritebackMode::Append;
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Written&&f.context.range.model->text==L"你好 Hello",
            "append verifies original independently after own insertion notification");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.own_write_session=true;
        auto sink=f.context.text_sink;sink->OnEndEdit(&f.context,7,nullptr);f.context.own_write_session=false;
        f.context.range.model->text=L"再见";f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->writes==0,
            "own notification exemption cannot bypass original text verification");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.own_write_session=true;
        auto sink=f.context.text_sink;sink->OnEndEdit(&f.context,7,nullptr);f.context.own_write_session=false;
        f.context.property.scope.kind=IS_PASSWORD;f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->reads==0,
            "own notification exemption cannot bypass sensitive metadata check");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.own_write_session=true;f.context.own_session_result=E_FAIL;
        EditRecord edit;f.context.range.start=0;f.context.range.end=1;
        auto sink=f.context.text_sink;sink->OnEndEdit(&f.context,7,&edit);
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Rejected&&f.diagnostic(27)==3,
            "failed own-session query does not exempt actual selection change and exposes fixed stage");
    }
    {
        Fixture f;auto command=prepare_writeback(f);EditRecord edit;edit.selection=false;edit.text_change=&f.context.range;
        auto sink=f.context.text_sink;sink->OnEndEdit(&f.context,7,&edit);
        command.mode=WritebackMode::Append;f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Written&&f.context.range.model->text==L"你好 Hello",
            "delayed own insertion notification does not prevent append");
    }
    {
        Fixture f;auto command=prepare_writeback(f);EditRecord edit;f.context.property.scope.kind=IS_PASSWORD;
        auto sink=f.context.text_sink;sink->OnEndEdit(&f.context,7,&edit);
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Rejected&&f.context.range.model->reads==0,
            "notification revalidation refuses password without reading text");
    }
    {
        Fixture f;auto command=prepare_writeback(f);EditRecord edit;
        f.context.range.model->after_get=[&]{f.keys->OnSetFocus(FALSE);};auto sink=f.context.text_sink;sink->OnEndEdit(&f.context,7,&edit);
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Rejected,
            "reentrant focus loss during notification check cannot resurrect target");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.manager.thread_focus=false;f.manager.foreground_service=GUID_NULL;
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Written&&f.context.range.model->text==L"Hello",
            "mouse writeback uses owned foreground context rather than transient keyboard routing metadata");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.range.model->text=L"再见";
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->writes==0,"changed original range never overwritten even with missing edit notification");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.property.scope.kind=IS_PASSWORD;
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->reads==0,"password rechecked before any writeback text read");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.read_only=true;
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->writes==0,"read-only context cannot be changed by writeback");
    }
    {
        Fixture f;auto command=prepare_writeback(f);Context other;f.manager.document.context=&other;
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Rejected&&other.writes==0,"another focused context never receives old English");
        f.manager.document.context=&f.context;
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.writeback_handler(f.writeback_owner,command);f.keys->OnSetFocus(FALSE);f.keys->OnSetFocus(TRUE);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->writes==0,"lost and regained focus invalidates deferred operation permanently");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.writeback_handler(f.writeback_owner,command);fixture_time+=1001;complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->writes==0,"late async session expires instead of surprising future input");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.writeback_handler(f.writeback_owner,command);f.service->Deactivate();complete_writeback(f);
        check(f.context.range.model->writes==0,"deactivated native service cannot be revived by deferred apply");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.range.model->set_failure=true;
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Unknown&&f.context.range.model->writes==1,"host failure after mutation is explicitly unknown");
        check(f.writeback_handler(f.writeback_owner,command)==WritebackResult::Unknown&&f.context.range.model->writes==1,"unknown result cannot be retried by duplicate packet");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.range.model->get_failure=true;
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->writes==0,"host cannot verify saved range: English writeback stays unavailable");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.range.model->after_get=[&]{f.keys->OnSetFocus(FALSE);};
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->writes==0,"reentrant focus loss during range verification blocks mutation");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.range.model->after_get=[&]{f.context.property.scope.kind=IS_PASSWORD;};
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->writes==0,"sensitive metadata changing during bounded read is rechecked before mutation");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.set_selection_failure=true;
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Written&&f.context.range.model->writes==1,"caret selection failure after successful SetText is still Written");
    }
    {
        Context context;Manager manager;manager.document.context=&context;
        context.range.model=std::make_shared<RangeText>();context.range.model->context=&context;
        context.range.model->text=L"你好 ";context.range.start=0;context.range.end=3;
        ComPtr<ITfRange> saved;context.range.Clone(saved.put());context.range.Collapse(7,TF_ANCHOR_END);
        const auto result=ApplyWriteback(&context,&manager,7,saved.get(),L"你好 ",L"Hello",WritebackMode::Append,false,nullptr,nullptr,[]{return true;});
        check(result==mansur::CommitResult::Written&&context.range.model->text==L"你好 Hello","append does not duplicate existing trailing whitespace");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.range.start=f.context.range.end=0;
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->writes==0&&f.context.range.model->reads==0,"moved caret without host notification rejects before reading source range");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.range.start=0;
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->writes==0,"noncollapsed selection cannot authorize English replacement");
    }
    {
        Fixture f;auto command=prepare_writeback(f);f.context.range.model->after_get=[&]{f.context.range.start=f.context.range.end=0;};
        f.writeback_handler(f.writeback_owner,command);complete_writeback(f);
        check(writeback_status(f,command)==WritebackResult::Rejected&&f.context.range.model->writes==0,"caret moved during verification is rechecked before replacement");
    }
}
int main() {
    try {initialize_dictionary();routing_tests();shift_routing_tests();mixed_language_tests();stability_tests();shell_editor_tests();personal_word_routing_tests();writeback_tests();check(live_objects==0&&mode_windows==0,"all service/edit/endpoint objects released");
        std::cout<<"Key-routing, Shift tap/combination/focus/safety/draft preservation, mode/lifecycle/context-pressure, restricted shell-editor and writeback safety scenarios passed with production callbacks, inert UI and fixed metadata.\n";return 0;
    }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
}
