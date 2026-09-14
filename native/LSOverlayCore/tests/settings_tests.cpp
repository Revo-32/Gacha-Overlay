#include "user_settings.hpp"
#include <fstream>
#include <iostream>
#include <limits>
#include <set>
int wmain(int argc, wchar_t **argv) {
  try {
    if (argc != 2)
      throw std::runtime_error("Private test settings path required");
    core::UserSettings settings;
    std::set<std::string> keys;
    unsigned index = 0;
    for (const auto &spec : core::settingSpecs()) {
      if (static_cast<unsigned>(spec.id) != index++ ||
          !keys.insert(spec.key).second)
        throw std::runtime_error("Settings schema identity");
      settings.set(spec.id, spec.maximum + 100);
      if (settings.get(spec.id) != spec.maximum)
        throw std::runtime_error("Setting upper bound");
      settings.set(spec.id, spec.minimum - 100);
      if (settings.get(spec.id) != spec.minimum)
        throw std::runtime_error("Setting lower bound");
      settings.set(spec.id, std::numeric_limits<float>::quiet_NaN());
      if (!std::isfinite(settings.get(spec.id)))
        throw std::runtime_error("Nonfinite setting");
      settings.set(spec.id, spec.maximum);
    }
    settings.save(std::filesystem::absolute(argv[1]));
    core::UserSettings loaded;
    loaded.load(std::filesystem::absolute(argv[1]));
    if (!(loaded == settings))
      throw std::runtime_error("Settings round trip");
    const auto legacy = std::filesystem::absolute(argv[1]).parent_path() /
                        L"legacy-settings.json";
    std::ofstream(legacy, std::ios::binary | std::ios::trunc)
        << R"({"schemaVersion":1,"channel":6})";
    core::UserSettings migrated;
    migrated.load(legacy);
    if (migrated.get(core::Setting::Channel) != 6 ||
        migrated.get(core::Setting::LastChannel) != 6)
      throw std::runtime_error("Legacy channel migration");
    std::filesystem::remove(legacy);
    std::cout << "PASS " << index
              << " settings: identity/bounds/finite/persistence\n";
    return 0;
  } catch (const std::exception &error) {
    std::cerr << error.what() << '\n';
    return 1;
  }
}
