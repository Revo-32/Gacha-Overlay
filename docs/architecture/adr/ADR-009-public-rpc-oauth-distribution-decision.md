# ADR-009: Public RPC/OAuth Distribution Decision

## Status

Deferred for broader public distribution

## Context

최초 v1.0.0 대상은 controlled small-group test release이며 unrestricted public distribution이 아니다. Discord public RPC eligibility, approval, OAuth 운영 구조는 별도 검토가 필요하다.

## Decision

`1.0.0-rc.1`에서는 현재 Local RPC/OAuth 구조를 controlled release에 사용한다. public eligibility와 unrestricted OAuth architecture는 broader distribution 전 release blocker로 남기되 rc.1 blocker로 취급하지 않는다.

## Consequences

현재 구조를 public-release-ready라고 문서화하거나 홍보하지 않는다. broader public release 전에 Discord 정책·승인·credential 운영을 재감사한다.

## Invariants

- shared Client Secret을 binary/settings/log에 embed하지 않는다.
- Client Secret과 OAuth token은 현재 사용자 범위 DPAPI 저장을 유지한다.
- User Token과 self-bot을 사용하지 않는다.
- public OAuth 적합성은 명시적으로 deferred다.
