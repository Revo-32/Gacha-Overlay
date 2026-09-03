"use strict";
// Test-only Node built-ins. None of this file/runtime enters the static image.
const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const script = fs.readFileSync(path.join(__dirname, "../public/status.js"), "utf8");
const keys = ["backend", "discord", "authentication", "remote"];
const snapshot = state => ({ schemaVersion: 1, overall: state, updatedAt: "2026-09-03T05:00:00Z", services: Object.fromEntries(keys.map(key => [key, state])) });
const flush = () => new Promise(resolve => setImmediate(resolve));

async function fixture(payload, behavior = {}) {
  const nodes = Object.fromEntries(["overall", "overall-title", "overall-note", "checked", ...keys].map(key => [key, { dataset: {}, textContent: "" }]));
  const timers = new Map(); const events = new Map(); const calls = []; let id = 0;
  const context = {
    document: { querySelector: () => nodes.overall, getElementById: key => nodes[key] },
    window: { addEventListener: (name, callback) => events.set(name, callback) },
    Intl, Date, AbortController,
    setTimeout: (callback, delay) => { timers.set(++id, { callback, delay }); return id; },
    clearTimeout: timer => timers.delete(timer),
    fetch: async (url, options) => {
      calls.push({ url, options });
      if (behavior.fail) throw new Error("private backend stack path token");
      if (behavior.pending) return behavior.pending(options.signal);
      return { ok: behavior.ok !== false, text: async () => typeof payload === "string" ? payload : JSON.stringify(payload) };
    },
  };
  vm.runInNewContext(script, context);
  await flush();
  return { nodes, timers, events, calls };
}

for (const [state, label] of Object.entries({ operational: "정상", degraded: "일부 기능 지연", maintenance: "점검 중", unavailable: "이용 불가", unknown: "상태 확인 중" })) {
  test(`${state}: exact safe mapping and UTC snapshot shown as KST`, async () => {
    const f = await fixture(snapshot(state));
    assert.equal(f.nodes.overall.dataset.state, state);
    for (const key of keys) assert.equal(f.nodes[key].textContent, label);
    assert.match(f.nodes.checked.textContent, /KST/);
    assert.equal(f.calls[0].url, "https://overlay.revo32.cloud/status/public");
    assert.equal(f.calls[0].options.credentials, "omit");
    assert.equal(f.calls[0].options.cache, "no-store");
    assert.equal(f.calls[0].options.redirect, "error");
    assert.deepEqual([...f.timers.values()].map(timer => timer.delay), [60000]);
  });
}

for (const scenario of ["network", "http", "json", "large", "extra-field", "invalid-state", "mixed-unknown", "timestamp"]) {
  test(`${scenario}: failure marks only Backend unavailable; dependencies unknown`, async () => {
    let value = snapshot("operational");
    if (scenario === "json") value = "<h1>private failure</h1>";
    if (scenario === "large") value = "x".repeat(4097);
    if (scenario === "extra-field") value.userId = "private identity";
    if (scenario === "invalid-state") value.services.discord = "<script>private</script>";
    if (scenario === "mixed-unknown") value.services.remote = "unknown";
    if (scenario === "timestamp") value.updatedAt = "not-a-date";
    const f = await fixture(value, { fail: scenario === "network", ok: scenario !== "http" });
    assert.equal(f.nodes.backend.dataset.state, "unavailable");
    for (const key of keys.slice(1)) {
      assert.equal(f.nodes[key].dataset.state, "unknown");
      assert.equal(f.nodes[key].textContent, "상태 확인 불가");
    }
    assert.equal(f.nodes["overall-title"].textContent, "서비스 상태를 불러올 수 없습니다.");
    assert.match(f.nodes.checked.textContent, /확인 실패/);
    assert.doesNotMatch(JSON.stringify(f.nodes), /private|<script>/);
  });
}

test("refresh is sequential at 60 seconds and pagehide cancels timers", async () => {
  const f = await fixture(snapshot("operational"));
  const refresh = [...f.timers.values()][0];
  f.timers.clear();
  refresh.callback(); await flush();
  assert.equal(f.calls.length, 2);
  assert.deepEqual([...f.timers.values()].map(timer => timer.delay), [60000]);
  f.events.get("pagehide")();
  assert.equal(f.timers.size, 0);
  f.events.get("pageshow")({ persisted: true }); await flush();
  assert.equal(f.calls.length, 3);
});

test("slow fetch is aborted at 8 seconds without a retry storm", async () => {
  const f = await fixture(null, { pending: signal => new Promise((resolve, reject) => signal.addEventListener("abort", () => reject(new Error("timeout")))) });
  assert.deepEqual([...f.timers.values()].map(timer => timer.delay), [8000]);
  [...f.timers.values()][0].callback(); await flush();
  assert.equal(f.nodes.backend.textContent, "이용 불가");
  assert.equal(f.calls.length, 1);
  assert.deepEqual([...f.timers.values()].map(timer => timer.delay), [60000]);
});
