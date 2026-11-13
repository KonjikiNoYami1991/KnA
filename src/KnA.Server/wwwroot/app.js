// KnA web UI — no framework, no build step, no external requests.
import { $, $$, esc, state, t, api, post, toast, guard, fmtTime, fmtSize, label, profileOf, fillSelect, showText, capability, loadI18n, translatePage, lang } from "./core.js";
import { renderSettingsForm, cloneSettings } from "./form.js";
import { openEditor, editorJobChanged } from "./editor.js";

// ---------------------------------------------------------------- theme
function applyTheme(theme) {
  const root = document.documentElement;
  if (theme === "Light") root.dataset.theme = "light";
  else if (theme === "Dark") root.dataset.theme = "dark";
  else delete root.dataset.theme;
  const dark = theme === "Dark" || (theme !== "Light" && matchMedia("(prefers-color-scheme: dark)").matches);
  $("#btn-theme use").setAttribute("href", dark ? "#i-sun" : "#i-moon");
  try { localStorage.setItem("kna-theme", theme); } catch { /* private mode */ }
}
$("#btn-theme").onclick = () => {
  const cur = document.documentElement.dataset.theme;
  const dark = cur === "dark" || (!cur && matchMedia("(prefers-color-scheme: dark)").matches);
  const next = dark ? "Light" : "Dark";
  applyTheme(next);
  state.settings.theme = next;
  api("PUT", "/api/settings", { theme: next }).catch(() => {});
};

// ---------------------------------------------------------------- side panel: presets + quick form
function renderPresets() {
  const sel = $("#f-preset");
  const cur = state.form.presetName ?? "";
  sel.innerHTML = `<option value="">${esc(t("— custom —"))}</option>` +
    `<optgroup label="${esc(t("Built-in"))}">${state.presets.filter(p => p.builtIn).map(p => `<option value="${esc(p.name)}">${esc(p.name)}</option>`).join("")}</optgroup>` +
    (state.presets.some(p => !p.builtIn) ? `<optgroup label="${esc(t("My presets"))}">${state.presets.filter(p => !p.builtIn).map(p => `<option value="${esc(p.name)}">${esc(p.name)}</option>`).join("")}</optgroup>` : "");
  sel.value = state.presets.some(p => p.name === cur) ? cur : "";
  const preset = state.presets.find(p => p.name === sel.value);
  $("#btn-delete-preset").disabled = !preset || preset.builtIn;
  sel.title = preset?.description ?? "";
}
$("#f-preset").onchange = e => {
  const p = state.presets.find(x => x.name === e.target.value);
  if (p) state.form = { ...cloneSettings(p.settings), presetName: p.name };
  else state.form.presetName = null;
  renderForm();
};
$("#btn-save-preset").onclick = guard(async () => {
  const name = prompt(t("Preset name"), state.form.presetName && !state.presets.find(p => p.name === state.form.presetName)?.builtIn ? state.form.presetName : "");
  if (!name) return;
  const p = await post("/api/presets", { name, description: null, settings: state.form });
  state.form.presetName = p.name;
  toast(t("Preset \"{0}\" saved", p.name), "ok");
});
$("#btn-delete-preset").onclick = guard(async () => {
  const name = $("#f-preset").value;
  if (!name || !confirm(t("Delete the preset \"{0}\"?", name))) return;
  await api("DELETE", "/api/presets/" + encodeURIComponent(name));
  state.form.presetName = null;
});

function renderForm() {
  renderSettingsForm($("#quick-form"), state.form, { onChange: () => { state.form.presetName = null; renderPresets(); } });
  renderPresets();
  renderControls();
}
$("#btn-save-defaults").onclick = guard(async () => {
  const r = await api("PUT", "/api/settings", { defaults: state.form });
  state.settings = r.settings; toast(t("Saved as default"), "ok");
});
$("#btn-apply-sel").onclick = guard(async () => {
  await post("/api/jobs/settings", { ids: [...state.selected], settings: state.form });
  toast(t("Updated {0} file(s)", state.selected.size), "ok");
});

// ---------------------------------------------------------------- wizard
const wiz = { step: 0, a: {} };
const wizSteps = () => [
  { key: "goal", title: t("What is the result for?"), choices: [
    ["Archive", t("Archive / collection"), t("MKV, best quality per MB, subtitles kept")],
    ["Tv", t("TV or Blu-ray player"), t("H.264 that every player reads")],
    ["Web", t("Web / streaming"), t("MP4 with fast start")],
    ["Phone", t("Phone / tablet"), t("Small 720p files")],
    ["OldPlayer", t("Old DivX player"), t("XviD + MP3 in AVI")],
    ["Editing", t("Timing / typesetting"), t("Workraw for Aegisub")],
    ["Images", t("Images"), t("Frames as PNG/JPEG…")],
    ["Extract", t("Extract tracks"), t("Demux without re-encoding")]] },
  { key: "codec", title: t("Compatibility or size?"), skip: a => ["Tv", "Phone", "OldPlayer", "Editing", "Images", "Extract"].includes(a.goal), choices: [
    ["Compatible", "H.264", t("Plays everywhere, biggest files")],
    ["Balanced", "HEVC", t("Half the size of H.264, very compatible")],
    ["Smallest", "AV1", t("Smallest files, newer devices")]] },
  { key: "speed", title: t("Speed or compression?"), skip: a => ["OldPlayer", "Images", "Extract"].includes(a.goal), choices: [
    ["BestQuality", t("Best compression (CPU)"), t("Slower, smaller files")],
    ["Fast", t("Fast (GPU)"), t("Uses a working hardware encoder when available")]] },
  { key: "quality", title: t("Quality"), skip: a => ["Images", "Extract"].includes(a.goal),
    choices: state.options.qualities.map(q => [q.value, q.label, ""]) },
  { key: "subtitles", title: t("Subtitles"), skip: a => ["Editing", "Extract"].includes(a.goal), choices: [
    ["Hardsub", t("Burn into the picture"), t("Always visible, any player")],
    ["Softsub", t("Keep as tracks"), t("Can be turned off, needs MKV")],
    ["None", t("No subtitles"), ""]] },
  { key: "audio", title: t("Audio"), skip: a => ["Images", "Extract"].includes(a.goal), choices: [
    ["Keep", t("Keep as it is"), t("Copied when the format allows it")],
    ["Stereo", t("Stereo"), t("Downmix surround")],
    ["Normalize", t("Normalize loudness"), t("Even volume between episodes")]] },
  { key: "name", title: t("Name your preset"), input: true },
];
function renderWizard() {
  const steps = wizSteps().filter(s => !s.skip?.(wiz.a));
  wiz.step = Math.min(wiz.step, steps.length - 1);
  const s = steps[wiz.step];
  $("#wiz-step").textContent = `${wiz.step + 1} / ${steps.length}`;
  $("#wiz-back").disabled = wiz.step === 0;
  $("#wiz-next").textContent = wiz.step === steps.length - 1 ? t("Create preset") : t("Next");
  $("#wiz-body").innerHTML = `<h3 style="margin-top:0">${esc(s.title)}</h3>` + (s.input
    ? `<input id="wiz-name" value="${esc(wiz.a.name ?? "")}" placeholder="${esc(t("e.g. Anime 1080p AV1"))}">`
    : `<div class="choices">${s.choices.map(([v, l, d]) => `<button type="button" class="choice ${wiz.a[s.key] === v ? "on" : ""}" data-v="${esc(v)}"><b>${esc(l)}</b><span>${esc(d)}</span></button>`).join("")}</div>`);
  $("#wiz-body").onclick = e => {
    const b = e.target.closest(".choice"); if (!b) return;
    wiz.a[s.key] = b.dataset.v; renderWizard();
  };
  wiz.current = s; wiz.count = steps.length;
}
$("#btn-wizard").onclick = () => { wiz.step = 0; wiz.a = { goal: "Archive", codec: "Balanced", speed: "BestQuality", quality: "Normal", subtitles: "Hardsub", audio: "Keep" }; renderWizard(); $("#dlg-wizard").showModal(); };
$("#wiz-back").onclick = () => { wiz.step--; renderWizard(); };
$("#wiz-next").onclick = guard(async () => {
  if (wiz.current.input) wiz.a.name = $("#wiz-name").value.trim();
  if (wiz.step < wiz.count - 1) { wiz.step++; renderWizard(); return; }
  if (!wiz.a.name) throw new Error(t("The preset needs a name."));
  const settings = await post("/api/presets/wizard", {
    goal: wiz.a.goal, codec: wiz.a.codec, speed: wiz.a.speed, quality: wiz.a.quality, resolution: 0, subtitles: wiz.a.subtitles, audio: wiz.a.audio, hdr: "Auto",
  });
  const p = await post("/api/presets", { name: wiz.a.name, description: null, settings });
  state.form = { ...cloneSettings(p.settings), presetName: p.name };
  $("#dlg-wizard").close();
  renderForm();
  toast(t("Preset \"{0}\" saved", p.name), "ok");
});

// ---------------------------------------------------------------- hardware
function renderHardware() {
  const caps = state.settings.capabilities, el = $("#hw-table");
  if (!caps) { el.innerHTML = ""; $("#hw-info").textContent = t("Not detected yet."); return; }
  const codecs = ["H264", "HEVC", "AV1"];
  let html = `<div></div>` + codecs.map(c => `<div class="h">${c === "H264" ? "H.264" : c}</div>`).join("");
  for (const b of state.options.backends.map(x => x.value)) {
    const rows = codecs.map(c => caps.encoders.find(x => x.codec === c && x.backend === b));
    if (rows.every(r => !r?.compiled) && b !== "Software") continue;
    html += `<div class="b">${esc(label(state.options.backends, b).replace(/\s*\(.*\)/, "").replace(/^(Apple|NVIDIA|Intel|AMD) /, ""))}</div>`;
    for (const e of rows) {
      if (!e) { html += `<div class="hw-cell na">—</div>`; continue; }
      const txt = e.works10Bit ? "10-bit" : e.works8Bit ? "8-bit" : "✕";
      html += `<div class="hw-cell ${e.works8Bit || e.works10Bit ? "ok" : ""}" title="${esc(e.encoder + (e.error ? ": " + e.error : ""))}">${txt}</div>`;
    }
  }
  el.innerHTML = html;
  $("#hw-info").textContent = `${caps.version.replace("ffmpeg version ", "FFmpeg ").split(" Copyright")[0]} · libass ${caps.hasLibass ? "✓" : "✕"} · zscale ${caps.hasZscale ? "✓" : "✕"} · libplacebo ${caps.hasLibplacebo ? "✓" : "✕"}`;
}
$("#btn-detect").onclick = guard(async () => {
  const b = $("#btn-detect"); b.disabled = true; b.textContent = t("Testing…");
  try {
    state.settings.capabilities = await post("/api/hardware/detect");
    renderHardware(); renderForm();
    toast(t("Hardware detection finished"), "ok");
  } finally { b.disabled = false; b.textContent = t("Detect again"); }
});

// ---------------------------------------------------------------- queue rendering
const statusClass = s => s.toLowerCase();
const statusText = s => ({ Ready: t("Ready"), Running: t("Running"), Paused: t("Paused"), Done: t("Done"), Warning: t("Warning"), Error: t("Error"), Stopped: t("Stopped") }[s] ?? s);

function jobHtml(j) {
  const s = j.settings, p = profileOf(s.profile);
  const chips = [`<span class="chip">${esc(p.label)}</span>`];
  if (s.presetName) chips.unshift(`<span class="chip hw">★ ${esc(s.presetName)}</span>`);
  if (p.codecs.length > 1) chips.push(`<span class="chip">${esc(label(state.options.codecs, s.codec).split(" /")[0])}</span>`);
  if (p.encodesVideo && s.backend !== "Software" && p.allowsHardware) chips.push(`<span class="chip hw">${esc(label(state.options.backends, s.backend))}</span>`);
  if (j.label.includes("10bit")) chips.push(`<span class="chip bit">10-bit</span>`);
  if (p.usesResolution) chips.push(`<span class="chip">${esc(label(state.options.resolutions, s.resolution))}</span>`);
  if (p.usesQuality) chips.push(`<span class="chip">${esc(label(state.options.qualities, s.quality))}</span>`);
  if (p.usesSubtitleMode && !j.edit.tracks) chips.push(`<span class="chip">${esc(label(state.options.subtitleModes, s.subtitles))}</span>`);
  if (s.chunks?.enabled && p.encodesVideo) chips.push(`<span class="chip hw">${esc(t("Chunked ×{0}", s.chunks.workers))}</span>`);
  if (j.isEdited) chips.push(`<span class="chip bit">✎ ${esc(t("edited"))}</span>`);
  if (j.edit.trimStart != null || j.edit.trimEnd != null) chips.push(`<span class="chip">✂ ${fmtTime(j.edit.trimStart ?? 0)}–${j.edit.trimEnd != null ? fmtTime(j.edit.trimEnd) : t("end")}</span>`);

  const running = j.status === "Running" || j.status === "Paused";
  const showBar = running || j.percent > 0;
  const stats = running ? `
      <div class="stats">
        <span>${esc(j.stage ?? "")}</span>
        <span><b>${fmtTime(j.positionSeconds)}</b> / ${fmtTime(j.durationSeconds)}</span>
        <span><b>${j.fps ? j.fps.toFixed(0) : "–"}</b> fps</span>
        <span><b>${j.speed ? j.speed.toFixed(2) + "×" : "–"}</b></span>
        <span>ETA <b>${fmtTime(j.remainingSeconds)}</b></span>
        <span>${esc(t("size"))} <b>${fmtSize(j.size)}</b>${j.estimatedSize ? " → ~" + fmtSize(j.estimatedSize) : ""}</span>
        <span>${esc(t("elapsed"))} <b>${fmtTime(j.elapsedSeconds)}</b></span>
      </div>` : (j.elapsedSeconds && !running ? `<div class="stats"><span>${esc(t("took"))} <b>${fmtTime(j.elapsedSeconds)}</b></span>${j.outputPath ? `<span title="${esc(j.outputPath)}">→ ${esc(j.outputPath.split(/[\\/]/).pop())}</span>` : ""}</div>` : "");

  const msgKind = j.status === "Error" || j.status === "Stopped" ? "err" : j.status === "Warning" ? "warn" : "";
  return `
    <input type="checkbox" class="sel" ${state.selected.has(j.id) ? "checked" : ""} aria-label="${esc(t("Select"))}">
    <div>
      <div class="title" title="${esc(j.sourcePath)}">${esc(j.fileName)}</div>
      <div class="folder" title="${esc(j.folder)}">&lrm;${esc(j.folder)}</div>
    </div>
    <div class="right">
      <span class="badge ${statusClass(j.status)}">${esc(statusText(j.status))}</span>
      <div class="actions">
        <button class="btn ghost icon sm" data-a="edit" title="${esc(t("Edit tracks, trim, filters, preview"))}" ${running ? "disabled" : ""}><svg><use href="#i-edit"/></svg></button>
        <button class="btn ghost icon sm" data-a="up" title="${esc(t("Move up"))}" ${running ? "disabled" : ""}><svg><use href="#i-up"/></svg></button>
        <button class="btn ghost icon sm" data-a="down" title="${esc(t("Move down"))}" ${running ? "disabled" : ""}><svg><use href="#i-down"/></svg></button>
        <button class="btn ghost icon sm" data-a="cmd" title="${esc(t("FFmpeg command"))}"><svg><use href="#i-term"/></svg></button>
        <button class="btn ghost icon sm" data-a="log" title="${esc(t("Log"))}"><svg><use href="#i-log"/></svg></button>
        <button class="btn ghost icon sm" data-a="reset" title="${esc(t("Process again"))}" ${running || j.status === "Ready" ? "disabled" : ""}><svg><use href="#i-redo"/></svg></button>
        <button class="btn ghost icon sm" data-a="remove" title="${esc(t("Remove"))}" ${running ? "disabled" : ""}><svg><use href="#i-trash"/></svg></button>
      </div>
    </div>
    <div class="meta">${chips.join("")}</div>
    ${j.mediaSummary ? `<div class="media">${esc(j.mediaSummary)}${j.hdrSummary && j.hdrSummary !== "SDR" ? " · " + esc(j.hdrSummary) : ""}</div>` : j.probeError ? `<div class="media err">${esc(j.probeError)}</div>` : `<div class="media">${esc(t("Analysing…"))}</div>`}
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
    container.append(el);
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
  $("#queue-text").textContent = q.paused ? t("Paused") : q.running
    ? `${t("Encoding")} ${q.done + 1}/${q.total}${cur ? " · " + Math.round(cur.percent) + "%" : ""}`
    : q.total ? t("{0} ready · {1} done", q.ready, q.done) + (q.failed ? " · " + t("{0} failed", q.failed) : "") : t("Idle");
  document.title = q.running && cur ? `${Math.round(cur.percent)}% · KnA` : "KnA · Konjiki no Autoencoder";

  const start = $("#btn-start");
  start.className = "btn " + (q.running ? "danger" : "primary");
  start.innerHTML = q.running ? `<svg><use href="#i-stop"/></svg><span>${esc(t("Stop"))}</span>` : `<svg><use href="#i-play"/></svg><span>${esc(t("Start"))}</span>`;
  start.disabled = !q.running && !q.ready;
  const pause = $("#btn-pause");
  pause.disabled = !q.running;
  pause.innerHTML = q.paused ? `<svg><use href="#i-play"/></svg><span>${esc(t("Resume"))}</span>` : `<svg><use href="#i-pause"/></svg><span>${esc(t("Pause"))}</span>`;
  $("#select-all").checked = state.jobs.size > 0 && state.selected.size === state.jobs.size;
  $("#settings-target").textContent = state.selected.size ? t("{0} selected", state.selected.size) : t("for new files");
  $("#btn-apply-sel").disabled = state.selected.size === 0;
  $("#btn-apply-sel").textContent = state.selected.size ? t("Apply to {0} selected", state.selected.size) : t("Apply to selected");
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
  if (a === "edit") return openEditor(id);
  if (a === "remove") await post("/api/jobs/remove", { ids: [id] });
  if (a === "reset") await post("/api/jobs/reset", { ids: [id] });
  if (a === "up" || a === "down") await post(`/api/jobs/${id}/move`, { delta: a === "up" ? -1 : 1 });
  if (a === "log") showText(t("Log") + " — " + state.jobs.get(id).fileName, await api("GET", `/api/jobs/${id}/log`) || t("(empty — the log fills while the file is processed)"));
  if (a === "cmd") showText(t("FFmpeg command"), await api("GET", `/api/jobs/${id}/command`));
}));
$("#jobs").addEventListener("dblclick", e => {
  const card = e.target.closest(".job");
  if (card && !e.target.closest("button,input")) openEditor(card.dataset.id);
});

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
$("#btn-copy").onclick = async () => {
  try { await navigator.clipboard.writeText($("#text-body").textContent); toast(t("Copied"), "ok"); }
  catch { toast(t("Clipboard not available over plain HTTP — select and copy manually."), "err"); }
};

// ---------------------------------------------------------------- file browser (videos, or external files for the editor)
async function browse(path) {
  const ext = state.browse.external;
  const r = await api("GET", "/api/browse?" + new URLSearchParams({ ...(path ? { path } : {}), external: ext }));
  state.browse.path = r.path;
  state.browse.selected.clear();
  const crumbs = [`<button type="button" data-p="">${esc(t("Roots"))}</button>`];
  if (r.path) {
    const sep = r.path.includes("\\") ? "\\" : "/";
    const root = r.roots.find(x => r.path === x || r.path.startsWith(x.endsWith(sep) ? x : x + sep)) ?? "";
    crumbs.push(`<button type="button" data-p="${esc(root)}">${esc(root)}</button>`);
    let acc = root;
    for (const part of r.path.slice(root.length).split(/[\\/]/).filter(Boolean)) {
      acc = acc + (acc.endsWith(sep) ? "" : sep) + part;
      crumbs.push(`<button type="button" data-p="${esc(acc)}">${esc(part)}</button>`);
    }
  }
  $("#crumbs").innerHTML = crumbs.join(`<span class="muted">›</span>`);
  $("#browse-list").innerHTML = r.entries.map(e => `
    <div class="entry ${e.isDirectory ? "dir" : "file"}" data-p="${esc(e.path)}" data-dir="${e.isDirectory}">
      <input type="checkbox" ${r.path === null || (ext && e.isDirectory) ? "disabled" : ""}>
      <svg><use href="#${e.isDirectory ? "i-folder" : ext ? "i-file" : "i-film"}"/></svg>
      <span class="name">${esc(e.name)}</span>
      <span class="size">${e.isDirectory ? "" : fmtSize(e.size)}</span>
    </div>`).join("") || `<p class="muted" style="padding:16px">${esc(ext ? t("No audio, subtitle, font or video files here.") : t("No folders or video files here."))}</p>`;
  $("#btn-add-current").hidden = ext;
  $("#btn-add-current").disabled = !r.path;
  updateBrowseSel();
}
function updateBrowseSel() {
  const n = state.browse.selected.size;
  $("#browse-sel").textContent = n ? t("{0} selected", n) : t("Tick files or folders, or open a folder");
  $("#btn-add-selected").disabled = n === 0;
}
$("#crumbs").onclick = guard(async e => { const b = e.target.closest("button"); if (b) await browse(b.dataset.p || null); });
$("#browse-list").onclick = guard(async e => {
  const row = e.target.closest(".entry"); if (!row) return;
  const p = row.dataset.p;
  const cb = $("input", row);
  if (e.target.matches("input")) {
    e.target.checked ? state.browse.selected.add(p) : state.browse.selected.delete(p);
  } else if (row.dataset.dir === "true") {
    return browse(p);
  } else if (!cb.disabled) {
    cb.checked = !cb.checked;
    cb.checked ? state.browse.selected.add(p) : state.browse.selected.delete(p);
  }
  updateBrowseSel();
});
async function finishPick(paths) {
  if (state.browse.onPick) {
    const cb = state.browse.onPick; state.browse.onPick = null;
    $("#dlg-browse").close();
    return cb(paths);
  }
  const r = await post("/api/jobs", { paths, settings: state.form, extensions: state.settings.folderExtensions });
  toast(r.added ? t("Added {0} file(s)", r.added) : t("No supported video files found"), r.added ? "ok" : "err");
  if (r.added) $("#dlg-browse").close();
}
$("#btn-add-selected").onclick = guard(() => finishPick([...state.browse.selected]));
$("#btn-add-current").onclick = guard(() => finishPick([state.browse.path]));
const openBrowser = guard(async (external = false) => {
  state.browse.external = external;
  if (!external) state.browse.onPick = null;
  $("#browse-title").textContent = external ? t("Add external files") : t("Add from server");
  await browse(state.browse.path);
  $("#dlg-browse").showModal();
});
$("#btn-add").onclick = () => openBrowser(false);
$("#btn-add-empty").onclick = () => openBrowser(false);
document.addEventListener("kna:browse", e => openBrowser(!!e.detail?.external));
$("#dlg-browse").addEventListener("close", () => { state.browse.external = false; });

// ---------------------------------------------------------------- settings dialog
$("#s-tabs").onclick = e => {
  const b = e.target.closest("button[data-tab]"); if (!b) return;
  $$("#s-tabs button").forEach(x => x.classList.toggle("on", x === b));
  $$("#dlg-settings [data-pane]").forEach(p => p.hidden = p.dataset.pane !== b.dataset.tab);
  if (b.dataset.tab === "updates") loadComponents();
};
function renderToolsBox(info) {
  const tl = state.tools;
  $("#tools-box").innerHTML = tl.ffmpeg
    ? `<b>FFmpeg</b> ${esc(tl.ffmpeg)}<br><b>FFprobe</b> ${esc(tl.ffprobe)}<br><b>SCXviD</b> ${esc(tl.scxvid ?? t("not found (keyframes use FFmpeg scene detection)"))}<br><b>dovi_tool</b> ${esc(tl.doviTool ?? "–")}<br><b>hdr10plus_tool</b> ${esc(tl.hdr10PlusTool ?? "–")}`
    : `<b style="color:var(--err)">${esc(t("FFmpeg not found."))}</b> ${esc(tl.instructions)}`;
  if (info) $("#about-box").innerHTML = `<b>KnA ${esc(info.version)}</b> · ${esc(info.platform)}<br>${esc(t("Data folder"))}: ${esc(info.dataDirectory)}<br>${esc(t("Media roots"))}: ${esc(info.roots.join(", "))}`;
}
$("#btn-settings").onclick = guard(async () => {
  const s = state.settings;
  fillSelect($("#s-lang"), [{ value: "Auto", label: t("Automatic") }, { value: "it", label: "Italiano" }, { value: "en", label: "English" }], s.language ?? "Auto");
  fillSelect($("#s-theme"), [{ value: "System", label: t("Same as the system") }, { value: "Light", label: t("Light theme") }, { value: "Dark", label: t("Dark theme") }], s.theme ?? "System");
  $("#s-autoupd").checked = s.checkUpdatesOnStart;
  $("#s-restore").checked = s.restoreQueue;
  $("#s-output").value = s.outputDirectory ?? "";
  $("#s-template").value = s.outputNameTemplate ?? "{name} [{label}]";
  fillSelect($("#s-priority"), [{ value: "Normal", label: t("Normal") }, { value: "BelowNormal", label: t("Below normal") }, { value: "Idle", label: t("Lowest (idle)") }], s.priority);
  fillSelect($("#s-post"), state.options.postActions, s.postAction);
  $("#s-post").disabled = !state.powerActionsAllowed;
  $("#s-post-note").textContent = state.powerActionsAllowed ? "" : t("Power actions are disabled on this server (KNA_ALLOW_POWER_ACTIONS).");
  $("#s-stall").value = s.stallTimeoutMinutes;
  $("#s-keeplogs").value = s.keepLogs;
  $("#s-vaapi").value = s.vaapiDevice ?? "";
  $("#s-ffmpeg").value = s.ffmpegPath ?? "";
  $("#s-extra").value = s.extraArguments ?? "";
  $("#s-temp").value = s.tempDirectory ?? "";
  const info = await api("GET", "/api/info");
  state.advancedAllowed = info.advancedAllowed;
  $$("[data-pane=advanced] input").forEach(i => i.disabled = !info.advancedAllowed);
  $("#s-adv-note").textContent = info.advancedAllowed ? "" : t("Locked: set KNA_ALLOW_ADVANCED=true on the server to edit these from the browser.");
  $("#s-ext").innerHTML = state.options.extensions.map(x =>
    `<label><input type="checkbox" value="${x}" ${s.folderExtensions.includes(x) ? "checked" : ""}>${x}</label>`).join("");
  renderToolsBox(info);
  $("#dlg-settings").showModal();
});
$("#btn-save-settings").onclick = guard(async () => {
  const body = {
    language: $("#s-lang").value, theme: $("#s-theme").value,
    checkUpdatesOnStart: $("#s-autoupd").checked, restoreQueue: $("#s-restore").checked,
    outputDirectory: $("#s-output").value.trim(), outputNameTemplate: $("#s-template").value.trim(),
    priority: $("#s-priority").value, postAction: $("#s-post").value,
    stallTimeoutMinutes: Number($("#s-stall").value) || 10, keepLogs: Number($("#s-keeplogs").value) || 300,
    vaapiDevice: $("#s-vaapi").value.trim(),
    folderExtensions: $$("#s-ext input:checked").map(i => i.value),
  };
  if (state.advancedAllowed) Object.assign(body, { ffmpegPath: $("#s-ffmpeg").value.trim(), extraArguments: $("#s-extra").value, tempDirectory: $("#s-temp").value.trim() });
  const langChanged = body.language !== (state.settings.language ?? "Auto");
  const r = await api("PUT", "/api/settings", body);
  state.settings = r.settings; state.tools = r.tools;
  applyTheme(state.settings.theme);
  $("#dlg-settings").close();
  toast(t("Settings saved"), "ok");
  if (langChanged) location.reload();
});

// ---------------------------------------------------------------- updates & components
async function checkUpdates(quiet = false) {
  const box = $("#upd-box");
  try {
    const u = await api("GET", "/api/updates");
    box.classList.toggle("new", u.isNewer);
    box.innerHTML = `${esc(u.message)}${u.isNewer ? `<br><a href="${esc(u.url)}" target="_blank" rel="noopener">${esc(t("Release notes"))}</a><br><span class="muted small">${esc(u.howTo)}</span>` : ""}`;
    $("#update-badge").hidden = !u.isNewer;
    if (u.isNewer && quiet) toast(u.message, "ok");
  } catch (e) { if (!quiet) box.textContent = e.message; }
}
$("#btn-check-updates").onclick = guard(() => checkUpdates(false));

async function loadComponents() {
  const list = $("#comp-list");
  try {
    const comps = await api("GET", "/api/components");
    list.innerHTML = comps.map(c => `
      <div class="comp" data-id="${esc(c.id)}">
        <b>${esc(c.name)}</b>
        <button class="btn sm ${c.updateAvailable ? "primary" : ""}" type="button" data-install="${esc(c.id)}" ${c.canInstall ? "" : "disabled"}>
          ${esc(c.installedVersion ? (c.updateAvailable ? t("Update") : t("Reinstall")) : t("Install"))}</button>
        <span>${esc(c.installedVersion ?? t("not installed"))}${c.latestVersion ? ` · ${esc(t("latest"))}: ${esc(c.latestVersion)}` : ""}</span>
        <span class="note">${esc(c.description)}${c.note ? " — " + esc(c.note) : ""}</span>
      </div>`).join("");
  } catch (e) { list.textContent = e.message; }
}
$("#comp-list").onclick = guard(async e => {
  const b = e.target.closest("[data-install]"); if (!b) return;
  b.disabled = true; b.textContent = t("Downloading…");
  try {
    await post(`/api/components/${b.dataset.install}/install`);
    toast(t("{0} installed", b.dataset.install), "ok");
    state.tools = (await api("GET", "/api/state")).tools;
    renderToolsBox();
  } finally { loadComponents(); }
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
      editorJobChanged(payload);
      renderControls();
    } else if (type === "removed") {
      state.jobs.delete(payload.id); state.selected.delete(payload.id);
      state.order = state.order.filter(x => x !== payload.id);
      renderQueue();
    } else if (type === "queue") {
      state.queue = payload.queue; state.order = payload.order;
      renderQueue();
    } else if (type === "presets") {
      state.presets = payload; renderPresets();
    } else if (type === "component") {
      const b = $(`[data-install="${payload.id}"]`);
      if (b && payload.percent != null) b.textContent = `${t("Downloading…")} ${payload.percent}%`;
    } else if (type === "detect") {
      $("#btn-detect").textContent = payload.line.trim().slice(0, 28);
    }
  };
  es.onopen = () => load(false).catch(() => {});
}

async function load(first = true) {
  const s = await api("GET", "/api/state");
  state.options = s.options; state.settings = s.settings; state.tools = s.tools; state.queue = s.queue;
  state.powerActionsAllowed = s.powerActionsAllowed; state.presets = s.presets;
  state.jobs = new Map(s.jobs.map(j => [j.id, j]));
  state.order = s.jobs.map(j => j.id);
  if (!state.form) state.form = cloneSettings(s.settings.defaults);
  $("#platform").textContent = `v${s.version} · ${s.platform}`;
  applyTheme(s.settings.theme ?? "System");
  $("#jobs").innerHTML = "";
  renderForm(); renderHardware(); renderQueue();
  if (first && !s.tools.ffmpeg) toast(t("FFmpeg not found — open Settings."), "err");
  if (first && s.settings.checkUpdatesOnStart) checkUpdates(true);
}

try { applyTheme(localStorage.getItem("kna-theme") ?? "System"); } catch { /* ignore */ }
await loadI18n();
translatePage();
load().then(connect).catch(e => toast(t("Cannot reach the KnA server: {0}", e.message), "err"));
