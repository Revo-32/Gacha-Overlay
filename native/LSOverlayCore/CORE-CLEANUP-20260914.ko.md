# Core 이미지 지연 / 기본판 운영 경로 정리 / 로그인 유지

2026-09-14. 현재 작업은 Core 1.0.0 이후의 수정이다. 기존 공개 릴리즈 ZIP/태그를 덮어쓰지 않았다. 수정 클라이언트는 별도 `cleanup-candidate`이며 소스는 미커밋 상태다.

## 조사 결과

- 조사 시 PC에는 정식 Core EXE만 실행 중이었으며 기본판 `GachaOverlay.App` 프로세스는 없었다. 두 클라이언트 충돌이 원인이라는 증거는 없다.
- 운영 Backend CPU 관측값 0.12%, 운영 미디어 worker 0.01%. 남아 있던 개발 bridge/worker/fixture도 CPU는 낮았으므로 이들을 이미지 지연의 원인으로 단정하지 않는다.
- 운영 미디어 worker의 조사 시점 누적 처리 36회, 캐시 적중 28회. 준비 시간 최대 868.93ms, warm validation 최대 0.64ms. 이 표본은 대부분 작은 이모지이며 큰 GIF 전체의 성능 보장은 아니다.
- Core는 매 미디어 GET의 변환 전/후 권한 검사를 모두 **Discord 강제 refresh**로 처리했다. 캐시된 이미지에도 반복 REST 조회가 선행되는 불필요한 지연 경로다.

## 수정

- 미디어 권한 검사 두 번은 유지하되 기존 ChatAuthorizationService의 2분 lease와 역할/채널/멤버 변경 무효화를 재사용한다. 별도 무기한 권한 캐시는 만들지 않았다. 만료된 lease는 새로 조회하고, 현재 메시지 존재/사용자 범위/자격증명과 변환 후 철회 검사도 유지한다.
- 동일 사용자의 연속 권한 검사 20회가 원본 조회 1회만 수행하며, 명시적 권한 무효화 후에는 즉시 재조회해 거부되는 회귀 테스트를 추가했다.
- 250ms 이상 미디어 요청의 전체/권한/worker 처리 시간과 상태 코드만 기록한다. URL·본문·사용자 ID·토큰은 기록하지 않는다.
- `LS_CORE_ONLY=1` 운영 모드에서 기본판 stream/bootstrap/chat HTTP 경로는 410으로 종료한다. Core 경로, 공용 OAuth/HTTPS 및 판매 완료/취소 API는 유지한다.
- 이 모드에서는 GTA 주간 이벤트 수집/파싱/리셋, Gemini 번역 hosted service, 구형 slash pairing 정리 worker를 등록하지 않는다. Gemini 키/모델 환경값도 새 운영 컨테이너에는 전달하지 않는다.
- 개발용 `lsoverlay-core-readonly-bridge-1`, `lsoverlay-core-media-1`, `lsoverlay-core-dev-fixture-1`을 중지하고 restart=no로 바꾸었다. 컨테이너/파일/캐시/운영 데이터는 삭제하지 않았으며 이전 restart 설정은 서버의 retirement 기록에 보존했다.

## 로그인 유지

기존에도 Windows 사용자 전용 DPAPI 로그인 저장/재사용이 있었으나 180일 고정 만료였다. 새 클라이언트는 만료가 30일 이내로 다가오면 Core 인증 갱신 API로 180일을 연장하고 갱신된 만료를 DPAPI 파일에 저장한다. 정상 사용 중에는 브라우저 승인을 반복하지 않는다. 토큰은 교체하지 않아 진행 중인 연결/미디어에 영향을 주지 않는다.

새 API는 기존 유효 Bearer 인증과 서버 멤버십을 확인하고 registry lock 안에서 다시 검증한다. 만료·폐기·교체된 토큰은 갱신하지 않는다. 180일 이상 사용하지 않아 실제로 만료된 경우나 접근 권한 철회는 다시 승인/권한 확인이 필요하다. 손상된 DPAPI 파일의 검증을 우회하지 않는다. 이 자동 갱신 호출은 새 수정 클라이언트에 포함되며, 이미 다운로드한 1.0.0 EXE가 자동으로 바뀌는 것은 아니다.

## 배포 및 검증

- 운영 이미지: `lsoverlay/backend:core-cleanup-20260914`
- 이미지 ID: `sha256:b6dfb1f5669e5e56f9c9eb100f82930f7ece3e1e1eed21139e0401df76cb2fbf`
- 정확한 이미지의 Backend DLL SHA256: `751d359dc59df3d4c189bf011c7304ace8ba8e09ecf63b0756f49b55ebdcb1a6`
- 운영 배포 경로: `/srv/apps/lsoverlay-core-release/cleanup-20260914`. 기존 base compose와 이 경로의 `compose.core.yaml`을 함께 사용한다.
- 이전 Core 이미지와 `/srv/apps/lsoverlay-core-release/rc1/compose.core.yaml`로 복구 가능. 데이터는 현재 것을 유지하며 과거 백업으로 자동 덮어쓰지 않는다.
- 배포 전 데이터 백업: `/srv/apps/lsoverlay-core-release/backups/before-core-20260914-044338`.
- 전체 .NET 테스트 2,587 PASS. 최종 trailing/prefix 경로 차단까지 포함한 실제 M8 이미지 Linux 통합 테스트 15 PASS. 네이티브 7/7 PASS.
- 운영 health/Core manifest 200, 폐기한 Full 경로(후행 slash/하위 경로 포함) 410. Status 서비스 변경 없음. 주간 이벤트/번역 서비스 로그 없음.
- 전환 직후 추가 미디어 9회가 worker 캐시 적중으로 처리됐고 250ms 이상 gateway 로그는 없었다. PC 화면 표시까지의 사용자 체감 시간은 별도로 확인해야 한다.
- 수정 EXE SHA256: `ca4a9bf928287fae12ce73946a4909920ae54469610e1f6888c68202a8653438`.
- 검증용 ZIP: `LS-Overlay-Core-1.0.0-win-x64-cleanup-candidate.zip`, SHA256 `cba535475dd0ba9a39dbe648ac7bb1ad1d97f5d359fc083500c715f4769854b9`.

Core에서 사용하지 않는 원본 소스와 이전 배포물은 복구용으로 남겨 두었다. 운영에서 관련 서비스/경로를 비활성화한 것이며 저장소·데이터를 무차별 삭제한 작업이 아니다.

## 사용자 확인 및 추가 관측

사용자는 수정본 실행 후 “잘 연결된다 잘 작동하고, 이미지도 빨라졌어”라고 확인했다. 이후 운영 표본은 미디어 처리 118회, 적중 90회이며 실패 카운터는 검증용 잘못된 입력 3회에서 증가하지 않았다. 느린 요청의 권한 검사 비용은 0.2~0.4ms였다. 최초 변환의 worker 준비 최대값은 6.69초로 증가했고 한 요청의 전체 시간은 7.57초였다. 따라서 반복 권한 조회 병목과 사용자 체감 개선은 확인됐지만, 모든 큰 GIF 최초 로딩이 즉시 완료된다고 주장하지 않는다. 이번에는 이미지 품질·프레임을 줄이는 변경을 하지 않았다.
