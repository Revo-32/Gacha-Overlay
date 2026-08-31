# Security Policy

## 지원 범위

현재 보안 수정은 최신 Release Candidate를 우선 대상으로 합니다. 오래된 테스트 빌드에는 별도 수정이 제공되지 않을 수 있습니다.

## 비공개 제보

보안 취약점 또는 credential 노출을 발견했다면 공개 Issue나 Discussion에 실제 값을 게시하지 마세요. GitHub 저장소의 **Security → Advisories → Report a vulnerability** 경로로 비공개 제보를 작성해 주세요.

제보에는 다음 정보만 포함해 주세요.

- 영향을 받는 버전과 기능
- 재현 절차
- 예상 영향
- 필요한 경우 값 자체를 제거한 로그 또는 스크린샷

Discord Client Secret, OAuth access/refresh token, User/Bot token, GitHub token, 인증 코드, 개인 메시지는 평문으로 첨부하지 마세요. 실수로 노출했다면 공개 글을 추가로 작성하지 말고 해당 제공자의 절차에 따라 credential을 폐기하거나 교체해야 합니다.

## 프로젝트 보안 원칙

- 사용자는 자신의 Discord Application credential을 직접 준비합니다.
- Client Secret과 OAuth token은 Windows 현재 사용자 범위 DPAPI 저장소에 보관합니다.
- User Token, self-bot 및 브라우저 token 추출은 지원하지 않습니다.
- 진단 ZIP은 자동 업로드되지 않으며 공유 전 사용자가 직접 내용을 확인해야 합니다.
