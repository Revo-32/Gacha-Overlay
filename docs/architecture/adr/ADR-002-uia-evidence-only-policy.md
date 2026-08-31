# ADR-002: UIA Evidence-Only Policy

## Status

Accepted

## Context

Discord RPC가 제공하지 않는 판매 반응 상태와 제한적인 opaque message 단서는 Windows 접근성 트리에서 관찰할 수 있다. UIA는 화면 상태와 Discord UI 변경에 영향을 받는다.

## Decision

UIA를 read-only, bounded fallback sensor로만 사용한다. raw `AutomationElement`, `AutomationId`, `ControlType` 탐색은 Windows App infrastructure 경계에서 끝내고 Core에는 해석된 evidence DTO만 전달한다.

## Consequences

UIA 실패는 Sales health를 Paused/Degraded로 만들 수 있지만 trusted queue나 Main Chat을 삭제하지 않는다.

## Invariants

- UIA로 Discord를 조작하지 않는다.
- NotObserved는 NotSold가 아니다.
- partial/incomplete scan은 부정 증거를 만들지 않는다.
- UIA 장애는 Main Chat subsystem으로 전파하지 않는다.
