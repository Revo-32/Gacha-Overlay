# ADR-001: Discord Local RPC Primary Source

## Status

Accepted

## Context

채팅과 판매 모집 메시지는 Discord 데스크톱 클라이언트의 local RPC로 조회할 수 있다. 별도 Gateway/Bot/User Token 구조는 권한·배포·보안 범위를 크게 확장한다.

## Decision

Discord Local RPC를 메시지의 primary source로 유지한다. Discord MessageId를 메시지 identity로 사용하며 CREATE/UPDATE/DELETE를 동일 identity에 적용한다.

## Consequences

Discord 데스크톱 실행 및 RPC 가용성에 의존한다. 연결 재수립 시 generation별 bootstrap과 live replay가 필요하다.

## Invariants

- User Token, self-bot, Gateway를 사용하지 않는다.
- UPDATE는 기존 MessageId를 patch하고 생성 순서를 바꾸지 않는다.
- DELETE는 동일 MessageId만 제거한다.
