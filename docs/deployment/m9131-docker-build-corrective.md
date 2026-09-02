# M9.13.1 — Docker 소스 누락 수정

## 확인된 원인

첫 Railway Docker build 실패는 Dockerfile의 프로젝트 COPY 범위가 아니라 **Git에 없는 소스** 때문이었습니다.
로컬 HEAD `37c7d05`에는 `DiscordMessagePipeline.cs`가 있지만 Core의 `Diagnostics` 소스 3개는 없습니다.
`IRuntimeMetrics`는 별도 stub이나 생성 파일이 아니라 기존 `Diagnostics/RuntimeMetrics.cs`에 정의되어 있습니다.

기존 `.gitignore`의 `diagnostics/`는 저장소 아래 어느 깊이의 같은 이름 디렉터리에도 적용됩니다.
이 Windows 작업 트리의 `core.ignorecase=true` 설정으로 대문자 `Diagnostics`도 매칭되어 커밋에서 제외됐습니다.
로컬 빌드는 디스크에 남아 있던 파일을 사용했지만, GitHub checkout 기반 Railway build는 그 파일을 받을 수 없었습니다.
[Git ignore 규칙](https://git-scm.com/docs/gitignore)

실제 확인:

- `git check-ignore -v`: `.gitignore`의 `diagnostics/` 규칙에 의해 제외됨.
- `git ls-tree`: 커밋에 Core Diagnostics가 없음.
- 해당 커밋의 Backend/Core/Protocol 및 루트 메타데이터만 별도 폴더에 추출해 publish:
  Railway와 같은 CS0234 1개, CS0246 2개 재현.

M9.13의 로컬 publish/정적 파일 검사는 Git 배포 경계까지 확인하지 않아 이 누락을 놓쳤습니다.
이번 검증은 원본 폴더에 우연히 남아 있는 Git-ignored 소스를 사용하지 않습니다.

## 수정 내용

1. `.gitignore`를 `/diagnostics/`로 제한해 저장소 루트의 런타임 출력만 제외합니다.
   기존 코드/테스트 파일 내용, metrics interface 및 수집 동작은 변경하지 않았습니다.
2. Dockerfile은 이미 올바른 프로젝트 단위 복사 모델이므로 실행 명령은 유지하고 검증 안내 주석만 추가했습니다.
3. `.dockerignore`의 광범위한 `!src/` 예외를 제거했습니다. Docker의 디렉터리 예외는 하위 파일에도 영향을 미치므로
   필요한 프로젝트 밖 WPF/Infrastructure 소스까지 컨텍스트에 들어갈 가능성이 있었습니다.
   Backend/Core/Protocol 각각의 전체 트리 허용은 유지합니다.
   [Docker context 문서](https://docs.docker.com/build/concepts/context/),
   [Moby의 부모 디렉터리 매칭 구현](https://github.com/moby/patternmatcher/blob/main/patternmatcher.go)
4. 프로젝트 참조를 MSBuild로 재귀 평가하는 격리 검증 도구와 회귀 테스트 8개를 추가했습니다.

프로젝트 참조 전체 범위는 Backend → Core, Protocol이며 두 참조 프로젝트에 추가 ProjectReference는 없습니다.
소스 하위 폴더나 개별 `.cs` 파일을 Dockerfile에 나열하지 않습니다.
Windows/WPF 프로젝트가 이 참조 범위에 추가되면 검증을 중단합니다.

## 반드시 커밋에 포함할 기존 파일

다음 10개는 새로 작성한 기능이 아니라 **이전에 로컬에만 남아 있던 기존 구현/테스트**입니다.
ignore 규칙 변경만 커밋하고 이 파일들을 빠뜨리면 재배포가 다시 실패할 수 있습니다.

| 경로 | 기존 파일 |
| --- | --- |
| `src/GachaOverlay.Core/Diagnostics/` | `DiagnosticSnapshots.cs`, `ProcessMetricsSampler.cs`, `RuntimeMetrics.cs` |
| `src/GachaOverlay.Infrastructure/Diagnostics/` | `CrashMetadataWriter.cs`, `DiagnosticBundleExporter.cs` |
| `tests/GachaOverlay.Tests/Diagnostics/` | `M82CacheMetricsTests.cs`, `M82CrashMetadataTests.cs`, `M82DiagnosticBundleTests.cs`, `M82RuntimeMetricsTests.cs`, `M82SyntheticReplaySoakTests.cs` |

Core 3개는 Backend container build에 필요합니다. Infrastructure와 기존 테스트 파일은 정상적인 WPF/테스트 checkout을 위해 복구하되
Docker 컨텍스트에는 포함하지 않습니다. 실제 운영 Token/Guild/Host ID를 이 파일에 추가하지 않았습니다.

## 격리 검증 도구

필요 도구: .NET 8 SDK, Git, PowerShell 7(`pwsh`). 새 회귀 테스트도 이 개발 도구를 사용합니다.
저장소 루트에서 실행:

```powershell
pwsh -NoProfile -File tools/dev/verify-backend-docker-context.ps1
```

동작:

1. MSBuild가 평가한 ProjectReference를 재귀 탐색합니다. Compile/Content/EmbeddedResource/AdditionalFiles를 함께 검사합니다.
2. 모든 참조 프로젝트에 대해 csproj restore용 COPY와 **프로젝트 전체 트리 COPY**가 Dockerfile에 있는지 확인합니다.
3. Git 추적 파일과 ignore되지 않은 새 파일만 후보로 사용합니다. 필요한 소스가 Git 후보에서 빠지면 실패합니다.
4. `.dockerignore`를 대소문자 구분 및 부모 디렉터리 매칭과 함께 적용합니다.
   현재 레시피의 glob 문법을 지원하며, 지원하지 않는 구문은 검증 오류로 처리합니다. Docker 엔진 자체는 아닙니다.
5. WPF 등 프로젝트 참조 범위 밖의 파일이나 런타임/비밀 부산물이 컨텍스트에 허용되면 실패합니다.
6. 고유한 임시 디렉터리에 허용된 전체 프로젝트 파일 집합과 빌드 메타데이터를 복사합니다. bin/obj 및 Git 이력은 복사하지 않습니다.
7. 그 디렉터리에서 프로젝트를 다시 평가하여 원본 저장소로 향하는 절대 소스 참조/외부 경로를 차단합니다.
8. 격리 디렉터리에서 Backend restore 후 Release/framework-dependent publish를 실행합니다.
9. 결과와 임시 경로를 출력합니다. 검사 목적으로 디렉터리를 남기며 사용자 폴더를 삭제하지 않습니다.

빠른 소스 검사만 실행:

```powershell
pwsh -NoProfile -File tools/dev/verify-backend-docker-context.ps1 -CheckOnly
```

커밋에 넣을 파일을 사용자가 Git에 추가한 뒤, index에 없는 필수 소스를 엄격히 거부하려면:

```powershell
pwsh -NoProfile -File tools/dev/verify-backend-docker-context.ps1 -RequireTracked
```

기본 검증은 아직 커밋하지 않은 정상 신규 파일도 포함합니다. 따라서 기본 검증 PASS가 해당 파일의 커밋/push 완료를 의미하지는 않습니다.
이 도구는 Git add/commit/push나 Railway 배포를 수행하지 않습니다.

## 회귀 테스트 범위

- 실제 Backend의 모든 평가된 소스가 Git 및 Docker 후보에 포함되는지 검사
- 간접 참조 프로젝트와 새 하위 폴더 자동 탐색
- Git ignore로 소스가 누락된 경우 실패
- Docker ignore로 소스가 누락된 경우 실패
- 참조 프로젝트 전체 COPY가 빠진 경우 실패
- `!src/` 같은 예외로 불필요한 프로젝트가 포함되는 경우 실패
- Windows/WPF 대상 프로젝트 유입 시 실패
- 전체 src/tests 소스가 Git ignore로 숨겨지지 않으면서 루트 runtime diagnostics는 여전히 제외되는지 검사

테스트는 `IRuntimeMetrics.cs` 같은 특정 증상 파일명에 의존하지 않습니다.
기존 diagnostics 테스트 5개 파일은 이미 이전 로컬 1,284개 기준에 포함돼 있었으므로 추가 테스트로 중복 계산하지 않습니다.

## 운영 범위

M9.13의 PORT/ASPNETCORE_URLS, Volume, replica, healthcheck, 종료 유예, 프록시/전송 보안 설정은 변경하지 않았습니다.
Backend/Core/Protocol의 기능 코드도 변경하지 않았으며 WPF/테스트를 이미지에 추가하지 않았습니다.
이번 작업에서 commit, push, PR, Release, Railway 재배포를 수행하지 않았습니다.
Cafe24/DNS, Web OAuth2, M10 UI/UX는 시작하지 않았습니다.

검증 결과는 `m9131-validation.json`에 기록합니다.
Docker CLI가 없는 환경의 격리 publish 성공은 실제 Docker/Linux 이미지 build PASS가 아닙니다.

**다음 단계:** 위 기존 소스 10개와 corrective 파일을 검토하여 사용자가 main에 커밋/push합니다.
이후 Railway auto-deploy의 실제 Docker build 및 `/healthz` HTTP 200을 확인합니다.
