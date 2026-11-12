// TnA web UI — no framework, no build step, no external requests.
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

const state = {
  info: null, options: null, settings: null, tools: null, queue: null,
  jobs: new Map(), order: [], selected: new Set(), form: null,
  browse: { path: null, selected: new Set() }, advancedAllowed: false,
};

// ---------------------------------------------------------------- API
async function api(method, url, body) {
  const res = await fetch(url, {
    method, headers: body ? { "Content-Type": "application/json" } : {},
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) {
    let msg = res.statusText;
    try { const j = await res.json(); msg = j.detail || j.title || msg; } catch { /* not json */ }
    throw new Error(msg);
  }
  const type = res.headers.get("content-type") || "";
  return type.includes("json") ? res.json() : res.text();
}
const post = (u, b) => api("POST", u, b ?? {});

function toast(text, kind = "") {
  const t = document.createElement("div");
  t.className = "toast " + kind;
  t.textContent = text;
  $("#toasts").append(t);
  setTimeout(() => t.remove(), kind === "err" ? 7000 : 3500);
}
const guard = fn => async (...a) => { try { await fn(...a); } catch (e) { toast(e.message, "err"); } };

// ---------------------------------------------------------------- formatting
const fmtTime = s => {
  if (s == null || !isFinite(s)) return "–";
  s = Math.max(0, Math.round(s));
  const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = s % 60;
  return (h ? h + ":" + String(m).padStart(2, "0") : m) + ":" + String(x).padStart(2, "0");
};
const fmtSize = b => {
  if (!b) return "–";
  const u = ["B", "KB", "MB", "GB", "TB"]; let i = 0;
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return b.toFixed(i >= 2 ? 1 : 0) + " " + u[i];
};
const label = (list, v) => list.find(o => o.value === String(v))?.label ?? v;
const profileOf = v => state.options.profiles.find(p => p.value === v);

// ---------------------------------------------------------------- theme
function applyTheme(theme) {
  const root = document.documentElement;
  if (theme === "Light") root.dataset.theme = "light";
  else if (theme === "Dark") root.dataset.theme = "dark";
  else delete root.dataset.theme;
  const dark = theme === "Dark" || (theme !== "Light" && matchMedia("(prefers-color-scheme: dark)").matches);
  $("#btn-theme use").setAttribute("href", dark ? "#i-sun" : "#i-moon");
  try { localStorage.setItem("tna-theme", theme); } catch { /* private mode */ }
}
$("#btn-theme").onclick = () => {
  const cur = document.documentElement.dataset.theme;
  const dark = cur === "dark" || (!cur && matchMedia("(prefers-color-scheme: dark)").matches);
  const next = dark ? "Light" : "Dark";
  applyTheme(next);
  api("PUT", "/api/settings", { theme: next }).catch(() => {});
};

// ---------------------------------------------------------------- capabilities
function capability(codec, backend) {
  return state.settings.capabilities?.encoders?.find(e => e.codec === codec && e.backend === backend);
}
function backendStatus(codec, backend, tenBit) {
  const c = capability(codec, backend);
  if (!state.settings.capabilities) return { ok: true, text: "" };
  if (!c) return { ok: false, text: "not available for this codec" };
  if (tenBit ? c.works10Bit : c.works8Bit) return { ok: true, text: "" };
  if (!c.compiled) return { ok: false, text: "not in this FFmpeg build" };
  return { ok: false, text: tenBit && c.works8Bit ? "8-bit only" : "no compatible GPU/driver" };
}

// ---------------------------------------------------------------- settings form
function fillSelect(sel, items, value) {
  sel.innerHTML = items.map(o => `<option value="${esc(o.value)}">${esc(o.label)}</option>`).join("");
  if (value != null) sel.value = String(value);
}

function renderForm() {
  const f = state.form, o = state.options, p = profileOf(f.profile);
  fillSelect($("#f-profile"), o.profiles, f.profile);
  $("#profile-desc").textContent = p.description;

  const encodes = p.usesQuality && p.codecs.length > 0 && !["RemuxMkv", "Keyframes"].includes(p.value);
  $("#row-codec").hidden = p.codecs.length <= 1;
  if (!p.codecs.includes(f.codec) && p.codecs.length) f.codec = p.codecs[0];
  $("#f-codec").innerHTML = p.codecs.map(c =>
    `<button type="button" data-v="${c}" class="${c === f.codec ? "on" : ""}">${esc(label(o.codecs, c).split(" /")[0])}</button>`).join("");

  const codec = p.value === "RemuxMp4" ? "H264" : f.codec;
  const tenBitPossible = ["Hevc", "HEVC", "AV1"].includes(codec) && ["UniversalMkv", "Streaming"].includes(p.value);
  const backends = o.backends.map(b => {
    const st = backendStatus(codec, b.value, f.tenBit && tenBitPossible);
    return { value: b.value, label: b.label + (st.ok ? "" : " — " + st.text), ok: st.ok };
  });
  const sel = $("#f-backend");
  sel.innerHTML = backends.map(b => `<option value="${b.value}" ${b.ok ? "" : "disabled"}>${esc(b.label)}</option>`).join("");
  if (!p.allowsHardware) f.backend = "Software";
  if (!backends.find(b => b.value === f.backend)?.ok) f.backend = backends.find(b => b.ok)?.value ?? "Software";
  sel.value = f.backend;
  sel.disabled = !p.allowsHardware;
  $("#row-backend").hidden = !encodes;
  $("#backend-note").textContent = !p.allowsHardware ? "This profile is CPU only."
    : f.backend === "Software" ? "CPU encoding: slowest, best compression." : "Hardware encoding: much faster, slightly bigger files.";

  $("#row-tenbit").hidden = !encodes;
  $("#f-tenbit").checked = f.tenBit && tenBitPossible;
  $("#f-tenbit").disabled = !tenBitPossible;
  $("#row-tenbit").classList.toggle("disabled", !tenBitPossible);

  fillSelect($("#f-res"), o.resolutions, f.resolution);
  fillSelect($("#f-quality"), o.qualities, f.quality);
  fillSelect($("#f-subs"), o.subtitleModes, f.subtitles);
  $("#row-res").hidden = !p.usesResolution;
  $("#row-quality").hidden = !p.usesQuality;
  $("#row-subs").hidden = !p.usesSubtitleMode;
  $("#row-sidecar").hidden = !p.usesSubtitleMode || f.subtitles !== "Hardsub";
  $("#f-sidecar").checked = f.useSidecarSubtitles;

  const n = state.selected.size;
  $("#btn-apply-sel").disabled = n === 0;
  $("#btn-apply-sel").textContent = n ? `Apply to ${n} selected` : "Apply to selected";
}

function formSettings() {
  const f = state.form;
  return {
    profile: f.profile, codec: f.codec, backend: f.backend, quality: f.quality,
    resolution: Number(f.resolution), subtitles: f.subtitles, tenBit: f.tenBit, useSidecarSubtitles: f.useSidecarSubtitles,
  };
}

$("#f-profile").onchange = e => { state.form.profile = e.target.value; renderForm(); };
$("#f-codec").onclick = e => { const b = e.target.closest("button"); if (b) { state.form.codec = b.dataset.v; renderForm(); } };
$("#f-backend").onchange = e => { state.form.backend = e.target.value; renderForm(); };
$("#f-tenbit").onchange = e => { state.form.tenBit = e.target.checked; renderForm(); };
$("#f-res").onchange = e => { state.form.resolution = Number(e.target.value); };
$("#f-quality").onchange = e => { state.form.quality = e.target.value; };
$("#f-subs").onchange = e => { state.form.subtitles = e.target.value; renderForm(); };
$("#f-sidecar").onchange = e => { state.form.useSidecarSubtitles = e.target.checked; };
$("#btn-save-defaults").onclick = guard(async () => {
  const r = await api("PUT", "/api/settings", { defaults: formSettings() });
  state.settings = r.settings; toast("Saved as default", "ok");
});
$("#btn-apply-sel").onclick = guard(async () => {
  await post("/api/jobs/settings", { ids: [...state.selected], settings: formSettings() });
  toast(`Updated ${state.selected.size} file(s)`, "ok");
});

// ---------------------------------------------------------------- hardware
function renderHardware() {
  const caps = state.settings.capabilities, el = $("#hw-table");
  if (!caps) { el.innerHTML = ""; $("#hw-info").textContent = "Not detected yet."; return; }
  const codecs = ["H264", "HEVC", "AV1"];
  const backends = state.options.backends.map(b => b.value);
  let html = `<div></div>` + codecs.map(c => `<div class="h">${c === "H264" ? "H.264" : c}</div>`).join("");
  for (const b of backends) {
    html += `<div class="b">${esc(label(state.options.backends, b).replace(" (software)", "").replace("Apple ", "").replace("NVIDIA ", "").replace("Intel ", "").replace("AMD ", ""))}</div>`;
    for (const c of codecs) {
      const e = caps.encoders.find(x => x.codec === c && x.backend === b);
      if (!e) { html += `<div class="hw-cell na">—</div>`; continue; }
      const txt = e.works10Bit ? "10-bit" : e.works8Bit ? "8-bit" : "✕";
      html += `<div class="hw-cell ${e.works8Bit || e.works10Bit ? "ok" : ""}" title="${esc(e.encoder + (e.error ? ": " + e.error : ""))}">${txt}</div>`;
    }
  }
  el.innerHTML = html;
  $("#hw-info").textContent = `${caps.version.replace("ffmpeg version ", "FFmpeg ").split(" Copyright")[0]} · libass ${caps.hasLibass ? "✓" : "missing!"} · tested ${new Date(caps.detectedAt).toLocaleString()}`;
}
$("#btn-detect").onclick = guard(async () => {
  const b = $("#btn-detect"); b.disabled = true; b.textContent = "Testing…";
  try {
    state.settings.capabilities = await post("/api/hardware/detect");
    renderHardware(); renderForm();
    toast("Hardware detection finished", "ok");
  } finally { b.disabled = false; b.textContent = "Detect again"; }
});

// ---------------------------------------------------------------- queue rendering
const statusClass = s => s.toLowerCase();

function jobHtml(j) {
  const s = j.settings, p = profileOf(s.profile);
  const chips = [`<span class="chip">${esc(p.label)}</span>`];
  if (p.codecs.length > 1) chips.push(`<span class="chip">${esc(label(state.options.codecs, s.codec).split(" /")[0])}</span>`);
  const encodes = !["RemuxMkv", "Keyframes"].includes(s.profile);
  if (encodes && s.backend !== "Software" && p.allowsHardware) chips.push(`<span class="chip hw">${esc(label(state.options.backends, s.backend))}</span>`);
  if (j.label.includes("10bit")) chips.push(`<span class="chip bit">10-bit</span>`);
  if (p.usesResolution) chips.push(`<span class="chip">${esc(label(state.options.resolutions, s.resolution))}</span>`);
  if (p.usesQuality) chips.push(`<span class="chip">${esc(label(state.options.qualities, s.quality))}</span>`);
  if (p.usesSubtitleMode) chips.push(`<span class="chip">${esc(label(state.options.subtitleModes, s.subtitles))}</span>`);

  const running = j.status === "Running" || j.status === "Paused";
  const showBar = running || j.percent > 0;
  const stats = running ? `
      <div class="stats">
        <span>${esc(j.stage ?? "")}</span>
        <span><b>${fmtTime(j.positionSeconds)}</b> / ${fmtTime(j.durationSeconds)}</span>
        <span><b>${j.fps ? j.fps.toFixed(0) : "–"}</b> fps</span>
        <span><b>${j.speed ? j.speed.toFixed(2) + "×" : "–"}</b></span>
        <span>ETA <b>${fmtTime(j.remainingSeconds)}</b></span>
        <span>size <b>${fmtSize(j.size)}</b>${j.estimatedSize ? " → ~" + fmtSize(j.estimatedSize) : ""}</span>
        <span>elapsed <b>${fmtTime(j.elapsedSeconds)}</b></span>
      </div>` : (j.elapsedSeconds && !running ? `<div class="stats"><span>took <b>${fmtTime(j.elapsedSeconds)}</b></span>${j.outputPath ? `<span title="${esc(j.outputPath)}">→ ${esc(j.outputPath.split(/[\\/]/).pop())}</span>` : ""}</div>` : "");

  const msgKind = j.status === "Error" || j.status === "Stopped" ? "err" : j.status === "Warning" ? "warn" : "";
  return `
    <input type="checkbox" class="sel" ${state.selected.has(j.id) ? "checked" : ""} aria-label="Select">
    <div>
      <div class="title" title="${esc(j.sourcePath)}">${esc(j.fileName)}</div>
      <div class="folder" title="${esc(j.folder)}">&lrm;${esc(j.folder)}</div>
    </div>
    <div class="right">
      <span class="badge ${statusClass(j.status)}">${esc(j.status)}</span>
      <div class="actions">
        <button class="btn ghost icon sm" data-a="up" title="Move up" ${running ? "disabled" : ""}><svg><use href="#i-up"/></svg></button>
        <button class="btn ghost icon sm" data-a="down" title="Move down" ${running ? "disabled" : ""}><svg><use href="#i-down"/></svg></button>
        <button class="btn ghost icon sm" data-a="cmd" title="FFmpeg command"><svg><use href="#i-term"/></svg></button>
        <button class="btn ghost icon sm" data-a="log" title="Log"><svg><use href="#i-log"/></svg></button>
        <button class="btn ghost icon sm" data-a="reset" title="Process again" ${running || j.status === "Ready" ? "disabled" : ""}><svg><use href="#i-redo"/></svg></button>
        <button class="btn ghost icon sm" data-a="remove" title="Remove" ${running ? "disabled" : ""}><svg><use href="#i-trash"/></svg></button>
      </div>
    </div>
    <div class="meta">${chips.join("")}</div>
    ${j.mediaSummary ? `<div class="media">${esc(j.mediaSummary)}</div>` : j.probeError ? `<div class="media err">${esc(j.probeError)}</div>` : `<div class="media">Analysing…</div>`}
    ${showBar ? `<div class="progress"><div class="bar"><i style="width:${j.percent}%"></i></div>${stats}</div>` : stats ? `<div class="progress">${stats}</div>` : ""}
    ${j.message ? `<div class="msg ${msgKind}">${esc(j.message)}</div>` : ""}`;
}

function renderJob(j) {
  let el = document.getElementById("job-" + j.id);
  if (!el) {
    el = document.createElement("article");
    el.id = "job-" + j.id;
    el.dataset.id = j.id;
    $("#jobs").append(el);
  }
  el.className = `job ${statusClass(j.status)} ${state.selected.has(j.id) ? "selected" : ""}`;
  el.innerHTML = jobHtml(j);
}

function renderQueue() {
  const container = $("#jobs");
  const ids = state.order.filter(id => state.jobs.has(id));
  for (const id of ids) {
    const el = document.getElementById("job-" + id) ?? (renderJob(state.jobs.get(id)), document.getElementById("job-" + id));
    container.append(el); // re-append = reorder
  }
  $$(".job", container).forEach(el => { if (!state.jobs.has(el.dataset.id)) el.remove(); });
  $("#empty").hidden = ids.length > 0;
  $("#queue-count").textContent = ids.length;
  for (const id of [...state.selected]) if (!state.jobs.has(id)) state.selected.delete(id);
  renderControls();
}

function renderControls() {
  const q = state.queue ?? {};
  const pill = $("#queue-pill");
  pill.classList.toggle("running", q.running && !q.paused);
  pill.classList.toggle("paused", !!q.paused);
  const cur = q.currentJob && state.jobs.get(q.currentJob);
  $("#queue-text").textContent = q.paused ? "Paused" : q.running
    ? `Encoding ${q.done + 1}/${q.total}${cur ? " · " + Math.round(cur.percent) + "%" : ""}`
    : q.total ? `${q.ready} ready · ${q.done} done${q.failed ? " · " + q.failed + " failed" : ""}` : "Idle";
  document.title = q.running && cur ? `${Math.round(cur.percent)}% · TnA` : "TnA Autohardsubber";

  const start = $("#btn-start");
  start.className = "btn " + (q.running ? "danger" : "primary");
  start.innerHTML = q.running ? `<svg><use href="#i-stop"/></svg><span>Stop</span>` : `<svg><use href="#i-play"/></svg><span>Start</span>`;
  start.disabled = !q.running && !q.ready;
  const pause = $("#btn-pause");
  pause.disabled = !q.running;
  pause.innerHTML = q.paused ? `<svg><use href="#i-play"/></svg><span>Resume</span>` : `<svg><use href="#i-pause"/></svg><span>Pause</span>`;
  $("#select-all").checked = state.jobs.size > 0 && state.selected.size === state.jobs.size;
  $("#settings-target").textContent = state.selected.size ? `${state.selected.size} selected` : "for new files";
  $("#btn-apply-sel").disabled = state.selected.size === 0;
  $("#btn-apply-sel").textContent = state.selected.size ? `Apply to ${state.selected.size} selected` : "Apply to selected";
}

$("#jobs").addEventListener("click", guard(async e => {
  const card = e.target.closest(".job"); if (!card) return;
  const id = card.dataset.id;
  if (e.target.classList.contains("sel")) {
    e.target.checked ? state.selected.add(id) : state.selected.delete(id);
    card.classList.toggle("selected", e.target.checked);
    renderControls();
    return;
  }
  const a = e.target.closest("button")?.dataset.a;
  if (!a) return;
  if (a === "remove") await post("/api/jobs/remove", { ids: [id] });
  if (a === "reset") await post("/api/jobs/reset", { ids: [id] });
  if (a === "up" || a === "down") await post(`/api/jobs/${id}/move`, { delta: a === "up" ? -1 : 1 });
  if (a === "log") showText("Log — " + state.jobs.get(id).fileName, await api("GET", `/api/jobs/${id}/log`) || "(empty — the log fills while the file is processed)");
  if (a === "cmd") showText("FFmpeg command", await api("GET", `/api/jobs/${id}/command`));
}));

$("#select-all").onchange = e => {
  state.selected = e.target.checked ? new Set(state.jobs.keys()) : new Set();
  $$(".job").forEach(el => { el.classList.toggle("selected", e.target.checked); $(".sel", el).checked = e.target.checked; });
  renderControls();
};
$("#btn-remove-sel").onclick = guard(() => post("/api/jobs/remove", { ids: [...state.selected] }));
$("#btn-reset-sel").onclick = guard(() => post("/api/jobs/reset", { ids: [...state.selected] }));
$("#btn-clear-done").onclick = guard(() => post("/api/jobs/clear", { statuses: ["Done", "Warning"] }));
$("#btn-start").onclick = guard(async () => post(state.queue?.running ? "/api/queue/stop" : "/api/queue/start"));
$("#btn-pause").onclick = guard(async () => post(state.queue?.paused ? "/api/queue/resume" : "/api/queue/pause"));

// ---------------------------------------------------------------- text dialog
function showText(title, text) {
  $("#text-title").textContent = title;
  $("#text-body").textContent = text;
  $("#dlg-text").showModal();
}
$("#btn-copy").onclick = async () => {
  try { await navigator.clipboard.writeText($("#text-body").textContent); toast("Copied", "ok"); }
  catch { toast("Clipboard not available over plain HTTP — select and copy manually.", "err"); }
};

// ---------------------------------------------------------------- file browser
async function browse(path) {
  const r = await api("GET", "/api/browse" + (path ? "?path=" + encodeURIComponent(path) : ""));
  state.browse.path = r.path;
  state.browse.selected.clear();
  const crumbs = [`<button type="button" data-p="">Roots</button>`];
  if (r.path) {
    const root = r.roots.find(x => r.path === x || r.path.startsWith(x.endsWith("/") || x.endsWith("\\") ? x : x + (x.includes("\\") ? "\\" : "/"))) ?? "";
    crumbs.push(`<button type="button" data-p="${esc(root)}">${esc(root)}</button>`);
    const rest = r.path.slice(root.length).split(/[\\/]/).filter(Boolean);
    let acc = root;
    for (const part of rest) {
      acc = acc + (acc.endsWith("/") || acc.endsWith("\\") ? "" : (root.includes("\\") ? "\\" : "/")) + part;
      crumbs.push(`<button type="button" data-p="${esc(acc)}">${esc(part)}</button>`);
    }
  }
  $("#crumbs").innerHTML = crumbs.join(`<span class="muted">›</span>`);
  const list = r.entries.map(e => `
    <div class="entry ${e.isDirectory ? "dir" : "file"}" data-p="${esc(e.path)}" data-dir="${e.isDirectory}">
      <input type="checkbox" ${r.path === null ? "disabled" : ""}>
      <svg><use href="#${e.isDirectory ? "i-folder" : "i-film"}"/></svg>
      <span class="name">${esc(e.name)}</span>
      <span class="size">${e.isDirectory ? "" : fmtSize(e.size)}</span>
    </div>`).join("");
  $("#browse-list").innerHTML = list || `<p class="muted" style="padding:16px">No folders or video files here.</p>`;
  $("#btn-add-current").disabled = !r.path;
  updateBrowseSel();
}
function updateBrowseSel() {
  const n = state.browse.selected.size;
  $("#browse-sel").textContent = n ? `${n} selected` : "Tick files or folders, or open a folder";
  $("#btn-add-selected").disabled = n === 0;
}
$("#crumbs").onclick = guard(async e => { const b = e.target.closest("button"); if (b) await browse(b.dataset.p || null); });
$("#browse-list").onclick = guard(async e => {
  const row = e.target.closest(".entry"); if (!row) return;
  const p = row.dataset.p;
  if (e.target.matches("input")) {
    e.target.checked ? state.browse.selected.add(p) : state.browse.selected.delete(p);
    updateBrowseSel();
  } else if (row.dataset.dir === "true") {
    await browse(p);
  } else {
    const cb = $("input", row); cb.checked = !cb.checked;
    cb.checked ? state.browse.selected.add(p) : state.browse.selected.delete(p);
    updateBrowseSel();
  }
});
async function addPaths(paths) {
  const r = await post("/api/jobs", { paths, settings: formSettings(), extensions: state.settings.folderExtensions });
  toast(r.added ? `Added ${r.added} file(s)` : "No supported video files found", r.added ? "ok" : "err");
  if (r.added) $("#dlg-browse").close();
}
$("#btn-add-selected").onclick = guard(() => addPaths([...state.browse.selected]));
$("#btn-add-current").onclick = guard(() => addPaths([state.browse.path]));
const openBrowser = guard(async () => { await browse(state.browse.path); $("#dlg-browse").showModal(); });
$("#btn-add").onclick = openBrowser;
$("#btn-add-empty").onclick = openBrowser;

// ---------------------------------------------------------------- settings dialog
function renderToolsBox() {
  const t = state.tools;
  $("#tools-box").innerHTML = t.ffmpeg
    ? `<b>FFmpeg</b> ${esc(t.ffmpeg)}<br><b>FFprobe</b> ${esc(t.ffprobe)}<br><b>SCXviD</b> ${esc(t.scxvid ?? "not found (keyframes use FFmpeg scene detection)")}`
    : `<b style="color:var(--err)">FFmpeg not found.</b> ${esc(t.instructions)}`;
  $("#btn-download").hidden = !t.canDownload;
}
$("#btn-settings").onclick = guard(async () => {
  const s = state.settings;
  $("#s-output").value = s.outputDirectory ?? "";
  fillSelect($("#s-post"), state.options.postActions, s.postAction);
  $("#s-post").disabled = !state.powerActionsAllowed;
  $("#s-post-note").textContent = state.powerActionsAllowed ? "" : "Power actions are disabled on this server (TNA_ALLOW_POWER_ACTIONS).";
  $("#s-vaapi").value = s.vaapiDevice ?? "";
  $("#s-ffmpeg").value = s.ffmpegPath ?? "";
  $("#s-extra").value = s.extraArguments ?? "";
  const info = await api("GET", "/api/info");
  state.advancedAllowed = info.advancedAllowed;
  $$("#s-advanced input").forEach(i => i.disabled = !info.advancedAllowed);
  $("#s-adv-note").textContent = info.advancedAllowed ? "" : "Locked: set TNA_ALLOW_ADVANCED=true on the server to edit these from the browser.";
  $("#s-ext").innerHTML = state.options.extensions.map(x =>
    `<label><input type="checkbox" value="${x}" ${s.folderExtensions.includes(x) ? "checked" : ""}>${x}</label>`).join("");
  renderToolsBox();
  $("#dlg-settings").showModal();
});
$("#btn-save-settings").onclick = guard(async () => {
  const body = {
    outputDirectory: $("#s-output").value.trim(),
    postAction: $("#s-post").value,
    vaapiDevice: $("#s-vaapi").value.trim(),
    folderExtensions: $$("#s-ext input:checked").map(i => i.value),
  };
  if (state.advancedAllowed) { body.ffmpegPath = $("#s-ffmpeg").value.trim(); body.extraArguments = $("#s-extra").value; }
  const r = await api("PUT", "/api/settings", body);
  state.settings = r.settings; state.tools = r.tools;
  $("#dlg-settings").close();
  toast("Settings saved", "ok");
});
$("#btn-download").onclick = guard(async () => {
  const b = $("#btn-download"); b.disabled = true;
  try { state.tools = await post("/api/ffmpeg/download"); renderToolsBox(); toast("FFmpeg installed", "ok"); }
  finally { b.disabled = false; b.textContent = "Download FFmpeg"; }
});

// ---------------------------------------------------------------- live events
function connect() {
  const es = new EventSource("/api/events");
  es.onmessage = ev => {
    const { type, payload } = JSON.parse(ev.data);
    if (type === "job") {
      state.jobs.set(payload.id, payload);
      if (!state.order.includes(payload.id)) { state.order.push(payload.id); renderQueue(); }
      renderJob(payload);
      renderControls();
    } else if (type === "removed") {
      state.jobs.delete(payload.id); state.selected.delete(payload.id);
      state.order = state.order.filter(x => x !== payload.id);
      renderQueue();
    } else if (type === "queue") {
      state.queue = payload.queue; state.order = payload.order;
      renderQueue();
    } else if (type === "download") {
      $("#btn-download").textContent = `Downloading… ${payload.percent}%`;
    } else if (type === "detect") {
      $("#btn-detect").textContent = payload.line.trim().slice(0, 28);
    }
  };
  es.onerror = () => { /* EventSource retries by itself */ };
  es.onopen = () => load().catch(() => {}); // resync after reconnects
}

async function load() {
  const s = await api("GET", "/api/state");
  state.options = s.options; state.settings = s.settings; state.tools = s.tools; state.queue = s.queue;
  state.powerActionsAllowed = s.powerActionsAllowed;
  state.jobs = new Map(s.jobs.map(j => [j.id, j]));
  state.order = s.jobs.map(j => j.id);
  if (!state.form) state.form = { ...s.settings.defaults };
  $("#platform").textContent = `v${s.version} · ${s.platform}`;
  applyTheme(s.settings.theme ?? "System");
  $("#jobs").innerHTML = "";
  renderForm(); renderHardware(); renderQueue();
  if (!s.tools.ffmpeg) toast("FFmpeg not found — open Settings.", "err");
}

try { applyTheme(localStorage.getItem("tna-theme") ?? "System"); } catch { /* ignore */ }
load().then(connect).catch(e => toast("Cannot reach the TnA server: " + e.message, "err"));
