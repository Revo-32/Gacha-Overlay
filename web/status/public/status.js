"use strict";

(() => {
  const API_URL = "https://overlay.revo32.cloud/status/public";
  const REFRESH_MS = 60_000;
  const TIMEOUT_MS = 8_000;
  const keys = ["backend", "discord", "authentication", "remote"];
  const labels = Object.freeze({ operational: "정상", degraded: "일부 기능 지연", maintenance: "점검 중", unavailable: "이용 불가", unknown: "상태 확인 중" });
  const titles = Object.freeze({ operational: "모든 시스템 정상", degraded: "일부 서비스에 문제가 있습니다", maintenance: "서비스 점검 중", unavailable: "서비스 이용에 문제가 있습니다", unknown: "서비스 상태를 확인하는 중입니다" });
  const overall = document.querySelector(".overall");
  const title = document.getElementById("overall-title");
  const note = document.getElementById("overall-note");
  const checked = document.getElementById("checked");
  const formatter = new Intl.DateTimeFormat("ko-KR", { timeZone: "Asia/Seoul", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
  let timer;
  let controller;
  let active = false;
  let suspended = false;

  function setService(key, state, label = labels[state]) {
    const row = document.getElementById(key);
    row.dataset.state = state;
    row.textContent = label;
  }

  function aggregate(states) {
    return ["unavailable", "maintenance", "degraded", "unknown"].find(state => states.includes(state)) || "operational";
  }

  function validSnapshot(data) {
    const exactKeys = (value, expected) => value && !Array.isArray(value) &&
      Object.keys(value).sort().join(",") === [...expected].sort().join(",");
    return exactKeys(data, ["schemaVersion", "overall", "updatedAt", "services"]) && data.schemaVersion === 1 &&
      exactKeys(data.services, keys) && keys.every(key => Object.hasOwn(labels, data.services[key])) &&
      data.overall === aggregate(keys.map(key => data.services[key])) &&
      typeof data.updatedAt === "string" && /(?:Z|\+00:00)$/.test(data.updatedAt) && Number.isFinite(Date.parse(data.updatedAt));
  }

  async function refresh() {
    if (active || suspended) return;
    active = true;
    controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), TIMEOUT_MS);
    try {
      const response = await fetch(API_URL, { cache: "no-store", credentials: "omit", redirect: "error", signal: controller.signal });
      if (!response.ok) throw new Error("Status unavailable");
      const body = await response.text();
      if (body.length > 4096) throw new Error("Unexpected status response");
      const data = JSON.parse(body);
      if (!validSnapshot(data)) throw new Error("Invalid status snapshot");
      overall.dataset.state = data.overall;
      title.textContent = titles[data.overall];
      note.textContent = "현재 서버가 확인한 서비스 준비 상태입니다.";
      keys.forEach(key => setService(key, data.services[key]));
      checked.textContent = "마지막 확인: " + formatter.format(new Date(data.updatedAt)) + " KST";
    } catch {
      if (!suspended) {
        overall.dataset.state = "unavailable";
        title.textContent = "서비스 상태를 불러올 수 없습니다.";
        note.textContent = "이 브라우저에서 Backend에 연결할 수 없습니다. 네트워크 상태에 따라 달라질 수 있습니다.";
        setService("backend", "unavailable");
        keys.filter(key => key !== "backend").forEach(key => setService(key, "unknown", "상태 확인 불가"));
        checked.textContent = "최근 확인 시도: " + formatter.format(new Date()) + " KST · 확인 실패";
      }
    } finally {
      clearTimeout(timeout);
      active = false;
      controller = undefined;
      // Completion-based scheduling prevents overlapping requests and retry storms.
      if (!suspended) timer = setTimeout(refresh, REFRESH_MS);
    }
  }

  window.addEventListener("pagehide", () => { suspended = true; clearTimeout(timer); controller?.abort(); });
  window.addEventListener("pageshow", event => { if (event.persisted) { suspended = false; clearTimeout(timer); void refresh(); } });
  void refresh();
})();
