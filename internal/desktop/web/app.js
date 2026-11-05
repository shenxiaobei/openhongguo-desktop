"use strict";

const byId = (id) => document.getElementById(id);
const state = {
  view: "discover", channel: "推荐", query: "", cursor: "", hasMore: false,
  items: [], listRequest: 0, listAbort: null, listLoading: false,
  detailRequest: 0, detailAbort: null, detailID: "", detail: null, selected: null,
  status: null, statusPending: false, historyRequest: 0, history: [],
  lastPlaybackState: "", playPending: false, stopped: false, toastTimer: null,
};

// 数据只能通过同源接口获取；取消过期请求，防止较慢响应覆盖新的操作。
async function request(path, options = {}) {
  const controller = new AbortController();
  const externalSignal = options.signal;
  const cancel = () => controller.abort();
  if (externalSignal) {
    if (externalSignal.aborted) controller.abort();
    externalSignal.addEventListener("abort", cancel, { once: true });
  }
  const timer = setTimeout(cancel, options.timeout || 60000);
  try {
    const response = await fetch(path, {
      credentials: "same-origin", ...options, signal: controller.signal,
      headers: { "Content-Type": "application/json", ...options.headers },
    });
    let result;
    try {
      result = await response.json();
    } catch {
      throw new Error("应用返回了异常响应，请重试或查看日志。");
    }
    if (!response.ok) throw new Error(result.error || "请求没有成功，请稍后重试。");
    return result;
  } catch (error) {
    if (error.name === "AbortError") {
      if (externalSignal && externalSignal.aborted) throw error;
      throw new Error("等待响应超时，请检查网络后重试。");
    }
    if (error instanceof TypeError) throw new Error("与桌面应用的连接已中断，请重新打开应用。");
    throw error;
  } finally {
    clearTimeout(timer);
    if (externalSignal) externalSignal.removeEventListener("abort", cancel);
  }
}

function post(path, body) {
  return request(path, { method: "POST", body: JSON.stringify(body || {}) });
}

function showToast(message, error = false) {
  if (state.stopped) return;
  clearTimeout(state.toastTimer);
  byId("toast").textContent = message;
  byId("toast").classList.toggle("error", error);
  byId("toast").hidden = false;
  state.toastTimer = setTimeout(() => { if (!state.stopped) byId("toast").hidden = true; }, error ? 12000 : 5000);
}

function setConnection(online, message) {
  byId("connection").className = "connection " + (online ? "online" : "offline");
  byId("connection-text").textContent = online ? "应用已连接" : "连接中断";
  byId("connection-notice").hidden = online;
  if (!online) byId("connection-notice-text").textContent = message;
}

function coverElement(url, title) {
  const holder = document.createElement("div");
  holder.className = "poster-fallback";
  const mark = document.createElement("span");
  mark.className = "fallback-mark";
  mark.textContent = "果";
  const text = document.createElement("span");
  text.textContent = title || "等待故事开场";
  holder.append(mark, text);
  const fragment = document.createDocumentFragment();
  fragment.append(holder);
  try {
    const parsed = new URL(url);
    if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return fragment;
    const image = document.createElement("img");
    image.alt = "";
    image.loading = "lazy";
    image.referrerPolicy = "no-referrer";
    image.addEventListener("load", () => { holder.hidden = true; });
    image.addEventListener("error", () => { image.remove(); holder.hidden = false; });
    image.src = parsed.href;
    fragment.append(image);
  } catch {
    // 封面缺失或过期时保留本地占位，不让图片失败影响剧集操作。
  }
  return fragment;
}

function renderSkeleton(container, count = 6) {
  container.replaceChildren();
  for (let index = 0; index < count; index++) {
    const card = document.createElement("div");
    card.className = "skeleton-card";
    card.setAttribute("aria-hidden", "true");
    for (const className of ["poster skeleton", "skeleton-line skeleton", "skeleton-line short skeleton"]) {
      const item = document.createElement("div");
      item.className = className;
      card.append(item);
    }
    container.append(card);
  }
}

function setView(view) {
  state.view = view;
  byId("catalog-view").hidden = view !== "discover";
  byId("history-view").hidden = view !== "history";
  byId("settings-view").hidden = view !== "settings";
  byId("page-title").textContent = { discover: "发现好剧", history: "观看记录", settings: "设置与诊断" }[view];
  for (const button of document.querySelectorAll("[data-view]")) {
    button.classList.toggle("active", button.dataset.view === view);
    if (button.dataset.view === view) button.setAttribute("aria-current", "page");
    else button.removeAttribute("aria-current");
  }
  if (view === "history") loadHistory();
  if (view === "settings") { renderSettings(); refreshStatus(); }
}

function renderCatalog() {
  const grid = byId("poster-grid");
  grid.replaceChildren();
  for (const item of state.items) {
    const card = document.createElement("button");
    card.className = "poster-card";
    card.setAttribute("aria-label", "查看《" + item.title + "》详情");
    const poster = document.createElement("div");
    poster.className = "poster";
    poster.append(coverElement(item.cover_url, item.title));
    if (item.episode_count) {
      const count = document.createElement("span");
      count.className = "poster-count";
      count.textContent = item.episode_count + " 集";
      poster.append(count);
    }
    const hover = document.createElement("span");
    hover.className = "poster-hover";
    hover.setAttribute("aria-hidden", "true");
    const play = document.createElement("span");
    play.textContent = "▶";
    hover.append(play);
    poster.append(hover);
    const title = document.createElement("span");
    title.className = "poster-title";
    title.textContent = item.title || "未命名剧集";
    title.title = item.title || "";
    const meta = document.createElement("span");
    meta.className = "poster-meta";
    meta.textContent = item.category || item.description || "点击查看剧集";
    card.append(poster, title, meta);
    card.addEventListener("click", () => openDetails(item.id));
    grid.append(card);
  }
  byId("result-count").textContent = state.items.length ? "已加载 " + state.items.length + " 部" : "";
  byId("catalog-empty").hidden = state.items.length > 0;
  byId("load-more").hidden = !(state.hasMore && state.cursor);
  byId("catalog-end").hidden = state.items.length === 0 || state.hasMore;
}

async function loadCatalog(append = false) {
  if (append && (state.listLoading || !state.cursor)) return;
  if (state.listAbort) state.listAbort.abort();
  const controller = new AbortController();
  state.listAbort = controller;
  const requestID = ++state.listRequest;
  state.listLoading = true;
  byId("poster-grid").setAttribute("aria-busy", "true");
  byId("catalog-error").hidden = true;
  byId("catalog-empty").hidden = true;
  byId("catalog-end").hidden = true;
  byId("load-more").disabled = true;
  byId("load-more").textContent = "正在加载…";
  byId("hero").hidden = Boolean(state.query);
  byId("channel-tabs").hidden = Boolean(state.query);
  byId("catalog-title").textContent = state.query ? "“" + state.query + "” 的搜索结果" : (state.channel === "推荐" ? "为你发现" : state.channel);
  if (!append) {
    state.items = [];
    state.cursor = "";
    state.hasMore = false;
    byId("result-count").textContent = "正在加载…";
    byId("load-more").hidden = true;
    renderSkeleton(byId("poster-grid"));
  }
  const params = new URLSearchParams();
  if (state.query) params.set("q", state.query);
  else params.set("channel", state.channel);
  if (append) params.set("cursor", state.cursor);
  try {
    const page = await request((state.query ? "/api/search?" : "/api/discover?") + params, { signal: controller.signal });
    if (requestID !== state.listRequest) return;
    const seen = new Set(state.items.map((item) => item.id));
    for (const item of page.items || []) {
      if (!item.id || seen.has(item.id)) continue;
      seen.add(item.id);
      state.items.push(item);
    }
    state.cursor = page.cursor || "";
    state.hasMore = Boolean(page.has_more);
    renderCatalog();
  } catch (error) {
    if (controller.signal.aborted || requestID !== state.listRequest) return;
    byId("catalog-error-text").textContent = error.message;
    byId("catalog-error").hidden = false;
    if (!append) byId("poster-grid").replaceChildren();
    byId("result-count").textContent = state.items.length ? "已加载 " + state.items.length + " 部" : "";
    byId("load-more").hidden = !append;
  } finally {
    if (requestID === state.listRequest) {
      state.listLoading = false;
      byId("poster-grid").setAttribute("aria-busy", "false");
      byId("load-more").disabled = false;
      byId("load-more").textContent = "加载更多";
    }
  }
}

function chooseChannel(channel) {
  state.channel = channel;
  state.query = "";
  byId("search-input").value = "";
  for (const button of document.querySelectorAll("[data-channel]")) {
    const selected = button.dataset.channel === channel;
    button.classList.toggle("active", selected);
    button.setAttribute("aria-pressed", String(selected));
  }
  setView("discover");
  loadCatalog();
}

function selectEpisode(episode) {
  state.selected = episode;
  for (const button of byId("episode-grid").children) {
    const selected = button.dataset.episode === episode.id;
    button.classList.toggle("selected", selected);
    button.setAttribute("aria-pressed", String(selected));
  }
  byId("selected-episode").textContent = "第 " + episode.number + " 集" + (episode.title ? " · " + episode.title : "");
  updatePlayButton();
}

function playerAvailable() {
  return Boolean(state.status && state.status.player && state.status.player.available);
}

function updatePlayButton() {
  if (state.stopped) return;
  byId("play-button").disabled = !state.selected || !playerAvailable() || state.playPending;
  byId("play-button").textContent = state.playPending ? "正在提交…" : "▶ 播放这一集";
  byId("detail-player-note").textContent = playerAvailable() ? "播放将打开本机播放器 · " + state.status.player.name : "尚未检测到播放器，请到“设置与诊断”查看";
}

function renderDetails(resumeEpisode) {
  const detail = state.detail;
  byId("series-title").textContent = detail.series.title || "剧集详情";
  byId("detail-cover").replaceChildren(coverElement(detail.series.cover_url, detail.series.title));
  const count = detail.series.episode_count || detail.episodes.length;
  const meta = [];
  if (count) meta.push(count + " 集");
  if (detail.series.category) meta.push(detail.series.category);
  if (detail.series.right_text) meta.push(detail.series.right_text);
  byId("series-meta").textContent = meta.join(" · ");
  byId("series-description").textContent = detail.series.description || "这部剧暂时没有剧情简介。";
  byId("episode-count").textContent = "共 " + detail.episodes.length + " 集";
  byId("episode-grid").replaceChildren();
  for (const episode of detail.episodes) {
    const button = document.createElement("button");
    button.className = "episode-button";
    button.textContent = episode.number;
    button.dataset.episode = episode.id;
    button.setAttribute("aria-label", "第 " + episode.number + " 集" + (episode.title ? " " + episode.title : ""));
    button.title = episode.playable === false ? "这集暂未开放播放" : (episode.title || "第 " + episode.number + " 集");
    button.disabled = episode.playable === false;
    button.addEventListener("click", () => selectEpisode(episode));
    byId("episode-grid").append(button);
  }
  let selection = detail.episodes.find((episode) => episode.number === resumeEpisode && episode.playable !== false);
  if (!selection && resumeEpisode > 0) {
    const playable = detail.episodes.filter((episode) => episode.playable !== false);
    if (playable.length > 0 && resumeEpisode > playable[playable.length - 1].number) selection = playable[playable.length - 1];
  }
  if (!selection) selection = detail.episodes.find((episode) => episode.playable !== false);
  if (selection) selectEpisode(selection);
  else {
    state.selected = null;
    byId("selected-episode").textContent = "暂无可播放分集";
    updatePlayButton();
  }
}

async function openDetails(id, resumeEpisode = 0) {
  if (!id) return;
  if (state.detailAbort) state.detailAbort.abort();
  const controller = new AbortController();
  state.detailAbort = controller;
  const requestID = ++state.detailRequest;
  state.detailID = id;
  state.detail = null;
  state.selected = null;
  byId("series-title").textContent = "正在加载剧集详情";
  byId("detail-loading").hidden = false;
  byId("detail-content").hidden = true;
  byId("detail-error").hidden = true;
  byId("detail-feedback").hidden = true;
  if (!byId("series-dialog").open) byId("series-dialog").showModal();
  try {
    const result = await request("/api/series?" + new URLSearchParams({ id }), { signal: controller.signal });
    if (requestID !== state.detailRequest) return;
    state.detail = { series: result.series || {}, episodes: result.episodes || [] };
    renderDetails(resumeEpisode);
    byId("detail-content").hidden = false;
  } catch (error) {
    if (controller.signal.aborted || requestID !== state.detailRequest) return;
    byId("detail-error-text").textContent = error.message;
    byId("detail-error").hidden = false;
  } finally {
    if (requestID === state.detailRequest) byId("detail-loading").hidden = true;
  }
}

async function playSelected() {
  if (!state.selected || !state.detail || state.playPending) return;
  const episode = state.selected;
  const series = state.detail.series;
  const requestID = state.detailRequest;
  state.playPending = true;
  updatePlayButton();
  byId("detail-feedback").hidden = true;
  try {
    await post("/api/play", { series_id: series.id || state.detailID, episode_id: episode.id, title: series.title || "", episode_title: "第 " + episode.number + " 集", quality: byId("quality-select").value });
    if (requestID === state.detailRequest && !state.stopped) {
      byId("detail-feedback").classList.remove("error");
      byId("detail-feedback").textContent = "正在准备播放，请稍候。本机播放器即将打开。";
      byId("detail-feedback").hidden = false;
    }
    await refreshStatus();
  } catch (error) {
    if (requestID === state.detailRequest && !state.stopped) {
      byId("detail-feedback").textContent = error.message;
      byId("detail-feedback").classList.add("error");
      byId("detail-feedback").hidden = false;
    }
  } finally {
    state.playPending = false;
    updatePlayButton();
  }
}

function renderPlayback() {
  if (state.stopped) return;
  const playback = state.status && state.status.playback ? state.status.playback : { state: "idle" };
  const current = playback.state || "idle";
  const active = current === "loading" || current === "playing";
  const labels = { idle: "待播放", loading: "准备中", playing: "播放中", stopped: "已停止", error: "播放异常" };
  byId("playback-bar").classList.toggle("is-active", active);
  byId("playback-bar").classList.toggle("is-error", current === "error");
  byId("playback-badge").textContent = labels[current] || "待播放";
  byId("playback-title").textContent = playback.title || "准备好，进入一个新故事";
  let description = "选择剧集后，播放将打开本机播放器";
  if (current === "loading") description = "正在获取播放信息，请稍候…";
  if (current === "playing") description = [playback.episode_title, "已在本机播放器中打开"].filter(Boolean).join(" · ");
  if (current === "stopped") description = "播放已结束，可以选择下一集或继续发现好剧。";
  if (current === "error") description = playback.error || "播放没有成功，请重试或到设置与诊断检查。";
  byId("playback-description").textContent = description;
  byId("stop-button").disabled = !active;
  if (byId("series-dialog").open && state.detail && playback.series_id === state.detailID) {
    if (current === "error") {
      byId("detail-feedback").textContent = description;
      byId("detail-feedback").classList.add("error");
      byId("detail-feedback").hidden = false;
    } else if (current === "playing") {
      byId("detail-feedback").textContent = "已打开本机播放器。切换或关闭播放器后，你仍可在这里选择其他分集。";
      byId("detail-feedback").classList.remove("error");
      byId("detail-feedback").hidden = false;
    }
  }
  if (state.lastPlaybackState !== current && state.view === "history" && !active) loadHistory();
  state.lastPlaybackState = current;
}

function renderSettings() {
  const status = state.status;
  if (!status) return;
  const player = status.player || {};
  byId("player-diagnostic").classList.toggle("missing", !player.available);
  byId("player-diagnostic").textContent = player.available ? "已就绪 · 将使用 " + player.name + " 播放" : "尚未检测到播放器。搜索和查看剧集可以正常使用；播放前请安装 mpv、ffplay 或 VLC。";
  byId("tool-list").replaceChildren();
  for (const tool of status.tools || []) {
    const row = document.createElement("div");
    row.className = "tool-row" + (tool.available ? " available" : "");
    const name = document.createElement("strong");
    name.textContent = tool.name;
    const path = document.createElement("span");
    path.textContent = tool.available ? (tool.path || "已找到") : "未检测到";
    row.append(name, path);
    byId("tool-list").append(row);
  }
  byId("config-path").textContent = status.config_dir || "未提供";
  byId("log-path").textContent = status.log_file || "未提供";
  byId("app-version").textContent = status.version || "桌面版";
}

async function refreshStatus() {
  if (state.statusPending || state.stopped) return;
  state.statusPending = true;
  try {
    state.status = await request("/api/status", { timeout: 10000 });
    if (state.stopped) return;
    setConnection(true);
    byId("sidebar-version").textContent = state.status.version || "桌面版";
    renderPlayback();
    renderSettings();
    updatePlayButton();
  } catch (error) {
    if (state.stopped) return;
    setConnection(false, error.message);
  } finally {
    state.statusPending = false;
  }
}

function formatPosition(seconds) {
  const value = Math.max(0, Math.floor(Number(seconds) || 0));
  return Math.floor(value / 60) + ":" + String(value % 60).padStart(2, "0");
}

function formatDate(value) {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return "";
  return date.toLocaleString("zh-CN", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

function renderHistory() {
  byId("history-list").replaceChildren();
  byId("history-empty").hidden = state.history.length > 0;
  for (const entry of state.history) {
    const row = document.createElement("article");
    row.className = "history-item";
    const number = document.createElement("span");
    number.className = "history-number";
    number.textContent = String(entry.episode || 1).padStart(2, "0");
    const copy = document.createElement("div");
    copy.className = "history-copy";
    const title = document.createElement("h3");
    title.textContent = entry.title || "剧集 " + entry.series_id;
    const position = document.createElement("p");
    position.textContent = "第 " + (entry.episode || 1) + " 集 · " + (entry.finished ? "已看完" : "观看至 " + formatPosition(entry.position));
    copy.append(title, position);
    const time = document.createElement("span");
    time.className = "history-date";
    time.textContent = formatDate(entry.updated_at);
    const button = document.createElement("button");
    button.className = "secondary-button";
    button.textContent = "继续观看";
    button.addEventListener("click", () => openDetails(entry.series_id, entry.finished ? entry.episode + 1 : entry.episode));
    row.append(number, copy, time, button);
    byId("history-list").append(row);
  }
}

async function loadHistory() {
  const requestID = ++state.historyRequest;
  byId("history-error").hidden = true;
  byId("history-empty").hidden = true;
  byId("history-list").setAttribute("aria-busy", "true");
  try {
    const result = await request("/api/history");
    if (requestID !== state.historyRequest) return;
    state.history = Array.isArray(result) ? result : [];
    renderHistory();
  } catch (error) {
    if (requestID !== state.historyRequest) return;
    byId("history-error-text").textContent = error.message;
    byId("history-error").hidden = false;
  } finally {
    if (requestID === state.historyRequest) byId("history-list").setAttribute("aria-busy", "false");
  }
}

async function stopPlayback() {
  byId("stop-button").disabled = true;
  try {
    await post("/api/stop");
    await refreshStatus();
  } catch (error) {
    showToast(error.message, true);
    renderPlayback();
  }
}

async function quitApplication() {
  byId("quit-button").disabled = true;
  try {
    await post("/api/shutdown");
    state.stopped = true;
    clearTimeout(state.toastTimer);
    state.listRequest++;
    state.detailRequest++;
    state.historyRequest++;
    if (state.listAbort) state.listAbort.abort();
    if (state.detailAbort) state.detailAbort.abort();
    if (byId("series-dialog").open) byId("series-dialog").close();
    const closed = document.createElement("main");
    closed.className = "app-closed";
    const copy = document.createElement("div");
    const title = document.createElement("h1");
    title.textContent = "今天的故事，先到这里。";
    const hint = document.createElement("p");
    hint.textContent = "应用已退出，可以关闭这个窗口。下次双击桌面版即可继续。";
    copy.append(title, hint);
    closed.append(copy);
    document.body.replaceChildren(closed);
  } catch (error) {
    showToast(error.message, true);
    byId("quit-button").disabled = false;
  }
}

for (const button of document.querySelectorAll("[data-view]")) button.addEventListener("click", () => setView(button.dataset.view));
for (const button of document.querySelectorAll("[data-channel]")) button.addEventListener("click", () => chooseChannel(button.dataset.channel));
document.querySelector(".brand").addEventListener("click", (event) => { event.preventDefault(); chooseChannel("推荐"); });
byId("search-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const query = byId("search-input").value.trim();
  if (!query) { chooseChannel(state.channel); return; }
  state.query = query;
  setView("discover");
  loadCatalog();
});
byId("refresh-button").addEventListener("click", () => loadCatalog());
byId("catalog-retry").addEventListener("click", () => loadCatalog(state.items.length > 0));
byId("load-more").addEventListener("click", () => loadCatalog(true));
byId("history-refresh").addEventListener("click", loadHistory);
byId("history-retry").addEventListener("click", loadHistory);
byId("history-discover").addEventListener("click", () => chooseChannel("推荐"));
byId("settings-refresh").addEventListener("click", refreshStatus);
byId("reconnect-button").addEventListener("click", refreshStatus);
byId("close-dialog").addEventListener("click", () => byId("series-dialog").close());
byId("series-dialog").addEventListener("close", () => { if (state.detailAbort) state.detailAbort.abort(); });
byId("series-dialog").addEventListener("click", (event) => {
  if (event.target !== byId("series-dialog")) return;
  const rect = byId("series-dialog").getBoundingClientRect();
  if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) byId("series-dialog").close();
});
byId("detail-retry").addEventListener("click", () => openDetails(state.detailID));
byId("play-button").addEventListener("click", playSelected);
byId("stop-button").addEventListener("click", stopPlayback);
byId("quit-button").addEventListener("click", quitApplication);

refreshStatus();
loadCatalog();
setInterval(() => { if (!state.stopped) refreshStatus(); }, 2500);
setInterval(() => { if (!state.stopped) post("/api/ping").catch(() => {}); }, 20000);
