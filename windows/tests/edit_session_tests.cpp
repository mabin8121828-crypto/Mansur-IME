// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "edit_sessions.hpp"
#include "ids.hpp"
#include <iostream>
#include <stdexcept>
#include <string>
#include <functional>
#include <memory>

namespace mansur::win {HINSTANCE module_instance=nullptr;std::atomic<long> live_objects{0};std::atomic<long> server_locks{0};}
using namespace mansur::win;
using namespace mansur::win::edit;
void check(bool value,const char* label){if(!value)throw std::runtime_error(label);}
#define UNKNOWN(type) \
 HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override {if(!out)return E_POINTER;*out=nullptr;if(iid!=IID_IUnknown&&iid!=__uuidof(type))return E_NOINTERFACE;*out=static_cast<type*>(this);AddRef();return S_OK;} \
 ULONG STDMETHODCALLTYPE AddRef() override{return 2;} \
 ULONG STDMETHODCALLTYPE Release() override{return 1;}
#define STUB(name,...) HRESULT STDMETHODCALLTYPE name(__VA_ARGS__) override {return E_NOTIMPL;}
struct RangeText {
    ITfContext* context=nullptr;std::wstring text;
    int reads=0,writes=0;ULONG maximum_read=0;
    bool get_failure=false,set_failure=false;
    std::function<void()> after_get,after_set;
};
struct Range final:ITfRange {
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out)override{*out=nullptr;if(iid!=IID_IUnknown&&iid!=IID_ITfRange)return E_NOINTERFACE;*out=this;AddRef();return S_OK;}
    ULONG STDMETHODCALLTYPE AddRef()override{return dynamic?++references:2;}
    ULONG STDMETHODCALLTYPE Release()override{if(!dynamic)return 1;auto n=--references;if(!n)delete this;return n;}
    bool dynamic=false;ULONG references=1;
    std::shared_ptr<RangeText> model;
    std::size_t start=0,end=0;
    int text_reads=0;bool collapse_failure=false;
    std::function<void()> after_collapse;
    HRESULT STDMETHODCALLTYPE GetText(TfEditCookie,DWORD flags,WCHAR* out,ULONG maximum,ULONG* count) override{
        ++text_reads;*count=0;if(!model)return E_FAIL;++model->reads;model->maximum_read=maximum;
        if(model->after_get)model->after_get();if(model->get_failure||flags)return E_FAIL;
        const auto a=std::min(start,model->text.size()),b=std::min(end,model->text.size());
        *count=static_cast<ULONG>(std::min<std::size_t>(maximum,b>=a?b-a:0));std::copy_n(model->text.data()+a,*count,out);return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetText(TfEditCookie,DWORD,const WCHAR* value,LONG size)override{
        if(!model)return E_NOTIMPL;++model->writes;
        if(start>model->text.size()||end<start||end>model->text.size())return E_FAIL;
        model->text.replace(start,end-start,value,static_cast<std::size_t>(size));end=start+size;
        if(model->after_set)model->after_set();return model->set_failure?E_FAIL:S_OK;
    }
    STUB(GetFormattedText,TfEditCookie,IDataObject**)
    STUB(GetEmbedded,TfEditCookie,REFGUID,REFIID,IUnknown**)
    STUB(InsertEmbedded,TfEditCookie,DWORD,IDataObject*)
    STUB(ShiftStart,TfEditCookie,LONG,LONG*,const TF_HALTCOND*)
    STUB(ShiftEnd,TfEditCookie,LONG,LONG*,const TF_HALTCOND*)
    STUB(ShiftStartToRange,TfEditCookie,ITfRange*,TfAnchor)
    STUB(ShiftEndToRange,TfEditCookie,ITfRange*,TfAnchor)
    STUB(ShiftStartRegion,TfEditCookie,TfShiftDir,BOOL*)
    STUB(ShiftEndRegion,TfEditCookie,TfShiftDir,BOOL*)
    STUB(IsEmpty,TfEditCookie,BOOL*)
    HRESULT STDMETHODCALLTYPE Collapse(TfEditCookie,TfAnchor anchor)override{if(after_collapse)after_collapse();if(collapse_failure)return E_FAIL;if(model){if(anchor==TF_ANCHOR_END)start=end;else end=start;}return S_OK;}
    HRESULT STDMETHODCALLTYPE IsEqualStart(TfEditCookie,ITfRange* other,TfAnchor anchor,BOOL* equal)override{
        *equal=FALSE;if(!model)return E_NOTIMPL;auto range=static_cast<Range*>(other);
        *equal=model==range->model&&start==(anchor==TF_ANCHOR_START?range->start:range->end);return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsEqualEnd(TfEditCookie,ITfRange* other,TfAnchor anchor,BOOL* equal)override{
        *equal=FALSE;if(!model)return E_NOTIMPL;auto range=static_cast<Range*>(other);
        *equal=model==range->model&&end==(anchor==TF_ANCHOR_START?range->start:range->end);return S_OK;
    }
    STUB(CompareStart,TfEditCookie,ITfRange*,TfAnchor,LONG*)
    STUB(CompareEnd,TfEditCookie,ITfRange*,TfAnchor,LONG*)
    STUB(AdjustForInsert,TfEditCookie,ULONG,BOOL*)
    STUB(GetGravity,TfGravity*,TfGravity*)
    STUB(SetGravity,TfEditCookie,TfGravity,TfGravity)
    HRESULT STDMETHODCALLTYPE Clone(ITfRange** out)override{if(!model){*out=this;AddRef();return S_OK;}auto value=new Range;value->dynamic=true;value->model=model;value->start=start;value->end=end;*out=value;return S_OK;}
    HRESULT STDMETHODCALLTYPE GetContext(ITfContext** out)override{*out=model?model->context:nullptr;if(*out)(*out)->AddRef();return *out?S_OK:E_NOTIMPL;}
};
struct Scope final:ITfInputScope {
    UNKNOWN(ITfInputScope)
    InputScope kind=IS_DEFAULT;bool failed=false;
    HRESULT STDMETHODCALLTYPE GetInputScopes(InputScope** values,UINT* count)override {
        *values=nullptr;*count=0;if(failed)return E_ACCESSDENIED;
        *values=static_cast<InputScope*>(CoTaskMemAlloc(sizeof(InputScope)));if(!*values)return E_OUTOFMEMORY;
        **values=kind;*count=1;return S_OK;
    }
    STUB(GetPhrase,BSTR**,UINT*)
    STUB(GetRegularExpression,BSTR*)
    STUB(GetSRGS,BSTR*)
    STUB(GetXML,BSTR*)
};
struct Property final:ITfReadOnlyProperty {
    UNKNOWN(ITfReadOnlyProperty)
    Scope scope;
    bool failed=false;
    HRESULT failure_result=E_FAIL;
    STUB(GetType,GUID*)
    STUB(EnumRanges,TfEditCookie,IEnumTfRanges**,ITfRange*)
    HRESULT STDMETHODCALLTYPE GetValue(TfEditCookie,ITfRange*,VARIANT* value)override {
        VariantInit(value);if(failed)return failure_result;
        value->vt=VT_UNKNOWN;value->punkVal=&scope;scope.AddRef();return S_OK;
    }
    STUB(GetContext,ITfContext**)
};
struct View final:ITfContextView {
    UNKNOWN(ITfContextView)
    bool extent_failure=false;
    HWND window=nullptr;
    unsigned extents=0;
    STUB(GetRangeFromPoint,TfEditCookie,const POINT*,DWORD,ITfRange**)
    HRESULT STDMETHODCALLTYPE GetTextExt(TfEditCookie,ITfRange*,RECT* rect,BOOL* clipped)override {
        ++extents;*rect={10,20,11,40};*clipped=FALSE;return extent_failure?E_FAIL:S_OK;
    }
    STUB(GetScreenExt,RECT*)
    HRESULT STDMETHODCALLTYPE GetWnd(HWND* out)override{*out=window;return window?S_OK:E_NOTIMPL;}
};
struct Context final:ITfContext,ITfInsertAtSelection,ITfSource {
    Range range;Property property;View view;
    bool selection_failure=false,start_failure=false,read_only=false,write_failure=false,set_selection_failure=false,scope_unsupported=false;
    bool selection_called=false;int writes=0;std::wstring written;
    bool test_phase=false,status_failure=false,app_property_failure=false;
    HRESULT request_result=S_OK,blocked_session=S_OK;
    int edit_requests=0;
    bool defer_action=false;
    ComPtr<ITfEditSession> queued_action;
    bool text_source_supported=false;
    bool own_write_session=false;
    HRESULT own_session_result=S_OK;
    ComPtr<ITfTextEditSink> text_sink;
    std::function<void()> after_insert;
    std::function<void()> after_selection;
    std::function<void()> after_insertion_query;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out)override {
        *out=nullptr;
        if(iid==IID_IUnknown||iid==IID_ITfContext)*out=static_cast<ITfContext*>(this);
        else if(iid==IID_ITfInsertAtSelection)*out=static_cast<ITfInsertAtSelection*>(this);
        else if(iid==IID_ITfSource&&text_source_supported)*out=static_cast<ITfSource*>(this);
        else return E_NOINTERFACE;AddRef();if(iid==IID_ITfInsertAtSelection&&after_insertion_query)after_insertion_query();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef()override{return 2;}
    ULONG STDMETHODCALLTYPE Release()override{return 1;}
    HRESULT STDMETHODCALLTYPE AdviseSink(REFIID iid,IUnknown* sink,DWORD* cookie)override{
        *cookie=TF_INVALID_COOKIE;if(!text_source_supported||iid!=IID_ITfTextEditSink)return E_NOINTERFACE;
        ComPtr<ITfTextEditSink> value;auto hr=sink->QueryInterface(iid,reinterpret_cast<void**>(value.put()));
        if(FAILED(hr))return hr;text_sink=std::move(value);*cookie=66;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnadviseSink(DWORD cookie)override{if(cookie!=66)return E_INVALIDARG;text_sink={};return S_OK;}
    HRESULT STDMETHODCALLTYPE RequestEditSession(TfClientId,ITfEditSession* session,DWORD flags,HRESULT* out)override {
        ++edit_requests;*out=E_PENDING;
        if(FAILED(request_result))return request_result;
        if(test_phase){*out=TF_E_SYNCHRONOUS;return S_OK;}
        if(FAILED(blocked_session)){*out=blocked_session;return S_OK;}
        if(defer_action&&!(flags&TF_ES_SYNC)){queued_action=ComPtr<ITfEditSession>(session);*out=TF_S_ASYNC;return S_OK;}
        *out=session->DoEditSession(7);return S_OK;
    }
    HRESULT STDMETHODCALLTYPE InWriteSession(TfClientId,BOOL* own)override{*own=own_write_session;return own_session_result;}
    HRESULT STDMETHODCALLTYPE GetSelection(TfEditCookie,ULONG,ULONG,TF_SELECTION* selection,ULONG* fetched)override {
        *selection={};*fetched=0;if(selection_failure)return E_FAIL;
        selection->range=&range;range.AddRef();*fetched=1;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetSelection(TfEditCookie,ULONG,const TF_SELECTION*)override{selection_called=true;if(after_selection)after_selection();return set_selection_failure?E_FAIL:S_OK;}
    HRESULT STDMETHODCALLTYPE GetStart(TfEditCookie,ITfRange** out)override{*out=nullptr;if(start_failure)return E_FAIL;*out=&range;range.AddRef();return S_OK;}
    STUB(GetEnd,TfEditCookie,ITfRange**)
    HRESULT STDMETHODCALLTYPE GetActiveView(ITfContextView** out)override{*out=&view;view.AddRef();return S_OK;}
    STUB(EnumViews,IEnumTfContextViews**)
    HRESULT STDMETHODCALLTYPE GetStatus(TF_STATUS* status)override{*status={};if(status_failure)return E_ACCESSDENIED;if(read_only)status->dwDynamicFlags=TF_SD_READONLY;return S_OK;}
    STUB(GetProperty,REFGUID,ITfProperty**)
    HRESULT STDMETHODCALLTYPE GetAppProperty(REFGUID,ITfReadOnlyProperty** out)override{*out=nullptr;if(app_property_failure)return E_ACCESSDENIED;if(scope_unsupported)return S_FALSE;*out=&property;property.AddRef();return S_OK;}
    STUB(TrackProperties,const GUID**,ULONG,const GUID**,ULONG,ITfReadOnlyProperty**)
    STUB(EnumProperties,IEnumTfProperties**)
    STUB(GetDocumentMgr,ITfDocumentMgr**)
    STUB(CreateRangeBackup,TfEditCookie,ITfRange*,ITfRangeBackup**)
    HRESULT STDMETHODCALLTYPE InsertTextAtSelection(TfEditCookie,DWORD,const WCHAR* text,LONG count,ITfRange** out)override {
        *out=nullptr;++writes;written.assign(text,static_cast<std::size_t>(count));
        if(range.model){range.model->text=written;range.start=0;range.end=written.size();}
        if(write_failure)return E_FAIL;*out=&range;range.AddRef();if(after_insert)after_insert();return S_OK;
    }
    STUB(InsertEmbeddedAtSelection,TfEditCookie,DWORD,IDataObject*,ITfRange**)
};
struct Document final:ITfDocumentMgr {
    UNKNOWN(ITfDocumentMgr)
    ITfContext* context=nullptr;
    STUB(CreateContext,TfClientId,DWORD,IUnknown*,ITfContext**,TfEditCookie*)
    STUB(Push,ITfContext*)
    STUB(Pop,DWORD)
    HRESULT STDMETHODCALLTYPE GetTop(ITfContext** out)override{*out=context;if(context)context->AddRef();return context?S_OK:E_FAIL;}
    STUB(GetBase,ITfContext**)
    STUB(EnumContexts,IEnumTfContexts**)
};
struct KeyboardCompartment final:ITfCompartment,ITfSource {
    LONG value=0;HRESULT get_result=S_OK,set_result=S_OK,advise_result=S_OK;
    unsigned writes=0,advises=0,unadvises=0;
    ComPtr<ITfCompartmentEventSink> sink;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out)override {
        *out=nullptr;if(iid==IID_IUnknown||iid==IID_ITfCompartment)*out=static_cast<ITfCompartment*>(this);
        else if(iid==IID_ITfSource)*out=static_cast<ITfSource*>(this);else return E_NOINTERFACE;AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef()override{return 2;}
    ULONG STDMETHODCALLTYPE Release()override{return 1;}
    HRESULT STDMETHODCALLTYPE SetValue(TfClientId,const VARIANT* next)override {
        if(FAILED(set_result))return set_result;++writes;value=next->lVal;
        auto target=sink;if(target)target->OnChange(GUID_COMPARTMENT_KEYBOARD_OPENCLOSE);return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetValue(VARIANT* out)override {VariantInit(out);if(FAILED(get_result))return get_result;out->vt=VT_I4;out->lVal=value;return S_OK;}
    HRESULT STDMETHODCALLTYPE AdviseSink(REFIID iid,IUnknown* target,DWORD* cookie)override {
        *cookie=TF_INVALID_COOKIE;if(FAILED(advise_result))return advise_result;
        if(iid!=IID_ITfCompartmentEventSink)return E_NOINTERFACE;
        ComPtr<ITfCompartmentEventSink> subscribed;
        auto hr=target->QueryInterface(iid,reinterpret_cast<void**>(subscribed.put()));if(FAILED(hr))return hr;
        sink=std::move(subscribed);*cookie=25;++advises;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnadviseSink(DWORD cookie)override {if(cookie!=25)return E_INVALIDARG;sink={};++unadvises;return S_OK;}
};
struct Manager final:ITfThreadMgr,ITfKeystrokeMgr,ITfSource,ITfCompartmentMgr {
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out)override {
        *out=nullptr;
        if(iid==IID_IUnknown||iid==IID_ITfThreadMgr)*out=static_cast<ITfThreadMgr*>(this);
        else if(iid==IID_ITfKeystrokeMgr)*out=static_cast<ITfKeystrokeMgr*>(this);
          else if(iid==IID_ITfSource)*out=static_cast<ITfSource*>(this);
          else if(iid==IID_ITfCompartmentMgr&&compartment_supported)*out=static_cast<ITfCompartmentMgr*>(this);
        else return E_NOINTERFACE;AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef()override{return 2;}
    ULONG STDMETHODCALLTYPE Release()override{return 1;}
    Document document;
    KeyboardCompartment keyboard;
    bool thread_focus=true,compartment_supported=true;
    CLSID foreground_service=kTextService;
    HRESULT STDMETHODCALLTYPE GetCompartment(REFGUID guid,ITfCompartment** out)override {
        *out=nullptr;if(guid!=GUID_COMPARTMENT_KEYBOARD_OPENCLOSE)return E_INVALIDARG;*out=&keyboard;keyboard.AddRef();return S_OK;
    }
    STUB(ClearCompartment,TfClientId,REFGUID)
    STUB(EnumCompartments,IEnumGUID**)
    STUB(Activate,TfClientId*)
    STUB(Deactivate,void)
    STUB(CreateDocumentMgr,ITfDocumentMgr**)
    STUB(EnumDocumentMgrs,IEnumTfDocumentMgrs**)
    HRESULT STDMETHODCALLTYPE GetFocus(ITfDocumentMgr** out)override{*out=&document;document.AddRef();return S_OK;}
    STUB(SetFocus,ITfDocumentMgr*)
    STUB(AssociateFocus,HWND,ITfDocumentMgr*,ITfDocumentMgr**)
    HRESULT STDMETHODCALLTYPE IsThreadFocus(BOOL* out)override{*out=thread_focus?TRUE:FALSE;return S_OK;}
    STUB(GetFunctionProvider,REFCLSID,ITfFunctionProvider**)
    STUB(EnumFunctionProviders,IEnumTfFunctionProviders**)
    STUB(GetGlobalCompartment,ITfCompartmentMgr**)
    HRESULT STDMETHODCALLTYPE AdviseSink(REFIID,IUnknown*,DWORD* cookie)override{*cookie=12;return S_OK;}
    HRESULT STDMETHODCALLTYPE UnadviseSink(DWORD)override{return S_OK;}
    HRESULT STDMETHODCALLTYPE AdviseKeyEventSink(TfClientId,ITfKeyEventSink*,BOOL)override{return S_OK;}
    HRESULT STDMETHODCALLTYPE UnadviseKeyEventSink(TfClientId)override{return S_OK;}
    HRESULT STDMETHODCALLTYPE GetForeground(CLSID* out)override{*out=foreground_service;return S_OK;}
    STUB(TestKeyDown,WPARAM,LPARAM,BOOL*)
    STUB(TestKeyUp,WPARAM,LPARAM,BOOL*)
    STUB(KeyDown,WPARAM,LPARAM,BOOL*)
    STUB(KeyUp,WPARAM,LPARAM,BOOL*)
    STUB(GetPreservedKey,ITfContext*,const TF_PRESERVEDKEY*,GUID*)
    STUB(IsPreservedKey,REFGUID,const TF_PRESERVEDKEY*,BOOL*)
    STUB(PreserveKey,TfClientId,REFGUID,const TF_PRESERVEDKEY*,const WCHAR*,ULONG)
    STUB(UnpreserveKey,REFGUID,const TF_PRESERVEDKEY*)
    STUB(SetPreservedKeyDescription,REFGUID,const WCHAR*,ULONG)
    STUB(GetPreservedKeyDescription,REFGUID,BSTR*)
    STUB(SimulatePreservedKey,ITfContext*,REFGUID,BOOL*)
};
struct FakeControlApi final:ControlMetadataApi {
    HWND focused=reinterpret_cast<HWND>(1);
    bool same_thread=true,related=true,style_valid=true,class_valid=true,masked=false,changed=false;
    DWORD window_style=0x50000104;
    HRESULT password_result=S_OK;
    std::wstring window_class=L"RichEditD2DPT";
    mutable unsigned sends=0;
    HWND focus() const noexcept override{return changed&&sends?reinterpret_cast<HWND>(3):focused;}
    bool belongs_to_thread(HWND) const noexcept override{return same_thread;}
    bool child_of(HWND,HWND) const noexcept override{return related;}
    bool class_name(HWND,wchar_t* buffer,int capacity) const noexcept override {
        if(!class_valid||window_class.size()>=static_cast<std::size_t>(capacity))return false;
        wcscpy_s(buffer,static_cast<std::size_t>(capacity),window_class.c_str());return true;
    }
    bool style(HWND,DWORD& value) const noexcept override{value=window_style;return style_valid;}
    HRESULT password_character(HWND,bool& value) const noexcept override{++sends;value=masked;return password_result;}
};
FakeControlApi fake_control_api;
namespace mansur::win::edit {const ControlMetadataApi& metadata_test_api() noexcept{return fake_control_api;}}
void control_fallback_tests() {
    unsigned passed=0;
    auto run=[&](const char* label,const auto& adjust,bool allowed,ControlFallback expected) {
        fake_control_api=FakeControlApi{};
        Context context;context.property.failed=true;context.view.window=reinterpret_cast<HWND>(1);
        adjust(context,fake_control_api);Metadata metadata;
        const HRESULT hr=inspect_context(&context,7,metadata);
        check(metadata.allowed==allowed,label);
        check(metadata.trace.control.result==expected,"specific control fallback diagnosis");
        check(!allowed||SUCCEEDED(hr),"successful control metadata permits input");
        check(context.range.text_reads==0&&context.writes==0,"metadata fallback cannot read/write text");
        if(expected==ControlFallback::NotAttempted)check(fake_control_api.sends==0,"other failure or known sensitive scope never invokes fallback");
        ++passed;
    };
    run("documented RichEditD2DPT metadata accepted",[](auto&,auto&){},true,ControlFallback::PlainEdit);
    run("E_NOTIMPL uses same limited metadata fallback",[](auto& c,auto&){c.property.failure_result=E_NOTIMPL;},true,ControlFallback::PlainEdit);
    run("standard Edit accepted",[](auto&,auto& a){a.window_class=L"Edit";},true,ControlFallback::PlainEdit);
    run("standard RichEdit50 accepted",[](auto&,auto& a){a.window_class=L"RICHEDIT50W";},true,ControlFallback::PlainEdit);
    run("focused child of context view accepted",[](auto& c,auto&){c.view.window=reinterpret_cast<HWND>(2);},true,ControlFallback::PlainEdit);
    run("unrelated focus rejected",[](auto& c,auto& a){c.view.window=reinterpret_cast<HWND>(2);a.related=false;},false,ControlFallback::OutsideView);
    run("missing focus rejected",[](auto&,auto& a){a.focused=nullptr;},false,ControlFallback::NoFocus);
    run("another input thread rejected",[](auto&,auto& a){a.same_thread=false;},false,ControlFallback::DifferentThread);
    check(fake_control_api.sends==0,"cross-thread password message never sent");
    run("unknown control rejected",[](auto&,auto& a){a.window_class=L"SomeCustomEditor";},false,ControlFallback::UnknownClass);
    run("RichEdit 1.0 without password message rejected",[](auto&,auto& a){a.window_class=L"RichEdit";},false,ControlFallback::UnknownClass);
    run("disabled style rejected",[](auto&,auto& a){a.window_style|=WS_DISABLED;},false,ControlFallback::Disabled);
    run("RichEdit explicit NOIME style rejected",[](auto&,auto& a){a.window_style|=0x00080000;},false,ControlFallback::Disabled);
    run("readonly style rejected",[](auto&,auto& a){a.window_style|=ES_READONLY;},false,ControlFallback::ReadOnly);
    run("password style rejected",[](auto&,auto& a){a.window_style|=ES_PASSWORD;},false,ControlFallback::PasswordStyle);
    check(fake_control_api.sends==0,"known password does not issue a further query");
    run("masked edit rejected even without style",[](auto&,auto& a){a.masked=true;},false,ControlFallback::MaskedPassword);
    run("password query failure rejected",[](auto&,auto& a){a.password_result=HRESULT_FROM_WIN32(ERROR_TIMEOUT);},false,ControlFallback::PasswordQueryFailed);
    run("focus changing during query rejected",[](auto&,auto& a){a.changed=true;},false,ControlFallback::Changed);
    run("style query failure rejected",[](auto&,auto& a){a.style_valid=false;},false,ControlFallback::StyleUnavailable);
    run("view cannot supply an HWND rejected",[](auto& c,auto&){c.view.window=nullptr;},false,ControlFallback::NoViewWindow);
    for(const HRESULT error:{E_ACCESSDENIED,E_OUTOFMEMORY,TF_E_NOLOCK,TF_E_DISCONNECTED})
        run("other HRESULT cannot enable fallback",[&](auto& c,auto&){c.property.failure_result=error;},false,ControlFallback::NotAttempted);
    for(const auto scope:{IS_PASSWORD,IS_PRIVATE,IS_NUMERIC_PASSWORD,IS_NUMERIC_PIN,IS_ALPHANUMERIC_PIN,IS_ALPHANUMERIC_PIN_SET})
        run("explicit sensitive scope always wins",[&](auto& c,auto&){c.property.failed=false;c.property.scope.kind=scope;},false,ControlFallback::NotAttempted);
    fake_control_api=FakeControlApi{};
    check(passed==29,"expected control-metadata scenario count");
}
void metadata_tests() {
    Context context;Metadata metadata;
    check(SUCCEEDED(inspect_context(&context,7,metadata))&&metadata.allowed&&metadata.caret.valid,"normal context allowed");
    context.view.extent_failure=true;
    check(SUCCEEDED(inspect_context(&context,7,metadata))&&metadata.allowed&&!metadata.caret.valid,"caret failure does not disable typing");
    context.selection_failure=true;
    check(SUCCEEDED(inspect_context(&context,7,metadata))&&metadata.allowed&&!metadata.caret.valid,"selection failure uses metadata-only start range");
    context.property.scope.kind=IS_PASSWORD;
    check(SUCCEEDED(inspect_context(&context,7,metadata))&&metadata.inspected&&!metadata.allowed,"password still rejected without selection");
    context.property.scope.kind=IS_PRIVATE;
    check(SUCCEEDED(inspect_context(&context,7,metadata))&&!metadata.allowed,"private field rejected");
    context.property.scope.kind=IS_NUMERIC_PIN;
    check(SUCCEEDED(inspect_context(&context,7,metadata))&&!metadata.allowed,"PIN rejected");
    context.property.scope.kind=IS_DEFAULT;context.start_failure=true;
    check(FAILED(inspect_context(&context,7,metadata))&&!metadata.inspected&&!metadata.allowed,"unreadable safety metadata not silently trusted");
    context.start_failure=false;context.selection_failure=false;context.property.failed=true;
    check(FAILED(inspect_context(&context,7,metadata))&&!metadata.allowed,"failed scope query rejected");
    context.property.failed=false;context.scope_unsupported=true;
    check(SUCCEEDED(inspect_context(&context,7,metadata))&&metadata.inspected&&metadata.allowed,"unsupported optional scope defaults to ordinary input");
    check(context.range.text_reads==0,"no application text read");
}
void write_tests() {
    mansur::Commit commit{1,L"你好。",true};
    {
        Context context;Manager manager;manager.document.context=&context;
        auto operation=new WriteCommit(&context,&manager,commit);
        check(SUCCEEDED(operation->DoEditSession(7))&&operation->outcome==mansur::CommitResult::Written&&context.writes==1,"one confirmed insertion");
        check(operation->DoEditSession(7)==E_UNEXPECTED&&context.writes==1,"duplicate callback cannot insert twice");
        check(context.written==commit.text&&context.range.text_reads==0,"only own commit written");operation->Release();
    }
    {
        Context context;context.set_selection_failure=true;Manager manager;manager.document.context=&context;
        auto operation=new WriteCommit(&context,&manager,commit);
        check(SUCCEEDED(operation->DoEditSession(7))&&operation->outcome==mansur::CommitResult::Written&&context.writes==1,"SetSelection failure does not undo confirmed write");operation->Release();
    }
    {
        Context context;context.write_failure=true;Manager manager;manager.document.context=&context;
        auto operation=new WriteCommit(&context,&manager,commit);
        check(FAILED(operation->DoEditSession(7))&&operation->outcome==mansur::CommitResult::Unknown&&context.writes==1,"failed mutation result unknown and not retried");operation->Release();
    }
    {
        Context context,other;Manager manager;manager.document.context=&other;
        auto operation=new WriteCommit(&context,&manager,commit);
        check(FAILED(operation->DoEditSession(7))&&operation->outcome==mansur::CommitResult::NotWritten&&context.writes==0&&other.writes==0,"focus mismatch cannot write to another context");operation->Release();
    }
    {
        Context context;context.property.scope.kind=IS_PASSWORD;Manager manager;manager.document.context=&context;
        auto operation=new WriteCommit(&context,&manager,commit);
        check(FAILED(operation->DoEditSession(7))&&operation->outcome==mansur::CommitResult::NotWritten&&context.writes==0,"password rechecked immediately before write");operation->Release();
    }
    {
        Context context;context.read_only=true;Manager manager;manager.document.context=&context;
        auto operation=new WriteCommit(&context,&manager,commit);
        check(FAILED(operation->DoEditSession(7))&&operation->outcome==mansur::CommitResult::NotWritten&&context.writes==0,"read-only context not modified");operation->Release();
    }
}
#ifndef MANSUR_KEY_ROUTING_TEST
int main() {
    try {metadata_tests();write_tests();control_fallback_tests();check(live_objects==0,"edit sessions all released");
        std::cout<<"17 existing simulated-host checks and 29 standard-control metadata scenarios passed. No real application, registration, UI, clipboard or model.\n";return 0;
    }catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
}
#endif
