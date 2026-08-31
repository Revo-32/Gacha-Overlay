# ADR-007: Surface vs Content Opacity

## Status

Accepted

## Context

HUD 배경 투명도와 텍스트/아이콘 투명도를 동일 계층에서 적용하면 글자 가독성과 hit-test 표면이 함께 약해진다.

## Decision

surface opacity와 content opacity를 분리한다. 사용자 surface 설정은 배경 계층에만 적용하고 텍스트, outline, icon, interaction chrome의 의미적 불투명도는 독립적으로 유지한다.

## Consequences

낮은 배경 투명도에서도 채팅과 상태 표시를 읽을 수 있다.

## Invariants

- HUD/Chat/Sales/Queue Detail surface 설정은 content brush를 곱하지 않는다.
- theme semantic color와 surface opacity는 서로 다른 책임이다.
- M7.5 Visual Freeze 없이 opacity 계층을 재결합하지 않는다.
