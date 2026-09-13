#pragma once
#include <algorithm>
#include <cmath>
#include <condition_variable>
#include <mmsystem.h>
#include <mutex>
#include <thread>
#include <vector>
#include <windows.h>
namespace core {
// An app-local PCM buffer scales amplitude; never changes Windows device
// volume.
class SalesSound {
public:
  SalesSound() : worker_([this](std::stop_token stop) { run(stop); }) {}
  ~SalesSound() {
    worker_.request_stop();
    wake_.notify_all();
  }
  void play(float volume) {
    std::lock_guard lock(mutex_);
    volume_ = std::clamp(volume, 0.0f, 100.0f);
    pending_ = true;
    wake_.notify_all();
  }

private:
  void run(std::stop_token stop) {
    while (!stop.stop_requested()) {
      float volume = 0;
      {
        std::unique_lock lock(mutex_);
        wake_.wait(lock, stop, [&] { return pending_; });
        if (stop.stop_requested())
          break;
        volume = volume_;
        pending_ = false;
      }
      if (volume <= 0)
        continue;
      constexpr unsigned rate = 22050, count = rate / 4;
      std::vector<unsigned char> wav(44 + count * 2);
      const auto put = [&](unsigned p, unsigned v, unsigned n) {
        for (unsigned i = 0; i < n; ++i)
          wav[p + i] = static_cast<unsigned char>(v >> (i * 8));
      };
      memcpy(wav.data(), "RIFF", 4);
      put(4, static_cast<unsigned>(wav.size() - 8), 4);
      memcpy(wav.data() + 8, "WAVEfmt ", 8);
      put(16, 16, 4);
      put(20, 1, 2);
      put(22, 1, 2);
      put(24, rate, 4);
      put(28, rate * 2, 4);
      put(32, 2, 2);
      put(34, 16, 2);
      memcpy(wav.data() + 36, "data", 4);
      put(40, count * 2, 4);
      for (unsigned i = 0; i < count; ++i) {
        const double t = static_cast<double>(i) / rate;
        const auto sample = static_cast<short>(
            std::sin(t * 2 * 3.141592653589793 * (i < count / 2 ? 660 : 880)) *
            12000 * volume / 100 * std::min(1.0, t * 80) *
            std::min(1.0, (0.25 - t) * 40));
        put(44 + i * 2, static_cast<unsigned short>(sample), 2);
      }
      PlaySoundW(reinterpret_cast<LPCWSTR>(wav.data()), nullptr,
                 SND_MEMORY | SND_SYNC | SND_NODEFAULT);
    }
  }
  std::mutex mutex_;
  std::condition_variable_any wake_;
  float volume_ = 0;
  bool pending_ = false;
  std::jthread worker_;
};
} // namespace core
