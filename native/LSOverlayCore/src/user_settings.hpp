#pragma once
#include <array>
#include <filesystem>
#include <string>
#include <vector>
namespace core {
enum class Setting : unsigned {
  HudOpacity,
  ChromeOpacity,
  ChatOpacity,
  SalesOpacity,
  DetailOpacity,
  ModifierDrag,
  Font,
  FontSize,
  NicknameOutline,
  MessageOutline,
  MentionBackground,
  NicknameThickness,
  MessageThickness,
  LineHeight,
  Spacing,
  RolePosition,
  ReactionSize,
  MaxLines,
  Images,
  Emoji,
  Stickers,
  Animated,
  HideUrl,
  LargeImages,
  Enlarge,
  SalesTracking,
  SalesCurrent,
  SalesWaiting,
  SalesProduct,
  SalesNext,
  SalesSound,
  SalesVolume,
  NotifyNext,
  NotifyCurrent,
  DetailHeight,
  ShowSession,
  Host,
  ShowTime,
  Channel,
  ChannelKeys,
  EmojiSize,
  SessionOutline,
  SessionOutlineThickness,
  Count
};
struct SettingSpec {
  Setting id;
  unsigned page;
  const char *key;
  const wchar_t *label;
  float initial, minimum, maximum, step;
  const wchar_t *choices;
};
const std::vector<SettingSpec> &settingSpecs();
class UserSettings {
public:
  UserSettings();
  float get(Setting key) const { return values_[static_cast<unsigned>(key)]; }
  bool enabled(Setting key) const { return get(key) != 0; }
  void set(Setting key, float value);
  void preset(unsigned index);
  void load(const std::filesystem::path &path);
  void save(const std::filesystem::path &path) const;
  static std::filesystem::path path();
  bool operator==(const UserSettings &) const = default;

private:
  std::array<float, static_cast<unsigned>(Setting::Count)> values_{};
};
} // namespace core
