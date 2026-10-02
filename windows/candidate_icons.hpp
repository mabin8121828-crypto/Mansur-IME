// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <algorithm>
// Lucide, immutable upstream revision 5a92b9ba262de5bf10e864219883267672c05db8.
// Exact 24x24 relative SVG paths converted to absolute vertices (no redesign):
// chevron-left: m15 18-6-6 6-6; chevron-right: m9 18 6-6-6-6.
// Source SVGs and complete ISC/MIT notice: desktop/iconassets/lucide/.
namespace mansur::win {
inline void PaintCandidateChevron(HDC dc,const RECT& area,COLORREF color,UINT dpi,bool right) noexcept {
    const int size=MulDiv(18,dpi,96),left=(area.left+area.right-size)/2,top=(area.top+area.bottom-size)/2;
    const POINT vertices_left[]={{15,18},{9,12},{15,6}},vertices_right[]={{9,18},{15,12},{9,6}};
    const auto* original=right?vertices_right:vertices_left;POINT points[3]{};
    for(int i=0;i<3;++i)points[i]={left+MulDiv(original[i].x,size,24),top+MulDiv(original[i].y,size,24)};
    LOGBRUSH brush{BS_SOLID,color,0};auto pen=ExtCreatePen(PS_GEOMETRIC|PS_SOLID|PS_ENDCAP_ROUND|PS_JOIN_ROUND,std::max(1,MulDiv(2,size,24)),&brush,0,nullptr);
    if(!pen)return;auto old=SelectObject(dc,pen);Polyline(dc,points,3);SelectObject(dc,old);DeleteObject(pen);
}
}
