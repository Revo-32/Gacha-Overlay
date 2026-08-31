# Gacha Overlay에 기여하기

Issue와 Pull Request를 환영합니다. 변경을 제안하기 전에 기존 Issue를 확인하고, 한 Pull Request에는 가능한 한 하나의 목적만 담아 주세요.

## 개발 환경

- Windows x64
- .NET 8 SDK
- WPF를 빌드할 수 있는 Windows 환경

검증 명령:

```powershell
dotnet restore GachaOverlay.sln
dotnet build GachaOverlay.sln
dotnet build GachaOverlay.sln -c Release
dotnet test GachaOverlay.sln
dotnet test GachaOverlay.sln -c Release
dotnet format GachaOverlay.sln --verify-no-changes
```

## Pull Request 기준

- Production 동작을 변경하면 관련 자동 테스트를 추가하거나 수정합니다.
- 기존 Debug/Release 테스트와 formatting 검사를 통과해야 합니다.
- 관련 없는 파일이나 생성된 `bin`, `obj`, `artifacts`, `tmp` 파일을 포함하지 않습니다.
- 사용자 문구를 추가하면 English, 한국어, 日本語 리소스를 함께 검토합니다.
- 실제 Discord Client Secret, OAuth token, User/Bot token, 진단 ZIP, 로그 또는 개인 메시지를 절대 commit하지 않습니다.

보안 취약점이나 credential 노출은 공개 Issue 대신 [SECURITY.md](SECURITY.md)의 절차를 이용해 주세요.
