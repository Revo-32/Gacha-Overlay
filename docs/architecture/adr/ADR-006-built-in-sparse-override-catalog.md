# ADR-006: Built-in + Sparse Override Catalog

## Status

Accepted

## Context

기본 상품 매핑은 앱과 함께 배포되어야 하며 사용자의 개발자 수정은 앱 업데이트 뒤에도 유지되어야 한다.

## Decision

embedded built-in catalog와 `%LOCALAPPDATA%`의 sparse developer override를 병합한다. ProductId가 business identity이며 `GuildId + EmojiId`가 mapping identity다.

## Consequences

표시명과 emoji 이름을 바꿔도 상품 grouping은 유지된다. built-in에서 제거된 key의 override는 자동 삭제하지 않고 custom/orphan candidate로 보존한다.

## Invariants

- catalog document schema는 현재 version 2다.
- override 저장은 temp + disk flush + atomic replace + one backup을 사용한다.
- legacy catalog migration은 원본을 보존한다.
- sparse override 외의 전체 built-in 복사본을 사용자 파일에 저장하지 않는다.
