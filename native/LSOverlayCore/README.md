# LS Overlay Core — M1 네이티브 검증 창

후속 구현: [M2 인증·통신 프로토타입](M2.md), [M3 네이티브 채팅 텍스트 프로토타입](M3.md). 아래 설명은 M1 shell 범위입니다. 현재 소스는 `build-native.ps1 -Stage m3`로 빌드해 보존된 M1/M2 기준 실행 파일을 덮어쓰지 않습니다. `-Stage`는 출력 경로를 선택하며 과거 소스 버전으로 전환하지 않습니다. 실제 Discord/Chat/미디어 parity 완료를 의미하지 않습니다.

이 경로는 기존 C# `GachaOverlay.Core`와 별개인 C++20 클라이언트입니다. 제품명은 **LS Overlay Core**, 실행 파일은 **LSOverlayCore.exe**입니다.

현재는 창·렌더링·입력·DPI·자원 사용을 확인하는 M1 단계입니다. **Discord 로그인, 실제 채팅, 판매, 세션, 미디어는 아직 구현하지 않았습니다.** 화면의 한국어는 고정 검증 문구이며 실제 서버 데이터가 아닙니다. Full을 대체하는 릴리즈가 아닙니다.

## 빌드 및 자동 검증

필수 도구는 Visual Studio 2022 C++ Build Tools, MSVC x64, Windows SDK, CMake입니다. 실행 시 .NET 또는 별도 VC 런타임 설치는 필요하지 않습니다. CRT는 정적으로 링크하고, 렌더링과 창 처리는 Windows 제공 DLL을 사용합니다.

저장소 루트에서:

```powershell
& .\tools\core\build-native.ps1 -Stage m1
& .\tools\core\verify-native.ps1 -Stage m1 -PhaseSeconds 15
```

실행 파일:

```text
artifacts/core/m1/native/Release/LSOverlayCore.exe
```

자동 검증 결과는 `artifacts/core/m1/runs/<timestamp>/`의 JSON/PNG입니다. 빌드·검증 도구는 기존 Full 빌드, 설정, 인증 파일을 변경하지 않습니다. 사용자 화면을 캡처하지 않고 **Core 자신이 그린 픽셀만** PNG로 저장합니다. 계측 파일과 실행 파일은 Git에서 제외됩니다.

`ctest`는 창을 열지 않는 상태·DPI·할당 경계 테스트만 실행합니다. GUI 자동 검증은 `verify-native.ps1`로 명시적으로 실행하며, 검증용 창은 끝나면 자동 종료됩니다. 타임아웃이면 도구가 시작한 해당 프로세스만 종료합니다.

## 조작

- F9: 표시/숨기기.
- F10: 잠금/잠금 해제. 잠기면 실제 layered-window click-through 및 no-activate 스타일을 적용합니다.
- 잠금 해제: 상단을 끌어 이동하고 가장자리/모서리를 잡아 크기를 조절합니다.
- 우클릭 또는 트레이 우클릭: 표시, 잠금, 배경 0%/85%, 종료.
- 배경 0%: 본문 배경만 투명합니다. 텍스트와 잠금 해제 시 편집 테두리/드래그 영역은 남습니다.
- 기존 Full이나 다른 앱이 F9/F10을 사용 중이면 등록을 빼앗지 않습니다. 기존 앱은 계속 작동하고 Core는 트레이 메뉴로 조작할 수 있습니다. 실제 키 검증 시에만 사용자가 기존 Full을 잠시 종료합니다.
- `--no-hotkeys`는 검증 시 전역 단축키 등록을 생략하는 옵션입니다.

설정과 인증을 저장하지 않으며 영구 백그라운드 서비스도 만들지 않습니다. 창 위치 영구 저장, 단축키 편집, Full 설정 가져오기는 이 단계의 구현 범위가 아닙니다.

## 렌더링 결정 및 측정 경계

Direct2D software DC render target → premultiplied BGRA surface → `UpdateLayeredWindow`로 투명 창을 구성합니다. DirectWrite가 한국어 레이아웃과 최종 DIP/픽셀 배치를 담당합니다. 투명 배경에서는 ClearType 색 번짐을 피하기 위해 grayscale text antialiasing을 사용합니다.

선택 이유는 M1에서 per-pixel alpha, 실제 click-through, 0% 배경의 resize hit testing과 작은 네이티브 기본 비용을 먼저 확인하기 위해서입니다. **대형 GIF에 대한 최종 GPU/합성 경로를 선정했다는 뜻은 아닙니다.** 이 경로는 변경 시 전체 창 surface를 업로드하므로 M4에서 실제 미디어 부하와 DirectComposition 등 대안을 비교해야 합니다.

변경·크기·DPI·표시 상태에 따라 다시 그리며 정적 화면에 상시 60 FPS 루프를 두지 않습니다. 타이머는 `--verify` 계측 모드에서만 사용합니다. 스레드 수 전체 조회는 비용이 크므로 CPU 측정 구간 밖에서 수행합니다.

자동 검증은 F9/F10의 동일 메시지 처리 경로, 실제 HWND 표시·스타일, alpha 픽셀, 100회 resize, render target 재생성, 정적/잠금/숨김 상태의 idle rendering 0회를 확인합니다. 등록 실패는 별도 지표로 남깁니다. **실제 키 입력과 다른 프로세스로의 물리적 마우스 전달을 대신 검증했다고 주장하지 않습니다.**

96/144/192 DPI 렌더 결과를 확인하되, 144/192 PNG는 합성 렌더-DPI 검증입니다. 실제 다른 배율 모니터 간 이동 및 사용자의 체감 조작 검증은 별도로 필요합니다. 자동 resize 지연은 동기 resize+render 호출 시간으로, 실제 드래그/스크롤 입력 지연이나 GPU present 완료 시간을 의미하지 않습니다.

Private Bytes/Working Set/CPU 결과는 **네트워크 미연결 shell** 수치입니다. 연결된 Core 전체 KPI, Full과의 실측 비교, GIF 품질/성능 달성으로 해석하지 않습니다.

관련 Windows 동작: [Layered Windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows), [Direct2D/GDI interop](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-gdi-interoperation-overview).
