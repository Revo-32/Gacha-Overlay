#pragma once
#include <algorithm>
#include <string>
// Windows SDK prerequisites must precede GDI+ even with WIN32_LEAN_AND_MEAN.
// clang-format off
#include <windows.h>
#include <commctrl.h>
#include <objidl.h>
#include <gdiplus.h>
// clang-format on

namespace core::settingsTheme {
inline constexpr COLORREF background = RGB(22, 27, 34),
                          surface = RGB(33, 38, 45), accent = RGB(88, 166, 255),
                          text = RGB(240, 246, 252), muted = RGB(139, 148, 158);
inline Gdiplus::Color color(COLORREF c) {
  return {255, GetRValue(c), GetGValue(c), GetBValue(c)};
}
inline void rounded(HDC dc, RECT r, float radius, COLORREF fill,
                    COLORREF stroke) {
  if (r.right <= r.left || r.bottom <= r.top)
    return;
  Gdiplus::Graphics graphics(dc);
  graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
  const float x = static_cast<float>(r.left) + .5f,
              y = static_cast<float>(r.top) + .5f,
              w = static_cast<float>(r.right - r.left) - 1,
              h = static_cast<float>(r.bottom - r.top) - 1;
  const float d = std::min(radius * 2, std::min(w, h));
  Gdiplus::GraphicsPath path;
  path.AddArc(x, y, d, d, 180, 90);
  path.AddArc(x + w - d, y, d, d, 270, 90);
  path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
  path.AddArc(x, y + h - d, d, d, 90, 90);
  path.CloseFigure();
  Gdiplus::SolidBrush brush(color(fill));
  Gdiplus::Pen pen(color(stroke), 1);
  graphics.FillPath(&brush, &path);
  graphics.DrawPath(&pen, &path);
}
inline void line(HDC dc, float x, float y, float xx, float yy, COLORREF c,
                 float width = 1.5f) {
  Gdiplus::Graphics g(dc);
  g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
  Gdiplus::Pen p(color(c), width);
  g.DrawLine(&p, x, y, xx, yy);
}
inline RECT scrollThumb(HWND h) {
  RECT r{}; GetClientRect(h,&r);
  SCROLLINFO si{sizeof(si),SIF_ALL};GetScrollInfo(h,SB_CTL,&si);
  const int extent=std::max(1,si.nMax-si.nMin+1);
  const int height=std::min<int>(r.bottom,std::max(24,static_cast<int>(static_cast<double>(r.bottom)*si.nPage/extent)));
  const int range=std::max(1,extent-static_cast<int>(si.nPage));
  const int top=static_cast<int>(static_cast<double>(r.bottom-height)*(si.nPos-si.nMin)/range);
  return {r.right/2-2,top,r.right/2+3,top+height};
}
// Retain the native controls' input, keyboard, focus and accessibility
// semantics. Only their painting is replaced; there is no timer or background
// render loop.
inline LRESULT CALLBACK paintControl(HWND h, UINT m, WPARAM w, LPARAM l,
                                     UINT_PTR kind, DWORD_PTR) {
  if (m == WM_NCDESTROY) {
    RemoveWindowSubclass(h, paintControl, kind);
    return DefSubclassProc(h, m, w, l);
  }
  if (m == WM_ERASEBKGND)
    return 1;
  // Wheel navigation must never silently edit sliders or closed dropdowns.
  if (m == WM_MOUSEWHEEL && (kind==2 || kind==3 || kind==4))
    return SendMessageW(GetParent(h),m,w,l);
  if(kind==4) {
    // Native SCROLLBAR draws synchronously during tracking/focus, outside
    // WM_PAINT. Handle input here so its legacy white rail cannot reappear.
    if(m==WM_LBUTTONDOWN || (m==WM_MOUSEMOVE && GetCapture()==h)) {
      RECT r{};GetClientRect(h,&r);const auto thumb=scrollThumb(h);
      const int y=static_cast<short>(HIWORD(l));
      if(m==WM_LBUTTONDOWN){SetFocus(h);SetCapture(h);SetPropW(h,L"CoreScrollGrab",reinterpret_cast<HANDLE>(static_cast<INT_PTR>((y>=thumb.top && y<thumb.bottom?y-thumb.top:(thumb.bottom-thumb.top)/2)+1)));}
      const int grab=static_cast<int>(reinterpret_cast<INT_PTR>(GetPropW(h,L"CoreScrollGrab")))-1;
      SCROLLINFO si{sizeof(si),SIF_ALL};GetScrollInfo(h,SB_CTL,&si);
      const int range=std::max(0,si.nMax-si.nMin+1-static_cast<int>(si.nPage));
      si.fMask=SIF_POS;si.nPos=si.nMin+static_cast<int>(static_cast<double>(std::clamp<long>(y-grab,0,std::max(0L,r.bottom-(thumb.bottom-thumb.top))))*range/std::max(1L,r.bottom-(thumb.bottom-thumb.top)));
      SetScrollInfo(h,SB_CTL,&si,FALSE);
      SendMessageW(GetParent(h),WM_VSCROLL,SB_THUMBPOSITION,reinterpret_cast<LPARAM>(h));
      InvalidateRect(h,nullptr,FALSE);return 0;
    }
    if(m==WM_LBUTTONUP){if(GetCapture()==h)ReleaseCapture();RemovePropW(h,L"CoreScrollGrab");InvalidateRect(h,nullptr,FALSE);return 0;}
    if(m==WM_KEYDOWN){const int action=w==VK_UP?SB_LINEUP:w==VK_DOWN?SB_LINEDOWN:w==VK_PRIOR?SB_PAGEUP:w==VK_NEXT?SB_PAGEDOWN:-1;if(action>=0)SendMessageW(GetParent(h),WM_VSCROLL,action,reinterpret_cast<LPARAM>(h));return 0;}
    if(m==WM_SETFOCUS || m==WM_KILLFOCUS || m==WM_MOUSEMOVE || m==WM_LBUTTONDBLCLK || m==WM_CAPTURECHANGED){InvalidateRect(h,nullptr,FALSE);return 0;}
  }
  if (m == WM_PAINT || m == WM_PRINTCLIENT) {
    PAINTSTRUCT ps{};
    HDC target = m == WM_PAINT ? BeginPaint(h, &ps) : reinterpret_cast<HDC>(w);
    RECT r{};
    GetClientRect(h, &r);
    HDC dc = CreateCompatibleDC(target);
    HBITMAP bmp = CreateCompatibleBitmap(target, std::max(1L, r.right),
                                         std::max(1L, r.bottom));
    auto oldBmp = SelectObject(dc, bmp);
    auto brush = CreateSolidBrush(background);
    FillRect(dc, &r, brush);
    DeleteObject(brush);
    const float scale = static_cast<float>(GetDpiForWindow(h)) / 96;
    const auto px = [&](float v) { return static_cast<int>(v * scale); };
    auto oldFont = SelectObject(
        dc, reinterpret_cast<HFONT>(SendMessageW(h, WM_GETFONT, 0, 0)));
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, IsWindowEnabled(h) ? text : muted);
    const bool focus = GetFocus() == h;
    POINT cursor{};
    GetCursorPos(&cursor);
    ScreenToClient(h, &cursor);
    const bool hover = PtInRect(&r, cursor) != FALSE;
    const auto edge = focus   ? accent
                      : hover ? RGB(72, 80, 91)
                              : RGB(48, 54, 61);
    wchar_t label[256]{};
    GetWindowTextW(h, label, 256);
    if (kind == 1) { // Push buttons and native auto-checkboxes.
      const bool toggle =
          (GetWindowLongPtrW(h, GWL_STYLE) & BS_TYPEMASK) == BS_AUTOCHECKBOX;
      if (toggle) {
        const bool on = SendMessageW(h, BM_GETCHECK, 0, 0) == BST_CHECKED;
        const int cy = r.bottom / 2;
        RECT track{px(2), cy - px(10), px(40), cy + px(10)};
        rounded(dc, track, 10 * scale, on ? RGB(64, 133, 207) : RGB(59, 67, 77),
                focus ? accent
                : on  ? accent
                      : RGB(59, 67, 77));
        const int left = on ? px(21) : px(5);
        RECT knob{left, cy - px(7), left + px(14), cy + px(7)};
        rounded(dc, knob, 7 * scale, on ? text : RGB(178, 187, 197),
                on ? text : RGB(178, 187, 197));
        r.left = px(52);
        if (focus) {
          RECT ring{px(46), 2, r.right - 2, r.bottom - 2};
          rounded(dc, ring, 5 * scale, background, accent);
        }
      } else {
        RECT button = r;
        InflateRect(&button, -1, -px(2));
        rounded(dc, button, 8 * scale,
                (SendMessageW(h, BM_GETSTATE, 0, 0) & BST_PUSHED)
                    ? RGB(43, 64, 88)
                : hover ? RGB(43, 50, 59)
                        : surface,
                edge);
      }
      DrawTextW(dc, label, -1, &r,
                DT_SINGLELINE | DT_VCENTER | (toggle ? DT_LEFT : DT_CENTER) |
                    DT_END_ELLIPSIS);
    } else if (kind == 2) { // Drop-down field; native popup and selection
                            // remain intact.
      RECT box = r;
      InflateRect(&box, -1, -1);
      rounded(dc, box, 8 * scale, surface, edge);
      r.left += px(12);
      r.right -= px(30);
      auto index = SendMessageW(h, CB_GETCURSEL, 0, 0);
      if (index != CB_ERR) {
        auto length =
            SendMessageW(h, CB_GETLBTEXTLEN, static_cast<WPARAM>(index), 0);
        if (length >= 0 && length < 255)
          SendMessageW(h, CB_GETLBTEXT, static_cast<WPARAM>(index),
                       reinterpret_cast<LPARAM>(label));
      }
      DrawTextW(dc, label, -1, &r,
                DT_SINGLELINE | DT_VCENTER | DT_LEFT | DT_END_ELLIPSIS);
      const float x = static_cast<float>(r.right) + 14 * scale,
                  y = static_cast<float>(r.bottom) / 2;
      line(dc, x - 3 * scale, y - 2 * scale, x, y + 1 * scale, muted);
      line(dc, x, y + 1 * scale, x + 3 * scale, y - 2 * scale, muted);
    } else if (kind ==
               3) { // Slider geometry follows the native thumb for hit testing.
      RECT thumb{};
      SendMessageW(h, TBM_GETTHUMBRECT, 0, reinterpret_cast<LPARAM>(&thumb));
      const int cx = (thumb.left + thumb.right) / 2, cy = r.bottom / 2;
      RECT channel{};
      SendMessageW(h, TBM_GETCHANNELRECT, 0,
                   reinterpret_cast<LPARAM>(&channel));
      RECT rail{channel.left, cy - px(2), channel.right, cy + px(3)};
      rounded(dc, rail, 3 * scale, RGB(48, 54, 61), RGB(48, 54, 61));
      RECT active = rail;
      active.right = cx;
      rounded(dc, active, 3 * scale, RGB(64, 133, 207), RGB(64, 133, 207));
      RECT knob{cx - px(6), cy - px(6), cx + px(7), cy + px(7)};
      rounded(dc, knob, 7 * scale, text, accent);
      if (focus) {
        RECT ring{cx - px(9), cy - px(9), cx + px(10), cy + px(10)};
        Gdiplus::Graphics g(dc);
        g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
        Gdiplus::Pen p(color(accent));
        g.DrawEllipse(&p, static_cast<int>(ring.left),
                      static_cast<int>(ring.top),
                      static_cast<int>(ring.right - ring.left),
                      static_cast<int>(ring.bottom - ring.top));
      }
    } else if (kind == 4) { // Scrollbar retains native range, mouse and
                            // keyboard behavior.
      RECT thumb=scrollThumb(h);
      rounded(dc, thumb, 3 * scale, RGB(86, 96, 109), RGB(86, 96, 109));
    }
    SelectObject(dc, oldFont);
    RECT client{};
    GetClientRect(h, &client);
    BitBlt(target, 0, 0, client.right, client.bottom, dc, 0, 0, SRCCOPY);
    SelectObject(dc, oldBmp);
    DeleteObject(bmp);
    DeleteDC(dc);
    if (m == WM_PAINT)
      EndPaint(h, &ps);
    return 0;
  }
  if (m == WM_MOUSEMOVE) {
    TRACKMOUSEEVENT track{sizeof(track), TME_LEAVE, h, 0};
    TrackMouseEvent(&track);
  }
  auto result = DefSubclassProc(h, m, w, l);
  if (m == WM_SETFOCUS || m == WM_KILLFOCUS || m == WM_MOUSEMOVE ||
      m == WM_MOUSELEAVE || m == WM_LBUTTONDOWN || m == WM_LBUTTONUP ||
      m == WM_KEYDOWN || m == WM_KEYUP || m == BM_SETCHECK ||
      m == CB_SETCURSEL || m == TBM_SETPOS || m == WM_ENABLE)
    InvalidateRect(h, nullptr, FALSE);
  return result;
}
} // namespace core::settingsTheme
