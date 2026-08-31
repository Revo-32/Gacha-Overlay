# ADR-004: Sales Trust Model

## Status

Accepted

## Context

판매 반응 관찰에는 Sold, NotSold, NotObserved와 Full/Partial coverage가 함께 존재한다. 관찰 실패를 판매 완료/미완료로 오해하면 queue가 손상된다.

## Decision

Sales domain state와 observation trust를 분리한다. trusted Sold/NotSold evidence만 state를 전이하며 NotObserved, unavailable, partial omission은 기존 trusted state를 삭제하지 않는다.

## Consequences

관찰 장애 중 queue가 보존되고 health가 불확실성을 사용자에게 표시한다. 완전하고 신뢰된 관찰에서 모든 Production completion marker의 부재가 확인된 경우에만 Sold가 Pending으로 돌아갈 수 있다.

## Invariants

- Sold + NotObserved는 Sold를 유지한다.
- Partial scan은 NotSold positive evidence를 만들지 않는다.
- NeverObserved는 새 current/next-self alert를 발생시키지 않는다.
- RPC Sales CREATE는 UIA 검증 전에도 queue에 참여한다.
