# ADR-003: No Server-Wide Subscription

## Status

Accepted

## Context

전달 메시지 원본을 찾기 위해 서버 전체 채널을 상시 구독하면 데이터 범위, 자원 사용량, 개인정보 노출 범위가 불필요하게 증가한다.

## Decision

메인/판매 대상 채널만 구독한다. 전달 원본은 snapshot first, 동일 source single-flight on-demand lookup second, bounded UIA fallback 순서로 해석한다.

## Consequences

일부 전달 원본은 fallback으로 표시될 수 있다. 대신 subscription 범위와 lookup 비용이 제한된다.

## Invariants

- server-wide subscription을 추가하지 않는다.
- source lookup 결과 자체를 MainChatStore에 삽입하지 않는다.
- cache, retry, negative result는 bounded여야 한다.
