// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include "learning_dispatch.hpp"
#include "settings.hpp"
#include <optional>
#include <string>
#include <string_view>
#include <vector>
namespace mansur::win {
enum class PreviewActionKind { Choose, PreviousPage, NextPage, EnglishCompletion };
struct PreviewAction { PreviewActionKind kind=PreviewActionKind::Choose;std::size_t index=0;std::uint64_t context=0,revision=0; };
struct PreviewCandidate { std::wstring text;std::size_t index=0;bool selected=false; };
struct PreviewContent {
    std::wstring title=L"Mansur Next",draft,notice;
    std::vector<PreviewCandidate> candidates;
    std::size_t page=0,pages=0;
    std::uint64_t context=0,revision=0;
    bool english_suggestions=false;
};
struct PreviewBox { RECT rect{};std::wstring text;bool candidate=false,selected=false;std::size_t index=0; };
struct PreviewLayout {
    SIZE size{};RECT title{},draft{},body{},previous{},next{},page_label{};
    int scroll_max=0,line_height=0;
    std::wstring page_text,draft_text;
    std::vector<PreviewBox> boxes;
};
PreviewLayout LayoutPreview(HDC,const PreviewContent&,const NativeSettings&,UINT dpi,SIZE maximum);
int SelectedPreviewScroll(const PreviewLayout&) noexcept;
void PaintPreviewContent(HDC,const PreviewContent&,const PreviewLayout&,const NativeSettings&,UINT dpi,
    int scroll=0,const std::optional<PreviewAction>& hover={}) noexcept;
std::optional<PreviewAction> HitPreview(const PreviewContent&,const PreviewLayout&,POINT,int scroll=0) noexcept;
SIZE MeasurePreview(HDC,std::wstring_view,UINT dpi,int maximum_width) noexcept;
void PaintPreview(HDC,const RECT&,std::wstring_view,UINT dpi) noexcept;
class PreviewWindow {
public:
    using ActionHandler=void(*)(void*,const PreviewAction&) noexcept;
    ~PreviewWindow();
    void set_action_handler(void*,ActionHandler) noexcept;
    bool ensure_ready() noexcept;
    bool show(std::wstring,const CaretLocation&) noexcept;
    bool show(PreviewContent,const CaretLocation&) noexcept;
    void watch_delivery(std::uint64_t,const CaretLocation&) noexcept;
    void hide() noexcept;
    void close() noexcept;
private:
    bool create();
    static LRESULT CALLBACK procedure(HWND,UINT,WPARAM,LPARAM) noexcept;
    HWND window_=nullptr;
    UINT dpi_=96;
    int region_width_=0,region_height_=0;
    UINT region_dpi_=0;
    PreviewContent content_;
    PreviewLayout layout_;
    NativeSettings settings_;
    int scroll_=0;
    std::optional<PreviewAction> hover_,pressed_;
    void* action_owner_=nullptr;
    ActionHandler action_handler_=nullptr;
    std::uint64_t watched_context_=0;
    ULONGLONG watch_started_=0;
    CaretLocation watched_caret_;
};
}
