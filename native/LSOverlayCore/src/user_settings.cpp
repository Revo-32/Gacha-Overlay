#include "user_settings.hpp"
#include "json.hpp"
#include "media_package.hpp"
#include <algorithm>
#include <cmath>
#include <fstream>
#include <shlobj.h>
namespace core {
const std::vector<SettingSpec> &settingSpecs() {
  static const std::vector<SettingSpec> specs{
      {Setting::HudOpacity, 0, "hudOpacity", L"HUD 배경 불투명도 (%)", 62, 0,
       100, 1, nullptr},
      {Setting::ChromeOpacity, 0, "chromeOpacity", L"상단 배경 불투명도 (%)", 0,
       0, 100, 1, nullptr},
      {Setting::ChatOpacity, 0, "chatOpacity", L"채팅 배경 불투명도 (%)", 35, 0,
       100, 1, nullptr},
      {Setting::SalesOpacity, 0, "salesOpacity", L"판매 배경 불투명도 (%)", 100,
       0, 100, 1, nullptr},
      {Setting::DetailOpacity, 0, "detailOpacity",
       L"대기열 상세 배경 불투명도 (%)", 100, 0, 100, 1, nullptr},
      {Setting::ModifierDrag, 0, "modifierDrag",
       L"잠금 상태에서 Alt + 드래그 사용", 0, 0, 1, 1, L""},
      {Setting::Font, 1, "font", L"글꼴", 2, 0, 4, 1,
       L"한국기계연구원|Pretendard|Wanted Sans|GTA 레거시|조선굴림체"},
      {Setting::FontSize, 1, "fontSize", L"글꼴 크기 (pt)", 12, 8, 32, 0.5f,
       nullptr},
      {Setting::NicknameOutline, 1, "nicknameOutline", L"닉네임 외곽선", 1, 0,
       1, 1, L""},
      {Setting::MessageOutline, 1, "messageOutline", L"메시지 외곽선", 1, 0, 1,
       1, L""},
      {Setting::MentionBackground, 1, "mentionBackground",
       L"나를 언급한 메시지 배경 강조", 1, 0, 1, 1, L""},
      {Setting::NicknameThickness, 1, "nicknameThickness",
       L"닉네임 외곽선 두께 (DIP)", 1.5f, 0, 10, 0.25f, nullptr},
      {Setting::MessageThickness, 1, "messageThickness",
       L"메시지 외곽선 두께 (DIP)", 1.5f, 0, 10, 0.25f, nullptr},
      {Setting::LineHeight, 1, "lineHeight", L"줄 간격 (0 = 최소 여백)", 1.4f, 0, 1.65f, 0.05f,
       nullptr},
      {Setting::Spacing, 1, "spacing", L"작성자 간격 (DIP)", 1, -2, 48, 0.25f,
       nullptr},
      {Setting::RolePosition, 1, "rolePosition", L"역할 아이콘 위치", 0, 0, 2,
       1, L"닉네임 왼쪽|닉네임 바로 오른쪽|행 오른쪽"},
      {Setting::ReactionSize, 1, "reactionSize", L"반응 크기 (DIP)", 18, 14, 42,
       1, nullptr},
      {Setting::MaxLines, 1, "maxLines", L"본문 최대 줄 수", 2, 1, 3, 1,
       L"1줄|2줄|3줄"},
      {Setting::Images, 2, "images", L"이미지 미리보기 표시", 1, 0, 1, 1, L""},
      {Setting::Emoji, 2, "emoji", L"커스텀 이모지 표시", 1, 0, 1, 1, L""},
      {Setting::Stickers, 2, "stickers", L"스티커 표시", 1, 0, 1, 1, L""},
      {Setting::Animated, 2, "animated", L"움직이는 미디어 재생", 1, 0, 1, 1,
       L""},
      {Setting::HideUrl, 2, "hideUrl",
       L"미리보기 로드 후 해당 원본 URL만 숨기기", 1, 0, 1, 1, L""},
      {Setting::LargeImages, 2, "largeImages", L"이미지 크기", 1, 0, 1, 1,
       L"작게|크게"},
      {Setting::Enlarge, 2, "enlarge", L"미리보기 클릭 동작", 1, 0, 1, 1,
       L"썸네일만|잠금 해제 시 클릭 확대"},
      {Setting::SalesTracking, 3, "salesTracking", L"판매 현황 표시", 1, 0, 1,
       1, L""},
      {Setting::SalesCurrent, 3, "salesCurrent", L"현재 판매자 표시", 1, 0, 1,
       1, L""},
      {Setting::SalesWaiting, 3, "salesWaiting", L"대기 인원 표시", 1, 0, 1, 1,
       L""},
      {Setting::SalesProduct, 3, "salesProduct", L"상품 표시", 1, 0, 1, 1, L""},
      {Setting::SalesNext, 3, "salesNext", L"다음 대기자 표시", 1, 0, 1, 1,
       L""},
      {Setting::SalesSound, 3, "salesSound", L"판매 차례 알림음", 1, 0, 1, 1,
       L""},
      {Setting::SalesVolume, 3, "salesVolume", L"알림음 볼륨 (%)", 50, 0, 100,
       1, nullptr},
      {Setting::NotifyNext, 3, "notifyNext", L"내가 다음 차례일 때 알림", 1, 0,
       1, 1, L""},
      {Setting::NotifyCurrent, 3, "notifyCurrent",
       L"내 판매 차례가 시작될 때 알림", 1, 0, 1, 1, L""},
      {Setting::DetailHeight, 3, "detailHeight", L"대기열 상세 최대 높이 (DIP)",
       280, 120, 640, 10, nullptr},
      {Setting::ShowSession, 4, "showSession", L"GTA 세션 인원 표시", 1, 0, 1,
       1, L""},
      {Setting::Host, 4, "host", L"세션 호스트", 1, 1, 2, 1,
       L"DE-SSANTA|-TheFirstStar-"},
      {Setting::ShowTime, 1, "showTime", L"메시지 시간 표시", 1, 0, 1, 1, L""},
      {Setting::Channel,0,"channel",L"표시할 채팅방",0,0,7,1,L"메인|1호실|2호실|3호실|4호실|5호실|6호실|잡떡"},
      {Setting::ChannelKeys,0,"channelKeys",L"이전 / 다음 채팅방 단축키",1,0,3,1,L"미지정|Ctrl + Alt + ← / →|Alt + ← / →|F7 / F8"},
      {Setting::EmojiSize,1,"emojiSize",L"채팅 이모지 크기 (DIP)",28,12,64,1,nullptr},
      {Setting::SessionOutline,4,"sessionOutline",L"세션 인원 글자 외곽선",1,0,1,1,L""},
      {Setting::SessionOutlineThickness,4,"sessionOutlineThickness",L"세션 인원 외곽선 두께 (DIP)",1.5f,0.5f,4,0.25f,nullptr}};
  return specs;
}
UserSettings::UserSettings() {
  for (const auto &spec : settingSpecs())
    set(spec.id, spec.initial);
}
void UserSettings::set(Setting key, float value) {
  const auto &s = settingSpecs().at(static_cast<unsigned>(key));
  if (!std::isfinite(value))
    value = s.initial;
  values_[static_cast<unsigned>(key)] =
      std::clamp(std::round((value - s.minimum) / s.step) * s.step + s.minimum,
                 s.minimum, s.maximum);
}
void UserSettings::preset(unsigned index) {
  const unsigned fonts[] = {1, 0, 2, 3};
  const float sizes[] = {12.5f, 12.5f, 14, 12.25f};
  const float opacities[] = {62, 68, 90, 42},
              lines[] = {1.42f, 1.4f, 1.5f, 1.32f},
              spaces[] = {1.5f, 1.25f, 2.5f, 0.5f};
  index = std::min(index, 3U);
  set(Setting::Font, static_cast<float>(fonts[index]));
  set(Setting::FontSize, sizes[index]);
  set(Setting::HudOpacity, opacities[index]);
  set(Setting::LineHeight, lines[index]);
  set(Setting::Spacing, spaces[index]);
  set(Setting::NicknameOutline, index == 2 ? 1.0f : 0.0f);
  set(Setting::MessageOutline, index == 2 ? 1.0f : 0.0f);
  set(Setting::MaxLines, 2);
  set(Setting::Images, 1);
}
std::filesystem::path UserSettings::path() {
  PWSTR root = nullptr;
  if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT,
                                  nullptr, &root)))
    throw std::runtime_error("Core settings directory unavailable");
  const auto value =
      std::filesystem::path(root) / L"LSOverlayCore" / L"settings.json";
  CoTaskMemFree(root);
  return value;
}
void UserSettings::load(const std::filesystem::path &path) {
  validateMediaPath(path);
  if (!std::filesystem::exists(path))
    return;
  const auto size = std::filesystem::file_size(path);
  if (size > 16384)
    throw std::runtime_error("Core settings size invalid");
  std::ifstream input(path, std::ios::binary);
  std::string bytes((std::istreambuf_iterator<char>(input)), {});
  Json json(bytes);
  if (!yyjson_is_obj(json.root()))
    throw std::runtime_error("Core settings object required");
  for (const auto &s : settingSpecs())
    if (auto *value = field(json.root(), s.key); yyjson_is_num(value))
      set(s.id, static_cast<float>(yyjson_get_num(value)));
}
void UserSettings::save(const std::filesystem::path &path) const {
  validateMediaPath(path);
  std::filesystem::create_directories(path.parent_path());
  const auto temporary = path.wstring() + L".pending";
  validateMediaPath(temporary);
  std::ofstream out(temporary, std::ios::binary | std::ios::trunc);
  out << "{\"schemaVersion\":1";
  for (const auto &s : settingSpecs())
    out << ",\"" << s.key << "\":" << get(s.id);
  out << "}";
  out.close();
  if (!out || !MoveFileExW(temporary.c_str(), path.c_str(),
                           MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    throw std::runtime_error("Core settings save failed");
}
} // namespace core
