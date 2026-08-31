# ADR-005: Async Generation/Revision Commit Policy

## Status

Accepted

## Context

media, forward lookup, opaque hydration은 메시지 UPDATE/DELETE, retention eviction, RPC reconnect보다 늦게 끝날 수 있다. CancellationToken만으로 commit correctness를 보장할 수 없다.

## Decision

비동기 결과 commit 시 RPC generation, MessageId, message/registration revision, source identity와 현재 존재 여부를 검증한다. CancellationToken은 중단 요청이며 commit authority가 아니다.

## Consequences

늦은 결과는 캐시 적재에 사용될 수 있어도 현재 UI/store를 덮어쓰거나 메시지를 부활시킬 수 없다.

## Invariants

- 이전 revision 결과는 최신 revision을 덮어쓰지 않는다.
- DELETE와 retention eviction 뒤에는 재삽입하지 않는다.
- 이전 RPC generation 결과는 폐기한다.
- 동일 source lookup은 single-flight할 수 있으나 wrapper별 commit guard는 독립적이다.
