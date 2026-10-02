// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "preview_window.hpp"
#include "module.hpp"
#include "candidate_icons.hpp"
#include <windowsx.h>
#include <algorithm>
namespace mansur::win {
namespace {
constexpr wchar_t kClass[]=L"Mansur.Next.Preview.v2";
int px(int v,UINT dpi) noexcept {return MulDiv(v,dpi,96);}
UINT window_dpi(HWND hwnd) noexcept {
    using Fn=UINT(WINAPI*)(HWND);auto module=GetModuleHandleW(L"user32.dll");
    auto fn=module?reinterpret_cast<Fn>(GetProcAddress(module,"GetDpiForWindow")):nullptr;
    UINT dpi=fn?fn(hwnd):0;return dpi>=72&&dpi<=768?dpi:96;
}
struct Palette {COLORREF bg,fg,muted,border,accent,selected,hover;};
Palette palette(const NativeSettings& s) noexcept {
    bool dark=s.theme==UiTheme::Dark||(s.theme==UiTheme::System&&s.system_dark);
    return dark?Palette{RGB(33,35,39),RGB(243,244,246),RGB(175,181,190),RGB(61,65,73),RGB(103,173,255),RGB(53,57,64),RGB(45,48,54)}:
        Palette{RGB(250,250,250),RGB(32,33,36),RGB(98,102,109),RGB(224,226,230),RGB(0,102,204),RGB(236,238,241),RGB(242,243,245)};
}
struct Fonts {
    HFONT body=nullptr,small=nullptr;
    Fonts(UINT dpi,unsigned points) noexcept {
        body=CreateFontW(-MulDiv(points,dpi,72),0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,ANTIALIASED_QUALITY,DEFAULT_PITCH,L"Microsoft YaHei UI");
        small=CreateFontW(-MulDiv(std::max(9u,points-2),dpi,72),0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,ANTIALIASED_QUALITY,DEFAULT_PITCH,L"Microsoft YaHei UI");
    }
    ~Fonts(){if(body)DeleteObject(body);if(small)DeleteObject(small);}
    HFONT get(bool title=false) const noexcept {auto f=title?small:body;return f?f:static_cast<HFONT>(GetStockObject(SYSTEM_FONT));}
};
struct DcState {HDC dc;int saved;explicit DcState(HDC value):dc(value),saved(SaveDC(value)){}~DcState(){if(saved)RestoreDC(dc,saved);}};
int text_width(HDC dc,std::wstring_view text){SIZE s{};GetTextExtentPoint32W(dc,text.data(),static_cast<int>(text.size()),&s);return s.cx;}
std::vector<std::wstring> wrap(HDC dc,std::wstring_view text,int width) {
    std::vector<std::wstring> rows;std::size_t start=0;
    do {auto end=text.find(L'\n',start);if(end==std::wstring_view::npos)end=text.size();auto line=text.substr(start,end-start);
        if(line.empty())rows.emplace_back();
        while(!line.empty()) {int fit=0;SIZE size{};GetTextExtentExPointW(dc,line.data(),static_cast<int>(line.size()),std::max(1,width),&fit,nullptr,&size);
            fit=std::min(static_cast<int>(line.size()),std::max(1,fit));
            if(fit>1&&fit<static_cast<int>(line.size())&&line[fit-1]>=0xD800&&line[fit-1]<=0xDBFF)--fit;
            if(fit==1&&line.size()>1&&line[0]>=0xD800&&line[0]<=0xDBFF)fit=2;
            rows.emplace_back(line.substr(0,fit));line.remove_prefix(fit);
        }start=end+1;
    }while(start<=text.size());return rows;
}
std::wstring join(const std::vector<std::wstring>& rows){std::wstring out;bool first=true;for(auto& row:rows){if(!first)out+=L'\n';out+=row;first=false;}return out;}
void fill(HDC dc,const RECT& r,COLORREF color) noexcept {SetDCBrushColor(dc,color);FillRect(dc,&r,static_cast<HBRUSH>(GetStockObject(DC_BRUSH)));}
void rounded_outline(HDC dc,const RECT& area,COLORREF color,int radius) noexcept {
    auto pen=CreatePen(PS_SOLID,1,color);if(!pen)return;
    auto old_pen=SelectObject(dc,pen);auto old_brush=SelectObject(dc,GetStockObject(NULL_BRUSH));
    RoundRect(dc,area.left,area.top,area.right,area.bottom,radius*2,radius*2);
    SelectObject(dc,old_brush);SelectObject(dc,old_pen);DeleteObject(pen);
}
void rounded_fill(HDC dc,const RECT& area,COLORREF color,int radius) noexcept {
    auto pen=SelectObject(dc,GetStockObject(NULL_PEN));
    auto brush=SelectObject(dc,GetStockObject(DC_BRUSH));SetDCBrushColor(dc,color);
    RoundRect(dc,area.left,area.top,area.right,area.bottom,radius*2,radius*2);
    SelectObject(dc,brush);SelectObject(dc,pen);
}
bool same_action(const PreviewAction& a,const PreviewAction& b) noexcept {return a.kind==b.kind&&a.index==b.index&&a.context==b.context&&a.revision==b.revision;}
PreviewContent plain(std::wstring text){PreviewContent c;auto nl=text.find(L'\n');if(nl==std::wstring::npos)c.notice=std::move(text);else{c.title=text.substr(0,nl);c.notice=text.substr(nl+1);}return c;}
}
PreviewLayout LayoutPreview(HDC dc,const PreviewContent& c,const NativeSettings& input,UINT dpi,SIZE maximum) {
    const auto settings=SanitizeSettings(input);Fonts fonts(dpi,settings.font_points);DcState saved(dc);SelectObject(dc,fonts.get());
    PreviewLayout out;int pad=px(5,dpi),gap=px(2,dpi),inset=px(6,dpi),vertical_inset=px(3,dpi);TEXTMETRICW metrics{};GetTextMetricsW(dc,&metrics);out.line_height=std::max(1L,metrics.tmHeight)+px(2,dpi);
    SelectObject(dc,fonts.get(true));const int number_gutter=text_width(dc,c.english_suggestions?L"Tab":L"9")+px(5,dpi);
    GetTextMetricsW(dc,&metrics);const int small=std::max(1L,metrics.tmHeight)+px(2,dpi);
    const int draft_width=text_width(dc,c.draft);
    if(c.pages>1)out.page_text=std::to_wstring(c.page+1)+L"/"+std::to_wstring(c.pages);
    const int desired_label=out.page_text.empty()?0:text_width(dc,out.page_text)+px(8,dpi);
    const int desired_button=px(24,dpi),pager_height=std::max(px(24,dpi),small+px(2,dpi));
    const int desired_pager=out.page_text.empty()?0:desired_label+desired_button*2;
    SelectObject(dc,fonts.get());
    int longest=0,sum=0;for(auto& candidate:c.candidates){int w=number_gutter+text_width(dc,candidate.text)+inset*2;longest=std::max(longest,w);sum+=w+gap;}
    int ordinary=std::min(px(640,dpi),std::max(draft_width,text_width(dc,c.notice)));
    int natural=std::max(px(c.notice.empty()?120:200,dpi),ordinary+pad*2+(c.draft.empty()||!desired_pager?0:desired_pager+gap));
    if(!c.candidates.empty())natural=std::max(natural,pad*2+(settings.layout==CandidateLayout::Vertical?longest:std::max(longest,std::min(sum-gap,px(920,dpi)))));
    int width=std::max(1,std::min(natural,static_cast<int>(maximum.cx))),available=std::max(1,width-pad*2),y=0,top=pad;
    if(!c.title.empty()){out.title={pad,top,width-pad,top+small};top+=small+gap;}
    const int button=std::min(desired_button,std::max(1,available/4));
    const int label=std::min(desired_label,std::max(1,available-button*2));
    const int pager_width=desired_pager?button*2+label:0;
    const bool inline_pager=desired_pager&&!c.draft.empty()&&available-pager_width-gap>=std::max(px(72,dpi),small*3);
    if(desired_pager){
        const int right=std::max(0,width-pad),left=std::max(0,right-pager_width);
        const int row_height=inline_pager?std::max(small,pager_height):pager_height;
        out.previous={left,top,left+button,top+row_height};out.page_label={out.previous.right,top,out.previous.right+label,top+row_height};out.next={out.page_label.right,top,right,top+row_height};
        if(!inline_pager)top+=row_height+(!c.draft.empty()?gap:0);
    }
    std::wstring remaining_draft;
    if(!c.draft.empty()){
        const int draft_right=inline_pager?out.previous.left-gap:width-pad;
        SelectObject(dc,fonts.get(true));
        auto first_rows=wrap(dc,c.draft,std::max(1,draft_right-pad));out.draft_text=first_rows.front();
        SelectObject(dc,fonts.get());
        remaining_draft=c.draft.substr(out.draft_text.size());if(!remaining_draft.empty()&&remaining_draft.front()==L'\n')remaining_draft.erase(0,1);
        const int row_height=inline_pager?std::max(small,pager_height):small;
        out.draft={pad,top,draft_right,top+row_height};top+=row_height+gap;
    }
    auto paragraph=[&](const std::wstring& text){if(text.empty())return;auto rows=wrap(dc,text,available);int h=static_cast<int>(rows.size())*out.line_height;out.boxes.push_back({{pad,y,width-pad,y+h},join(rows),false,false,0});y+=h+gap;};
    // Keep the first draft row and pager visible; a long draft continues intact
    // in the scrollable body, leaving the complete candidate list accessible.
    paragraph(remaining_draft);
    int x=pad,rowheight=0;
    for(auto& candidate:c.candidates){
        int w=settings.layout==CandidateLayout::Vertical?available:std::min(available,number_gutter+text_width(dc,candidate.text)+inset*2);
        auto rows=wrap(dc,candidate.text,std::max(1,w-inset*2-number_gutter));
        int h=std::max(px(32,dpi),static_cast<int>(rows.size())*out.line_height+vertical_inset*2);
        if(x>pad&&(x+w>width-pad||settings.layout==CandidateLayout::Vertical)){y+=rowheight+gap;x=pad;rowheight=0;}
        out.boxes.push_back({{x,y,x+w,y+h},join(rows),true,candidate.selected,candidate.index});x+=w+gap;rowheight=std::max(rowheight,h);
    }
    if(!c.candidates.empty())y+=rowheight;
    if(!c.notice.empty()){if(y) y+=gap;paragraph(c.notice);}
    if(!y&&c.draft.empty()&&!desired_pager)y=out.line_height;
    const int height=std::max(1,std::min(top+y+pad,static_cast<int>(maximum.cy)));
    const int bodybottom=std::max(0,height-pad),bodytop=std::min(top,bodybottom);
    out.size={width,height};out.body={0,bodytop,width,bodybottom};out.scroll_max=std::max(0,y-(bodybottom-bodytop));
    for(auto area:{&out.title,&out.draft,&out.previous,&out.page_label,&out.next}){
        area->left=std::clamp(area->left,0L,static_cast<LONG>(width));area->right=std::clamp(area->right,area->left,static_cast<LONG>(width));
        area->top=std::clamp(area->top,0L,static_cast<LONG>(height));area->bottom=std::clamp(area->bottom,area->top,static_cast<LONG>(height));
    }
    return out;
}
void PaintPreviewContent(HDC dc,const PreviewContent& c,const PreviewLayout& layout,const NativeSettings& settings,UINT dpi,int scroll,const std::optional<PreviewAction>& hover) noexcept {
    Fonts fonts(dpi,settings.font_points);DcState saved(dc);try {
        auto p=palette(settings);int inset=px(6,dpi);RECT all{0,0,layout.size.cx,layout.size.cy};fill(dc,all,p.bg);rounded_outline(dc,all,p.border,px(6,dpi));
        SetBkMode(dc,TRANSPARENT);SelectObject(dc,fonts.get(true));SetTextColor(dc,p.muted);RECT title=layout.title;DrawTextW(dc,c.title.c_str(),-1,&title,DT_SINGLELINE|DT_VCENTER|DT_END_ELLIPSIS|DT_NOPREFIX);
        SelectObject(dc,fonts.get(true));SetTextColor(dc,p.muted);RECT draft=layout.draft;DrawTextW(dc,layout.draft_text.c_str(),-1,&draft,DT_SINGLELINE|DT_VCENTER|DT_NOPREFIX);
        SelectObject(dc,fonts.get(true));
        TEXTMETRICW number_metrics{},word_metrics{};GetTextMetricsW(dc,&number_metrics);
        const int number_gutter=text_width(dc,c.english_suggestions?L"Tab":L"9")+px(5,dpi);
        SelectObject(dc,fonts.get());GetTextMetricsW(dc,&word_metrics);
        int bodysaved=SaveDC(dc);IntersectClipRect(dc,layout.body.left,layout.body.top,layout.body.right,layout.body.bottom);SelectObject(dc,fonts.get());
        for(auto& box:layout.boxes){RECT area=box.rect;OffsetRect(&area,0,layout.body.top-std::clamp(scroll,0,layout.scroll_max));if(area.bottom<=layout.body.top||area.top>=layout.body.bottom)continue;
            bool over=hover&&(hover->kind==PreviewActionKind::Choose||hover->kind==PreviewActionKind::EnglishCompletion)&&hover->index==box.index&&box.candidate;
            if(box.candidate){
                if(box.selected||over)rounded_fill(dc,area,box.selected?p.selected:p.hover,px(6,dpi));
                if(box.selected){RECT marker{area.left+px(1,dpi),area.top+px(9,dpi),area.left+px(3,dpi),area.bottom-px(9,dpi)};fill(dc,marker,p.accent);}
                const auto lines=1+std::count(box.text.begin(),box.text.end(),L'\n');
                const int text_height=static_cast<int>(lines-1)*layout.line_height+word_metrics.tmHeight;
                area.left+=inset;area.right-=inset;area.top+=std::max(0L,(area.bottom-area.top-text_height)/2);
                SelectObject(dc,fonts.get(true));SetTextColor(dc,p.muted);
                const auto number=c.english_suggestions?(box.index==0?std::wstring(L"Tab"):std::wstring{}):std::to_wstring(box.index%9+1);
                TextOutW(dc,area.left,area.top+std::max(0L,(word_metrics.tmHeight-number_metrics.tmHeight)/2),number.c_str(),static_cast<int>(number.size()));
                area.left+=number_gutter;
            }
            SelectObject(dc,fonts.get());SetTextColor(dc,p.fg);std::size_t start=0;int y=area.top;
            do{auto end=box.text.find(L'\n',start);if(end==std::wstring::npos)end=box.text.size();TextOutW(dc,area.left,y,box.text.data()+start,static_cast<int>(end-start));y+=layout.line_height;start=end+1;}while(start<=box.text.size());
        }
        if(layout.scroll_max>0&&layout.body.bottom>layout.body.top){RECT track{layout.size.cx-px(5,dpi),layout.body.top,layout.size.cx-px(2,dpi),layout.body.bottom};fill(dc,track,p.hover);int h=track.bottom-track.top,thumb=std::max(px(8,dpi),h*h/(h+layout.scroll_max));track.top+=MulDiv(std::clamp(scroll,0,layout.scroll_max),std::max(0,h-thumb),layout.scroll_max);track.bottom=std::min(layout.body.bottom,track.top+thumb);fill(dc,track,p.border);}
        if(bodysaved)RestoreDC(dc,bodysaved);
        if(scroll>0&&layout.body.top>0){RECT edge{px(5,dpi),layout.body.top-1,layout.size.cx-px(5,dpi),layout.body.top};fill(dc,edge,p.border);}
        SelectObject(dc,fonts.get(true));SetTextColor(dc,p.muted);
        if(c.pages>1){auto button=[&](RECT r,bool enabled,PreviewActionKind kind){if(hover&&hover->kind==kind&&enabled)rounded_fill(dc,r,p.hover,px(5,dpi));PaintCandidateChevron(dc,r,enabled?p.fg:p.border,dpi,kind==PreviewActionKind::NextPage);};
            button(layout.previous,c.page>0,PreviewActionKind::PreviousPage);button(layout.next,c.page+1<c.pages,PreviewActionKind::NextPage);
            RECT label=layout.page_label;SetTextColor(dc,p.muted);DrawTextW(dc,layout.page_text.c_str(),-1,&label,DT_SINGLELINE|DT_CENTER|DT_VCENTER|DT_NOPREFIX);
        }
    }catch(...){}
}
int SelectedPreviewScroll(const PreviewLayout& layout) noexcept {
    const int height=std::max(0L,layout.body.bottom-layout.body.top);
    for(const auto& box:layout.boxes)if(box.candidate&&box.selected) {
        const int offset=box.rect.bottom-box.rect.top>height?box.rect.top:box.rect.bottom-height;
        return std::clamp(offset,0,layout.scroll_max);
    }
    return 0;
}
std::optional<PreviewAction> HitPreview(const PreviewContent& c,const PreviewLayout& layout,POINT point,int scroll) noexcept {
    if(PtInRect(&layout.previous,point)&&c.page>0)return PreviewAction{PreviewActionKind::PreviousPage,0,c.context,c.revision};
    if(PtInRect(&layout.next,point)&&c.page+1<c.pages)return PreviewAction{PreviewActionKind::NextPage,0,c.context,c.revision};
    if(!PtInRect(&layout.body,point))return {};point.y-=layout.body.top-std::clamp(scroll,0,layout.scroll_max);
    for(auto& box:layout.boxes)if(box.candidate&&PtInRect(&box.rect,point))return PreviewAction{c.english_suggestions?PreviewActionKind::EnglishCompletion:PreviewActionKind::Choose,box.index,c.context,c.revision};return {};
}
SIZE MeasurePreview(HDC dc,std::wstring_view text,UINT dpi,int width) noexcept {try{return LayoutPreview(dc,plain(std::wstring(text)),ReadNativeSettings(),dpi,{width,32767}).size;}catch(...){return {width,px(160,dpi)};}}
void PaintPreview(HDC dc,const RECT& area,std::wstring_view text,UINT dpi) noexcept {try{auto c=plain(std::wstring(text));auto s=ReadNativeSettings();auto layout=LayoutPreview(dc,c,s,dpi,{area.right-area.left,area.bottom-area.top});int saved=SaveDC(dc);SetViewportOrgEx(dc,area.left,area.top,nullptr);PaintPreviewContent(dc,c,layout,s,dpi);if(saved)RestoreDC(dc,saved);}catch(...){}}
PreviewWindow::~PreviewWindow(){close();}
void PreviewWindow::set_action_handler(void* owner,ActionHandler handler) noexcept {action_owner_=owner;action_handler_=handler;}
bool PreviewWindow::ensure_ready() noexcept {try{return create();}catch(...){return false;}}
bool PreviewWindow::create(){if(window_){if(IsWindow(window_)&&GetWindowLongPtrW(window_,GWLP_USERDATA)==reinterpret_cast<LONG_PTR>(this))return true;window_=nullptr;watched_context_=0;watch_started_=0;}
    WNDCLASSEXW wc{};wc.cbSize=sizeof(wc);wc.lpfnWndProc=procedure;wc.hInstance=module_instance;wc.hCursor=LoadCursorW(nullptr,IDC_ARROW);wc.lpszClassName=kClass;
    if(!RegisterClassExW(&wc)&&GetLastError()!=ERROR_CLASS_ALREADY_EXISTS)return false;
    region_width_=region_height_=0;region_dpi_=0;
    window_=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW|WS_EX_TOPMOST,kClass,L"Mansur Next",WS_POPUP,0,0,0,0,GetFocus(),nullptr,module_instance,this);return window_!=nullptr;
}
bool PreviewWindow::show(std::wstring text,const CaretLocation& caret) noexcept {try{return show(plain(std::move(text)),caret);}catch(...){return false;}}
bool PreviewWindow::show(PreviewContent content,const CaretLocation& caret) noexcept {
    try{if(!create())return false;KillTimer(window_,1);watched_context_=0;HWND owner=caret.owner&&IsWindow(caret.owner)?caret.owner:GetFocus();
        // A popup's owner must be a top-level window. A child Edit HWND does
        // not establish the destruction relationship required by Windows.
        if(owner){HWND root=GetAncestor(owner,GA_ROOT);if(root)owner=root;}
        if(owner!=window_)SetWindowLongPtrW(window_,GWLP_HWNDPARENT,reinterpret_cast<LONG_PTR>(owner));bool visible=IsWindowVisible(window_)!=FALSE;
        content_=std::move(content);settings_=ReadNativeSettings();scroll_=0;hover_.reset();pressed_.reset();dpi_=window_dpi(owner?owner:window_);
        POINT origin=caret.valid?POINT{caret.rect.left,caret.rect.bottom+px(7,dpi_)}:POINT{px(40,dpi_),px(80,dpi_)};MONITORINFO info{};info.cbSize=sizeof(info);RECT work{0,0,GetSystemMetrics(SM_CXSCREEN),GetSystemMetrics(SM_CYSCREEN)};
        if(GetMonitorInfoW(MonitorFromPoint(origin,MONITOR_DEFAULTTONEAREST),&info))work=info.rcWork;HDC dc=GetDC(window_);if(!dc)return false;
        try{layout_=LayoutPreview(dc,content_,settings_,dpi_,{std::max(1L,work.right-work.left),std::max(1L,work.bottom-work.top)});}catch(...){ReleaseDC(window_,dc);throw;}ReleaseDC(window_,dc);
        scroll_=SelectedPreviewScroll(layout_);
        int width=layout_.size.cx,height=layout_.size.cy;origin.x=std::clamp(origin.x,work.left,std::max(work.left,work.right-width));if(origin.y+height>work.bottom&&caret.valid)origin.y=caret.rect.top-height-px(7,dpi_);origin.y=std::clamp(origin.y,work.top,std::max(work.top,work.bottom-height));
        if(width!=region_width_||height!=region_height_||dpi_!=region_dpi_){
            HRGN region=CreateRoundRectRgn(0,0,width+1,height+1,px(12,dpi_),px(12,dpi_));
            if(region){if(SetWindowRgn(window_,region,FALSE)){region_width_=width;region_height_=height;region_dpi_=dpi_;}else DeleteObject(region);}
        }
        SetWindowPos(window_,HWND_TOPMOST,origin.x,origin.y,width,height,SWP_NOACTIVATE|SWP_SHOWWINDOW);InvalidateRect(window_,nullptr,TRUE);NotifyWinEvent(visible?EVENT_OBJECT_IME_CHANGE:EVENT_OBJECT_IME_SHOW,window_,OBJID_CLIENT,CHILDID_SELF);return true;
    }catch(...){hide();return false;}
}
void PreviewWindow::watch_delivery(std::uint64_t id,const CaretLocation& caret) noexcept {if(!window_)return;watched_context_=id;watched_caret_=caret;watch_started_=GetTickCount64();SetTimer(window_,1,200,nullptr);}
void PreviewWindow::hide() noexcept {if(window_){KillTimer(window_,1);watched_context_=0;pressed_.reset();bool visible=IsWindowVisible(window_)!=FALSE;ShowWindow(window_,SW_HIDE);if(visible)NotifyWinEvent(EVENT_OBJECT_IME_HIDE,window_,OBJID_CLIENT,CHILDID_SELF);}}
void PreviewWindow::close() noexcept {hide();HWND old=window_;window_=nullptr;if(old)DestroyWindow(old);UnregisterClassW(kClass,module_instance);}
LRESULT CALLBACK PreviewWindow::procedure(HWND hwnd,UINT message,WPARAM w,LPARAM l) noexcept {
    try{if(message==WM_NCCREATE){auto cs=reinterpret_cast<CREATESTRUCTW*>(l);SetWindowLongPtrW(hwnd,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(cs->lpCreateParams));}auto self=reinterpret_cast<PreviewWindow*>(GetWindowLongPtrW(hwnd,GWLP_USERDATA));
        switch(message){
        case WM_MOUSEACTIVATE:return MA_NOACTIVATE;
        case WM_NCHITTEST:return self&&(!self->content_.candidates.empty()||self->layout_.scroll_max)?HTCLIENT:HTTRANSPARENT;
        case WM_ERASEBKGND:return 1;
        case WM_MOUSEMOVE:if(self){
            auto next=HitPreview(self->content_,self->layout_,{GET_X_LPARAM(l),GET_Y_LPARAM(l)},self->scroll_);
            const bool changed=next.has_value()!=self->hover_.has_value()||(next&&self->hover_&&!same_action(*next,*self->hover_));
            self->hover_=next;SetCursor(LoadCursorW(nullptr,self->hover_?IDC_HAND:IDC_ARROW));
            TRACKMOUSEEVENT track{sizeof(track),TME_LEAVE,hwnd,0};TrackMouseEvent(&track);
            if(changed)InvalidateRect(hwnd,nullptr,FALSE);
        }return 0;
        case WM_MOUSELEAVE:if(self&&self->hover_){self->hover_.reset();InvalidateRect(hwnd,nullptr,FALSE);}return 0;
        case WM_MOUSEWHEEL:if(self){self->scroll_=std::clamp(self->scroll_-GET_WHEEL_DELTA_WPARAM(w)/WHEEL_DELTA*self->layout_.line_height*3,0,self->layout_.scroll_max);self->pressed_.reset();InvalidateRect(hwnd,nullptr,FALSE);}return 0;
        case WM_LBUTTONDOWN:if(self)self->pressed_=HitPreview(self->content_,self->layout_,{GET_X_LPARAM(l),GET_Y_LPARAM(l)},self->scroll_);return 0;
        case WM_LBUTTONUP:if(self){auto hit=HitPreview(self->content_,self->layout_,{GET_X_LPARAM(l),GET_Y_LPARAM(l)},self->scroll_);auto pressed=self->pressed_;self->pressed_.reset();if(hit&&pressed&&same_action(*hit,*pressed)&&self->action_handler_){auto handler=self->action_handler_;void* owner=self->action_owner_;handler(owner,*hit);}}return 0;
        case WM_TIMER:if(self&&w==1){auto status=GetLastDeliveryStatus();if(GetTickCount64()-self->watch_started_>5000||(status.context_id&&status.context_id!=self->watched_context_)){KillTimer(hwnd,1);self->watched_context_=0;return 0;}if(status.context_id==self->watched_context_){if(status.status==DeliveryStatus::Delivered){KillTimer(hwnd,1);self->watched_context_=0;}else if(status.status==DeliveryStatus::Unavailable||status.status==DeliveryStatus::TimedOut||status.status==DeliveryStatus::Rejected)self->show(L"Mansur Next\n中文已提交。学习服务暂不可用，请启动伴读程序后再次确认。\n可以继续输入中文。",self->watched_caret_);}}return 0;
        case WM_PAINT:{PAINTSTRUCT ps{};HDC dc=BeginPaint(hwnd,&ps);if(self)PaintPreviewContent(dc,self->content_,self->layout_,self->settings_,self->dpi_,self->scroll_,self->hover_);EndPaint(hwnd,&ps);return 0;}
        case WM_NCDESTROY:if(self&&self->window_==hwnd){self->window_=nullptr;self->watched_context_=0;self->watch_started_=0;}SetWindowLongPtrW(hwnd,GWLP_USERDATA,0);break;
        default:break;}return DefWindowProcW(hwnd,message,w,l);
    }catch(...){return 0;}
}
}
