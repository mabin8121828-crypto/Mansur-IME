// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include "com_ptr.hpp"
#include "module.hpp"
#include "learning_dispatch.hpp"
#include "control_metadata.hpp"
#include <msctf.h>
#include <initguid.h>
#include <inputscope.h>
#include <atomic>
#include <cstdio>
#include <new>

namespace mansur::win::edit {
template<class F> HRESULT boundary(F&& fn) noexcept {
    try {return fn();} catch(const std::bad_alloc&) {return E_OUTOFMEMORY;} catch(...) {return E_FAIL;}
}
inline bool same_identity(IUnknown* left,IUnknown* right) {
    if(!left||!right) return left==right;
    ComPtr<IUnknown> a,b;
    return SUCCEEDED(left->QueryInterface(IID_IUnknown,reinterpret_cast<void**>(a.put())))&&
        SUCCEEDED(right->QueryInterface(IID_IUnknown,reinterpret_cast<void**>(b.put())))&&a.get()==b.get();
}
inline bool compartment_enabled(ITfContext* context,REFGUID key) {
    ComPtr<ITfCompartmentMgr> manager;
    if(FAILED(context->QueryInterface(IID_ITfCompartmentMgr,reinterpret_cast<void**>(manager.put())))) return false;
    ComPtr<ITfCompartment> compartment;
    if(FAILED(manager->GetCompartment(key,compartment.put()))) return false;
    VARIANT value; VariantInit(&value);
    const HRESULT hr=compartment->GetValue(&value);
    const bool enabled=SUCCEEDED(hr)&&((value.vt==VT_I4&&value.lVal!=0)||(value.vt==VT_BOOL&&value.boolVal!=VARIANT_FALSE));
    VariantClear(&value);return enabled;
}
inline bool sensitive_scope(InputScope scope) noexcept {
    return scope==IS_PASSWORD||scope==IS_PRIVATE||scope==IS_NUMERIC_PASSWORD||scope==IS_NUMERIC_PIN||
        scope==IS_ALPHANUMERIC_PIN||scope==IS_ALPHANUMERIC_PIN_SET;
}
enum class MetadataStage : unsigned { NotStarted, Status, Disabled, Selection, Start,
    AppProperty, PropertyValue, ScopeInterface, InputScopes, Allowed, Sensitive, StandardControl };
struct MetadataTrace {
    MetadataStage stage=MetadataStage::NotStarted;
    HRESULT status=E_PENDING,selection=E_PENDING,start=E_PENDING,app_property=E_PENDING,
        property_value=E_PENDING,scope_interface=E_PENDING,input_scopes=E_PENDING;
    ControlTrace control;
};
struct Metadata {bool inspected=false;bool allowed=false;CaretLocation caret;MetadataTrace trace;};
// TestKeyDown is a routing query. A failed query here must not prevent the real
// key callback from obtaining its own lock and producing a precise diagnostic.
// Explicit no-input flags can reject routing; no draft characters are accepted.
inline bool can_route_key(ITfContext* context) {
    TF_STATUS status{};
    if(SUCCEEDED(context->GetStatus(&status))&&(status.dwDynamicFlags&TF_SD_READONLY)!=0) return false;
    return !compartment_enabled(context,GUID_COMPARTMENT_KEYBOARD_DISABLED)&&
        !compartment_enabled(context,GUID_COMPARTMENT_EMPTYCONTEXT);
}
inline std::wstring metadata_failure_notice(bool test,HRESULT request,HRESULT session,bool called,const Metadata& metadata) {
    // Fixed enum/number diagnostics only: never include key codes, text or ranges.
    wchar_t trace[1024]{};
    const auto& t=metadata.trace;
    swprintf_s(trace,L"\nphase=%ls callback=%u stage=%u\nrequest=%08lX session=%08lX\nstatus=%08lX selection=%08lX start=%08lX\nproperty=%08lX value=%08lX\nQI=%08lX scopes=%08lX fallback=%u\nview=%08lX wnd=%08lX mask=%08lX\nclass=%ls style=%08lX",
        test?L"test":L"key",called?1u:0u,static_cast<unsigned>(t.stage),
        static_cast<unsigned long>(request),static_cast<unsigned long>(session),
        static_cast<unsigned long>(t.status),static_cast<unsigned long>(t.selection),static_cast<unsigned long>(t.start),
        static_cast<unsigned long>(t.app_property),static_cast<unsigned long>(t.property_value),
        static_cast<unsigned long>(t.scope_interface),static_cast<unsigned long>(t.input_scopes),static_cast<unsigned>(t.control.result),
        static_cast<unsigned long>(t.control.view),static_cast<unsigned long>(t.control.window),static_cast<unsigned long>(t.control.password),
        t.control.class_name,static_cast<unsigned long>(t.control.style));
    return L"Mansur Next\n暂时无法确认编辑器的输入安全属性，本次按键已交还软件。\n草稿仍保留。诊断代码："+std::wstring(trace);
}
inline HRESULT decode_scope(const VARIANT& value,Metadata& result,bool& sensitive) {
    sensitive=false;
    if(value.vt==VT_EMPTY) return S_OK;
    if(value.vt!=VT_UNKNOWN||!value.punkVal) return E_FAIL;
    ComPtr<ITfInputScope> scopes;
    result.trace.stage=MetadataStage::ScopeInterface;
    const HRESULT queried=result.trace.scope_interface=value.punkVal->QueryInterface(IID_ITfInputScope,reinterpret_cast<void**>(scopes.put()));
    if(FAILED(queried)||!scopes) return FAILED(queried)?queried:E_FAIL;
    InputScope* values=nullptr;UINT count=0;
    result.trace.stage=MetadataStage::InputScopes;
    const HRESULT obtained=result.trace.input_scopes=scopes->GetInputScopes(&values,&count);
    const bool valid=SUCCEEDED(obtained)&&count<=128&&(!count||values);
    if(valid) for(UINT i=0;i<count;++i) sensitive=sensitive||sensitive_scope(values[i]);
    CoTaskMemFree(values);
    return valid?S_OK:(FAILED(obtained)?obtained:E_FAIL);
}
// Reads only selection coordinates and input-scope metadata. No user text is read.
inline HRESULT inspect_context(ITfContext* context,TfEditCookie cookie,Metadata& result,bool standard_edit_only=false) {
    result={};
    result.trace.stage=MetadataStage::Status;
    TF_STATUS status{};
    HRESULT hr=result.trace.status=context->GetStatus(&status);
    if(FAILED(hr)) return hr;
    result.inspected=true;
    result.trace.stage=MetadataStage::Disabled;
    if((status.dwDynamicFlags&TF_SD_READONLY)!=0 ||
        compartment_enabled(context,GUID_COMPARTMENT_KEYBOARD_DISABLED)||
        compartment_enabled(context,GUID_COMPARTMENT_EMPTYCONTEXT)) return S_OK;
    TF_SELECTION selection{}; ULONG fetched=0;
    result.trace.stage=MetadataStage::Selection;
    hr=result.trace.selection=context->GetSelection(cookie,TF_DEFAULT_SELECTION,1,&selection,&fetched);
    ComPtr<ITfRange> range; range.attach(selection.range);
    const bool has_selection=SUCCEEDED(hr)&&fetched==1&&range;
    if(!has_selection) {
        ComPtr<ITfRange> start;
        result.trace.stage=MetadataStage::Start;
        hr=result.trace.start=context->GetStart(cookie,start.put());
        if(FAILED(hr)||!start) {result.inspected=false;return FAILED(hr)?hr:E_FAIL;}
        range=std::move(start);
    }
    ComPtr<ITfReadOnlyProperty> property;
    result.trace.stage=MetadataStage::AppProperty;
    hr=result.trace.app_property=context->GetAppProperty(GUID_PROP_INPUTSCOPE,property.put());
    // S_FALSE means the application does not support this optional property.
    // Its absence is a normal default input scope, not an unsafe query failure.
    if(FAILED(hr)||(hr!=S_FALSE&&!property)) {result.inspected=false;return FAILED(hr)?hr:E_FAIL;}
    if(property) {
        VARIANT value;VariantInit(&value);
        result.trace.stage=MetadataStage::PropertyValue;
        const HRESULT scope_hr=result.trace.property_value=property->GetValue(cookie,range.get(),&value);
        bool sensitive=false;
        HRESULT decoded=scope_hr;
        if(SUCCEEDED(scope_hr)) decoded=decode_scope(value,result,sensitive);
        VariantClear(&value);
        if(scope_hr==E_FAIL||scope_hr==E_NOTIMPL) {
            const auto fallback=inspect_standard_control(context,result.trace.control);
            if(fallback==ControlFallback::PlainEdit) decoded=S_OK;
            else if(fallback==ControlFallback::PasswordStyle||fallback==ControlFallback::MaskedPassword) {
                result.trace.stage=MetadataStage::Sensitive;return S_OK;
            } else if(fallback==ControlFallback::Disabled||fallback==ControlFallback::ReadOnly) {
                result.trace.stage=MetadataStage::Disabled;return S_OK;
            }
        }
        if(FAILED(decoded)) {result.inspected=false;return decoded;}
        if(sensitive) {result.trace.stage=MetadataStage::Sensitive;return S_OK;}
    }
    if(standard_edit_only&&result.trace.control.result!=ControlFallback::PlainEdit) {
        result.trace.stage=MetadataStage::StandardControl;
        if(inspect_standard_control(context,result.trace.control)!=ControlFallback::PlainEdit)return S_OK;
    }
    result.allowed=true;
    result.trace.stage=MetadataStage::Allowed;
    ComPtr<ITfContextView> view;
    if(SUCCEEDED(context->GetActiveView(view.put()))&&view) {
        view->GetWnd(&result.caret.owner);
        ComPtr<ITfRange> caret;
        if(has_selection&&SUCCEEDED(range->Clone(caret.put()))&&caret&&SUCCEEDED(caret->Collapse(cookie,TF_ANCHOR_END))) {
            BOOL clipped=FALSE;
            result.caret.valid=SUCCEEDED(view->GetTextExt(cookie,caret.get(),&result.caret.rect,&clipped))&&!clipped;
        }
    }
    return S_OK;
}
class ReadMetadata final:public ITfEditSession,private ModuleObject {
public:
    explicit ReadMetadata(ITfContext* context,bool standard_edit_only=false):context_(context),standard_edit_only_(standard_edit_only) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) noexcept override {
        if(!out) return E_POINTER;*out=nullptr;
        if(iid!=IID_IUnknown&&iid!=IID_ITfEditSession) return E_NOINTERFACE;
        *out=static_cast<ITfEditSession*>(this);AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override{return ++references_;}
    ULONG STDMETHODCALLTYPE Release() noexcept override {const auto count=--references_;if(!count) delete this;return count;}
    HRESULT STDMETHODCALLTYPE DoEditSession(TfEditCookie cookie) noexcept override {
        called=true;
        return boundary([&]{return inspect_context(context_.get(),cookie,metadata,standard_edit_only_);});
    }
    Metadata metadata;
    bool called=false;
private:
    std::atomic<ULONG> references_{1};ComPtr<ITfContext> context_;
    bool standard_edit_only_=false;
};
class WriteCommit final:public ITfEditSession,private ModuleObject {
public:
    WriteCommit(ITfContext* context,ITfThreadMgr* manager,const Commit& commit,bool standard_edit_only=false,HWND owner=nullptr,HWND editor=nullptr)
        :context_(context),manager_(manager),commit_(commit),standard_edit_only_(standard_edit_only),expected_owner_(owner),expected_editor_(editor) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) noexcept override {
        if(!out) return E_POINTER;*out=nullptr;
        if(iid!=IID_IUnknown&&iid!=IID_ITfEditSession) return E_NOINTERFACE;
        *out=static_cast<ITfEditSession*>(this);AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override{return ++references_;}
    ULONG STDMETHODCALLTYPE Release() noexcept override {const auto count=--references_;if(!count) delete this;return count;}
    HRESULT STDMETHODCALLTYPE DoEditSession(TfEditCookie cookie) noexcept override {
        return boundary([&]() -> HRESULT {
            // Even a misbehaving host calling this object twice cannot write twice.
            if(called_) return E_UNEXPECTED;
            called_=true;
            ComPtr<ITfDocumentMgr> focused;ComPtr<ITfContext> top;
            HRESULT hr=manager_->GetFocus(focused.put());
            if(FAILED(hr)||!focused) {focus_retained=false;return E_FAIL;}
            hr=focused->GetTop(top.put());
            if(FAILED(hr)||!same_identity(top.get(),context_.get())) {focus_retained=false;return E_ABORT;}
            Metadata metadata;
            hr=inspect_context(context_.get(),cookie,metadata,standard_edit_only_);
            if(FAILED(hr)||!metadata.allowed) {focus_retained=false;return E_ACCESSDENIED;}
            if(standard_edit_only_&&expected_editor_&&(metadata.trace.control.owner!=expected_owner_||metadata.trace.control.focused!=expected_editor_)){focus_retained=false;return E_ABORT;}
            // Metadata providers and same-thread standard-control messages may
            // reenter the host. Recheck focus immediately before mutation.
            ComPtr<ITfDocumentMgr> verified_document;ComPtr<ITfContext> verified_context;
            hr=manager_->GetFocus(verified_document.put());
            if(FAILED(hr)||!verified_document||FAILED(verified_document->GetTop(verified_context.put()))||
                !same_identity(verified_context.get(),context_.get())) {focus_retained=false;return E_ABORT;}
            caret=metadata.caret;
            ComPtr<ITfInsertAtSelection> insertion;
            hr=context_->QueryInterface(IID_ITfInsertAtSelection,reinterpret_cast<void**>(insertion.put()));
            if(FAILED(hr)) return hr;
            if(standard_edit_only_&&!check_restricted_focus(metadata))return E_ABORT;
            // After entering the mutation call a failed result cannot prove that
            // the host wrote nothing. Never retry such a request automatically.
            outcome=CommitResult::Unknown;
            ComPtr<ITfRange> inserted;
            hr=insertion->InsertTextAtSelection(cookie,0,commit_.text.data(),static_cast<LONG>(commit_.text.size()),inserted.put());
            if(FAILED(hr)) return hr;
            outcome=CommitResult::Written;
            if(standard_edit_only_) {
                // A label editor may be removed synchronously by its parent
                // after the text mutation. Never move its selection or query
                // caret geometry after the focus/control identity has changed.
                if(!check_restricted_focus(metadata)){caret={};return S_OK;}
            }
            // Preserve only our own insertion for an explicit later English
            // writeback. Failure here never changes successful Chinese input.
            if(commit_.learn&&inserted)inserted->Clone(committed_range.put());
            if(inserted&&SUCCEEDED(inserted->Collapse(cookie,TF_ANCHOR_END))) {
                if(standard_edit_only_&&!check_restricted_focus(metadata)){caret={};return S_OK;}
                TF_SELECTION selection{inserted.get(),{TF_AE_NONE,FALSE}};
                context_->SetSelection(cookie,1,&selection);
                if(standard_edit_only_&&!check_restricted_focus(metadata)){caret={};return S_OK;}
                ComPtr<ITfContextView> view;
                if(SUCCEEDED(context_->GetActiveView(view.put()))&&view) {
                    BOOL clipped=FALSE;RECT next{};
                    if(SUCCEEDED(view->GetTextExt(cookie,inserted.get(),&next,&clipped))&&!clipped) {caret.rect=next;caret.valid=true;}
                }
            }
            return S_OK;
        });
    }
    CommitResult outcome=CommitResult::NotWritten;
    ComPtr<ITfRange> committed_range;
    CaretLocation caret;
    bool focus_retained=true;
private:
    bool check_restricted_focus(const Metadata& metadata) {
        ComPtr<ITfDocumentMgr> document;ComPtr<ITfContext> context;ControlTrace control;
        focus_retained=SUCCEEDED(manager_->GetFocus(document.put()))&&document&&
            SUCCEEDED(document->GetTop(context.put()))&&same_identity(context.get(),context_.get())&&
            inspect_standard_control(context_.get(),control)==ControlFallback::PlainEdit&&
            control.owner==metadata.trace.control.owner&&control.focused==metadata.trace.control.focused;
        if(focus_retained) {
            // Standard-control metadata messages can reenter the host too.
            // Recheck TSF focus after that query, before any further mutation.
            ComPtr<ITfDocumentMgr> after_document;ComPtr<ITfContext> after_context;
            focus_retained=SUCCEEDED(manager_->GetFocus(after_document.put()))&&after_document&&
                SUCCEEDED(after_document->GetTop(after_context.put()))&&same_identity(after_context.get(),context_.get());
        }
        return focus_retained;
    }
    std::atomic<ULONG> references_{1};
    ComPtr<ITfContext> context_;ComPtr<ITfThreadMgr> manager_;Commit commit_;bool called_=false,standard_edit_only_=false;
    HWND expected_owner_=nullptr,expected_editor_=nullptr;
};
}
