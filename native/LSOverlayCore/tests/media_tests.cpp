#include "media_package.hpp"
#include "media_layout.hpp"
#include <iostream>
#include <functional>
#include <stdexcept>

void check(bool value, const char* name) { if (!value) throw std::runtime_error(name); }
void reject(const std::function<void()>& action) { try { action(); } catch (const std::exception&) { return; } throw std::runtime_error("Invalid timeline accepted"); }
int wmain(int count, wchar_t** args) {
    try {
        const auto square=core::chatMediaBox(580,100,100);
        check(square.width==270 && square.height==270,"small GIF uses large logical preview, independent of source pixels/DPI");
        const auto wide=core::chatMediaBox(580,800,400);
        check(wide.width==360 && wide.height==180,"wide image aspect preserved");
        const auto narrow=core::chatMediaBox(200,800,400);
        check(narrow.width==200 && narrow.height==100,"narrow viewport bounds");
        const auto emoji=core::fitMedia(16,16,32,16);
        check(emoji.width==16 && emoji.height==8,"wide sales emoji is not squeezed into square");
        const auto tall=core::fitMedia(16,16,8,32);
        check(tall.width==4 && tall.height==16,"tall emoji is not stretched");
        core::MediaTimeline timeline({70,130,90,110,50},0);
        check(timeline.duration() == 450,"exact duration");
        check(timeline.at(0).index == 0 && timeline.at(69).index == 0 && timeline.at(70).index == 1,"exact frame boundary");
        check(timeline.at(200).index == 2 && timeline.at(450).index == 0,"frame order and loop");
        check(timeline.at(450000070).index == 1,"long resume keeps logical phase");
        core::MediaTimeline finite({70,130},3);
        check(!finite.at(599).finished && finite.at(600).finished && finite.at(600).index == 1,"finite loops end on last frame");
        core::MediaTimeline instant({0,1,0,1},0);
        check(instant.at(0).index == 1 && instant.at(1).index == 3 && instant.at(2).index == 1,"zero duration is instantaneous not clamped");
        core::MediaTimeline highFps({1,1,1},0); check(highFps.at(1).index == 1 && highFps.at(2).nextMs == 3,"no FPS cap");
        check(core::MediaTimeline({0},1).at(0).finished,"static has no wakeup");
        reject([] { core::MediaTimeline bad({},0); }); reject([] { core::MediaTimeline bad({0,0},0); });
        reject([] { core::MediaTimeline bad({86400001},1); });
        if (count == 2) {
            check(SUCCEEDED(CoInitializeEx(nullptr,COINIT_MULTITHREADED)),"COM initialization");
            {
                Microsoft::WRL::ComPtr<IWICImagingFactory> factory;
                check(SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory))),"WIC initialization");
                core::MediaPackage package(std::filesystem::absolute(args[1]));
                check(package.count() == 5 && package.width() == 8 && package.height() == 8,"server package header");
                for (std::size_t i = 0; i < 5; ++i) {
                    auto pixels = package.decode(factory.Get(),i); check(pixels.bgra.size() == 256,"one physical frame allocation");
                    if (i == 1) check(pixels.bgra[(2*8+2)*4+1] == 255,"native composed green frame");
                    if (i == 3) check(pixels.bgra[(4*8+4)*4+3] == 0,"native alpha disposal");
                }
            }
            CoUninitialize();
        }
        std::cout << "PASS native media timeline" << (count == 2 ? " + server package WIC decode" : "") << '\n'; return 0;
    } catch (const std::exception& ex) { std::cerr << ex.what() << '\n'; return 1; }
}
