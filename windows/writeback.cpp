// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "writeback.hpp"
#include "edit_sessions.hpp"
#include <algorithm>
#include <cstring>
#include <cwctype>

namespace mansur::win {
bool ValidWritebackToken(const std::string& value) noexcept {
    return value.size()==32&&std::all_of(value.begin(),value.end(),[](char c){return (c>='0'&&c<='9')||(c>='a'&&c<='f');});
}
bool ParseWritebackPacket(const void* bytes,std::size_t size,WritebackCommand& out) noexcept {
    try {
        if(!bytes||size<kWritebackHeaderBytes||size>kWritebackHeaderBytes+8192)return false;
        std::uint32_t fields[4]{};std::memcpy(fields,bytes,sizeof(fields));
        if(fields[0]!=1||fields[1]<1||fields[1]>3||fields[2]>1||fields[3]>4096||
            size!=kWritebackHeaderBytes+2ull*fields[3]||(fields[1]!=2&&fields[3]))return false;
        WritebackCommand command;command.operation=static_cast<WritebackOperation>(fields[1]);
        command.mode=static_cast<WritebackMode>(fields[2]);
        command.token.assign(static_cast<const char*>(bytes)+16,32);
        if(!ValidWritebackToken(command.token))return false;
        command.text.resize(fields[3]);
        if(fields[3])std::memcpy(command.text.data(),static_cast<const char*>(bytes)+48,fields[3]*2);
        if(fields[1]==2&&command.text.empty())return false;
        for(std::size_t i=0;i<command.text.size();++i) {
            const auto c=static_cast<unsigned>(command.text[i]);
            if(!c||(c<32&&c!=9&&c!=10&&c!=13))return false;
            if(c>=0xd800&&c<=0xdbff) {if(++i==command.text.size()||command.text[i]<0xdc00||command.text[i]>0xdfff)return false;}
            else if(c>=0xdc00&&c<=0xdfff)return false;
        }
        out=std::move(command);return true;
    }catch(...){return false;}
}
std::string NewWritebackToken() {
    GUID guid{};if(FAILED(CoCreateGuid(&guid)))return {};
    static constexpr char digits[]="0123456789abcdef";std::string token;token.reserve(32);
    const auto bytes=reinterpret_cast<const unsigned char*>(&guid);
    for(std::size_t i=0;i<sizeof(GUID);++i) {const auto c=bytes[i];token+=digits[c>>4];token+=digits[c&15];}
    return token;
}
HRESULT WritebackEditSink::QueryInterface(REFIID iid,void** out) noexcept {
    if(!out)return E_POINTER;*out=nullptr;if(iid!=IID_IUnknown&&iid!=IID_ITfTextEditSink)return E_NOINTERFACE;
    *out=static_cast<ITfTextEditSink*>(this);AddRef();return S_OK;
}
ULONG WritebackEditSink::AddRef() noexcept{return ++references_;}
ULONG WritebackEditSink::Release() noexcept{const auto count=--references_;if(!count)delete this;return count;}
HRESULT WritebackEditSink::OnEndEdit(ITfContext* context,TfEditCookie cookie,ITfEditRecord* record) noexcept {
    ComPtr<ITfTextEditSink> lifetime(this);
    try {
        auto trace=trace_;
        BOOL own=FALSE;
        // Our successful commit triggers this notification after DoEditSession.
        // It is not a subsequent edit. ApplyWriteback independently checks the
        // saved text, metadata and selection before any later mutation.
        if(client_!=TF_CLIENTID_NULL&&context&&SUCCEEDED(context->InWriteSession(client_,&own))&&own) {
            if(trace)trace(20,S_OK);return S_OK;
        }
        bool changed=!record,inspectable=record!=nullptr;BOOL selection=FALSE;
        if(!record&&trace)trace(7,E_POINTER);
        if(record) {
            const auto selection_hr=record->GetSelectionStatus(&selection);
            inspectable=SUCCEEDED(selection_hr);
            if(!inspectable&&trace)trace(8,selection_hr);
            changed=!inspectable||selection;
            if(inspectable) {
                ComPtr<IEnumTfRanges> ranges;
                const GUID* properties[]={&GUID_PROP_INPUTSCOPE};
                const auto updates_hr=record->GetTextAndPropertyUpdates(TF_GTP_INCL_TEXT,properties,1,ranges.put());
                if(FAILED(updates_hr)||!ranges){changed=true;inspectable=false;if(trace)trace(9,updates_hr);}
                else {ComPtr<ITfRange> first;ULONG fetched=0;const auto hr=ranges->Next(1,first.put(),&fetched);inspectable=SUCCEEDED(hr);changed=changed||!inspectable||fetched!=0;if(!inspectable&&trace)trace(10,hr);}
            }
        }
        // Hosts may emit delayed insertion/selection synchronisation. A reported
        // update is not proof our saved text or caret changed. Revalidate ONLY
        // our own bounded insertion using this read-only edit cookie.
        auto unchanged=unchanged_;
        if(changed&&inspectable&&unchanged&&unchanged(context,cookie))changed=false;
        auto invalidate=invalidate_;if(changed&&invalidate)invalidate();
    }catch(...){try {auto invalidate=invalidate_;if(invalidate)invalidate();}catch(...) {}}
    return S_OK;
}
bool OwnWritebackRangeUnchanged(ITfContext* context,TfEditCookie cookie,ITfRange* original_range,
    const std::wstring& original,bool restricted,HWND owner,HWND editor,WritebackProbe* probe) {
    auto mark=[&](std::uint32_t stage,HRESULT result=S_OK){if(probe){probe->stage=stage;probe->result=result;}};
    mark(11);
    if(!context||!original_range||original.empty()||original.size()>2048)return false;
    edit::Metadata metadata;
    const auto metadata_hr=edit::inspect_context(context,cookie,metadata,restricted);mark(1,metadata_hr);
    if(FAILED(metadata_hr)||!metadata.inspected||!metadata.allowed||
        (restricted&&(metadata.trace.control.owner!=owner||metadata.trace.control.focused!=editor)))return false;
    ComPtr<ITfContext> range_context;
    const auto context_hr=original_range->GetContext(range_context.put());mark(2,context_hr);
    if(FAILED(context_hr)||!edit::same_identity(range_context.get(),context))return false;
    auto matches=[&](std::uint32_t stage) {
        TF_SELECTION selection{};ULONG fetched=0;ComPtr<ITfRange> current;
        const auto hr=context->GetSelection(cookie,TF_DEFAULT_SELECTION,1,&selection,&fetched);current.attach(selection.range);
        mark(stage,hr);
        BOOL start=FALSE,end=FALSE;
        return SUCCEEDED(hr)&&fetched==1&&current&&
            SUCCEEDED(current->IsEqualStart(cookie,original_range,TF_ANCHOR_END,&start))&&start&&
            SUCCEEDED(current->IsEqualEnd(cookie,original_range,TF_ANCHOR_END,&end))&&end;
    };
    if(!matches(3))return false;
    std::vector<wchar_t> text(original.size()+1);ULONG count=0;
    const auto text_hr=original_range->GetText(cookie,0,text.data(),static_cast<ULONG>(text.size()),&count);mark(4,text_hr);
    if(FAILED(text_hr)||
        count!=original.size()||!std::equal(original.begin(),original.end(),text.begin()))return false;
    edit::Metadata final_metadata;
    const auto final_hr=edit::inspect_context(context,cookie,final_metadata,restricted);mark(5,final_hr);
    const bool same=SUCCEEDED(final_hr)&&final_metadata.inspected&&final_metadata.allowed&&
        (!restricted||(final_metadata.trace.control.owner==owner&&final_metadata.trace.control.focused==editor))&&matches(6);
    if(same)mark(0);return same;
}
CommitResult ApplyWriteback(ITfContext* context,ITfThreadMgr* manager,TfEditCookie cookie,ITfRange* original_range,
    const std::wstring& original,const std::wstring& english,WritebackMode mode,
    bool restricted,HWND owner,HWND editor,const std::function<bool()>& guard) {
    if(!context||!manager||!original_range||original.empty()||original.size()>2048||english.empty()||english.size()>4096||!guard())return CommitResult::NotWritten;
    auto focused=[&] {
        ComPtr<ITfDocumentMgr> document;ComPtr<ITfContext> top;
        return guard()&&SUCCEEDED(manager->GetFocus(document.put()))&&document&&
            SUCCEEDED(document->GetTop(top.put()))&&edit::same_identity(top.get(),context)&&guard();
    };
    if(!focused())return CommitResult::NotWritten;
    edit::Metadata metadata;
    if(FAILED(edit::inspect_context(context,cookie,metadata,restricted))||!metadata.inspected||!metadata.allowed||
        (restricted&&(metadata.trace.control.owner!=owner||metadata.trace.control.focused!=editor))||!focused())return CommitResult::NotWritten;
    ComPtr<ITfRange> range;ComPtr<ITfContext> range_context;
    if(FAILED(original_range->Clone(range.put()))||!range||FAILED(range->GetContext(range_context.put()))||
        !edit::same_identity(range_context.get(),context)||!focused())return CommitResult::NotWritten;
    auto selection_matches=[&] {
        TF_SELECTION selection{};ULONG fetched=0;ComPtr<ITfRange> current;
        const auto hr=context->GetSelection(cookie,TF_DEFAULT_SELECTION,1,&selection,&fetched);
        current.attach(selection.range);
        if(FAILED(hr)||fetched!=1||!current||!focused())return false;
        BOOL start=FALSE,end=FALSE;
        if(FAILED(current->IsEqualStart(cookie,range.get(),TF_ANCHOR_END,&start))||!start||!focused())return false;
        return SUCCEEDED(current->IsEqualEnd(cookie,range.get(),TF_ANCHOR_END,&end))&&end&&focused();
    };
    // Even if an application omits an edit notification, a moved/selected
    // caret cannot authorize replacement of the earlier committed sentence.
    if(!selection_matches())return CommitResult::NotWritten;
    // No IGNOREEND: this can never extend into surrounding application text.
    std::vector<wchar_t> found(original.size()+1);ULONG count=0;
    if(FAILED(range->GetText(cookie,0,found.data(),static_cast<ULONG>(found.size()),&count))||
        count!=original.size()||!std::equal(original.begin(),original.end(),found.begin())||!focused())return CommitResult::NotWritten;
    std::wstring replacement=english;
    if(mode==WritebackMode::Append) {
        if(!std::iswspace(original.back()))replacement.insert(replacement.begin(),L' ');
        if(FAILED(range->Collapse(cookie,TF_ANCHOR_END))||!focused())return CommitResult::NotWritten;
    }
    if(restricted) {
        edit::ControlTrace control;
        if(edit::inspect_standard_control(context,control)!=edit::ControlFallback::PlainEdit||control.owner!=owner||control.focused!=editor||!focused())return CommitResult::NotWritten;
    }
    // A metadata provider can reenter while the bounded range is read. Never
    // rely solely on the safety state observed before that host call.
    edit::Metadata final_metadata;
    if(FAILED(edit::inspect_context(context,cookie,final_metadata,restricted))||!final_metadata.inspected||!final_metadata.allowed||
        (restricted&&(final_metadata.trace.control.owner!=owner||final_metadata.trace.control.focused!=editor)))return CommitResult::NotWritten;
    if(!selection_matches()||!focused())return CommitResult::NotWritten;
    // After invoking a host mutation, even a failure is not proof of no write.
    if(FAILED(range->SetText(cookie,0,replacement.data(),static_cast<LONG>(replacement.size()))))return CommitResult::Unknown;
    try {
        if(focused()&&SUCCEEDED(range->Collapse(cookie,TF_ANCHOR_END))&&focused()) {
            TF_SELECTION selection{range.get(),{TF_AE_NONE,FALSE}};context->SetSelection(cookie,1,&selection);
        }
    }catch(...) { /* Successful text mutation remains Written if caret cleanup fails. */ }
    return CommitResult::Written;
}
}
