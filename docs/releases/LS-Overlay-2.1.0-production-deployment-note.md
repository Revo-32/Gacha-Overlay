# LS Overlay 2.1.0 - Production release note

이 문서는 사용자 승인 뒤 실행할 절차를 기록한 준비 메모입니다. Stable Preparation 단계에서는 아래 작업을 실행하지 않습니다.

## 승인 후 순서

1. 검토가 끝난 Stable Preparation 변경을 `develop/2.1`에 커밋하고 필요하면 원격에 푸시합니다.
2. 저장소 릴리즈 정책에 따라 승인된 정확한 커밋을 `main`으로 병합하거나 fast-forward합니다.
3. Production Railway가 승인된 `main` 소스에서 LS Overlay 2.1 Backend를 배포하도록 확인합니다.
4. `/healthz`가 정상인지 확인합니다.
5. 기존 2.0 Remote 클라이언트가 의도된 호환 범위에서 계속 연결되는지 확인합니다.
6. 2.1 클라이언트의 Chat, Sales, Presence와 GTA 컴패니언 bootstrap/snapshot을 확인합니다.
7. GTA 컴패니언의 현재 주간 데이터와 Last-Good 복원이 정상인지 확인합니다.
8. 승인된 소스 커밋에 `v2.1.0` 태그를 만들고, 검토한 불변 ZIP·PDF·체크섬만 GitHub Stable Release에 게시합니다.

## 보존 조건

- Production Volume의 기존 데이터와 `gta-companion-events.json`을 삭제하거나 초기화하지 않습니다.
- Production 환경 변수, OAuth Secret, Discord 설정과 DNS를 Stable Preparation 값으로 바꾸지 않습니다.
- staging을 Production 도메인에 연결하지 않습니다.
- 게시 뒤 `v2.1.0` 태그와 공개 산출물은 교체하지 않습니다. 후속 수정은 새 버전으로 배포합니다.
