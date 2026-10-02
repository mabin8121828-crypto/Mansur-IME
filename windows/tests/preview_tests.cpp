// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#include "preview_window.hpp"
#include "module.hpp"
#include "com_ptr.hpp"
#include <wincodec.h>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
namespace mansur::win {
HINSTANCE module_instance=nullptr;std::atomic<long> live_objects{0},server_locks{0};
DeliverySnapshot GetLastDeliveryStatus() noexcept{return {};}
}
using namespace mansur::win;
void check(bool value,const char* why){if(!value)throw std::runtime_error(why);}
struct Surface {
    HDC dc=CreateCompatibleDC(nullptr);HBITMAP bitmap=nullptr;HGDIOBJ previous=nullptr;void* pixels=nullptr;int width,height;
    Surface(int w,int h):width(w),height(h){BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=w;info.bmiHeader.biHeight=-h;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=BI_RGB;bitmap=CreateDIBSection(dc,&info,DIB_RGB_COLORS,&pixels,nullptr,0);check(dc&&bitmap,"offscreen surface");previous=SelectObject(dc,bitmap);}
    ~Surface(){SelectObject(dc,previous);DeleteObject(bitmap);DeleteDC(dc);}
};
void png(Surface& surface,const std::filesystem::path& path,int width,int height){
    ComPtr<IWICImagingFactory> factory;check(SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(factory.put()))),"WIC factory");
    ComPtr<IWICStream> stream;check(SUCCEEDED(factory->CreateStream(stream.put())),"WIC stream");check(SUCCEEDED(stream->InitializeFromFilename(path.c_str(),GENERIC_WRITE)),"PNG file");
    ComPtr<IWICBitmapEncoder> encoder;check(SUCCEEDED(factory->CreateEncoder(GUID_ContainerFormatPng,nullptr,encoder.put())),"PNG encoder");check(SUCCEEDED(encoder->Initialize(stream.get(),WICBitmapEncoderNoCache)),"PNG initialize");
    ComPtr<IWICBitmapFrameEncode> frame;check(SUCCEEDED(encoder->CreateNewFrame(frame.put(),nullptr)),"PNG frame");check(SUCCEEDED(frame->Initialize(nullptr)),"PNG frame initialize");check(SUCCEEDED(frame->SetSize(width,height)),"PNG size");
    WICPixelFormatGUID format=GUID_WICPixelFormat32bppBGRA;check(SUCCEEDED(frame->SetPixelFormat(&format))&&IsEqualGUID(format,GUID_WICPixelFormat32bppBGRA),"PNG pixels");
    auto pixels=static_cast<std::uint32_t*>(surface.pixels);for(int y=0;y<height;++y)for(int x=0;x<width;++x)pixels[y*surface.width+x]|=0xff000000u;
    check(SUCCEEDED(frame->WritePixels(height,surface.width*4,surface.width*surface.height*4,static_cast<BYTE*>(surface.pixels))),"PNG write");check(SUCCEEDED(frame->Commit())&&SUCCEEDED(encoder->Commit()),"PNG commit");
}
int window_fixture(const wchar_t* log){
    std::ofstream output(std::filesystem::path(log),std::ios::binary);
    try {
        wchar_t desktop_name[128]{};DWORD needed=0;
        check(GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),UOI_NAME,desktop_name,sizeof(desktop_name),&needed)&&std::wstring_view(desktop_name).find(L"MansurFocusProbe")==0,"private desktop required; never create fixture on user desktop");
        module_instance=GetModuleHandleW(nullptr);
        auto make_editor=[](){HWND owner=CreateWindowExW(0,L"STATIC",L"Candidate fixture",WS_OVERLAPPEDWINDOW|WS_VISIBLE,30,30,700,450,nullptr,nullptr,module_instance,nullptr);check(owner!=nullptr,"fixture owner");return owner;};
        HWND owner=make_editor();HWND editor=CreateWindowExW(0,L"EDIT",L"",WS_CHILD|WS_VISIBLE,10,10,500,100,owner,nullptr,module_instance,nullptr);check(editor!=nullptr,"fixture edit");SetFocus(editor);
        PreviewContent c;c.title.clear();c.draft=L"nihao│";c.pages=7;c.context=12;c.revision=34;
        for(int i=0;i<9;++i)c.candidates.push_back({L"你好",static_cast<std::size_t>(i),i==0});
        CaretLocation caret;caret.valid=true;caret.owner=editor;caret.rect={100,100,100,120};
        PreviewWindow preview;unsigned checks=0;
        check(preview.show(c,caret),"actual candidate window shows");++checks;
        HWND popup=FindWindowW(L"Mansur.Next.Preview.v2",nullptr);DWORD pid=0;GetWindowThreadProcessId(popup,&pid);
        check(popup&&pid==GetCurrentProcessId()&&IsWindowVisible(popup),"visible own popup");++checks;
        check((GetWindowLongPtrW(popup,GWL_EXSTYLE)&(WS_EX_NOACTIVATE|WS_EX_TOPMOST))==(WS_EX_NOACTIVATE|WS_EX_TOPMOST)&&GetFocus()==editor,"candidate popup stays on top without taking focus");++checks;
        HRGN shape=CreateRectRgn(0,0,0,0);check(shape!=nullptr,"fixture region");
        check(GetWindowRgn(popup,shape)!=ERROR&&!PtInRegion(shape,0,0)&&PtInRegion(shape,20,20),"actual window shape has rounded corners");DeleteObject(shape);++checks;
        // Warm up the Windows paint/update-region cache before measuring a
        // second complete resize cycle. Continued growth fails the gate.
        SendMessageW(popup,WM_PAINT,0,0);
        const auto objects=GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS);
        for(int i=0;i<100;++i){c.draft=i%2?L"今天我们一起练习中文输入 nihao│":L"nihao│";c.candidates.back().text=i%2?L"这个候选保留完整文字":L"你好";check(preview.show(c,caret)&&GetFocus()==editor,"resize does not activate");SendMessageW(popup,WM_PAINT,0,0);}
        const auto after_objects=GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS);output<<"GDI before="<<objects<<" after100="<<after_objects<<'\n';
        for(int i=0;i<100;++i){c.draft=i%2?L"今天我们一起练习中文输入 nihao│":L"nihao│";c.candidates.back().text=i%2?L"这个候选保留完整文字":L"你好";check(preview.show(c,caret)&&GetFocus()==editor,"second resize cycle does not activate");SendMessageW(popup,WM_PAINT,0,0);}
        const auto steady_objects=GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS);output<<"GDI after200="<<steady_objects<<'\n';
        check(steady_objects<=after_objects,"resize and paint do not accumulate region/font/pen handles after warmup");++checks;
        preview.hide();check(!IsWindowVisible(popup)&&GetFocus()==editor,"hide preserves edit focus");++checks;
        check(preview.show(c,caret)&&GetFocus()==editor,"redisplay preserves focus");++checks;
        DestroyWindow(owner);check(!IsWindow(popup),"owner destruction destroys its popup");++checks;
        owner=make_editor();caret.owner=owner;
        check(preview.show(c,caret),"preview recreates after owner destruction");++checks;
        popup=FindWindowW(L"Mansur.Next.Preview.v2",nullptr);shape=CreateRectRgn(0,0,0,0);
        check(popup&&GetWindowRgn(popup,shape)!=ERROR&&!PtInRegion(shape,0,0),"recreated window receives its own fresh rounded region");DeleteObject(shape);++checks;
        preview.close();check(!IsWindow(popup),"close releases own window");++checks;DestroyWindow(owner);
        output<<"GDI after close="<<GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS)<<'\n';
        output<<"PASS "<<checks<<" native window checks; private desktop, no user UI or input injection.\n";return 0;
    }catch(const std::exception& e){output<<"FAIL "<<e.what()<<'\n';return 1;}
}
int wmain(int argc,wchar_t** argv){if(argc==3)return window_fixture(argv[2]);try{
    CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);Surface surface(2000,1600);
    PreviewContent c;c.title.clear();c.draft=L"今天我们一起 wan│";c.context=12;c.revision=34;c.pages=7;
    const wchar_t* words[]={L"玩",L"晚",L"完",L"万",L"碗",L"湾",L"弯",L"丸",L"婉"};
    for(int i=0;i<9;++i)c.candidates.push_back({words[i],static_cast<std::size_t>(i),i==0});
    unsigned count=0;
    if(argc>1)std::filesystem::create_directories(argv[1]);
    for(auto theme:{UiTheme::Light,UiTheme::Dark})for(auto direction:{CandidateLayout::Horizontal,CandidateLayout::Vertical})for(UINT dpi:{96u,120u,144u,192u}){
        NativeSettings s;s.theme=theme;s.layout=direction;auto layout=LayoutPreview(surface.dc,c,s,dpi,{1400,1200});
        check(layout.size.cx<1100&&layout.size.cy<=1200,"adaptive width and work area");
        check(layout.previous.bottom<=layout.body.top&&layout.previous.top==layout.draft.top&&layout.previous.left>layout.draft.right,"compact pager shares draft row without covering text");
        check(layout.page_text==L"1/7","pager has no shortcut help or loose spacing");
        check(layout.size.cy-layout.body.bottom==MulDiv(5,dpi,96),"compact edge padding without pagination footer");
        unsigned candidates=0;for(auto& box:layout.boxes)if(box.candidate){++candidates;check(box.rect.left>=0&&box.rect.right<=layout.size.cx,"candidate whole card in bounds");
            check(box.rect.bottom-box.rect.top>=MulDiv(32,dpi,96),"candidate retains a practical mouse target height");
            auto complete=box.text;complete.erase(std::remove(complete.begin(),complete.end(),L'\n'),complete.end());
            check(complete==c.candidates[box.index].text,"candidate text is complete and numbering does not split its wrap");
            POINT point{box.rect.left+2,box.rect.top+layout.body.top+2};auto hit=HitPreview(c,layout,point);check(hit&&hit->index==box.index&&hit->context==12&&hit->revision==34,"candidate hit uses exact revision and index");}
        check(candidates==9,"all nine complete candidates");
        POINT prev{layout.previous.left+1,layout.previous.top+1},next{layout.next.left+1,layout.next.top+1};
        check(!HitPreview(c,layout,prev),"first-page previous disabled");auto action=HitPreview(c,layout,next);check(action&&action->kind==PreviewActionKind::NextPage,"next-page click");
        auto finalpage=c;finalpage.page=finalpage.pages-1;auto finallayout=LayoutPreview(surface.dc,finalpage,s,dpi,{1400,1200});
        check(!HitPreview(finalpage,finallayout,{finallayout.next.left+1,finallayout.next.top+1}),"last-page next remains disabled in pinned pager");
        PaintPreviewContent(surface.dc,c,layout,s,dpi);
        check(GetPixel(surface.dc,layout.size.cx/2,3)==(theme==UiTheme::Dark?RGB(33,35,39):RGB(250,250,250)),"theme fills opaque neutral background");
        for(const auto& box:layout.boxes)if(box.candidate&&box.selected) {
            const int top=box.rect.top+layout.body.top;
            check(GetPixel(surface.dc,box.rect.left,top)==(theme==UiTheme::Dark?RGB(33,35,39):RGB(250,250,250)),"selected card uses a rounded corner without a blue rectangle border");
            check(GetPixel(surface.dc,box.rect.left+MulDiv(4,dpi,96),top+(box.rect.bottom-box.rect.top)/2)==(theme==UiTheme::Dark?RGB(53,57,64):RGB(236,238,241)),"selected card has a neutral fill without an accent outline");
            check(GetPixel(surface.dc,box.rect.left+MulDiv(2,dpi,96),top+(box.rect.bottom-box.rect.top)/2)==(theme==UiTheme::Dark?RGB(103,173,255):RGB(0,102,204)),"thin accent marker identifies the selection");
        }
        if(argc>1){auto name=(theme==UiTheme::Dark?L"dark-":L"light-")+std::wstring(direction==CandidateLayout::Vertical?L"vertical-":L"horizontal-")+std::to_wstring(dpi)+L".png";png(surface,std::filesystem::path(argv[1])/name,layout.size.cx,layout.size.cy);}++count;
    }
    NativeSettings s;s.font_points=20;s.layout=CandidateLayout::Vertical;
    c.candidates[4].text=L"这个候选词条长度超过一个小屏幕但仍然和它的编号一起显示";
    auto compact=LayoutPreview(surface.dc,c,s,144,{420,500});
    check(compact.size.cx<=420&&compact.size.cy<=500&&compact.scroll_max>0,"small work area scrolls rather than growing offscreen");
    check(compact.previous.bottom<=compact.body.top&&compact.previous.bottom<=500,"scroll retains pinned page buttons");
    check(compact.page_text==L"1/7","narrow header keeps complete page number and omits optional shortcut");
    std::size_t cards=0;for(auto& box:compact.boxes)if(box.candidate)++cards;check(cards==9,"long candidate remains one grouped card");
    auto last=compact.boxes.back();POINT point{last.rect.left+2,last.rect.top+compact.body.top-compact.scroll_max+2};
    auto hit=HitPreview(c,compact,point,compact.scroll_max);check(hit&&hit->index==8,"scrolled last candidate clickable");
    for(auto& box:compact.boxes)if(box.candidate)box.selected=box.index==8;
    check(SelectedPreviewScroll(compact)>0,"keyboard-selected candidate automatically enters visible body");
    PaintPreviewContent(surface.dc,c,compact,s,144,compact.scroll_max);
    if(argc>1)png(surface,std::filesystem::path(argv[1])/L"small-workarea-20pt.png",compact.size.cx,compact.size.cy);
    PreviewContent longwords=c;longwords.draft=L"今天我们一起 wan│";
    const wchar_t* phrases[]={L"完成今天的英语学习目标",L"晚上一起吃饭",L"万事如意",L"玩得开心",L"这个候选词条超过屏幕宽度时仍然完整保留在同一个编号卡片里",L"晚一点联系",L"完整的输入体验",L"晚安",L"完成"};
    for(int i=0;i<9;++i)longwords.candidates[i].text=phrases[i];
    s.layout=CandidateLayout::Horizontal;s.theme=UiTheme::Dark;
    auto longlayout=LayoutPreview(surface.dc,longwords,s,144,{1040,1400});
    unsigned longcards=0;for(auto& box:longlayout.boxes)if(box.candidate){++longcards;check(box.rect.right<=longlayout.size.cx,"long phrase card constrained to screen");
        auto complete=box.text;complete.erase(std::remove(complete.begin(),complete.end(),L'\n'),complete.end());check(complete==longwords.candidates[box.index].text,"long phrase is unchanged after visual wrapping");}
    check(longcards==9,"all long phrases retained as independent grouped entries");
    PaintPreviewContent(surface.dc,longwords,longlayout,s,144);
    if(argc>1)png(surface,std::filesystem::path(argv[1])/L"long-phrases-horizontal-20pt-144.png",longlayout.size.cx,longlayout.size.cy);
    PreviewContent single=c;single.pages=1;single.candidates.resize(3);s.font_points=12;
    auto singlelayout=LayoutPreview(surface.dc,single,s,96,{1000,700});
    check(singlelayout.page_text.empty()&&IsRectEmpty(&singlelayout.previous)&&IsRectEmpty(&singlelayout.next),"single page has no footer controls");
    check(singlelayout.draft.top==5&&singlelayout.size.cy-singlelayout.body.bottom==5&&IsRectEmpty(&singlelayout.title),"normal single page has only compact edge padding, no title or help reservation");
    PaintPreviewContent(surface.dc,single,singlelayout,s,96);
    if(argc>1)png(surface,std::filesystem::path(argv[1])/L"single-page-compact-96.png",singlelayout.size.cx,singlelayout.size.cy);
    PreviewContent wrapped=c;wrapped.draft=L"今天我们一起练习中文输入并且学习英语，这一整句话仍然会完整保留在草稿中 wan│";
    s.font_points=20;s.layout=CandidateLayout::Horizontal;s.theme=UiTheme::Light;
    auto narrow=LayoutPreview(surface.dc,wrapped,s,144,{420,500});
    std::wstring restored=narrow.draft_text;for(const auto& box:narrow.boxes)if(!box.candidate)restored+=box.text;
    restored.erase(std::remove(restored.begin(),restored.end(),L'\n'),restored.end());
    check(restored==wrapped.draft,"draft continuation keeps every character after reserving pager width");
    check(narrow.scroll_max>0&&narrow.previous.bottom<=narrow.body.top,"wrapped draft leaves bounded scrolling body and pinned pager");
    auto narrowpage=HitPreview(wrapped,narrow,{narrow.next.left+1,narrow.next.top+1},narrow.scroll_max);
    check(narrowpage&&narrowpage->kind==PreviewActionKind::NextPage,"pager remains clickable after long draft body scrolls");
    PaintPreviewContent(surface.dc,wrapped,narrow,s,144);
    if(argc>1)png(surface,std::filesystem::path(argv[1])/L"wrapped-draft-20pt-144.png",narrow.size.cx,narrow.size.cy);
    auto tiny=LayoutPreview(surface.dc,wrapped,s,96,{160,400});
    check(tiny.previous.bottom<=tiny.draft.top&&tiny.draft.right<=tiny.size.cx,"very narrow viewport wraps pager above draft instead of overlapping");
    PaintPreviewContent(surface.dc,wrapped,tiny,s,96);
    if(argc>1)png(surface,std::filesystem::path(argv[1])/L"narrow-stacked-pager-20pt-96.png",tiny.size.cx,tiny.size.cy);
    // The user's comparison state, rendered by the production painter.
    PreviewContent greeting;greeting.title.clear();greeting.draft=L"nihao│";greeting.pages=1;
    const wchar_t* greeting_words[]={L"你好",L"你号",L"你浩",L"你豪",L"拟好",L"你耗",L"你郝",L"尼好",L"nihao"};
    for(int i=0;i<9;++i)greeting.candidates.push_back({greeting_words[i],static_cast<std::size_t>(i),i==0});
    NativeSettings greeting_settings;greeting_settings.theme=UiTheme::Light;
    auto greeting_layout=LayoutPreview(surface.dc,greeting,greeting_settings,96,{1000,700});
    std::cout<<"nihao: "<<greeting_layout.size.cx<<" x "<<greeting_layout.size.cy<<"\n";
    check(greeting_layout.size.cy<=66&&greeting_layout.size.cx<580,"reference greeting is visibly compact while retaining all nine candidates");
    PaintPreviewContent(surface.dc,greeting,greeting_layout,greeting_settings,96);
    if(argc>1)png(surface,std::filesystem::path(argv[1])/L"nihao-96.png",greeting_layout.size.cx,greeting_layout.size.cy);
    NativeSettings bad;bad.font_points=90;bad.theme=static_cast<UiTheme>(12);bad.layout=static_cast<CandidateLayout>(5);bad.fuzzy_mask=0xffffffff;
    PreviewContent english;english.title.clear();english.draft=L"Hel│";english.english_suggestions=true;
    english.candidates={{L"Hello",0,true},{L"Help",1,false}};
    auto english_layout=LayoutPreview(surface.dc,english,greeting_settings,96,{1000,700});
    for(const auto& box:english_layout.boxes)if(box.candidate){auto action=HitPreview(english,english_layout,{box.rect.left+2,box.rect.top+english_layout.body.top+2});
        check(action&&action->kind==PreviewActionKind::EnglishCompletion&&action->index==box.index,"English click has explicit completion action, not Chinese digit routing");}
    PaintPreviewContent(surface.dc,english,english_layout,greeting_settings,96);
    if(argc>1)png(surface,std::filesystem::path(argv[1])/L"english-completion-96.png",english_layout.size.cx,english_layout.size.cy);
    auto clean=SanitizeSettings(bad);check(clean.font_points==20&&clean.theme==UiTheme::System&&clean.layout==CandidateLayout::Horizontal&&clean.fuzzy_mask==255,"settings bounds");
    const auto objects=GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS);
    for(int i=0;i<32;++i)PaintPreviewContent(surface.dc,c,compact,s,144,compact.scroll_max);
    check(GetGuiResources(GetCurrentProcess(),GR_GDIOBJECTS)<=objects,"painting releases temporary fonts");
    std::cout<<count<<" appearance combinations plus constrained viewport, hit testing and settings passed; offscreen only.\n";CoUninitialize();return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
