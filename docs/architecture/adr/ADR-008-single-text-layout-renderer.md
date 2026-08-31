# ADR-008: Single Text Layout Renderer

## Status

Accepted

## Context

fill과 outline을 별도 텍스트 레이아웃으로 그리면 mixed language, 긴 nickname, wrapping, inline emoji에서 glyph 위치가 달라질 수 있다.

## Decision

TextFormatter가 만든 하나의 logical TextLine/GlyphRun geometry를 solid black stroke와 fill이 공유한다. nickname, body, mention, self mention, emoji, wrapping, ellipsis, DPI는 같은 renderer를 사용한다.

## Consequences

paint 속성 변경은 layout cache를 재구성하지 않는다. inline object는 동일 line metrics와 baseline 계약을 지켜야 한다.

## Invariants

- shadow renderer를 다시 추가하지 않는다.
- outline과 fill에 별도 text layout을 만들지 않는다.
- glyph run마다 character hit 기반 baseline origin을 적용한다.
- M7.5.8 visual regression tests를 유지한다.
