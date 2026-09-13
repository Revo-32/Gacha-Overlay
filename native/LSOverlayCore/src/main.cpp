#include "renderer.hpp"
#include "fixture_client.hpp"
#include "transport.hpp"
#include "chat_view.hpp"
#include "sales_view.hpp"
#include "dev_client.hpp"
#include "settings_window.hpp"
#include "sales_sound.hpp"
#include <windowsx.h>
#include <commctrl.h>
#include <shellapi.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <chrono>
#include <cmath>
#include <fstream>
#include <iomanip>
#include <memory>
#include <string>
#include <vector>
#include <mutex>
#include <thread>

namespace {
constexpr UINT drawMessage = WM_APP + 1, trayMessage = WM_APP + 2, connectionMessage = WM_APP + 3, mediaMessage = WM_APP + 4;
constexpr int showHotkey = 1, lockHotkey = 2;
constexpr int previousChannelHotkey=3,nextChannelHotkey=4;
constexpr UINT menuShow = 100, menuLock = 101, menuOpacity = 102, menuExit = 103;
constexpr UINT menuFont = 104, menuLarger = 105, menuSmaller = 106, menuMention = 107, menuLatest = 108;
constexpr UINT menuOutline = 109;
constexpr UINT menuRolePosition = 110;
constexpr UINT menuHost = 111;
constexpr UINT menuSettings=112,altMessage=WM_APP+5;
constexpr UINT salesActionMessage=WM_APP+6;
constexpr int minWidthDip = 360, minHeightDip = 480;
using Clock = std::chrono::steady_clock;

int nativeHit(core::Hit hit) {
    switch (hit) {
    case core::Hit::outside: return HTNOWHERE;
    case core::Hit::through: return HTTRANSPARENT;
    case core::Hit::drag: return HTCAPTION;
    case core::Hit::left: return HTLEFT;
    case core::Hit::right: return HTRIGHT;
    case core::Hit::top: return HTTOP;
    case core::Hit::bottom: return HTBOTTOM;
    case core::Hit::topLeft: return HTTOPLEFT;
    case core::Hit::topRight: return HTTOPRIGHT;
    case core::Hit::bottomLeft: return HTBOTTOMLEFT;
    case core::Hit::bottomRight: return HTBOTTOMRIGHT;
    default: return HTCLIENT;
    }
}

struct Metrics {
    std::uint64_t privateBytes{}, workingSet{}, cpu100ns{};
    DWORD handles{}, gdi{}, user{};
};
Metrics measure() {
    Metrics m;
    PROCESS_MEMORY_COUNTERS_EX memory{};
    memory.cb = sizeof(memory);
    if (!GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory)))
        throw std::runtime_error("GetProcessMemoryInfo failed");
    m.privateBytes = memory.PrivateUsage; m.workingSet = memory.WorkingSetSize;
    if (!GetProcessHandleCount(GetCurrentProcess(), &m.handles)) throw std::runtime_error("GetProcessHandleCount failed");
    m.gdi = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    m.user = GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS);
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) throw std::runtime_error("GetProcessTimes failed");
    auto ticks = [](FILETIME time) { return (static_cast<std::uint64_t>(time.dwHighDateTime) << 32) | time.dwLowDateTime; };
    m.cpu100ns = ticks(kernel) + ticks(user);
    return m;
}
// System-wide thread enumeration is costly. Keep it OUTSIDE timed idle intervals.
DWORD threadCount() {
    DWORD count = 0;
    const auto snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE) throw std::runtime_error("Thread snapshot failed");
    THREADENTRY32 entry{}; entry.dwSize = sizeof(entry);
    if (Thread32First(snapshot, &entry)) {
        do { if (entry.th32OwnerProcessID == GetCurrentProcessId()) ++count; }
        while (Thread32Next(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return count;
}

struct Phase {
    std::string name;
    Clock::time_point start;
    Metrics before;
    std::uint64_t rendersBefore = 0;
    std::vector<Metrics> samples;
    double seconds = 0, cpuOneCorePercent = 0;
    std::uint64_t idleRenders = 0;
    DWORD threadsBefore = 0, threadsAfter = 0;
};

struct Options {
    std::filesystem::path verificationDirectory;
    std::filesystem::path fixtureVerificationDirectory;
    std::wstring fixtureEndpoint;
    std::wstring developmentEndpoint;
    std::filesystem::path developmentObservation;
    std::filesystem::path chatFixture, chatVerificationDirectory;
    std::filesystem::path mediaFixture, mediaVerificationDirectory;
    std::filesystem::path salesVerificationDirectory;
    unsigned phaseSeconds = 15;
    bool noHotkeys = false;
    bool chatCaptureOnly = false;
    bool salesActions = false;
    std::filesystem::path settingsVerificationDirectory;
};

class Shell final {
public:
    explicit Shell(Options options) : options_(std::move(options)) {}
    ~Shell() { if (window_) DestroyWindow(window_); }
    int run(HINSTANCE instance) {
        instance_ = instance;
        WNDCLASSEXW klass{};
        klass.cbSize = sizeof(klass); klass.hInstance = instance;
        klass.lpszClassName = L"LSOverlayCore.NativeShell.M1";
        klass.lpfnWndProc = windowProc;
        klass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        klass.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(101));
        klass.hIconSm = klass.hIcon;
        if (!RegisterClassExW(&klass)) throw std::runtime_error("RegisterClassEx failed");
        window_ = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            klass.lpszClassName, L"LS Overlay Core", WS_POPUP | WS_THICKFRAME,
            80,80,640,520,nullptr,nullptr,instance,this);
        if (!window_) throw std::runtime_error("Native window creation failed");
        dpi_ = GetDpiForWindow(window_);
        SetWindowPos(window_, nullptr,0,0,core::toPixels(640,dpi_),core::toPixels(options_.mediaVerificationDirectory.empty() && options_.salesVerificationDirectory.empty() ? 520.0f : 880.0f,dpi_),SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (!options_.noHotkeys) {
            showRegistered_ = RegisterHotKey(window_,showHotkey,MOD_NOREPEAT,VK_F9) != FALSE;
            lockRegistered_ = RegisterHotKey(window_,lockHotkey,MOD_NOREPEAT,VK_F10) != FALSE;
            if(!showRegistered_ || !lockRegistered_)MessageBoxW(window_,L"F9 또는 F10을 다른 앱이 사용 중입니다. Full 등 해당 앱을 종료한 뒤 Core를 다시 실행해 주세요. 트레이 메뉴의 숨기기·잠금은 계속 사용할 수 있습니다.",L"LS Overlay Core · 단축키 충돌",MB_OK|MB_ICONWARNING);
        }
        taskbarCreated_ = RegisterWindowMessageW(L"TaskbarCreated");
        if (!addTray()) throw std::runtime_error("Tray icon unavailable; refusing an unrecoverable hidden window");
        ShowWindow(window_, SW_SHOWNOACTIVATE);
        if (!options_.mediaFixture.empty()) {
            const HWND target = window_;
            media_ = std::make_shared<core::MediaStore>(options_.mediaFixture,[target] { (void)PostMessageW(target,mediaMessage,0,0); });
            renderer_.setMedia(media_);
        }
        if (!options_.developmentEndpoint.empty()) {
            const HWND target=window_;
            media_=std::make_shared<core::MediaStore>([target] { (void)PostMessageW(target,mediaMessage,0,0); });
            renderer_.setMedia(media_);
        }
        if (!options_.chatFixture.empty()) loadChat(options_.chatFixture);
        if(!options_.developmentEndpoint.empty() || !options_.settingsVerificationDirectory.empty()) {
            if(options_.settingsVerificationDirectory.empty()) {try {preferences_.load(core::UserSettings::path());}catch(const std::exception&) {settingsLoadFailed_=true;}}
            applyPreferences();
        }
        renderNow();
        if (verifying()) {
            std::filesystem::create_directories(options_.verificationDirectory);
            if (!SetTimer(window_,1,1000,nullptr)) throw std::runtime_error("Verification timer unavailable");
        }
        if (!options_.fixtureEndpoint.empty() || !options_.developmentEndpoint.empty()) {
            const HWND target = window_;
            renderer_.setConnectionStatus(options_.developmentEndpoint.empty() ? L"M2 합성 환경에 연결 중 · 실제 Discord 아님" : L"Core 연결 준비 중"); queueDraw();
            connectionWorker_ = std::jthread([this,target](std::stop_token stop) {
                const auto publish=[this,target](std::wstring status,std::shared_ptr<const core::Json> snapshot) {
                    // One owned latest-value slot: no external message carries a raw pointer.
                    std::lock_guard lock(connectionMutex_);
                    latestConnectionStatus_ = std::move(status); if (snapshot) latestChatSnapshot_ = std::move(snapshot);
                    // Coalesce wakeups too, not just the payload. A temporarily busy
                    // UI must not accumulate one Win32 queue entry per server revision.
                    if (!connectionNotificationQueued_) connectionNotificationQueued_ = PostMessageW(target,connectionMessage,0,0) != FALSE;
                };
                if (options_.developmentEndpoint.empty()) core::runFixtureClient(options_.fixtureEndpoint,stop,publish);
                else core::runDevelopmentClient(options_.developmentEndpoint,stop,publish,[this](std::string_view token) {
                    media_->setFetcher(core::makeMediaFetcher(core::Endpoint(options_.developmentEndpoint),token));
                },channelControl_,options_.salesActions?&salesActions_:nullptr,[target]{PostMessageW(target,salesActionMessage,0,0);});
            });
            if (!options_.fixtureVerificationDirectory.empty()) {
                std::filesystem::create_directories(options_.fixtureVerificationDirectory);
                if (!SetTimer(window_,2,1000,nullptr)) throw std::runtime_error("Fixture verification timer unavailable");
            }
        }
        if (!options_.chatVerificationDirectory.empty()) {
            std::filesystem::create_directories(options_.chatVerificationDirectory);
            if (!SetTimer(window_,3,1000,nullptr)) throw std::runtime_error("Chat verification timer unavailable");
        }
        if (!options_.mediaVerificationDirectory.empty()) {
            std::filesystem::create_directories(options_.mediaVerificationDirectory);
            if (!SetTimer(window_,4,1000,nullptr)) throw std::runtime_error("Media verification timer unavailable");
        }
        if (!options_.salesVerificationDirectory.empty()) {
            std::filesystem::create_directories(options_.salesVerificationDirectory);
            if (!SetTimer(window_,5,1000,nullptr)) throw std::runtime_error("Sales verification timer unavailable");
        }
        if (!options_.developmentObservation.empty()) {
            std::filesystem::create_directories(options_.developmentObservation);
            if (!SetTimer(window_,6,5000,nullptr)) throw std::runtime_error("Development observation timer unavailable");
        }
        if(!options_.settingsVerificationDirectory.empty()) {std::filesystem::create_directories(options_.settingsVerificationDirectory);SetTimer(window_,7,1000,nullptr);}
        MSG message{};
        int result = 0;
        while ((result = GetMessageW(&message,nullptr,0,0)) > 0) {
            if(settingsWindow_ && settingsWindow_->dialog(message))continue;
            TranslateMessage(&message); DispatchMessageW(&message);
        }
        if (result < 0) throw std::runtime_error("GetMessage failed");
        return failure_ ? 1 : static_cast<int>(message.wParam);
    }
private:
    core::UserSettings preferences_;
    std::unique_ptr<core::SettingsWindow> settingsWindow_;
    core::SalesSound sound_;
    core::SalesActions salesActions_;
    void refreshSalesAction() {
        if(options_.salesActions && renderer_.sales())renderer_.sales()->setAction(salesActions_.offered(),salesActions_.busy(),salesActions_.status());
    }
    bool dispatchSalesAction(float x,float y) {
        if(!renderer_.sales())return false;
        const auto action=renderer_.sales()->actionAt(x,y,!state_.locked);
        if(!action)return false;
        // Explicit click dispatches without a modal dialog. The controller still
        // rejects stale targets and duplicate intents before any network write.
        (void)salesActions_.submit(*action);
        refreshSalesAction();queueDraw();return true;
    }
    bool settingsLoadFailed_=false,altDrag_=false;
    core::ChannelControl channelControl_;int requestedChannel_=-1,channelKeyMode_=-1;
    std::vector<unsigned> availableChannels_;
    void channelStep(int direction) {
        if(availableChannels_.empty())return;
        auto current=static_cast<unsigned>(preferences_.get(core::Setting::Channel));auto it=std::find(availableChannels_.begin(),availableChannels_.end(),current);
        const auto index=it==availableChannels_.end()?0:static_cast<int>(it-availableChannels_.begin());const auto count=static_cast<int>(availableChannels_.size());
        preferences_.set(core::Setting::Channel,static_cast<float>(availableChannels_[static_cast<std::size_t>((index+direction+count)%count)]));applyPreferences();
        preferences_.save(core::UserSettings::path());
    }
    HHOOK altHook_=nullptr;inline static Shell* altOwner_=nullptr;
    std::string alertGeneration_,alertCurrent_,alertNext_;
    bool alertEligible_=false;
    static LRESULT CALLBACK altProc(int code,WPARAM w,LPARAM l) {
        if(code>=0 && altOwner_) {const auto* key=reinterpret_cast<KBDLLHOOKSTRUCT*>(l);if(key->vkCode==VK_LMENU || key->vkCode==VK_RMENU || key->vkCode==VK_MENU)PostMessageW(altOwner_->window_,altMessage,w==WM_KEYDOWN || w==WM_SYSKEYDOWN,0);}
        return CallNextHookEx(nullptr,code,w,l);
    }
    void applyPreferences() {
        const int channel=static_cast<int>(preferences_.get(core::Setting::Channel));if(channel!=requestedChannel_){requestedChannel_=channel;channelControl_.select(static_cast<unsigned>(channel));}
        const int keyMode=static_cast<int>(preferences_.get(core::Setting::ChannelKeys));
        if(!options_.noHotkeys && keyMode!=channelKeyMode_) {
            UnregisterHotKey(window_,previousChannelHotkey);UnregisterHotKey(window_,nextChannelHotkey);channelKeyMode_=keyMode;
            if(keyMode) {const UINT mods=MOD_NOREPEAT|(keyMode==1?MOD_CONTROL|MOD_ALT:keyMode==2?MOD_ALT:0);const UINT previous=keyMode==3?VK_F7:VK_LEFT,next=keyMode==3?VK_F8:VK_RIGHT;
                const bool first=RegisterHotKey(window_,previousChannelHotkey,mods,previous)!=FALSE,second=RegisterHotKey(window_,nextChannelHotkey,mods,next)!=FALSE;
                if(!first || !second){UnregisterHotKey(window_,previousChannelHotkey);UnregisterHotKey(window_,nextChannelHotkey);channelKeyMode_=-1;throw std::runtime_error("Channel shortcut conflict");}
            }
        }
        state_.backgroundOpacity=preferences_.get(core::Setting::HudOpacity)/100;renderer_.applySettings(preferences_);
        if(media_)media_->setAnimated(preferences_.enabled(core::Setting::Animated));
        if(preferences_.enabled(core::Setting::ModifierDrag) && !altHook_) {altOwner_=this;altHook_=SetWindowsHookExW(WH_KEYBOARD_LL,altProc,instance_,0);if(!altHook_){altOwner_=nullptr;preferences_.set(core::Setting::ModifierDrag,0);throw std::runtime_error("Alt drag hook unavailable");}}
        if(!preferences_.enabled(core::Setting::ModifierDrag) && altHook_) {UnhookWindowsHookEx(altHook_);altHook_=nullptr;altOwner_=nullptr;PostMessageW(window_,altMessage,FALSE,0);}
        queueDraw();
    }
    void openSettings() {
        if(!settingsWindow_)settingsWindow_=std::make_unique<core::SettingsWindow>(preferences_,[this] {applyPreferences();},[this](unsigned action) {
            if(action==512 && media_)media_->clearCache();
            else if(action==513)sound_.play(preferences_.get(core::Setting::SalesVolume));
            else if(action==511)SetWindowPos(window_,nullptr,0,0,core::toPixels(640,dpi_),core::toPixels(520,dpi_),SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE);
            else if(action==510) {MONITORINFO info{sizeof(info)};GetMonitorInfoW(MonitorFromWindow(window_,MONITOR_DEFAULTTONEAREST),&info);RECT r{};GetWindowRect(window_,&r);SetWindowPos(window_,nullptr,info.rcWork.left+(info.rcWork.right-info.rcWork.left-r.right+r.left)/2,info.rcWork.top+(info.rcWork.bottom-info.rcWork.top-r.bottom+r.top)/2,0,0,SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE);}
            queueDraw();
        },options_.settingsVerificationDirectory.empty()?std::filesystem::path{}:options_.settingsVerificationDirectory/L"settings.json");
        settingsWindow_->status((settingsLoadFailed_?L"기존 Core 설정을 읽지 못해 기본값으로 표시합니다.\r\n":L"")+developmentStatus_);settingsWindow_->show(window_);
    }
    void salesAlert(const core::Json& snapshot) {
        if(!renderer_.compact())return;auto* root=snapshot.root();auto* sales=core::field(root,"sales");if(!yyjson_is_obj(sales))return;
        const auto text=[&](yyjson_val* value,const char* key) {auto* item=core::field(value,key);return yyjson_is_str(item)?std::string(core::stringValue(item)):std::string{};};
        const auto generation=text(root,"generation"),current=text(sales,"currentMessageId"),next=text(sales,"nextMessageId");auto* presentation=core::field(sales,"presentation");
        const bool eligible=yyjson_is_obj(presentation) && yyjson_is_true(core::field(presentation,"isTrustedForNewPersonalAlert"));
        if(generation==alertGeneration_ && eligible && alertEligible_ && preferences_.enabled(core::Setting::SalesTracking) && preferences_.enabled(core::Setting::SalesSound) &&
            ((current!=alertCurrent_ && yyjson_is_true(core::field(sales,"currentIsSelf")) && preferences_.enabled(core::Setting::NotifyCurrent)) ||
             (next!=alertNext_ && yyjson_is_true(core::field(sales,"nextIsSelf")) && preferences_.enabled(core::Setting::NotifyNext))))sound_.play(preferences_.get(core::Setting::SalesVolume));
        alertGeneration_=generation;alertCurrent_=current;alertNext_=next;alertEligible_=eligible;
    }
    unsigned settingsTicks_=0;
    void settingsTick() {
        ++settingsTicks_;
        if(settingsTicks_==1) {
            assertion(renderer_.compact(),"Compact shell is active without changing chat layout");
            RECT bounds{};GetClientRect(window_,&bounds);const int x=static_cast<int>(bounds.right)-core::toPixels(27,dpi_),y=core::toPixels(25,dpi_);POINT p{x,y};ClientToScreen(window_,&p);
            assertion(SendMessageW(window_,WM_NCHITTEST,0,MAKELPARAM(p.x,p.y))==HTCLIENT,"Settings gear is clickable, not a drag caption");
            preferences_.set(core::Setting::HudOpacity,0);preferences_.set(core::Setting::ChatOpacity,0);applyPreferences();renderNow();
            assertion(renderer_.alphaAt(x,y)>0 && renderer_.alphaAt(4,100)>0,"Zero opacity keeps settings and resize targets");
            renderer_.savePng(options_.settingsVerificationDirectory/L"compact-zero-opacity.png");
            assertion(renderer_.chat()->scrollbarVisible(),"Overflowing unlocked chat shows scrollbar");
            SendMessageW(window_,WM_HOTKEY,lockHotkey,0);renderNow();
            assertion(!renderer_.chat()->scrollbarVisible(),"Locked chat hides scrollbar");
            unsigned railAlpha=0;const int railX=static_cast<int>(bounds.right)-core::toPixels(14,dpi_);
            for(int sy=core::toPixels(50,dpi_);sy<bounds.bottom-core::toPixels(12,dpi_);++sy)railAlpha+=renderer_.alphaAt(railX,sy);
            assertion(railAlpha==0,"Locked scrollbar pixels are transparent at zero HUD opacity");
            renderer_.savePng(options_.settingsVerificationDirectory/L"locked-no-scrollbar.png");
            SendMessageW(window_,WM_HOTKEY,lockHotkey,0);renderNow();
            assertion(renderer_.chat()->scrollbarVisible(),"Unlock restores scrollbar without changing chat history");
            preferences_.set(core::Setting::HudOpacity,62);preferences_.set(core::Setting::ChatOpacity,35);applyPreferences();renderNow();renderer_.savePng(options_.settingsVerificationDirectory/L"compact-overlay.png");openSettings();return;
        }
        if(settingsTicks_>=2 && settingsTicks_<=7) {
            const unsigned page=settingsTicks_-2;settingsWindow_->selectPage(page);UpdateWindow(settingsWindow_->handle());
            assertion(IsWindowVisible(settingsWindow_->handle())!=FALSE,"Settings page visible");
            core::Renderer::saveWindowPng(settingsWindow_->handle(),options_.settingsVerificationDirectory/(L"settings-"+std::to_wstring(page)+L".png"));
            return;
        }
        if(settingsTicks_==8) {
            settingsWindow_->selectPage(1);
            struct Find {int id;HWND found=nullptr;} search{100+static_cast<int>(core::Setting::FontSize)};
            EnumChildWindows(settingsWindow_->handle(),[](HWND h,LPARAM p)->BOOL {auto& item=*reinterpret_cast<Find*>(p);if(GetDlgCtrlID(h)==item.id) {item.found=h;return FALSE;}return TRUE;},reinterpret_cast<LPARAM>(&search));
            assertion(search.found!=nullptr,"Font size control exists");SendMessageW(search.found,TBM_SETPOS,TRUE,20);SendMessageW(GetParent(search.found),WM_HSCROLL,TB_THUMBPOSITION,reinterpret_cast<LPARAM>(search.found));
            assertion(preferences_.get(core::Setting::FontSize)==18,"Slider updates live model");SendMessageW(settingsWindow_->handle(),WM_TIMER,1,0);
            const auto beforeWheel=preferences_;const HWND scrollbar=GetDlgItem(settingsWindow_->handle(),11);
            SendMessageW(search.found,WM_MOUSEWHEEL,MAKEWPARAM(0,static_cast<WORD>(-WHEEL_DELTA)),0);
            assertion(preferences_==beforeWheel,"Wheel over slider never changes settings");
            SCROLLINFO scrollInfo{sizeof(scrollInfo),SIF_POS};GetScrollInfo(scrollbar,SB_CTL,&scrollInfo);
            assertion(scrollInfo.nPos>0,"Wheel over slider scrolls page");
            RECT scrollBounds{};GetClientRect(scrollbar,&scrollBounds);
            SendMessageW(scrollbar,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(4,scrollBounds.bottom/2));
            SendMessageW(scrollbar,WM_MOUSEMOVE,MK_LBUTTON,MAKELPARAM(4,scrollBounds.bottom-20));
            SendMessageW(scrollbar,WM_LBUTTONUP,0,MAKELPARAM(4,scrollBounds.bottom-20));
            RedrawWindow(scrollbar,nullptr,nullptr,RDW_INVALIDATE|RDW_UPDATENOW);
            core::Renderer::saveWindowPng(settingsWindow_->handle(),options_.settingsVerificationDirectory/L"settings-scroll-drag.png");
            assertion(preferences_==beforeWheel,"Scrollbar drag never changes settings");
            core::UserSettings loaded;loaded.load(options_.settingsVerificationDirectory/L"settings.json");assertion(loaded==preferences_,"All settings persist and reload in isolated test directory");
            search.id=100+static_cast<int>(core::Setting::NicknameOutline);search.found=nullptr;
            EnumChildWindows(settingsWindow_->handle(),[](HWND h,LPARAM p)->BOOL {auto& item=*reinterpret_cast<Find*>(p);if(GetDlgCtrlID(h)==item.id){item.found=h;return FALSE;}return TRUE;},reinterpret_cast<LPARAM>(&search));
            assertion(search.found!=nullptr,"Modern toggle retains native control semantics");const bool oldToggle=preferences_.enabled(core::Setting::NicknameOutline);SendMessageW(search.found,BM_CLICK,0,0);assertion(preferences_.enabled(core::Setting::NicknameOutline)!=oldToggle,"Rounded toggle changes live setting");
            SetFocus(search.found);SendMessageW(search.found,WM_KEYDOWN,VK_SPACE,0);SendMessageW(search.found,WM_KEYUP,VK_SPACE,0);assertion(preferences_.enabled(core::Setting::NicknameOutline)==oldToggle,"Space key operates modern toggle");
            search.id=100+static_cast<int>(core::Setting::Font);search.found=nullptr;
            EnumChildWindows(settingsWindow_->handle(),[](HWND h,LPARAM p)->BOOL {auto& item=*reinterpret_cast<Find*>(p);if(GetDlgCtrlID(h)==item.id){item.found=h;return FALSE;}return TRUE;},reinterpret_cast<LPARAM>(&search));
            assertion(search.found!=nullptr,"Modern dropdown exists");SetFocus(search.found);SendMessageW(search.found,WM_KEYDOWN,VK_HOME,0);assertion(preferences_.get(core::Setting::Font)==0,"Dropdown keyboard selection updates model");
            SendMessageW(search.found,CB_SHOWDROPDOWN,TRUE,0);COMBOBOXINFO combo{sizeof(combo)};GetComboBoxInfo(search.found,&combo);assertion(IsWindowVisible(combo.hwndList)!=FALSE,"Native accessible dropdown popup opens");
            core::Renderer::saveWindowPng(combo.hwndList,options_.settingsVerificationDirectory/L"settings-dropdown.png");SendMessageW(search.found,CB_SHOWDROPDOWN,FALSE,0);
            settingsWindow_.reset();preferences_.set(core::Setting::FontSize,12);applyPreferences();return;
        }
        if(settingsTicks_==9) {
            const auto fontBeforeEmoji=preferences_.get(core::Setting::FontSize);
            auto emojiFixture=std::make_shared<core::Json>(R"({"protocolVersion":1,"generation":"emoji-size-test","selfUserId":"test","chat":[{"id":"emoji","presentationHash":"0000000000000000000000000000000000000000000000000000000000000002","attention":"Normal","showAuthorHeader":true,"author":{"id":"test","displayName":"이모지 크기 독립 설정","color":16777215},"createdAt":"2026-09-14T01:00:00Z","runs":[{"kind":"Text","text":"한글 Agjpqy "},{"kind":"CustomEmoji","text":"검증","mediaId":"fixture-emoji"},{"kind":"Text","text":" 다음 글자\n다음 줄이 겹치지 않습니다."}],"media":[],"reactions":[],"forwarded":[],"details":[],"reply":null}],"sales":null,"session":[]})");
            renderer_.setChatSnapshot(emojiFixture);
            preferences_.set(core::Setting::EmojiSize,64);preferences_.set(core::Setting::LineHeight,0);applyPreferences();renderNow();
            assertion(preferences_.get(core::Setting::FontSize)==fontBeforeEmoji,"Emoji size does not alter font size");
            renderer_.savePng(options_.settingsVerificationDirectory/L"emoji-size-64.png");
            preferences_.set(core::Setting::Images,0);preferences_.set(core::Setting::Emoji,0);preferences_.set(core::Setting::Stickers,0);
            auto textFixture=std::make_shared<core::Json>(R"({"protocolVersion":1,"generation":"text-ink-test","selfUserId":"test","chat":[{"id":"ink","presentationHash":"0000000000000000000000000000000000000000000000000000000000000001","attention":"Normal","showAuthorHeader":true,"author":{"id":"test","displayName":"DE-SSANTA · 한글 Agjpqy","color":16777215,"iconUnicode":"★"},"createdAt":"2026-09-14T01:00:00Z","runs":[{"kind":"Text","text":"가나다라마바사 아자차카타파하\nAgjpqy 0123456789\n줄 간격을 줄여도 글자가 온전히 보입니다."}],"media":[],"reactions":[],"forwarded":[],"details":[],"reply":null}],"sales":null,"session":[]})");
            renderer_.setChatSnapshot(textFixture);preferences_.set(core::Setting::LineHeight,0);preferences_.set(core::Setting::Spacing,-2);preferences_.set(core::Setting::FontSize,16);preferences_.set(core::Setting::MaxLines,3);
            assertion(preferences_.get(core::Setting::LineHeight)==0,"Zero line spacing accepted without collapsing glyphs");
            for(unsigned font=0;font<5;++font){preferences_.set(core::Setting::Font,static_cast<float>(font));applyPreferences();renderNow();renderer_.savePng(options_.settingsVerificationDirectory/(L"tight-text-"+std::to_wstring(font)+L".png"));}
            SendMessageW(window_,WM_HOTKEY,lockHotkey,0);assertion(state_.locked,"F10 handler locks");SendMessageW(window_,WM_HOTKEY,lockHotkey,0);assertion(!state_.locked,"F10 handler unlocks");
            SendMessageW(window_,WM_HOTKEY,showHotkey,0);assertion(!state_.visible,"F9 handler hides");SendMessageW(window_,WM_HOTKEY,showHotkey,0);assertion(state_.visible,"F9 handler shows");
            applyPreferences();renderNow();return;
        }
        if(settingsTicks_==10) {
            auto sessionFixture=std::make_shared<core::Json>(R"({"protocolVersion":1,"generation":"session-outline-test","selfUserId":"test","chat":[],"sales":null,"session":[{"hostSlot":1,"state":"gtaOnline","currentPlayers":12,"maximumPlayers":32}]})");
            renderer_.setChatSnapshot(sessionFixture);
            preferences_.set(core::Setting::HudOpacity,0);preferences_.set(core::Setting::ChromeOpacity,0);preferences_.set(core::Setting::ChatOpacity,0);
            preferences_.set(core::Setting::NicknameOutline,0);preferences_.set(core::Setting::MessageOutline,0);preferences_.set(core::Setting::SessionOutline,0);preferences_.set(core::Setting::Host,1);preferences_.set(core::Setting::Font,2);applyPreferences();renderNow();
            auto headerInk=[&](){unsigned ink=0;for(int y=core::toPixels(10,dpi_);y<core::toPixels(44,dpi_);++y)for(int x=core::toPixels(12,dpi_);x<core::toPixels(95,dpi_);++x)ink+=renderer_.alphaAt(x,y);return ink;};
            const auto withoutOutline=headerInk();renderer_.savePng(options_.settingsVerificationDirectory/L"session-outline-off.png");
            preferences_.set(core::Setting::SessionOutline,1);applyPreferences();renderNow();
            assertion(renderer_.sales()->sessionLabel()==L"12 / 32","Session outline preserves player counts");
            assertion(headerInk()>withoutOutline,"Session outline renders independently of disabled chat outlines at zero opacity");
            renderer_.savePng(options_.settingsVerificationDirectory/L"session-outline-on.png");
            if(media_)assertion(media_->statistics().visible==0,"Disabled media stops visible decoding");
            std::ofstream output(options_.settingsVerificationDirectory/L"settings-checks.json");output<<"{\"status\":\"PASS\",\"synthetic\":true,\"assertions\":"<<assertions_<<"}";output.close();DestroyWindow(window_);
        }
    }
    unsigned developmentSamples_ = 0;
    unsigned developmentSnapshots_ = 0;
    std::string developmentState_ = "pending";
    std::wstring developmentStatus_;
    void developmentSample() {
        if (++developmentSamples_ > 2160) { KillTimer(window_,6); return; }
        const auto metrics=measure();
        const auto media=media_ ? media_->statistics() : core::MediaStatistics{};
        const auto actions=salesActions_.metrics();
        std::ofstream output(options_.developmentObservation/"native-observation.jsonl",std::ios::app);
        output << "{\"sample\":" << developmentSamples_ << ",\"unixSeconds\":" << std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count()
            << ",\"phase\":\"" << developmentState_ << "\",\"snapshots\":" << developmentSnapshots_
            << ",\"chatCount\":" << (renderer_.chat() ? renderer_.chat()->messageCount() : 0)
            << ",\"salesCount\":" << (renderer_.sales() ? renderer_.sales()->count() : 0)
            << ",\"session\":" << core::jsonQuote(renderer_.sales() ? core::narrow(renderer_.sales()->sessionLabel()) : "pending")
            << ",\"status\":" << core::jsonQuote(core::narrow(developmentStatus_))
            << ",\"salesActionsEnabled\":" << (options_.salesActions?"true":"false")
            << ",\"salesActionPending\":" << (salesActions_.busy()?"true":"false")
            << ",\"salesActionSubmitted\":" << actions.submitted << ",\"salesActionCompleted\":" << actions.completed << ",\"salesActionUndone\":" << actions.undone
            << ",\"salesActionRejected\":" << actions.rejected << ",\"salesActionUncertain\":" << actions.uncertain
            << ",\"privateBytes\":" << metrics.privateBytes << ",\"workingSet\":" << metrics.workingSet << ",\"cpu100ns\":" << metrics.cpu100ns
            << ",\"handles\":" << metrics.handles << ",\"mediaVisible\":" << media.visible << ",\"mediaDecoded\":" << media.decoded
            << ",\"mediaFailures\":" << media.failures << ",\"mediaLateFrames\":" << media.lateFrames << ",\"mediaPixelBytes\":" << media.ownedPixelBytes
            << ",\"mediaDownloads\":"<<media.downloads<<",\"mediaDownloadMs\":"<<media.downloadMs<<",\"mediaMaxDownloadMs\":"<<media.maxDownloadMs
            << ",\"mediaFirstFrames\":"<<media.firstFrames<<",\"mediaFirstFrameMs\":"<<media.firstFrameMs<<",\"mediaMaxFirstFrameMs\":"<<media.maxFirstFrameMs<<"}\n";
        if (!output) throw std::runtime_error("Development observation write failed");
    }
    void loadChat(const std::filesystem::path& path) {
        std::ifstream input(path,std::ios::binary | std::ios::ate);
        const auto length = input ? static_cast<std::streamoff>(input.tellg()) : 0;
        if (length <= 0 || length > 1024*1024) throw std::runtime_error("Invalid chat fixture size");
        input.seekg(0); std::string bytes(static_cast<std::size_t>(length),'\0');
        if (!input.read(bytes.data(),length)) throw std::runtime_error("Cannot read chat fixture");
        renderer_.setChatSnapshot(std::make_shared<core::Json>(bytes)); queueDraw();
        renderer_.setConnectionStatus(L"로컬 채팅 Snapshot · 실시간 연결 아님");
    }
    bool verifying() const { return !options_.verificationDirectory.empty(); }
    void assertion(bool value, const char* name) {
        ++assertions_;
        if (!value) throw std::runtime_error(std::string("Native smoke assertion: ") + name);
    }
    bool addTray() {
        NOTIFYICONDATAW icon{};
        icon.cbSize = sizeof(icon); icon.hWnd = window_; icon.uID = 1;
        icon.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        icon.uCallbackMessage = trayMessage;
        icon.hIcon = LoadIconW(instance_,MAKEINTRESOURCEW(101));
        wcscpy_s(icon.szTip, L"LS Overlay Core · 내부 개발 검증");
        return Shell_NotifyIconW(NIM_ADD,&icon) != FALSE;
    }
    void queueDraw(bool mediaOnly = false) {
        fullDrawRequired_ |= !mediaOnly;
        if (state_.visible && !drawQueued_ && window_) {
            drawQueued_ = PostMessageW(window_,drawMessage,0,0) != FALSE;
        }
    }
    void renderNow(bool mediaOnly = false) {
        if (!state_.visible || !window_) return;
        RECT bounds{};
        if (!GetClientRect(window_,&bounds)) throw std::runtime_error("GetClientRect failed");
        if (bounds.right <= 0 || bounds.bottom <= 0) return;
        renderer_.draw(window_,bounds.right,bounds.bottom,dpi_,state_,showRegistered_ && lockRegistered_,mediaOnly);
    }
    void toggleVisible() {
        if (renderer_.chat()) renderer_.chat()->endScrollbar();
        if (GetCapture() == window_) ReleaseCapture();
        state_.toggleVisible();
        SendMessageW(window_,altMessage,FALSE,0);
        ShowWindow(window_,state_.visible ? SW_SHOWNOACTIVATE : SW_HIDE);
        if (!state_.visible && renderer_.chat()) renderer_.chat()->pauseMedia();
        if (state_.visible) queueDraw();
    }
    void toggleLocked() {
        if (renderer_.chat()) renderer_.chat()->endScrollbar();
        if (GetCapture() == window_) ReleaseCapture();
        state_.toggleLocked();
        altDrag_=false;
        auto style = GetWindowLongPtrW(window_,GWL_EXSTYLE);
        if (state_.locked) style |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        else style &= ~(static_cast<LONG_PTR>(WS_EX_TRANSPARENT) | WS_EX_NOACTIVATE);
        SetLastError(0);
        const auto previous = SetWindowLongPtrW(window_,GWL_EXSTYLE,style);
        if (previous == 0 && GetLastError() != 0) throw std::runtime_error("Lock window style failed");
        if (!SetWindowPos(window_,nullptr,0,0,0,0,SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED))
            throw std::runtime_error("Lock frame update failed");
        queueDraw();
    }
    void menu(POINT point) {
        const auto popup = CreatePopupMenu();
        if (!popup) return;
        AppendMenuW(popup,MF_STRING,menuShow,state_.visible ? L"숨기기 (F9)" : L"표시 (F9)");
        AppendMenuW(popup,MF_STRING,menuLock,state_.locked ? L"잠금 해제 (F10)" : L"잠금 (F10)");
        AppendMenuW(popup,MF_STRING,menuSettings,L"설정…");
        if(!renderer_.compact())AppendMenuW(popup,MF_STRING,menuOpacity,state_.backgroundOpacity == 0 ? L"배경 불투명도 85%" : L"배경 불투명도 0% 검증");
        AppendMenuW(popup,MF_SEPARATOR,0,nullptr);
        if(renderer_.chat() && renderer_.compact())AppendMenuW(popup,MF_STRING,menuLatest,L"최신 메시지로 이동");
        if (renderer_.chat() && !renderer_.compact()) {
            AppendMenuW(popup,MF_STRING,menuHost,L"세션 호스트 변경");
            AppendMenuW(popup,MF_STRING,menuFont,(L"글꼴 변경 · "+renderer_.chat()->fontName()).c_str());
            AppendMenuW(popup,MF_STRING,menuLarger,L"채팅 글자 크게");
            AppendMenuW(popup,MF_STRING,menuSmaller,L"채팅 글자 작게");
            AppendMenuW(popup,MF_STRING,menuMention,L"나를 언급한 메시지 배경 켜기/끄기");
            AppendMenuW(popup,MF_STRING,menuOutline,L"글자 외곽선 켜기/끄기");
            AppendMenuW(popup,MF_STRING,menuRolePosition,L"역할 아이콘 위치 변경 (왼쪽/이름 뒤/오른쪽)");
            AppendMenuW(popup,MF_STRING,menuLatest,L"최신 메시지로 이동");
        }
        AppendMenuW(popup,MF_STRING,menuExit,L"LS Overlay Core 종료");
        SetForegroundWindow(window_);
        const auto selected = TrackPopupMenu(popup,TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON,point.x,point.y,0,window_,nullptr);
        DestroyMenu(popup);
        PostMessageW(window_,WM_NULL,0,0);
        if (selected) SendMessageW(window_,WM_COMMAND,selected,0);
    }
    int hit(LPARAM coordinates) const {
        POINT point{GET_X_LPARAM(coordinates),GET_Y_LPARAM(coordinates)};
        ScreenToClient(window_,&point);
        RECT bounds{}; GetClientRect(window_,&bounds);
        return nativeHit(core::hitTest(core::toDip(point.x,dpi_),core::toDip(point.y,dpi_),
            core::toDip(bounds.right,dpi_),core::toDip(bounds.bottom,dpi_),state_));
    }
    void capture(const wchar_t* name) { renderer_.savePng(options_.verificationDirectory / name); }
    void smoke() {
        assertion(AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(),DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2), "PerMonitorV2 manifest active");
        assertion(dpi_ == GetDpiForWindow(window_), "actual window DPI");
        assertion(renderer_.renderCount() > 0, "Direct2D presentation happened");
        capture(L"shell-unlocked.png");
        state_.backgroundOpacity = 0;
        renderNow();
        assertion(renderer_.alphaAt(4,100) > 0, "zero-opacity resize strip remains hit-testable");
        assertion(renderer_.alphaAt(core::toPixels(300,dpi_),core::toPixels(280,dpi_)) == 0, "zero-opacity blank content transparent");
        capture(L"shell-zero-opacity.png");
        POINT edge{core::toPixels(4,dpi_),core::toPixels(100,dpi_)};
        ClientToScreen(window_,&edge);
        assertion(SendMessageW(window_,WM_NCHITTEST,0,MAKELPARAM(edge.x,edge.y)) == HTLEFT, "native zero-opacity edge hit");
        SendMessageW(window_,WM_HOTKEY,lockHotkey,0);
        assertion(state_.locked, "F10 handler locks");
        assertion((GetWindowLongPtrW(window_,GWL_EXSTYLE) & (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)) == (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE), "locked cross-process click-through styles");
        renderNow();
        assertion(renderer_.alphaAt(4,100) == 0, "locked removes editing strip");
        capture(L"shell-locked-zero-opacity.png");
        SendMessageW(window_,WM_HOTKEY,showHotkey,0);
        assertion(!state_.visible && !IsWindowVisible(window_), "F9 hides actual native window");
        const auto hiddenCount = renderer_.renderCount();
        renderNow();
        assertion(renderer_.renderCount() == hiddenCount, "hidden draw suppressed");
        SendMessageW(window_,WM_HOTKEY,showHotkey,0);
        assertion(state_.visible && state_.locked && IsWindowVisible(window_), "F9 show preserves lock");
        SendMessageW(window_,WM_HOTKEY,lockHotkey,0);
        assertion(!state_.locked && !(GetWindowLongPtrW(window_,GWL_EXSTYLE) & WS_EX_TRANSPARENT), "unlock restores input");
        state_.backgroundOpacity = 0.85f;
        // Synthetic render-DPI coverage is distinct from physical monitor movement.
        // Hide only this test window while producing its own 150%/200% captures.
        ShowWindow(window_,SW_HIDE);
        for (unsigned renderDpi : {144U,192U}) {
            const int width = core::toPixels(640,renderDpi), height = core::toPixels(520,renderDpi);
            SetWindowPos(window_,nullptr,0,0,width,height,SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            renderer_.draw(window_,width,height,renderDpi,state_,showRegistered_ && lockRegistered_);
            assertion(renderer_.backingBytes() == core::surfaceBytes(width,height), "DPI backing size is physical resolution, not upscaled low-res bitmap");
            capture(renderDpi == 144 ? L"shell-render-dpi-144.png" : L"shell-render-dpi-192.png");
        }
        SetWindowPos(window_,nullptr,0,0,core::toPixels(640,dpi_),core::toPixels(520,dpi_),SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        ShowWindow(window_,SW_SHOWNOACTIVATE);
        const auto started = Clock::now();
        renderNow();
        const auto before = measure();
        resizeLatencies_.reserve(100);
        for (int i = 0; i < 100; ++i) {
            const auto begin = Clock::now();
            const int width = i % 2 == 0 ? 360 : 900;
            SetWindowPos(window_,nullptr,0,0,core::toPixels(static_cast<float>(width),dpi_),core::toPixels(640,dpi_),SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            renderNow();
            resizeLatencies_.push_back(std::chrono::duration<double,std::milli>(Clock::now()-begin).count());
        }
        SetWindowPos(window_,nullptr,0,0,core::toPixels(360,dpi_),core::toPixels(640,dpi_),SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        renderNow(); capture(L"shell-narrow.png");
        SetWindowPos(window_,nullptr,0,0,core::toPixels(640,dpi_),core::toPixels(520,dpi_),SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        renderer_.discardSurface(); renderNow();
        const auto after = measure();
        assertion(after.gdi <= before.gdi + 2, "resize and render-target rebuild GDI objects bounded");
        assertion(after.user <= before.user + 2, "resize USER objects bounded");
        resizeGdiBefore_ = before.gdi; resizeGdiAfter_ = after.gdi;
        resizeMs_ = std::chrono::duration<double,std::milli>(Clock::now()-started).count();
        smokeDone_ = true;
        // Let posted invalidations and one-time font/WIC work settle before idle measurements.
        settleTicks_ = 3;
    }
    void beginPhase(const char* name) {
        Phase phase;
        phase.name = name;
        phase.threadsBefore = threadCount();
        phase.before = measure(); phase.start = Clock::now();
        phase.rendersBefore = renderer_.renderCount();
        phases_.push_back(std::move(phase));
    }
    void tick() {
        if (!smokeDone_) { smoke(); return; }
        if (settleTicks_ > 0) { --settleTicks_; return; }
        if (phases_.empty()) { beginPhase("visible_unlocked_idle"); return; }
        auto& phase = phases_.back();
        if (phase.seconds != 0) {
            if (phases_.size() == 1) beginPhase("visible_locked_idle");
            else if (phases_.size() == 2) beginPhase("hidden_idle");
            return;
        }
        phase.samples.push_back(measure());
        const double elapsed = std::chrono::duration<double>(Clock::now()-phase.start).count();
        if (elapsed < options_.phaseSeconds) return;
        phase.seconds = elapsed;
        phase.cpuOneCorePercent = static_cast<double>(phase.samples.back().cpu100ns-phase.before.cpu100ns) / 100000.0 / elapsed;
        phase.threadsAfter = threadCount();
        phase.idleRenders = renderer_.renderCount()-phase.rendersBefore;
        assertion(phase.idleRenders == 0, "static/locked/hidden phase performs no idle rendering");
        if (phases_.size() == 1) { toggleLocked(); settleTicks_ = 2; }
        else if (phases_.size() == 2) { toggleVisible(); settleTicks_ = 2; }
        else {
            writeReport();
            KillTimer(window_,1);
            DestroyWindow(window_);
        }
    }
    void writeReport() const {
        std::ofstream output(options_.verificationDirectory / "native-shell-metrics.json",std::ios::binary);
        if (!output) throw std::runtime_error("Cannot create native measurement report");
        output << std::fixed << std::setprecision(3)
            << "{\n  \"scope\": \"M1 native feasibility shell; NOT connected Core or Full comparison\",\n"
            << "  \"renderer\": \"Direct2D software DC + DirectWrite + UpdateLayeredWindow\",\n"
            << "  \"dpi\": " << dpi_ << ",\n  \"assertionsPassed\": " << assertions_
            << ",\n  \"f9Registered\": " << (showRegistered_ ? "true" : "false")
            << ",\n  \"f10Registered\": " << (lockRegistered_ ? "true" : "false")
            << ",\n  \"renderCount\": " << renderer_.renderCount()
            << ",\n  \"layoutBuildCount\": " << renderer_.layoutBuildCount()
            << ",\n  \"backingSurfaceBytes\": " << renderer_.backingBytes()
            << ",\n  \"resizeCycles\": 100,\n  \"resizeTotalMs\": " << resizeMs_
            << ",\n  \"resizeGdiBefore\": " << resizeGdiBefore_ << ",\n  \"resizeGdiAfter\": " << resizeGdiAfter_
            << ",\n  \"resizeMs\": [";
        for (std::size_t i = 0; i < resizeLatencies_.size(); ++i) output << (i ? "," : "") << resizeLatencies_[i];
        output << "],\n  \"phases\": [\n";
        for (std::size_t i = 0; i < phases_.size(); ++i) {
            const auto& phase = phases_[i];
            output << (i ? ",\n" : "") << "    {\"name\":\"" << phase.name << "\",\"seconds\":" << phase.seconds
                << ",\"cpuOneCorePercent\":" << phase.cpuOneCorePercent << ",\"idleRenders\":" << phase.idleRenders
                << ",\"threadsBefore\":" << phase.threadsBefore << ",\"threadsAfter\":" << phase.threadsAfter << ",\"samples\":[";
            for (std::size_t j = 0; j < phase.samples.size(); ++j) {
                const auto& sample = phase.samples[j];
                output << (j ? "," : "") << "{\"privateBytes\":" << sample.privateBytes << ",\"workingSet\":" << sample.workingSet
                    << ",\"handles\":" << sample.handles << ",\"gdiObjects\":" << sample.gdi << ",\"userObjects\":" << sample.user << "}";
            }
            output << "]}";
        }
        output << "\n  ]\n}\n";
        output.flush();
        if (!output) throw std::runtime_error("Native measurement report write failed");
    }
    void fixtureTick() {
        ++fixtureTicks_;
        if (fixtureTicks_ == 5) {
            if (!fixtureReady_) throw std::runtime_error("Synthetic connection not ready within five seconds");
            fixtureBefore_ = measure(); fixtureStarted_ = Clock::now();
            fixtureRendersBefore_ = renderer_.renderCount();
        }
        if (fixtureTicks_ < 15) return;
        const auto after = measure();
        const double elapsed = std::chrono::duration<double>(Clock::now()-fixtureStarted_).count();
        const auto idleRenders = renderer_.renderCount()-fixtureRendersBefore_;
        if (!fixtureReady_ || idleRenders != 0) throw std::runtime_error("Synthetic connected idle redraw/readiness regression");
        renderer_.savePng(options_.fixtureVerificationDirectory / "native-synthetic-connected.png");
        std::ofstream output(options_.fixtureVerificationDirectory / "native-synthetic-connected.json");
        output << "{\"scope\":\"synthetic fixture; NOT live Discord or chat parity\",\"seconds\":" << elapsed
            << ",\"privateBytesBefore\":" << fixtureBefore_.privateBytes << ",\"privateBytesAfter\":" << after.privateBytes
            << ",\"workingSetBefore\":" << fixtureBefore_.workingSet << ",\"workingSetAfter\":" << after.workingSet
            << ",\"cpuOneCorePercent\":" << static_cast<double>(after.cpu100ns-fixtureBefore_.cpu100ns)/100000.0/elapsed
            << ",\"idleRenders\":" << idleRenders << ",\"handlesBefore\":" << fixtureBefore_.handles
            << ",\"handlesAfter\":" << after.handles << ",\"ready\":true}";
        output.flush();
        if (!output) throw std::runtime_error("Cannot write fixture measurements");
        DestroyWindow(window_);
    }
    void chatTick() {
        auto* chat = renderer_.chat();
        if (!chat) throw std::runtime_error("Chat test view missing");
        ++chatTicks_;
        if (options_.chatCaptureOnly) {
            if (chatTicks_ == 1) {
                assertion(chat->messageCount() > 0,"Captured canonical chat is not empty");
                chat->followLatest(); renderNow();
                renderer_.savePng(options_.chatVerificationDirectory / "captured-chat-latest.png");
                chat->scroll(-chat->scrolling().maximum()); renderNow();
                renderer_.savePng(options_.chatVerificationDirectory / "captured-chat-first.png");
                chatBefore_ = measure(); chatStarted_ = Clock::now(); chatIdleRenders_ = renderer_.renderCount();
                return;
            }
            if (chatTicks_ < 4) return;
            const auto after = measure();
            const double elapsed = std::chrono::duration<double>(Clock::now()-chatStarted_).count();
            assertion(renderer_.renderCount() == chatIdleRenders_,"Captured chat idle does not repaint");
            std::ofstream output(options_.chatVerificationDirectory / "captured-chat-metrics.json");
            output << "{\"scope\":\"offline captured chat rendering; NOT OAuth/live stream/media parity\",\"messages\":" << chat->messageCount()
                << ",\"assertions\":" << assertions_ << ",\"privateBytes\":" << after.privateBytes << ",\"workingSet\":" << after.workingSet
                << ",\"seconds\":" << elapsed << ",\"cpuOneCorePercent\":" << static_cast<double>(after.cpu100ns-chatBefore_.cpu100ns)/100000.0/elapsed
                << ",\"idleRenders\":0}";
            output.flush(); if (!output) throw std::runtime_error("Captured chat report failed");
            DestroyWindow(window_);
            return;
        }
        if (chatTicks_ == 1) {
            assertion(chat->messageCount() == 20 && chat->scrolling().following(),"Chat20 initially follows latest");
            renderer_.savePng(options_.chatVerificationDirectory / "chat20.png");
            const auto outlined = chat->outlineBuilds(); renderNow();
            assertion(outlined > 0 && chat->outlineBuilds() == outlined,"Glyph outlines are cached between paints");
            chat->toggleOutline(); renderNow(); renderer_.savePng(options_.chatVerificationDirectory / "chat-no-outline.png");
            assertion(chat->outlineBuilds() == outlined,"Disabling outlines does not rebuild geometry");
            chat->toggleOutline(); renderNow();
            for (unsigned i = 0; i < 3; ++i) {
                chat->cycleRolePosition(); renderNow();
                renderer_.savePng(options_.chatVerificationDirectory / (L"chat-role-"+std::to_wstring(i)+L".png"));
            }
            assertion(chat->messageCount() == 20,"Role placement changes preserve retained messages");
            chat->scroll(-chat->scrolling().maximum()/2); renderNow();
            const auto anchor = chat->scrolling().anchor();
            loadChat(options_.chatFixture.parent_path()/L"chat21.json"); renderNow();
            assertion(chat->scrolling().anchor().id == anchor.id && chat->scrolling().unread() == 1,"New message preserves reading anchor/unread");
            const auto beforeLayout = chat->layoutBuilds();
            for (int i = 0; i < 100; ++i) { chat->scroll(i%2 ? -24.0f : 24.0f); renderNow(); }
            assertion(chat->layoutBuilds() == beforeLayout,"Scrolling reuses text layout");
            RECT chatBounds{}; GetClientRect(window_,&chatBounds);
            const float right = static_cast<float>(chatBounds.right)*96/dpi_-24;
            assertion(chat->pressScrollbar(right-2,110),"Scrollbar accepts track press");
            chat->dragScrollbar(100000); chat->endScrollbar();
            assertion(chat->scrolling().following() && chat->scrolling().unread() == 0,"Scrollbar bottom restores latest following");
            chat->scroll(-chat->scrolling().maximum()/2); renderNow();
            const auto beforeResize = measure();
            for (int i = 0; i < 40; ++i) {
                const auto started = Clock::now();
                SetWindowPos(window_,nullptr,0,0,core::toPixels(i%2 ? 640.0f : 360.0f,dpi_),core::toPixels(620,dpi_),SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                renderNow(); chatResizeMs_.push_back(std::chrono::duration<double,std::milli>(Clock::now()-started).count());
            }
            assertion(measure().gdi <= beforeResize.gdi+2,"Chat resize GDI resources bounded");
            SetWindowPos(window_,nullptr,0,0,core::toPixels(360,dpi_),core::toPixels(620,dpi_),SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            renderNow(); renderer_.savePng(options_.chatVerificationDirectory / "chat-narrow.png");
            SetWindowPos(window_,nullptr,0,0,core::toPixels(640,dpi_),core::toPixels(620,dpi_),SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            for (unsigned i = 0; i < 5; ++i) {
                chat->followLatest(); renderNow();
                renderer_.savePng(options_.chatVerificationDirectory / (L"chat-font-"+std::to_wstring(i)+L".png"));
                chat->cycleFont();
            }
            chat->changeSize(8); renderNow(); renderer_.savePng(options_.chatVerificationDirectory / "chat-large-font.png");
            chat->changeSize(-8);
            state_.backgroundOpacity = 0; renderNow();
            assertion(renderer_.alphaAt(4,100) > 0,"Chat zero-opacity edit edge remains visible");
            renderer_.savePng(options_.chatVerificationDirectory / "chat-zero-opacity.png");
            state_.backgroundOpacity = 0.85f;
            loadChat(options_.chatFixture.parent_path()/L"retained20.json"); renderNow();
            assertion(chat->messageCount() == 20,"Server retention applied without local ghost rows");
            loadChat(options_.chatFixture); chat->followLatest(); renderNow();
            return;
        }
        if (chatTicks_ == 4) { chatBefore_ = measure(); chatStarted_ = Clock::now(); chatIdleRenders_ = renderer_.renderCount(); }
        if (chatTicks_ < 12) return;
        const auto after = measure(); const auto elapsed = std::chrono::duration<double>(Clock::now()-chatStarted_).count();
        assertion(renderer_.renderCount() == chatIdleRenders_,"Chat idle does not repaint");
        std::ofstream output(options_.chatVerificationDirectory / "native-chat-metrics.json");
        output << "{\"scope\":\"M3 synthetic text prototype; NOT live Discord/media parity\",\"assertions\":" << assertions_
            << ",\"messages\":" << chat->messageCount() << ",\"seconds\":" << elapsed << ",\"privateBytes\":" << after.privateBytes
            << ",\"workingSet\":" << after.workingSet << ",\"cpuOneCorePercent\":" << static_cast<double>(after.cpu100ns-chatBefore_.cpu100ns)/100000.0/elapsed
            << ",\"idleRenders\":0,\"ownedTextSpanBytes\":" << chat->layoutTextBytes() << ",\"layoutBuilds\":" << chat->layoutBuilds()
            << ",\"outlineBuilds\":" << chat->outlineBuilds() << ",\"resizeMs\":[";
        for (std::size_t i = 0; i < chatResizeMs_.size(); ++i) output << (i ? "," : "") << chatResizeMs_[i];
        output << "]}"; output.flush(); if (!output) throw std::runtime_error("Chat measurement write failed");
        DestroyWindow(window_);
    }
    void mediaTick() {
        ++mediaTicks_; auto* chat = renderer_.chat();
        assertion(chat && media_,"Media fixture configured");
        const auto stats = media_->statistics();
        assertion(stats.failures == 0,"Native media decode has no failures");
        if (mediaTicks_ == 1) {
            assertion(stats.published > 0 && stats.visible == 3,"Visible GIF, static image and inline emoji start automatically");
            core::Json links(R"([{"kind":"Text","text":"앞 "},{"kind":"MediaSource","text":"https://fixture.test/visible.gif","mediaId":"fixture-animation"},{"kind":"Text","text":" 뒤 https://example.test/keep"},{"kind":"MediaSource","text":" https://fixture.test/missing.gif","mediaId":"unavailable"}])");
            const auto filtered=chat->externalRuns(links.root(),16).text;
            assertion(filtered==L"앞  뒤 https://example.test/keep https://fixture.test/missing.gif","Only successfully loaded media's source link disappears; unrelated and unavailable links survive");
            mediaBefore_ = measure(); mediaStarted_ = Clock::now(); mediaVisibleBefore_ = stats; mediaLayoutBefore_ = chat->layoutBuilds();
            renderer_.savePng(options_.mediaVerificationDirectory/"media-visible.png");
        } else if (mediaTicks_ == 4) {
            mediaVisibleAfter_ = stats;
            assertion(stats.published > mediaVisibleBefore_.published+5,"Animation advances on native renderer");
            assertion(chat->layoutBuilds() == mediaLayoutBefore_,"Animation does not rebuild text layout");
            const auto after = measure(); const auto seconds = std::chrono::duration<double>(Clock::now()-mediaStarted_).count();
            std::ofstream output(options_.mediaVerificationDirectory/"native-media-visible-metrics.json");
            output << "{\"scope\":\"M4 synthetic native media; NOT Full A/B or real GIF validation\",\"seconds\":" << seconds
                << ",\"privateBytes\":" << after.privateBytes << ",\"workingSet\":" << after.workingSet
                << ",\"cpuOneCorePercent\":" << static_cast<double>(after.cpu100ns-mediaBefore_.cpu100ns)/100000.0/seconds
                << ",\"decoded\":" << stats.decoded << ",\"published\":" << stats.published << ",\"nonconsecutiveFrames\":" << stats.lateFrames
                << ",\"ownedPixelBytes\":" << stats.ownedPixelBytes << "}";
            output.flush(); if (!output) throw std::runtime_error("Media metrics write failed");
            toggleVisible();
        } else if (mediaTicks_ == 5 || mediaTicks_ == 10) {
            assertion(stats.visible == 0 && stats.ownedPixelBytes == 0,"Hidden/offscreen media releases current and next buffers");
            mediaIdleDecoded_ = stats.decoded; mediaIdleRenders_ = renderer_.renderCount();
        } else if (mediaTicks_ == 7 || mediaTicks_ == 12) {
            assertion(stats.decoded == mediaIdleDecoded_,"Hidden/offscreen media does not decode");
            assertion(renderer_.renderCount() == mediaIdleRenders_,"Hidden/offscreen media does not repaint");
            if (mediaTicks_ == 7) toggleVisible();
            else { chat->followLatest(); renderNow(); }
        } else if (mediaTicks_ == 8) {
            assertion(stats.published > mediaVisibleAfter_.published,"Animation resumes after hide/show");
            state_.backgroundOpacity = 0; renderNow();
            assertion(renderer_.alphaAt(4,100) > 0,"Zero opacity preserves resize border");
            renderer_.savePng(options_.mediaVerificationDirectory/"media-zero-opacity.png");
        } else if (mediaTicks_ == 9) { chat->scroll(-100000); renderNow(); }
        else if (mediaTicks_ == 13) {
            assertion(stats.visible > 0 && stats.decoded > mediaIdleDecoded_,"Offscreen media resumes correctly");
            renderer_.discardSurface(); renderNow();
            renderer_.savePng(options_.mediaVerificationDirectory/"media-device-recreated.png");
            chat->cycleFont(); chat->changeSize(2); renderNow();
        } else if (mediaTicks_ == 14) {
            renderer_.savePng(options_.mediaVerificationDirectory/"media-inline-font-change.png");
            std::ofstream output(options_.mediaVerificationDirectory/"native-media-checks.json");
            output << "{\"status\":\"PASS\",\"synthetic\":true,\"assertions\":" << assertions_ << ",\"failures\":" << stats.failures << "}";
            output.flush(); if (!output) throw std::runtime_error("Media assertions write failed");
            DestroyWindow(window_);
        }
    }
    void salesTick() {
        ++salesTicks_; auto* sales = renderer_.sales(); assertion(sales != nullptr,"Native canonical Sales view exists");
        const auto load = [&](const wchar_t* name) { loadChat(options_.chatFixture.parent_path()/name); renderNow(); };
        const auto captureSales = [&](const wchar_t* name) { renderer_.savePng(options_.salesVerificationDirectory/name); };
        const auto centeredAction = [&] {
            const auto button=sales->actionBounds(),text=sales->actionTextBounds();
            assertion(text.right>text.left && text.bottom>text.top &&
                std::abs((button.left+button.right)-(text.left+text.right))<0.05f &&
                std::abs((button.top+button.bottom)-(text.top+text.bottom))<0.05f,
                "Sales button text is centered horizontally and vertically");
            assertion(text.left>=button.left && text.right<=button.right && text.top>=button.top && text.bottom<=button.bottom,
                "Centered Sales label fits inside button");
        };
        const auto centeredHeadline = [&] {
            const auto header=sales->headerBounds(),label=sales->headlineTextBounds();
            assertion(std::abs((header.top+header.bottom)-(label.top+label.bottom))<0.05f,
                "Sales headline is vertically centered using measured text height");
            assertion(std::abs(label.left-(header.left+14))<0.05f,
                "Sales headline keeps its original left inset");
        };
        const auto lockedControls = [&](const wchar_t* captureName) {
            const auto before=sales->actionBounds();const auto count=sales->count();const auto headline=sales->headline();
            const auto submissions=salesActions_.metrics().submitted;
            state_.locked=true;renderNow();centeredHeadline();
            const auto hidden=sales->actionBounds(),hiddenText=sales->actionTextBounds();
            assertion(hidden.right==hidden.left && hiddenText.right==hiddenText.left,
                "Locked Sales action draws neither button nor text");
            assertion(!sales->actionAt((before.left+before.right)/2,(before.top+before.bottom)/2,true),
                "Hidden Sales control cannot expose a stale hit target");
            assertion(sales->count()==count && sales->headline()==headline && salesActions_.metrics().submitted==submissions,
                "Lock preserves canonical Sales state and never dispatches an action");
            captureSales(captureName);
            state_.locked=false;renderNow();centeredAction();centeredHeadline();
        };
        if (salesTicks_ == 1) {
            assertion(sales->visible() && sales->count() == 3,"Canonical queue and next turn displayed");
            assertion(sales->sessionLabel() == L"12 / 30","Session player count from server");
            captureSales(L"sales-next.png");
            const auto header = sales->headerBounds(); const auto x = (header.left+header.right)/2, y = (header.top+header.bottom)/2;
            assertion(!sales->click(x,y,false),"Locked Sales header is not interactive");
            assertion(sales->click(x,y,true) && !sales->expanded(),"Unlocked header collapses details"); renderNow();
            assertion(sales->count() == 3,"Collapse preserves canonical queue"); captureSales(L"sales-collapsed.png");
            const auto collapsed = sales->headerBounds(); sales->click(x,(collapsed.top+collapsed.bottom)/2,true); load(L"current.json");
        } else if (salesTicks_ == 2) {
            assertion(sales->headline().find(L"판매할 차례") != std::wstring::npos,"Own current turn uses canonical headline");
            assertion(sales->headline().find(L"벙커") == std::wstring::npos,"Headline does not repeat product details");
            captureSales(L"sales-current.png");
            renderer_.applySettings(preferences_);renderNow();
            core::SalesCommand command{"123","synthetic",{},false};sales->setAction(command,false,L"");renderNow();
            centeredAction();
            centeredHeadline();lockedControls(L"sales-current-locked.png");captureSales(L"sales-current-unlocked.png");
            const auto actionHeader=sales->headerBounds();const auto ax=actionHeader.right-80,ay=(actionHeader.top+actionHeader.bottom)/2;
            assertion(!sales->actionAt(ax,ay,false),"Locked Sales action is not interactive");
            assertion(sales->actionAt(ax,ay,true)==command,"Unlocked complete button targets the server supplied message");captureSales(L"sales-action-complete.png");
            // Exercise the actual click dispatch, not just button hit-testing.
            // No network worker exists in this fixture: Discord writes stay zero.
            salesActions_.observe(core::Json(R"({"sales":{"currentMessageId":"123","actions":{"generation":"synthetic","targets":[{"messageId":"123","canComplete":true,"canUndo":false,"botCompleted":false}]}}})"));
            state_.locked=false;
            assertion(dispatchSalesAction(ax,ay) && salesActions_.busy(),"One click queues Sales action without a confirmation dialog");
            (void)dispatchSalesAction(ax,ay);
            assertion(salesActions_.metrics().submitted==1,"Repeated click dispatch cannot enqueue a duplicate write");
            const auto originalCount=sales->count();sales->setAction({},true,L"서버 판매 상태 확인 중");renderNow();
            centeredAction();
            lockedControls(L"sales-pending-locked.png");
            assertion(!sales->actionAt(ax,ay,true) && sales->count()==originalCount,"Pending blocks double clicks and preserves authoritative queue");captureSales(L"sales-action-pending.png");
            sales->setAction({},false,L"");load(L"recovering.json");
        } else if (salesTicks_ == 3) {
            assertion(sales->count() == 3 && sales->headline().find(L"판매할 차례") != std::wstring::npos,"Recovery retains Full current alert state");
            assertion(sales->sessionLabel() == L"세션 정보 확인 중","Unknown session is not fabricated zero players");
            captureSales(L"sales-recovering.png"); load(L"completed.json");
        } else if (salesTicks_ == 4) {
            assertion(sales->count() == 2 && sales->headline().find(L"판매할 차례") == std::wstring::npos,"Server completion removes prior seller");
            sales->cycleHost(); renderNow(); assertion(sales->sessionLabel() == L"호스트 오프라인","Host selection preserves offline meaning");
            captureSales(L"sales-completed.png"); load(L"empty.json");
        } else if (salesTicks_ == 5) {
            assertion(!sales->visible() && sales->count() == 0,"Canonical empty live queue hides panel");
            captureSales(L"sales-empty.png");
            sales->setAction(core::SalesCommand{"123","synthetic",{},true},false,L"판매 완료 확인됨");renderNow();
            centeredAction();
            lockedControls(L"sales-undo-locked.png");
            assertion(sales->visible() && sales->count()==0,"Own completed item can be undone even when active queue is empty");
            const auto undoHeader=sales->headerBounds();auto undo=sales->actionAt(undoHeader.right-80,(undoHeader.top+undoHeader.bottom)/2,true);
            assertion(undo && undo->undo,"Empty queue exposes the server supplied undo target");captureSales(L"sales-action-undo-empty.png");
            SetWindowPos(window_,nullptr,0,0,core::toPixels(360,dpi_),core::toPixels(640,dpi_),SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE);renderNow();captureSales(L"sales-action-narrow.png");
            centeredAction();
            centeredHeadline();
            sales->setAction({},false,L"");renderNow();assertion(!sales->visible(),"No synthetic action remains after revocation");
            std::ofstream output(options_.salesVerificationDirectory/"native-sales-checks.json");
            output << "{\"status\":\"PASS\",\"synthetic\":true,\"discordWrites\":0,\"assertions\":" << assertions_ << "}";
            output.flush(); if (!output) throw std::runtime_error("Sales verification write failed");
            DestroyWindow(window_);
        }
    }
    void fail(const char* reason) noexcept {
        failure_ = true;
        if (verifying() || !options_.fixtureVerificationDirectory.empty() || !options_.chatVerificationDirectory.empty() || !options_.mediaVerificationDirectory.empty() || !options_.salesVerificationDirectory.empty() || !options_.settingsVerificationDirectory.empty()) {
            const auto& directory = !options_.settingsVerificationDirectory.empty()?options_.settingsVerificationDirectory:verifying() ? options_.verificationDirectory : !options_.fixtureVerificationDirectory.empty() ? options_.fixtureVerificationDirectory : !options_.chatVerificationDirectory.empty() ? options_.chatVerificationDirectory : !options_.mediaVerificationDirectory.empty() ? options_.mediaVerificationDirectory : options_.salesVerificationDirectory;
            try { std::ofstream(directory / "native-shell-error.txt") << reason; } catch (...) {}
        } else {
            MessageBoxA(window_,reason,"LS Overlay Core - native shell error",MB_OK | MB_ICONERROR);
        }
        if (window_) DestroyWindow(window_);
        else PostQuitMessage(1);
    }
    static LRESULT CALLBACK windowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) noexcept {
        auto* self = reinterpret_cast<Shell*>(GetWindowLongPtrW(window,GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            self = static_cast<Shell*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams);
            self->window_ = window;
            SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(self));
        }
        if (!self) return DefWindowProcW(window,message,wparam,lparam);
        try { return self->dispatch(window,message,wparam,lparam); }
        catch (const std::exception& error) { self->fail(error.what()); return 0; }
        catch (...) { self->fail("Unknown native shell failure"); return 0; }
    }
    LRESULT dispatch(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
        if (taskbarCreated_ != 0 && message == taskbarCreated_) { if (!addTray()) throw std::runtime_error("Tray recreation failed"); return 0; }
        switch (message) {
        case WM_NCCALCSIZE: return 0;
        case WM_NCHITTEST: {
            POINT p{GET_X_LPARAM(lparam),GET_Y_LPARAM(lparam)};ScreenToClient(window_,&p);
            if(!state_.locked && renderer_.settingsHit(core::toDip(p.x,dpi_),core::toDip(p.y,dpi_)))return HTCLIENT;
            if(altDrag_ && state_.locked)return HTCAPTION;return hit(lparam);
        }
        case altMessage: {
            altDrag_=wparam && state_.visible && state_.locked && preferences_.enabled(core::Setting::ModifierDrag);
            auto style=GetWindowLongPtrW(window_,GWL_EXSTYLE);if(state_.locked && !altDrag_)style|=WS_EX_TRANSPARENT;else style&=~static_cast<LONG_PTR>(WS_EX_TRANSPARENT);
            SetWindowLongPtrW(window_,GWL_EXSTYLE,style);return 0;
        }
        case WM_ERASEBKGND: return 1;
        case WM_PAINT: {
            PAINTSTRUCT paint{}; BeginPaint(window,&paint); EndPaint(window,&paint); queueDraw(); return 0;
        }
        case WM_SIZE: queueDraw(); return 0;
        case WM_DISPLAYCHANGE:
        case WM_DWMCOMPOSITIONCHANGED:
            renderer_.discardSurface(); queueDraw(); return 0;
        case WM_POWERBROADCAST:
            if (wparam == PBT_APMRESUMEAUTOMATIC || wparam == PBT_APMRESUMESUSPEND) { renderer_.discardSurface(); queueDraw(); }
            return TRUE;
        case WM_GETMINMAXINFO: {
            auto* limits = reinterpret_cast<MINMAXINFO*>(lparam);
            limits->ptMinTrackSize = {core::toPixels(minWidthDip,dpi_),core::toPixels(renderer_.compact()?240.0f:static_cast<float>(minHeightDip),dpi_)};
            return 0;
        }
        case WM_DPICHANGED: {
            dpi_ = HIWORD(wparam);
            const auto* suggested = reinterpret_cast<RECT*>(lparam);
            SetWindowPos(window,nullptr,suggested->left,suggested->top,suggested->right-suggested->left,suggested->bottom-suggested->top,SWP_NOZORDER | SWP_NOACTIVATE);
            queueDraw(); return 0;
        }
        case drawMessage: {
            const bool mediaOnly = !fullDrawRequired_; fullDrawRequired_ = false;
            drawQueued_ = false; renderNow(mediaOnly); return 0;
        }
        case connectionMessage: {
            refreshSalesAction();
            std::wstring status;
            std::shared_ptr<const core::Json> snapshot;
            { std::lock_guard lock(connectionMutex_); connectionNotificationQueued_ = false; status = latestConnectionStatus_; snapshot = std::move(latestChatSnapshot_); }
            fixtureReady_ = status.find(L"M2 합성 연결 정상") == 0;
            developmentStatus_ = status;
            if(settingsWindow_)settingsWindow_->status(status);
            developmentState_ = status.find(L"브라우저")!=std::wstring::npos ? "approval-pending" :
                (status.find(L"개발 실시간 채팅") == 0 || status.find(L"실시간 채팅") == 0) ? "chat-live" : status.find(L"재연결")!=std::wstring::npos ? "reconnecting" : "not-live";
            if (snapshot) {
                auto* selection=core::field(snapshot->root(),"chatSelection");availableChannels_.clear();auto* slots=yyjson_is_obj(selection)?core::field(selection,"availableSlots"):nullptr;
                if(yyjson_is_arr(slots) && yyjson_arr_size(slots)<=8){std::size_t i=0,n=0;yyjson_val* slot=nullptr;yyjson_arr_foreach(slots,i,n,slot)if(yyjson_is_uint(slot) && yyjson_get_uint(slot)<8)availableChannels_.push_back(static_cast<unsigned>(yyjson_get_uint(slot)));}
                salesAlert(*snapshot);
                ++developmentSnapshots_;
                if (media_ && !options_.developmentEndpoint.empty()) {
                    std::set<std::string> references;
                    const auto visit=[&](auto&& self,yyjson_val* value,unsigned depth)->void {
                        if (depth>32) throw std::runtime_error("Media reference nesting limit");
                        if (yyjson_is_str(value)) {const std::string id(core::stringValue(value));if (core::validMediaIdentity(id)) references.insert(id);}
                        else if (yyjson_is_arr(value)) {std::size_t i=0,n=0;yyjson_val* item=nullptr;yyjson_arr_foreach(value,i,n,item) self(self,item,depth+1);}
                        else if (yyjson_is_obj(value)) {std::size_t i=0,n=0;yyjson_val* key=nullptr;yyjson_val* item=nullptr;yyjson_obj_foreach(value,i,n,key,item) self(self,item,depth+1);}
                    };
                    visit(visit,snapshot->root(),0);media_->retain(references);
                }
                auto* chat = core::field(snapshot->root(),"chat");
                // M2-only fixtures remain usable as a connection shell.
                if (!options_.developmentEndpoint.empty() || renderer_.chat() || (yyjson_arr_size(chat) && yyjson_is_str(core::field(yyjson_arr_get(chat,0),"presentationHash")))) renderer_.setChatSnapshot(std::move(snapshot));
            }
            refreshSalesAction();renderer_.setConnectionStatus(std::move(status)); queueDraw(); return 0;
        }
        case salesActionMessage:
            refreshSalesAction();queueDraw();return 0;
        case WM_HOTKEY:
            if (wparam == showHotkey) toggleVisible();
            else if (wparam == lockHotkey) toggleLocked();
            else if(wparam==previousChannelHotkey)channelStep(-1);
            else if(wparam==nextChannelHotkey)channelStep(1);
            return 0;
        case WM_MOUSEWHEEL: {
            POINT pointer{GET_X_LPARAM(lparam),GET_Y_LPARAM(lparam)}; ScreenToClient(window_,&pointer);
            if (renderer_.sales() && !state_.locked && renderer_.sales()->scroll(static_cast<float>(pointer.x)*96/dpi_,static_cast<float>(pointer.y)*96/dpi_,
                -static_cast<float>(GET_WHEEL_DELTA_WPARAM(wparam))/WHEEL_DELTA*48)) { queueDraw(); return 0; }
            if (renderer_.chat() && !state_.locked) { renderer_.chat()->scroll(-static_cast<float>(GET_WHEEL_DELTA_WPARAM(wparam))/WHEEL_DELTA*64); queueDraw(); }
            return 0;
        }
        case WM_LBUTTONDOWN:
            if(!state_.locked && renderer_.settingsHit(core::toDip(GET_X_LPARAM(lparam),dpi_),core::toDip(GET_Y_LPARAM(lparam),dpi_))) {openSettings();return 0;}
            if(!state_.locked && renderer_.chat() && renderer_.chat()->clickMedia(core::toDip(GET_X_LPARAM(lparam),dpi_),core::toDip(GET_Y_LPARAM(lparam),dpi_))) {queueDraw();return 0;}
            if(options_.salesActions && dispatchSalesAction(core::toDip(GET_X_LPARAM(lparam),dpi_),core::toDip(GET_Y_LPARAM(lparam),dpi_)))return 0;
            if (renderer_.sales() && renderer_.sales()->click(static_cast<float>(GET_X_LPARAM(lparam))*96/dpi_,static_cast<float>(GET_Y_LPARAM(lparam))*96/dpi_,!state_.locked)) { queueDraw(); return 0; }
            if (renderer_.chat() && !state_.locked && renderer_.chat()->pressScrollbar(
                static_cast<float>(GET_X_LPARAM(lparam))*96/dpi_,static_cast<float>(GET_Y_LPARAM(lparam))*96/dpi_)) {
                SetCapture(window_); queueDraw();
            }
            return 0;
        case WM_MOUSEMOVE:
            if (renderer_.chat() && renderer_.chat()->draggingScrollbar() && !state_.locked) {
                renderer_.chat()->dragScrollbar(static_cast<float>(GET_Y_LPARAM(lparam))*96/dpi_); queueDraw();
            }
            return 0;
        case WM_CAPTURECHANGED:
            if (renderer_.chat()) renderer_.chat()->endScrollbar();
            return 0;
        case WM_LBUTTONUP:
            if (renderer_.chat() && !state_.locked) {
                if (renderer_.chat()->draggingScrollbar()) { renderer_.chat()->endScrollbar(); ReleaseCapture(); return 0; }
                RECT bounds{}; GetClientRect(window,&bounds);
                if (!renderer_.compact() && GET_Y_LPARAM(lparam) > bounds.bottom-core::toPixels(60,dpi_)) { renderer_.chat()->followLatest(); queueDraw(); }
            }
            return 0;
        case WM_KEYDOWN:
            if (renderer_.chat() && !state_.locked) {
                auto* chat = renderer_.chat();
                if (wparam == VK_END) chat->followLatest();
                else if (wparam == VK_HOME) chat->scroll(-chat->scrolling().maximum());
                else if (wparam == VK_UP || wparam == VK_PRIOR) chat->scroll(wparam == VK_UP ? -32.0f : -250.0f);
                else if (wparam == VK_DOWN || wparam == VK_NEXT) chat->scroll(wparam == VK_DOWN ? 32.0f : 250.0f);
                queueDraw();
            }
            return 0;
        case WM_CONTEXTMENU: {
            POINT point{GET_X_LPARAM(lparam),GET_Y_LPARAM(lparam)};
            if (point.x == -1 && point.y == -1) GetCursorPos(&point);
            menu(point); return 0;
        }
        case trayMessage:
            if (lparam == WM_RBUTTONUP || lparam == WM_CONTEXTMENU) { POINT point{}; GetCursorPos(&point); menu(point); }
            else if (lparam == WM_LBUTTONDBLCLK) toggleVisible();
            return 0;
        case WM_COMMAND:
            switch (LOWORD(wparam)) {
            case menuSettings:openSettings();break;
            case menuShow: toggleVisible(); break;
            case menuLock: toggleLocked(); break;
            case menuOpacity: state_.backgroundOpacity = state_.backgroundOpacity == 0 ? 0.85f : 0; queueDraw(); break;
            case menuExit: DestroyWindow(window); break;
            case menuFont: if (renderer_.chat()) { renderer_.chat()->cycleFont(); queueDraw(); } break;
            case menuLarger: if (renderer_.chat()) { renderer_.chat()->changeSize(2); queueDraw(); } break;
            case menuSmaller: if (renderer_.chat()) { renderer_.chat()->changeSize(-2); queueDraw(); } break;
            case menuMention: if (renderer_.chat()) { renderer_.chat()->toggleMentionBackground(); queueDraw(); } break;
            case menuOutline: if (renderer_.chat()) { renderer_.chat()->toggleOutline(); queueDraw(); } break;
            case menuRolePosition: if (renderer_.chat()) { renderer_.chat()->cycleRolePosition(); queueDraw(); } break;
            case menuHost: if (renderer_.sales()) { renderer_.sales()->cycleHost(); queueDraw(); } break;
            case menuLatest: if (renderer_.chat()) { renderer_.chat()->followLatest(); queueDraw(); } break;
            default: break;
            }
            return 0;
        case mediaMessage: {
            bool reflow = renderer_.chat() && renderer_.chat()->mediaUpdated();
            if (renderer_.sales() && renderer_.chat()) reflow = renderer_.sales()->mediaUpdated(*renderer_.chat()) || reflow;
            queueDraw(!reflow); return 0;
        }
        case WM_TIMER:
            if(wparam==7 && !options_.settingsVerificationDirectory.empty()) {settingsTick();return 0;}
            if (!options_.developmentObservation.empty() && wparam == 6) { developmentSample(); return 0; }
            if (verifying() && wparam == 1) tick();
            else if (!options_.fixtureVerificationDirectory.empty() && wparam == 2) fixtureTick();
            else if (!options_.chatVerificationDirectory.empty() && wparam == 3) chatTick();
            else if (!options_.mediaVerificationDirectory.empty() && wparam == 4) mediaTick();
            else if (!options_.salesVerificationDirectory.empty() && wparam == 5) salesTick();
            return 0;
        case WM_CLOSE: DestroyWindow(window); return 0;
        case WM_DESTROY: {
            KillTimer(window,7);
            settingsWindow_.reset();if(altHook_) {UnhookWindowsHookEx(altHook_);altHook_=nullptr;altOwner_=nullptr;}
            KillTimer(window,1);
            KillTimer(window,2);
            KillTimer(window,3);
            KillTimer(window,4);
            KillTimer(window,5);
            connectionWorker_.request_stop();
            if (connectionWorker_.joinable()) connectionWorker_.join();
            if (renderer_.chat()) renderer_.chat()->setMedia(nullptr);
            media_.reset();
            if (showRegistered_) UnregisterHotKey(window,showHotkey);
            if (lockRegistered_) UnregisterHotKey(window,lockHotkey);
            UnregisterHotKey(window,previousChannelHotkey);UnregisterHotKey(window,nextChannelHotkey);
            NOTIFYICONDATAW icon{}; icon.cbSize = sizeof(icon); icon.hWnd = window; icon.uID = 1;
            Shell_NotifyIconW(NIM_DELETE,&icon);
            PostQuitMessage(failure_ ? 1 : 0); return 0;
        }
        case WM_NCDESTROY:
            SetWindowLongPtrW(window,GWLP_USERDATA,0); window_ = nullptr;
            return DefWindowProcW(window,message,wparam,lparam);
        default: return DefWindowProcW(window,message,wparam,lparam);
        }
    }
    Options options_;
    HINSTANCE instance_ = nullptr;
    HWND window_ = nullptr;
    core::ShellState state_;
    core::Renderer renderer_;
    std::shared_ptr<core::MediaStore> media_;
    unsigned dpi_ = 96, assertions_ = 0, settleTicks_ = 0;
    UINT taskbarCreated_ = 0;
    bool drawQueued_ = false, showRegistered_ = false, lockRegistered_ = false;
    bool fullDrawRequired_ = true;
    bool failure_ = false, smokeDone_ = false;
    double resizeMs_ = 0;
    DWORD resizeGdiBefore_ = 0, resizeGdiAfter_ = 0;
    std::vector<double> resizeLatencies_;
    std::vector<Phase> phases_;
    std::mutex connectionMutex_;
    bool connectionNotificationQueued_ = false;
    std::wstring latestConnectionStatus_;
    std::shared_ptr<const core::Json> latestChatSnapshot_;
    std::jthread connectionWorker_;
    bool fixtureReady_ = false;
    unsigned fixtureTicks_ = 0;
    Metrics fixtureBefore_;
    Clock::time_point fixtureStarted_;
    std::uint64_t fixtureRendersBefore_ = 0;
    unsigned chatTicks_ = 0;
    Metrics chatBefore_;
    Clock::time_point chatStarted_;
    std::uint64_t chatIdleRenders_ = 0;
    std::vector<double> chatResizeMs_;
    unsigned mediaTicks_ = 0;
    unsigned salesTicks_ = 0;
    Metrics mediaBefore_{};
    core::MediaStatistics mediaVisibleBefore_{}, mediaVisibleAfter_{};
    std::uint64_t mediaIdleDecoded_ = 0, mediaIdleRenders_ = 0, mediaLayoutBefore_ = 0;
    Clock::time_point mediaStarted_;
};

Options parseOptions() {
    int count = 0;
    auto* argv = CommandLineToArgvW(GetCommandLineW(),&count);
    if (!argv) throw std::runtime_error("CommandLineToArgv failed");
    struct Cleanup { LPWSTR* value; ~Cleanup() { LocalFree(value); } } cleanup{argv};
    Options options;
    // A public build is usable by double-clicking; dev/fixture modes remain explicit.
    if(count==1){options.developmentEndpoint=L"https://overlay.revo32.cloud";options.salesActions=true;}
    for (int i = 1; i < count; ++i) {
        const std::wstring arg = argv[i];
        if (arg == L"--verify" && i + 1 < count) options.verificationDirectory = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--fixture-endpoint" && i + 1 < count) options.fixtureEndpoint = argv[++i];
        else if (arg == L"--core-dev-endpoint" && i + 1 < count) options.developmentEndpoint = argv[++i];
        else if (arg == L"--sales-actions") options.salesActions = true;
        else if (arg == L"--core-dev-observe" && i + 1 < count) options.developmentObservation = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--fixture-verify" && i + 1 < count) options.fixtureVerificationDirectory = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--chat-fixture" && i + 1 < count) options.chatFixture = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--chat-verify" && i + 1 < count) options.chatVerificationDirectory = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--chat-capture" && i + 1 < count) { options.chatCaptureOnly = true; options.chatVerificationDirectory = std::filesystem::absolute(argv[++i]); }
        else if (arg == L"--media-fixture" && i + 1 < count) options.mediaFixture = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--media-verify" && i + 1 < count) options.mediaVerificationDirectory = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--sales-verify" && i + 1 < count) options.salesVerificationDirectory = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--settings-verify" && i+1<count) options.settingsVerificationDirectory=std::filesystem::absolute(argv[++i]);
        else if (arg == L"--phase-seconds" && i + 1 < count) {
            std::size_t used = 0;
            const std::wstring value = argv[++i];
            const auto seconds = std::stoul(value,&used);
            if (used != value.size() || seconds < 2 || seconds > 300) throw std::runtime_error("Phase seconds must be 2..300");
            options.phaseSeconds = static_cast<unsigned>(seconds);
        } else if (arg == L"--no-hotkeys") options.noHotkeys = true;
        else throw std::runtime_error("Invalid native Core development arguments");
    }
    if (!options.fixtureEndpoint.empty()) {
        const core::Endpoint endpoint(options.fixtureEndpoint);
        if (!endpoint.loopback || endpoint.secure || !options.verificationDirectory.empty())
            throw std::runtime_error("Fixture mode requires explicit HTTP loopback, separate from shell verification");
    } else if (!options.fixtureVerificationDirectory.empty()) throw std::runtime_error("Fixture verification requires an explicit fixture endpoint");
    if (!options.developmentEndpoint.empty() && (!options.fixtureEndpoint.empty() || !options.chatFixture.empty() || !options.verificationDirectory.empty() || !options.mediaFixture.empty()))
        throw std::runtime_error("Real read-only development connection is separate from synthetic fixtures");
    if (!options.developmentObservation.empty() && options.developmentEndpoint.empty()) throw std::runtime_error("Development observations require the explicit bridge");
    if(options.salesActions && options.developmentEndpoint.empty())throw std::runtime_error("Sales writes require an explicit authenticated development connection");
    if (!options.chatFixture.empty() && (!options.fixtureEndpoint.empty() || !options.verificationDirectory.empty())) throw std::runtime_error("Chat fixture mode is separate from network/shell verification");
    if (!options.chatVerificationDirectory.empty() && options.chatFixture.empty()) throw std::runtime_error("Chat verification requires a local semantic snapshot");
    if (!options.mediaFixture.empty() && (options.chatFixture.empty() || !options.chatVerificationDirectory.empty())) throw std::runtime_error("Media fixture requires a separate local chat snapshot mode");
    if (!options.mediaVerificationDirectory.empty() && options.mediaFixture.empty()) throw std::runtime_error("Media verification requires explicit media fixture");
    if (!options.salesVerificationDirectory.empty() && (options.chatFixture.empty() || !options.chatVerificationDirectory.empty() || !options.mediaVerificationDirectory.empty())) throw std::runtime_error("Sales verification requires separate local canonical fixtures");
    if(!options.settingsVerificationDirectory.empty() && (options.chatFixture.empty() || !options.developmentEndpoint.empty() || !options.mediaVerificationDirectory.empty() || !options.salesVerificationDirectory.empty() || !options.chatVerificationDirectory.empty()))throw std::runtime_error("Settings verification requires isolated synthetic fixture");
    return options;
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    const auto com = CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    if (FAILED(com)) return 1;
    int result = 1;
    Options options;
    try {
        options = parseOptions();
        if (!options.verificationDirectory.empty()) std::filesystem::create_directories(options.verificationDirectory);
        if (!options.chatVerificationDirectory.empty()) std::filesystem::create_directories(options.chatVerificationDirectory);
        if (!options.mediaVerificationDirectory.empty()) std::filesystem::create_directories(options.mediaVerificationDirectory);
        if (!options.salesVerificationDirectory.empty()) std::filesystem::create_directories(options.salesVerificationDirectory);
        if (!options.settingsVerificationDirectory.empty()) std::filesystem::create_directories(options.settingsVerificationDirectory);
        Shell shell(options); result = shell.run(instance);
    } catch (const std::exception& error) {
        if (!options.verificationDirectory.empty()) {
            try { std::ofstream(options.verificationDirectory / "native-shell-error.txt") << error.what(); } catch (...) {}
        } else if (!options.mediaVerificationDirectory.empty()) {
            try { std::ofstream(options.mediaVerificationDirectory / "native-shell-error.txt") << error.what(); } catch (...) {}
        } else if (!options.salesVerificationDirectory.empty()) {
            try { std::ofstream(options.salesVerificationDirectory / "native-shell-error.txt") << error.what(); } catch (...) {}
        } else if(!options.settingsVerificationDirectory.empty()) {
            std::ofstream(options.settingsVerificationDirectory/"native-shell-error.txt")<<error.what();
        } else MessageBoxA(nullptr,error.what(),"LS Overlay Core - startup error",MB_OK | MB_ICONERROR);
    }
    CoUninitialize();
    return result;
}
