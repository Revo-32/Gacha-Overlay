#include "settings_window.hpp"
#include "settings_theme.hpp"
#include "shell_state.hpp"
#include <commctrl.h>
#include <dwmapi.h>
#include <sstream>
#include <uxtheme.h>
#include <windowsx.h>
namespace core {
namespace {
constexpr wchar_t klass[] = L"LSOverlayCore.Settings";
constexpr unsigned first = 100;
const wchar_t *pages[] = {L"HUD",  L"채팅",      L"미디어",
                          L"판매", L"세션 인원", L"진단"};
} // namespace
SettingsWindow::SettingsWindow(UserSettings &settings,
                               std::function<void()> changed,
                               std::function<void(unsigned)> action,
                               std::filesystem::path storage)
    : settings_(settings), changed_(std::move(changed)),
      action_(std::move(action)),
      storage_(storage.empty() ? UserSettings::path() : std::move(storage)) {
  background_ = CreateSolidBrush(RGB(22, 27, 34));
  surface_ = CreateSolidBrush(RGB(33, 38, 45));
  Gdiplus::GdiplusStartupInput startup;
  if (Gdiplus::GdiplusStartup(&graphicsToken_, &startup, nullptr) !=
      Gdiplus::Ok)
    throw std::runtime_error("Settings graphics initialization failed");
}
SettingsWindow::~SettingsWindow() {
  close();
  if (font_)
    DeleteObject(font_);
  if (titleFont_)
    DeleteObject(titleFont_);
  if (headingFont_)
    DeleteObject(headingFont_);
  DeleteObject(background_);
  DeleteObject(surface_);
  if (graphicsToken_)
    Gdiplus::GdiplusShutdown(graphicsToken_);
}
void SettingsWindow::close() {
  if (window_)
    DestroyWindow(window_);
}
bool SettingsWindow::dialog(MSG &msg) const {
  return window_ && IsDialogMessageW(window_, &msg);
}
void SettingsWindow::status(std::wstring value) {
  connection_ = std::move(value);
  if (window_ && selected_ == 5)
    populate();
}
HWND SettingsWindow::control(const wchar_t *type, const wchar_t *text,
                             DWORD style, unsigned id, HWND parent) {
  auto h = CreateWindowExW(0, type, text, WS_CHILD | WS_VISIBLE | style, 0, 0,
                           0, 0, parent,
                           reinterpret_cast<HMENU>(static_cast<UINT_PTR>(id)),
                           GetModuleHandleW(nullptr), nullptr);
  if (!h)
    throw std::runtime_error("Settings control creation failed");
  SendMessageW(h, WM_SETFONT, reinterpret_cast<WPARAM>(font_), TRUE);
  const UINT_PTR kind = wcscmp(type, L"BUTTON") == 0         ? 1
                        : wcscmp(type, L"COMBOBOX") == 0     ? 2
                        : wcscmp(type, TRACKBAR_CLASSW) == 0 ? 3
                        : wcscmp(type, L"SCROLLBAR") == 0    ? 4
                                                             : 0;
  if (kind) {
    SetWindowTheme(h, L"", L"");
    SetWindowSubclass(h, settingsTheme::paintControl, kind, 0);
  }
  return h;
}
void SettingsWindow::show(HWND owner) {
  if (window_) {
    ShowWindow(window_, SW_RESTORE);
    SetForegroundWindow(window_);
    return;
  }
  INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_BAR_CLASSES};
  InitCommonControlsEx(&controls);
  WNDCLASSEXW wc{sizeof(wc)};
  wc.hInstance = GetModuleHandleW(nullptr);
  wc.lpfnWndProc = proc;
  wc.lpszClassName = klass;
  wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
  wc.hIcon = LoadIconW(wc.hInstance, MAKEINTRESOURCEW(101));
  if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
    throw std::runtime_error("Settings class registration failed");
  dpi_ = GetDpiForWindow(owner);
  RECT bounds{};
  GetWindowRect(owner, &bounds);
  MONITORINFO monitor{sizeof(monitor)};
  GetMonitorInfoW(MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST), &monitor);
  const int width = std::min(
                toPixels(900, dpi_),
                static_cast<int>(monitor.rcWork.right - monitor.rcWork.left)),
            height = std::min(
                toPixels(820, dpi_),
                static_cast<int>(monitor.rcWork.bottom - monitor.rcWork.top));
  window_ = CreateWindowExW(
      WS_EX_CONTROLPARENT, klass, L"LS Overlay Core · 설정",
      WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
      monitor.rcWork.left +
          (monitor.rcWork.right - monitor.rcWork.left - width) / 2,
      monitor.rcWork.top +
          (monitor.rcWork.bottom - monitor.rcWork.top - height) / 2,
      width, height, owner, nullptr, wc.hInstance, this);
  if (!window_)
    throw std::runtime_error("Settings window creation failed");
  BOOL dark = TRUE;
  DwmSetWindowAttribute(window_, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark,
                        sizeof(dark));
  ShowWindow(window_, SW_SHOW);
  SetForegroundWindow(window_);
}
void SettingsWindow::selectPage(unsigned page) {
  selected_ = std::min(page, 5U);
  offset_ = 0;
  SendMessageW(navigation_, LB_SETCURSEL, selected_, 0);
  populate();
}
void SettingsWindow::populate() {
  loading_ = true;
  for (auto &r : rows_) {
    DestroyWindow(r.label);
    DestroyWindow(r.editor);
    if (r.value)
      DestroyWindow(r.value);
  }
  rows_.clear();
  for (auto h : actions_)
    DestroyWindow(h);
  actions_.clear();
  int y = 12;
  const auto button = [&](const wchar_t *label, unsigned id) {
    auto h = control(L"BUTTON", label, BS_PUSHBUTTON | WS_TABSTOP, id, page_);
    actions_.push_back(h);
    return h;
  };
  if (selected_ == 1) {
    button(L"Clean", 500);
    button(L"모던", 501);
    button(L"고가독성", 502);
    button(L"GTA 레거시", 503);
    y = 56;
  }
  for (unsigned i = 0; i < settingSpecs().size(); ++i) {
    const auto &s = settingSpecs()[i];
    if (s.page != selected_)
      continue;
    Row r{};
    r.spec = i;
    r.y = y;
    r.height = 60;
    if (s.choices && !*s.choices) {
      r.label = control(L"STATIC", L"", 0, 0, page_);
      r.editor = control(L"BUTTON", s.label, BS_AUTOCHECKBOX | WS_TABSTOP,
                         first + i, page_);
      r.height = 38;
      SendMessageW(r.editor, BM_SETCHECK,
                   settings_.enabled(s.id) ? BST_CHECKED : BST_UNCHECKED, 0);
    } else {
      r.label = control(L"STATIC", s.label, 0, 0, page_);
      if (s.choices) {
        r.height = 76;
        r.editor = control(L"COMBOBOX", L"",
                           CBS_DROPDOWNLIST | CBS_OWNERDRAWFIXED |
                               CBS_HASSTRINGS | WS_VSCROLL | WS_TABSTOP,
                           first + i, page_);
        SendMessageW(r.editor, CB_SETITEMHEIGHT, static_cast<WPARAM>(-1),
                     toPixels(32, dpi_));
        SendMessageW(r.editor, CB_SETITEMHEIGHT, 0, toPixels(30, dpi_));
        std::wistringstream stream(s.choices);
        std::wstring part;
        while (std::getline(stream, part, L'|'))
          SendMessageW(r.editor, CB_ADDSTRING, 0,
                       reinterpret_cast<LPARAM>(part.c_str()));
        SendMessageW(r.editor, CB_SETCURSEL,
                     static_cast<WPARAM>(std::lround(
                         (settings_.get(s.id) - s.minimum) / s.step)),
                     0);
      } else {
        r.editor =
            control(TRACKBAR_CLASSW, L"", TBS_HORZ | TBS_NOTICKS | WS_TABSTOP,
                    first + i, page_);
        SendMessageW(r.editor, TBM_SETRANGE, TRUE,
                     MAKELPARAM(0, static_cast<int>(std::lround(
                                       (s.maximum - s.minimum) / s.step))));
        SendMessageW(r.editor, TBM_SETPOS, TRUE,
                     static_cast<LPARAM>(std::lround(
                         (settings_.get(s.id) - s.minimum) / s.step)));
        r.value = control(L"STATIC", L"", SS_RIGHT, 0, page_);
      }
    }
    rows_.push_back(r);
    y += r.height;
  }
  if (selected_ == 0) {
    button(L"현재 모니터 중앙으로", 510);
    button(L"HUD 크기 초기화", 511);
    y += 48;
  }
  if (selected_ == 2) {
    button(L"미디어 캐시 비우기", 512);
    y += 48;
  }
  if (selected_ == 3) {
    button(L"알림음 테스트", 513);
    y += 48;
  }
  if (selected_ == 5) {
    auto label = control(
        L"STATIC",
        (L"연결 상태\r\n\r\n" + connection_ +
         L"\r\n\r\n현재 연결은 판매 읽기 전용 개발 검증입니다.\r\n설정은 "
         L"Core에만 저장되며 Full에는 영향을 주지 않습니다.")
            .c_str(),
        SS_LEFT, 0, page_);
    actions_.push_back(label);
    y = 220;
  }
  total_ = y + 12;
  loading_ = false;
  arrange();
}
void SettingsWindow::arrange() {
  if (!page_)
    return;
  RECT r{};
  GetClientRect(window_, &r);
  const int w = static_cast<int>(toDip(r.right, dpi_)),
            h = static_cast<int>(toDip(r.bottom, dpi_));
  const auto place = [&](HWND child, int x, int y, int width, int height) {
    MoveWindow(child, toPixels(static_cast<float>(x), dpi_),
               toPixels(static_cast<float>(y), dpi_),
               toPixels(static_cast<float>(std::max(width, 1)), dpi_),
               toPixels(static_cast<float>(std::max(height, 1)), dpi_), TRUE);
  };
  place(navigation_, 30, 92, 158, h - 164);
  place(page_, 214, 132, w - 248, h - 200);
  place(status_, 32, h - 46, w - 64, 26);
  const int height = h - 200, width = w - 270;
  offset_ = std::clamp(offset_, 0, std::max(0, total_ - height));
  place(scrollbar_, w - 31, 132, 12, height);
  SCROLLINFO si{sizeof(si), SIF_RANGE | SIF_PAGE | SIF_POS};
  si.nMin = 0;
  si.nMax = total_ - 1;
  si.nPage = static_cast<UINT>(height);
  si.nPos = offset_;
  SetScrollInfo(scrollbar_, SB_CTL, &si, FALSE);
  InvalidateRect(scrollbar_, nullptr, FALSE);
  ShowWindow(scrollbar_, total_ > height ? SW_SHOW : SW_HIDE);
  for (const auto &row : rows_) {
    const auto &s = settingSpecs()[row.spec];
    int y = row.y - offset_;
    if (s.choices && !*s.choices) {
      place(row.label, 0, y, 1, 1);
      place(row.editor, 10, y, width - 20, 30);
    } else {
      place(row.label, 10, y, width - 20, 21);
      place(row.editor, 10, y + 23,
            s.choices ? std::min(360, width - 20) : width - 90,
            s.choices ? 250 : 28);
    }
    if (row.value) {
      wchar_t number[32]{};
      swprintf_s(number, L"%.2f", settings_.get(s.id));
      SetWindowTextW(row.value, number);
      place(row.value, width - 72, y + 26, 62, 22);
    }
  }
  for (unsigned i = 0; i < actions_.size(); ++i) {
    if (selected_ == 1)
      place(actions_[i], 10 + static_cast<int>(i) * 108, 8 - offset_, 104, 32);
    else if (selected_ == 5)
      place(actions_[i], 10, 12 - offset_, width - 20, 210);
    else
      place(actions_[i], 10 + static_cast<int>(i) * 190, total_ - 58 - offset_,
            184, 32);
  }
  InvalidateRect(page_, nullptr, TRUE);
  InvalidateRect(window_, nullptr, FALSE);
}
void SettingsWindow::commit() {
  changed_();
  SetWindowTextW(status_, L"변경 사항을 적용했습니다. 자동 저장 중…");
  SetTimer(window_, 1, 400, nullptr);
}
LRESULT CALLBACK SettingsWindow::proc(HWND h, UINT m, WPARAM w, LPARAM l) {
  auto *self =
      reinterpret_cast<SettingsWindow *>(GetWindowLongPtrW(h, GWLP_USERDATA));
  if (m == WM_NCCREATE) {
    self = static_cast<SettingsWindow *>(
        reinterpret_cast<CREATESTRUCTW *>(l)->lpCreateParams);
    SetWindowLongPtrW(h, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
  }
  if (!self)
    return DefWindowProcW(h, m, w, l);
  try {
    return self->message(h, m, w, l);
  } catch (const std::exception &) {
    if (self->status_)
      SetWindowTextW(
          self->status_,
          L"설정을 적용하거나 저장하지 못했습니다. 기존 파일은 유지됩니다.");
    return 0;
  }
}
LRESULT SettingsWindow::message(HWND h, UINT m, WPARAM w, LPARAM l) {
  switch (m) {
  case WM_CREATE:
    if (reinterpret_cast<CREATESTRUCTW *>(l)->style & WS_CHILD)
      return 0;
    window_ = h;
    font_ = CreateFontW(-toPixels(13, dpi_), 0, 0, 0, FW_NORMAL, FALSE, FALSE,
                        FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                        CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH,
                        L"맑은 고딕");
    titleFont_ =
        CreateFontW(-toPixels(23, dpi_), 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                    DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"맑은 고딕");
    headingFont_ =
        CreateFontW(-toPixels(18, dpi_), 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                    DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"맑은 고딕");
    navigation_ = control(L"LISTBOX", L"",
                          LBS_NOTIFY | LBS_NOINTEGRALHEIGHT |
                              LBS_OWNERDRAWFIXED | LBS_HASSTRINGS | WS_TABSTOP,
                          10, h);
    for (auto title : pages)
      SendMessageW(navigation_, LB_ADDSTRING, 0,
                   reinterpret_cast<LPARAM>(title));
    SendMessageW(navigation_, LB_SETITEMHEIGHT, 0, toPixels(48, dpi_));
    page_ = CreateWindowExW(WS_EX_CONTROLPARENT, klass, L"",
                            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, 1, 1,
                            h, nullptr, GetModuleHandleW(nullptr), this);
    scrollbar_ = control(L"SCROLLBAR", L"", SBS_VERT, 11, h);
    status_ = control(L"STATIC",
                      L"변경 사항은 즉시 반영되며 Core에만 자동 저장됩니다.", 0,
                      0, h);
    selectPage(selected_);
    return 0;
  case WM_SIZE:
    if (h == window_)
      arrange();
    return 0;
  case WM_PAINT: {
    PAINTSTRUCT ps{};
    auto dc = BeginPaint(h, &ps);
    RECT r{};
    GetClientRect(h, &r);
    FillRect(dc, &r, background_);
    if (h == window_) {
      const auto px = [&](float value) { return toPixels(value, dpi_); };
      auto frame = r;
      InflateRect(&frame, -px(14), -px(14));
      settingsTheme::rounded(dc, frame, 18.0f * static_cast<float>(dpi_) / 96,
                             settingsTheme::background, RGB(48, 54, 61));
      SetBkMode(dc, TRANSPARENT);
      SetTextColor(dc, settingsTheme::text);
      auto old = SelectObject(dc, titleFont_);
      RECT title{px(34), px(28), r.right - px(30), px(64)};
      DrawTextW(dc, L"LS Overlay  ·  설정", -1, &title,
                DT_LEFT | DT_SINGLELINE | DT_VCENTER);
      SelectObject(dc, headingFont_);
      RECT heading{px(224), px(86), r.right - px(30), px(120)};
      DrawTextW(dc, pages[selected_], -1, &heading, DT_SINGLELINE | DT_VCENTER);
      settingsTheme::line(
          dc, static_cast<float>(px(204)), static_cast<float>(px(92)),
          static_cast<float>(px(204)), static_cast<float>(r.bottom - px(70)),
          RGB(48, 54, 61), 1);
      SelectObject(dc, old);
    }
    EndPaint(h, &ps);
    return 0;
  }
  case WM_ERASEBKGND: {
    RECT r{};
    GetClientRect(h, &r);
    FillRect(reinterpret_cast<HDC>(w), &r, background_);
    return 1;
  }
  case WM_CTLCOLORSTATIC:
  case WM_CTLCOLORBTN:
  case WM_CTLCOLORLISTBOX:
  case WM_CTLCOLOREDIT:
    SetTextColor(reinterpret_cast<HDC>(w), reinterpret_cast<HWND>(l) == status_
                                               ? settingsTheme::muted
                                               : settingsTheme::text);
    SetBkColor(reinterpret_cast<HDC>(w), RGB(22, 27, 34));
    return reinterpret_cast<LRESULT>(background_);
  case WM_HSCROLL:
    if (!loading_ && l) {
      const unsigned id =
          static_cast<unsigned>(GetDlgCtrlID(reinterpret_cast<HWND>(l)));
      if (id >= first && id < first + settingSpecs().size()) {
        const auto &s = settingSpecs()[id - first];
        settings_.set(s.id,
                      s.minimum + s.step * static_cast<float>(SendMessageW(
                                               reinterpret_cast<HWND>(l),
                                               TBM_GETPOS, 0, 0)));
        commit();
        arrange();
      }
    }
    return 0;
  case WM_COMMAND: {
    const auto id = LOWORD(w);
    if (id == 10 && HIWORD(w) == LBN_SELCHANGE) {
      selectPage(
          static_cast<unsigned>(SendMessageW(navigation_, LB_GETCURSEL, 0, 0)));
      return 0;
    }
    if (id >= 500 && id <= 503) {
      settings_.preset(id - 500);
      commit();
      populate();
      return 0;
    }
    if (id >= 510 && id <= 513) {
      action_(id);
      return 0;
    }
    if (id >= first && id < first + settingSpecs().size() && !loading_) {
      const auto &s = settingSpecs()[id - first];
      if (!s.choices)
        return 0;
      if (!*s.choices && HIWORD(w) == BN_CLICKED)
        settings_.set(s.id, SendMessageW(reinterpret_cast<HWND>(l), BM_GETCHECK,
                                         0, 0) == BST_CHECKED
                                ? 1.0f
                                : 0.0f);
      else if (*s.choices && HIWORD(w) == CBN_SELCHANGE)
        settings_.set(s.id,
                      s.minimum + s.step * static_cast<float>(SendMessageW(
                                               reinterpret_cast<HWND>(l),
                                               CB_GETCURSEL, 0, 0)));
      else
        return 0;
      commit();
    }
    return 0;
  }
  case WM_MOUSEWHEEL:
    offset_ -= GET_WHEEL_DELTA_WPARAM(w) / WHEEL_DELTA * 60;
    arrange();
    return 0;
  case WM_VSCROLL: {
    SCROLLINFO si{sizeof(si), SIF_ALL};
    GetScrollInfo(scrollbar_, SB_CTL, &si);
    switch (LOWORD(w)) {
    case SB_THUMBTRACK:
      offset_ = si.nTrackPos;
      break;
    case SB_THUMBPOSITION:
      offset_ = si.nPos;
      break;
    case SB_LINEUP:
      offset_ -= 24;
      break;
    case SB_LINEDOWN:
      offset_ += 24;
      break;
    case SB_PAGEUP:
      offset_ -= static_cast<int>(si.nPage);
      break;
    case SB_PAGEDOWN:
      offset_ += static_cast<int>(si.nPage);
      break;
    }
    arrange();
    return 0;
  }
  case WM_MEASUREITEM: {
    auto item = reinterpret_cast<MEASUREITEMSTRUCT *>(l);
    item->itemHeight = static_cast<UINT>(toPixels(30, dpi_));
    return TRUE;
  }
  case WM_DRAWITEM: {
    auto *item = reinterpret_cast<DRAWITEMSTRUCT *>(l);
    if (item->CtlType == ODT_COMBOBOX) {
      FillRect(item->hDC, &item->rcItem,
               (item->itemState & ODS_SELECTED) ? surface_ : background_);
      if (item->itemID == static_cast<UINT>(-1))
        return TRUE;
      wchar_t value[256]{};
      if (SendMessageW(item->hwndItem, CB_GETLBTEXTLEN, item->itemID, 0) < 256)
        SendMessageW(item->hwndItem, CB_GETLBTEXT, item->itemID,
                     reinterpret_cast<LPARAM>(value));
      SetBkMode(item->hDC, TRANSPARENT);
      SetTextColor(item->hDC, settingsTheme::text);
      auto rect = item->rcItem;
      rect.left += toPixels(12, dpi_);
      DrawTextW(item->hDC, value, -1, &rect,
                DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS);
      return TRUE;
    }
    if (item->CtlID != 10 || item->itemID >= std::size(pages))
      break;
    FillRect(item->hDC, &item->rcItem, background_);
    const bool selected = (item->itemState & ODS_SELECTED) != 0;
    auto rect = item->rcItem;
    InflateRect(&rect, -toPixels(2, dpi_), -toPixels(3, dpi_));
    if (selected)
      settingsTheme::rounded(item->hDC, rect,
                             9.0f * static_cast<float>(dpi_) / 96,
                             RGB(35, 58, 81), settingsTheme::accent);
    SetBkMode(item->hDC, TRANSPARENT);
    SetTextColor(item->hDC,
                 selected ? settingsTheme::accent : settingsTheme::text);
    rect.left += toPixels(18, dpi_);
    DrawTextW(item->hDC, pages[item->itemID], -1, &rect,
              DT_SINGLELINE | DT_VCENTER | DT_LEFT);
    return TRUE;
  }
  case WM_TIMER:
    if (h == window_ && w == 1) {
      KillTimer(h, 1);
      settings_.save(storage_);
      SetWindowTextW(status_,
                     L"적용 및 저장 완료 · Full 설정은 변경하지 않았습니다.");
    }
    return 0;
  case WM_DPICHANGED:
    if (h == window_) {
      dpi_ = HIWORD(w);
      auto nextFont = CreateFontW(
          -toPixels(13, dpi_), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
          DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
          CLEARTYPE_QUALITY, DEFAULT_PITCH, L"맑은 고딕");
      SendMessageW(navigation_, WM_SETFONT, reinterpret_cast<WPARAM>(nextFont),
                   TRUE);
      SendMessageW(status_, WM_SETFONT, reinterpret_cast<WPARAM>(nextFont),
                   TRUE);
      const auto old = font_;
      font_ = nextFont;
      populate();
      DeleteObject(old);
      DeleteObject(titleFont_);
      DeleteObject(headingFont_);
      titleFont_ = CreateFontW(-toPixels(23, dpi_), 0, 0, 0, FW_BOLD, FALSE,
                               FALSE, FALSE, DEFAULT_CHARSET, 0, 0,
                               CLEARTYPE_QUALITY, 0, L"맑은 고딕");
      headingFont_ = CreateFontW(-toPixels(18, dpi_), 0, 0, 0, FW_BOLD, FALSE,
                                 FALSE, FALSE, DEFAULT_CHARSET, 0, 0,
                                 CLEARTYPE_QUALITY, 0, L"맑은 고딕");
      SendMessageW(navigation_, LB_SETITEMHEIGHT, 0, toPixels(48, dpi_));
      const auto r = reinterpret_cast<RECT *>(l);
      SetWindowPos(h, nullptr, r->left, r->top, r->right - r->left,
                   r->bottom - r->top, SWP_NOZORDER | SWP_NOACTIVATE);
      arrange();
    }
    return 0;
  case WM_GETMINMAXINFO:
    reinterpret_cast<MINMAXINFO *>(l)->ptMinTrackSize = {toPixels(740, dpi_),
                                                         toPixels(480, dpi_)};
    return 0;
  case WM_CLOSE:
    DestroyWindow(h);
    return 0;
  case WM_DESTROY:
    if (h == window_) {
      KillTimer(h, 1);
      settings_.save(storage_);
    }
    return 0;
  case WM_NCDESTROY:
    if (h == window_) {
      window_ = page_ = navigation_ = status_ = scrollbar_ = nullptr;
      rows_.clear();
      actions_.clear();
      if (font_) {
        DeleteObject(font_);
        font_ = nullptr;
      }
      if (titleFont_) {
        DeleteObject(titleFont_);
        titleFont_ = nullptr;
      }
      if (headingFont_) {
        DeleteObject(headingFont_);
        headingFont_ = nullptr;
      }
    }
    SetWindowLongPtrW(h, GWLP_USERDATA, 0);
    return DefWindowProcW(h, m, w, l);
  default:
    return DefWindowProcW(h, m, w, l);
  }
  return DefWindowProcW(h, m, w, l);
}
} // namespace core
