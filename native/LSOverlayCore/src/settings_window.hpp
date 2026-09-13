#pragma once
#include "user_settings.hpp"
#include <functional>
#include <windows.h>
namespace core {
class SettingsWindow {
public:
  SettingsWindow(UserSettings &settings, std::function<void()> changed,
                 std::function<void(unsigned)> action,
                 std::filesystem::path storage = {});
  ~SettingsWindow();
  void show(HWND owner);
  void close();
  bool dialog(MSG &message) const;
  HWND handle() const { return window_; }
  void status(std::wstring value);
  void selectPage(unsigned page);

private:
  static LRESULT CALLBACK proc(HWND, UINT, WPARAM, LPARAM);
  LRESULT message(HWND, UINT, WPARAM, LPARAM);
  void populate();
  void arrange();
  void commit();
  HWND control(const wchar_t *type, const wchar_t *text, DWORD style,
               unsigned id, HWND parent);
  struct Row {
    HWND label, editor, value;
    unsigned spec;
    int y, height;
  };
  UserSettings &settings_;
  std::function<void()> changed_;
  std::function<void(unsigned)> action_;
  HWND window_ = nullptr, page_ = nullptr, navigation_ = nullptr,
       status_ = nullptr, scrollbar_ = nullptr;
  HFONT font_ = nullptr, titleFont_ = nullptr, headingFont_ = nullptr;
  HBRUSH background_ = nullptr, surface_ = nullptr;
  ULONG_PTR graphicsToken_ = 0;
  std::vector<Row> rows_;
  std::vector<HWND> actions_;
  unsigned selected_ = 0, dpi_ = 96;
  int offset_ = 0, total_ = 0;
  bool loading_ = false;
  std::wstring connection_;
  std::filesystem::path storage_;
};
} // namespace core
