# ADR-010: Diagnostic Bundle Allowlist

## Status

Accepted foundation; exporter deferred to M8.2

## Context

진단 ZIP이 애플리케이션 데이터 전체를 수집한 뒤 denylist로 일부 secret만 제거하면 새 credential 형식이나 raw payload가 누출될 수 있다.

## Decision

Diagnostic Bundle은 allowlist로만 구성한다. M8.0은 중앙 log redaction과 수집 계약만 고정하며 실제 ZIP exporter/UI는 M8.2로 연기한다.

## Consequences

새 diagnostic 항목은 명시적으로 검토·추가해야 한다. 누락보다 비밀정보 과수집 방지를 우선한다.

## Invariants

- 허용: sanitized settings/logs, app version/build, runtime metrics, RPC/UIA state, crash metadata, monitor/DPI summary, catalog version, health snapshot.
- 금지: Client Secret, access/refresh token, authorization header, raw DPAPI blob/storage, raw Discord payload/message content.
- 로그 boundary에서 credential-like 값을 중앙 redaction한다.
