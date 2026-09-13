#include "renderer.hpp"
#include "fixture_client.hpp"
#include "transport.hpp"
#include "chat_view.hpp"
#include <windowsx.h>
#include <shellapi.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <chrono>
#include <fstream>
#include <iomanip>
#include <memory>
#include <string>
#include <vector>
#include <mutex>
#include <thread>

namespace {
constexpr UINT drawMessage = WM_APP + 1, trayMessage = WM_APP + 2, connectionMessage = WM_APP + 3;
constexpr int showHotkey = 1, lockHotkey = 2;
constexpr UINT menuShow = 100, menuLock = 101, menuOpacity = 102, menuExit = 103;
constexpr UINT menuFont = 104, menuLarger = 105, menuSmaller = 106, menuMention = 107, menuLatest = 108;
constexpr UINT menuOutline = 109;
constexpr UINT menuRolePosition = 110;
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
    std::filesystem::path chatFixture, chatVerificationDirectory;
    unsigned phaseSeconds = 15;
    bool noHotkeys = false;
    bool chatCaptureOnly = false;
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
            klass.lpszClassName, L"LS Overlay Core · 내부 개발 검증", WS_POPUP | WS_THICKFRAME,
            80,80,640,520,nullptr,nullptr,instance,this);
        if (!window_) throw std::runtime_error("Native window creation failed");
        dpi_ = GetDpiForWindow(window_);
        SetWindowPos(window_, nullptr,0,0,core::toPixels(640,dpi_),core::toPixels(520,dpi_),SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (!options_.noHotkeys) {
            showRegistered_ = RegisterHotKey(window_,showHotkey,MOD_NOREPEAT,VK_F9) != FALSE;
            lockRegistered_ = RegisterHotKey(window_,lockHotkey,MOD_NOREPEAT,VK_F10) != FALSE;
        }
        taskbarCreated_ = RegisterWindowMessageW(L"TaskbarCreated");
        if (!addTray()) throw std::runtime_error("Tray icon unavailable; refusing an unrecoverable hidden window");
        ShowWindow(window_, SW_SHOWNOACTIVATE);
        if (!options_.chatFixture.empty()) loadChat(options_.chatFixture);
        renderNow();
        if (verifying()) {
            std::filesystem::create_directories(options_.verificationDirectory);
            if (!SetTimer(window_,1,1000,nullptr)) throw std::runtime_error("Verification timer unavailable");
        }
        if (!options_.fixtureEndpoint.empty()) {
            const HWND target = window_;
            renderer_.setConnectionStatus(L"M2 합성 환경에 연결 중 · 실제 Discord 아님"); queueDraw();
            connectionWorker_ = std::jthread([this,target](std::stop_token stop) {
                core::runFixtureClient(options_.fixtureEndpoint,stop,[this,target](std::wstring status,std::shared_ptr<const core::Json> snapshot) {
                    // One owned latest-value slot: no external message carries a raw pointer.
                    std::lock_guard lock(connectionMutex_);
                    latestConnectionStatus_ = std::move(status); if (snapshot) latestChatSnapshot_ = std::move(snapshot);
                    // Coalesce wakeups too, not just the payload. A temporarily busy
                    // UI must not accumulate one Win32 queue entry per server revision.
                    if (!connectionNotificationQueued_) connectionNotificationQueued_ = PostMessageW(target,connectionMessage,0,0) != FALSE;
                });
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
        MSG message{};
        int result = 0;
        while ((result = GetMessageW(&message,nullptr,0,0)) > 0) {
            TranslateMessage(&message); DispatchMessageW(&message);
        }
        if (result < 0) throw std::runtime_error("GetMessage failed");
        return failure_ ? 1 : static_cast<int>(message.wParam);
    }
private:
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
    void queueDraw() {
        if (state_.visible && !drawQueued_ && window_) {
            drawQueued_ = PostMessageW(window_,drawMessage,0,0) != FALSE;
        }
    }
    void renderNow() {
        if (!state_.visible || !window_) return;
        RECT bounds{};
        if (!GetClientRect(window_,&bounds)) throw std::runtime_error("GetClientRect failed");
        if (bounds.right <= 0 || bounds.bottom <= 0) return;
        renderer_.draw(window_,bounds.right,bounds.bottom,dpi_,state_,showRegistered_ && lockRegistered_);
    }
    void toggleVisible() {
        if (renderer_.chat()) renderer_.chat()->endScrollbar();
        if (GetCapture() == window_) ReleaseCapture();
        state_.toggleVisible();
        ShowWindow(window_,state_.visible ? SW_SHOWNOACTIVATE : SW_HIDE);
        if (state_.visible) queueDraw();
    }
    void toggleLocked() {
        if (renderer_.chat()) renderer_.chat()->endScrollbar();
        if (GetCapture() == window_) ReleaseCapture();
        state_.toggleLocked();
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
        AppendMenuW(popup,MF_STRING,menuOpacity,state_.backgroundOpacity == 0 ? L"배경 불투명도 85%" : L"배경 불투명도 0% 검증");
        AppendMenuW(popup,MF_SEPARATOR,0,nullptr);
        if (renderer_.chat()) {
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
    void fail(const char* reason) noexcept {
        failure_ = true;
        if (verifying() || !options_.fixtureVerificationDirectory.empty() || !options_.chatVerificationDirectory.empty()) {
            const auto& directory = verifying() ? options_.verificationDirectory : !options_.fixtureVerificationDirectory.empty() ? options_.fixtureVerificationDirectory : options_.chatVerificationDirectory;
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
        case WM_NCHITTEST: return hit(lparam);
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
            limits->ptMinTrackSize = {core::toPixels(minWidthDip,dpi_),core::toPixels(minHeightDip,dpi_)};
            return 0;
        }
        case WM_DPICHANGED: {
            dpi_ = HIWORD(wparam);
            const auto* suggested = reinterpret_cast<RECT*>(lparam);
            SetWindowPos(window,nullptr,suggested->left,suggested->top,suggested->right-suggested->left,suggested->bottom-suggested->top,SWP_NOZORDER | SWP_NOACTIVATE);
            queueDraw(); return 0;
        }
        case drawMessage: drawQueued_ = false; renderNow(); return 0;
        case connectionMessage: {
            std::wstring status;
            std::shared_ptr<const core::Json> snapshot;
            { std::lock_guard lock(connectionMutex_); connectionNotificationQueued_ = false; status = latestConnectionStatus_; snapshot = std::move(latestChatSnapshot_); }
            fixtureReady_ = status.find(L"M2 합성 연결 정상") == 0;
            if (snapshot) {
                auto* chat = core::field(snapshot->root(),"chat");
                // M2-only fixtures remain usable as a connection shell.
                if (renderer_.chat() || (yyjson_arr_size(chat) && yyjson_is_str(core::field(yyjson_arr_get(chat,0),"presentationHash")))) renderer_.setChatSnapshot(std::move(snapshot));
            }
            renderer_.setConnectionStatus(std::move(status)); queueDraw(); return 0;
        }
        case WM_HOTKEY:
            if (wparam == showHotkey) toggleVisible();
            else if (wparam == lockHotkey) toggleLocked();
            return 0;
        case WM_MOUSEWHEEL:
            if (renderer_.chat() && !state_.locked) { renderer_.chat()->scroll(-static_cast<float>(GET_WHEEL_DELTA_WPARAM(wparam))/WHEEL_DELTA*64); queueDraw(); }
            return 0;
        case WM_LBUTTONDOWN:
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
                if (GET_Y_LPARAM(lparam) > bounds.bottom-core::toPixels(60,dpi_)) { renderer_.chat()->followLatest(); queueDraw(); }
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
            case menuLatest: if (renderer_.chat()) { renderer_.chat()->followLatest(); queueDraw(); } break;
            default: break;
            }
            return 0;
        case WM_TIMER:
            if (verifying() && wparam == 1) tick();
            else if (!options_.fixtureVerificationDirectory.empty() && wparam == 2) fixtureTick();
            else if (!options_.chatVerificationDirectory.empty() && wparam == 3) chatTick();
            return 0;
        case WM_CLOSE: DestroyWindow(window); return 0;
        case WM_DESTROY: {
            KillTimer(window,1);
            KillTimer(window,2);
            KillTimer(window,3);
            connectionWorker_.request_stop();
            if (connectionWorker_.joinable()) connectionWorker_.join();
            if (showRegistered_) UnregisterHotKey(window,showHotkey);
            if (lockRegistered_) UnregisterHotKey(window,lockHotkey);
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
    unsigned dpi_ = 96, assertions_ = 0, settleTicks_ = 0;
    UINT taskbarCreated_ = 0;
    bool drawQueued_ = false, showRegistered_ = false, lockRegistered_ = false;
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
};

Options parseOptions() {
    int count = 0;
    auto* argv = CommandLineToArgvW(GetCommandLineW(),&count);
    if (!argv) throw std::runtime_error("CommandLineToArgv failed");
    struct Cleanup { LPWSTR* value; ~Cleanup() { LocalFree(value); } } cleanup{argv};
    Options options;
    for (int i = 1; i < count; ++i) {
        const std::wstring arg = argv[i];
        if (arg == L"--verify" && i + 1 < count) options.verificationDirectory = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--fixture-endpoint" && i + 1 < count) options.fixtureEndpoint = argv[++i];
        else if (arg == L"--fixture-verify" && i + 1 < count) options.fixtureVerificationDirectory = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--chat-fixture" && i + 1 < count) options.chatFixture = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--chat-verify" && i + 1 < count) options.chatVerificationDirectory = std::filesystem::absolute(argv[++i]);
        else if (arg == L"--chat-capture" && i + 1 < count) { options.chatCaptureOnly = true; options.chatVerificationDirectory = std::filesystem::absolute(argv[++i]); }
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
    if (!options.chatFixture.empty() && (!options.fixtureEndpoint.empty() || !options.verificationDirectory.empty())) throw std::runtime_error("Chat fixture mode is separate from network/shell verification");
    if (!options.chatVerificationDirectory.empty() && options.chatFixture.empty()) throw std::runtime_error("Chat verification requires a local semantic snapshot");
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
        Shell shell(options); result = shell.run(instance);
    } catch (const std::exception& error) {
        if (!options.verificationDirectory.empty()) {
            try { std::ofstream(options.verificationDirectory / "native-shell-error.txt") << error.what(); } catch (...) {}
        } else MessageBoxA(nullptr,error.what(),"LS Overlay Core - startup error",MB_OK | MB_ICONERROR);
    }
    CoUninitialize();
    return result;
}
