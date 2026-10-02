// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include "com_ptr.hpp"
#include "module.hpp"
#include "mansur/core.hpp"
#include <msctf.h>
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace mansur::win {
inline constexpr ULONG_PTR kWritebackPacketTag=0x4D4E5752;
inline constexpr std::size_t kWritebackHeaderBytes=48;
enum class WritebackOperation:std::uint32_t {Query=1,Apply=2,Status=3};
enum class WritebackMode:std::uint32_t {Replace=0,Append=1};
enum class WritebackResult:std::uint32_t {Unavailable=0,Ready=1,Written=2,Rejected=3,Unknown=4};
struct WritebackCommand {
    WritebackOperation operation=WritebackOperation::Query;
    WritebackMode mode=WritebackMode::Replace;
    std::string token;
    std::wstring text;
};
bool ParseWritebackPacket(const void*,std::size_t,WritebackCommand&) noexcept;
bool ValidWritebackToken(const std::string&) noexcept;
std::string NewWritebackToken();

// One native apartment owns this object. It has no user text in diagnostics.
class WritebackEditSink final:public ITfTextEditSink,private ModuleObject {
public:
    explicit WritebackEditSink(std::function<void()> invalidate,
        std::function<bool(ITfContext*,TfEditCookie)> unchanged={},TfClientId client=TF_CLIENTID_NULL,
        std::function<void(std::uint32_t,HRESULT)> trace={}):invalidate_(std::move(invalidate)),unchanged_(std::move(unchanged)),client_(client),trace_(std::move(trace)){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID,void**) noexcept override;
    ULONG STDMETHODCALLTYPE AddRef() noexcept override;
    ULONG STDMETHODCALLTYPE Release() noexcept override;
    HRESULT STDMETHODCALLTYPE OnEndEdit(ITfContext*,TfEditCookie,ITfEditRecord*) noexcept override;
    void Detach() noexcept {invalidate_={};unchanged_={};trace_={};}
private:
    std::atomic<ULONG> references_{1};
    std::function<void()> invalidate_;
    std::function<bool(ITfContext*,TfEditCookie)> unchanged_;
    TfClientId client_=TF_CLIENTID_NULL;
    std::function<void(std::uint32_t,HRESULT)> trace_;
};

struct WritebackProbe {std::uint32_t stage=0;HRESULT result=S_OK;};
bool OwnWritebackRangeUnchanged(ITfContext*,TfEditCookie,ITfRange*,const std::wstring&,
    bool standard_edit_only,HWND owner,HWND editor,WritebackProbe* probe=nullptr);

// Verifies and edits ONLY the range returned for our own prior insertion. The
// guard is rechecked after reentrant host calls and immediately before SetText.
CommitResult ApplyWriteback(ITfContext*,ITfThreadMgr*,TfEditCookie,ITfRange*,
    const std::wstring& original,const std::wstring& english,WritebackMode,
    bool standard_edit_only,HWND owner,HWND editor,const std::function<bool()>& guard);
}
