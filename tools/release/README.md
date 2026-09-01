# Release Engineering — 1.0.0-rc.1

## 안전한 기본 동작

`build-release.ps1`은 인수 없이 실행하면 버전/manifest만 읽는 prepare-only 모드이며 빌드, 테스트, 게시, 패키징 또는 프로세스 조회를 수행하지 않습니다.

Final mode는 빠른 시작과 전체 사용자 설명서, 장시간 Soak 결과, 라이선스 최종 확인과 사용자 승인이 모두 준비된 뒤에만 사용합니다. 실행 중인 Gacha Overlay가 감지되면 실패하며 프로세스를 종료하지 않습니다.

## Final Gate 명령

다음 명령은 M8.3.1에서 실행하지 않았습니다.

```powershell
dotnet restore GachaOverlay.sln
dotnet build GachaOverlay.sln
dotnet build GachaOverlay.sln -c Release
dotnet test GachaOverlay.sln
dotnet test GachaOverlay.sln -c Release
dotnet format GachaOverlay.sln --verify-no-changes
dotnet restore src\GachaOverlay.App\GachaOverlay.App.csproj -p:PublishProfile=src\GachaOverlay.App\Properties\PublishProfiles\win-x64-singlefile.pubxml
dotnet publish src\GachaOverlay.App\GachaOverlay.App.csproj -c Release --no-restore -p:PublishProfile=src\GachaOverlay.App\Properties\PublishProfiles\win-x64-singlefile.pubxml
.\tools\release\build-release.ps1 -Finalize
```

`-Finalize`는 위 검증과 publish를 자체 수행하므로 실제 최종화에서는 개별 명령을 진단용으로 반복할 필요가 없습니다. 스크립트는 패키지 루트가 `Gacha Overlay.exe`, `Gacha Overlay 빠른 시작.pdf`, `Gacha Overlay 사용자 설명서.pdf`, `Licenses/`의 정확히 네 항목인지 확인하고, 버전, 금지 파일, 라이선스, 비밀정보 패턴, 해시, ZIP 및 fresh extraction 구조도 검증합니다.

독립된 읽기 전용 패키지 확인:

```powershell
.\tools\release\verify-release.ps1 -PackageRoot <extracted-package-root> -ExpectedVersion 1.0.0-rc.1
.\tools\release\verify-release.ps1 -ZipPath <zip-path> -ExpectedVersion 1.0.0-rc.1
```

## GitHub Draft Release — M8.3.3 publication gate

Current tree, Git index, 전체 reachable history, manual/screenshot 및 final ZIP secret audit가 모두 PASS한 뒤에만 실행합니다.

```powershell
gh release create v1.0.0-rc.1 <final-zip-path> --title "Gacha Overlay 1.0.0-rc.1" --notes-file docs\release\github-release-v1.0.0-rc.1.md --prerelease --draft
```

이 명령은 Draft와 Prerelease 상태로만 생성합니다. 사용자가 Draft 페이지를 직접 검토하고 별도로 공개 승인을 내리기 전에는 `gh release edit --draft=false` 또는 동등한 공개 Publish 작업을 수행하지 않습니다.
