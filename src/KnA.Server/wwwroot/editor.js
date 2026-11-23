// Per-file editor (the pencil): preset, visual trim, video (encoder, rate control, every parameter on sub-tabs),
// audio and subtitles (a tab view: one tab per output track, with +, ✕, ← → and duplicates), filters (picture, GPU, VapourSynth/AviSynth), output, preview.
// It works on explicit settings only: when it opens, the server writes the profile's automatic choices into the
// fields (/materialize); a profile or preset can only be imported. Every change is saved and re-validated (debounced).
// Mirrors KnA.App/Views/JobEditorWindow.cs.
import { $, $$, esc, state, t, api, post, toast, guard, fmtStamp, parseStamp, fmtTime, label, profileOf, fillSelect, debounce, showText } from "./core.js";
import { renderSettingsForm, cloneSettings, parsePairs, formatPairs } from "./form.js";
import { openAssistant, analyzeJob } from "./ai.js";
import { pickFiles } from "./files.js";
import { openOcr } from "./ocr.js";

let ed = null;

export async function openEditor(id, tab = "video") {
  const job = state.jobs.get(id);
  if (!job) return;
  // "trim-only": the cut as a standalone tool (keep a part, drop openings/endings) without the other tabs.
  $("#dlg-editor").classList.toggle("trim-only", tab === "trim-only");
  if (tab === "trim-only") tab = "trim";
  if (tab === "tracks") tab = "video";   // the tracks are now in the tab of their kind
  ed = { id, job, settings: cloneSettings(job.settings), edit: cloneSettings(job.edit), analysis: null, tab, playhead: job.edit.segments?.[0]?.start ?? job.edit.trimStart ?? 0, final: true, parts: null, seg: 0,
    videoSub: "general", trackSub: {} };
  $("#ed-title").textContent = job.fileName;
  $("#ed-sub").textContent = job.folder;
  $$("#ed-tabs button").forEach(b => b.classList.toggle("on", b.dataset.tab === tab));
  $("#ed-body").innerHTML = `<p class="muted">${esc(t("Loading…"))}</p>`;
  $("#ed-issues").innerHTML = "";
  $("#dlg-editor").showModal();
  try {
    await materializeNow({}, false);
    if (ed.playhead === 0) ed.playhead = Math.min(ed.analysis.files[0].duration / 3, 60);
    renderIssues();
    renderTab();
  } catch (e) { $("#ed-body").innerHTML = `<p class="issue err">${esc(e.message)}</p>`; }
}

$("#ed-close").onclick = () => $("#dlg-editor").close();
$("#ed-tabs").onclick = e => {
  const b = e.target.closest("button"); if (!b || !ed) return;
  ed.tab = b.dataset.tab;
  $$("#ed-tabs button").forEach(x => x.classList.toggle("on", x === b));
  renderTab();
};

// ---------------------------------------------------------------- saving
const tracks = () => ed.edit.tracks ?? ed.analysis.tracks;
function materialize() { if (!ed.edit.tracks) ed.edit.tracks = cloneSettings(ed.analysis.tracks); return ed.edit.tracks; }

const saveNow = async (rerender = false) => {
  if (!ed) return;
  const id = ed.id;
  $("#ed-saving").textContent = t("Saving…");
  try {
    const a = await api("PUT", `/api/jobs/${id}/edit`, { settings: ed.settings, edit: ed.edit });
    if (!ed || ed.id !== id) return;
    ed.analysis = a;
    if (ed.edit.tracks) ed.edit.tracks = a.tracks;
    renderIssues();
    if (rerender) renderTab();
    $("#ed-saving").textContent = t("Saved");
  } catch (e) { $("#ed-saving").textContent = ""; toast(e.message, "err"); }
};
const save = debounce(() => saveNow(false), 450);
const saveAndRender = () => saveNow(true);

/** Saves and lets the server write the profile's choices into the fields left to it (open, profile / preset import). */
async function materializeNow(extra = {}, render = true) {
  const id = ed.id;
  $("#ed-saving").textContent = t("Saving…");
  const r = await api("POST", `/api/jobs/${id}/materialize`, { settings: ed.settings, edit: ed.edit, ...extra });
  if (!ed || ed.id !== id) return;
  ed.settings = r.settings; ed.edit = r.edit; ed.analysis = r.analysis;
  $("#ed-saving").textContent = t("Saved");
  if (render) { renderIssues(); renderTab(); }
}

function renderIssues() {
  const a = ed.analysis, list = [...a.issues];
  const html = list.map(i => `<div class="issue ${i.isError ? "err" : "warn"}">${i.isError ? "✖" : "⚠"} ${esc(i.message)}</div>`).join("");
  const info = [a.container && `${t("Container")}: ${a.container.toUpperCase()}`, a.hdrSummary && a.hdrSummary !== "SDR" && `HDR: ${a.hdrSummary}`,
    a.outputDuration && `${t("Output duration")}: ${fmtTime(a.outputDuration)}`].filter(Boolean).join(" · ");
  $("#ed-issues").innerHTML = html + (info ? `<div class="issue info">${esc(info)}</div>` : "");
}

function renderTab() {
  const body = $("#ed-body");
  body.onchange = body.oninput = body.onclick = null;
  ed.prev = cloneSettings(ed.settings);
  const render = ({ preset: renderPresetTab, trim: renderTrim, video: renderVideo, audio: b => renderStreams(b, "audio"), subtitles: b => renderStreams(b, "subtitle"),
     filters: renderFilters, output: renderOutput, preview: renderPreview })[ed.tab] ?? renderVideo;
  render(body);
}

// ---------------------------------------------------------------- tracks
function streamInfo(tp) {
  const f = ed.analysis.files.find(x => x.path === tp.file);
  return f?.streams.find(s => s.index === tp.stream);
}
const isPgs = tp => ["hdmv_pgs_subtitle", "pgssub", "dvd_subtitle", "dvdsub"].includes(tp.codec ?? streamInfo(tp)?.codec);
const srcPath = () => ed.analysis.files[0].path;
const trackName = tp => `${tp.stream >= 0 ? "#" + tp.stream + " " : ""}${tp.codec ?? streamInfo(tp)?.codec ?? ""}${tp.language ? " " + tp.language : ""}${tp.file !== srcPath() ? " · " + tp.file.split(/[\\/]/).pop() : ""}`;

/**
 * The video tracks, the attachments (Output tab) and, for demux, the tracks to extract: tick to keep, order, remove
 * an added file. Video rows also carry their labels. Audio and subtitles of an output are tab views (trackTabs).
 */
function trackList(kind) {
  const list = tracks(), p = profileOf(ed.settings.profile);
  const rows = list.map((tp, i) => ({ tp, i })).filter(x => x.tp.kind === kind);
  const title = { video: t("Video tracks"), audio: t("Audio tracks"), subtitle: t("Subtitle tracks"), attachment: t("Attachments") }[kind];
  const add = kind === "attachment" ? `<button class="btn sm" data-a="add-att"><svg><use href="#i-plus"/></svg><span>${esc(t("Add attachments…"))}</span></button>`
    : `<button class="btn sm" data-a="add-ext"><svg><use href="#i-plus"/></svg><span>${esc(t("Add external files…"))}</span></button>
       <button class="btn sm" data-a="upload-ext"><svg><use href="#i-upload"/></svg><span>${esc(t("Upload from this device…"))}</span></button>`;
  const video = kind === "video";
  const externals = ed.edit.externalFiles ?? [];
  return `<div class="section"><h3 class="row" style="justify-content:space-between;flex-wrap:wrap"><span class="kind ${kind}">${esc(title)}</span><span class="row">${add}</span></h3>
    ${!rows.length ? `<p class="muted small">${esc(kind === "attachment" ? t("No attachments. Fonts, covers or any other file can be added.") : t("No tracks of this kind."))}</p>` : `
    <table class="tracks"><thead><tr><th>${esc(t("Use"))}</th><th>${esc(t("Stream"))}</th>
      ${video ? `<th>${esc(t("Language"))}</th><th class="hide-sm">${esc(t("Title"))}</th><th>${esc(t("Delay (ms)"))}</th>${p.encodesVideo || p.isRemux ? `<th>${esc(t("Output"))}</th>` : ""}` : ""}
<th></th></tr></thead><tbody>
    ${rows.map(({ tp, i }) => {
      const si = streamInfo(tp), standalone = kind === "attachment" && tp.stream < 0;
      const removable = standalone || (tp.file !== srcPath() && externals.includes(tp.file));
      return `<tr class="${tp.include || tp.burn ? "" : "off"}" data-i="${i}">
        <td><input type="checkbox" data-f="include" ${tp.include ? "checked" : ""}></td>
        <td title="${esc(tp.file)}"><span class="codec">${esc(standalone ? tp.title ?? tp.file.split(/[\\/]/).pop() : trackName(tp))}</span>${tp.title && !video && !standalone ? ` · ${esc(tp.title)}` : ""}<div class="details">${esc(si?.details ?? (standalone ? tp.codec ?? "" : ""))}</div></td>
        ${video ? `<td><input class="lang" data-f="language" value="${esc(tp.language ?? "")}" maxlength="3" placeholder="und"></td>
          <td class="hide-sm"><input data-f="title" value="${esc(tp.title ?? "")}"></td>
          <td><input class="delay" type="number" step="10" data-f="delayMs" value="${tp.delayMs || 0}"></td>
          ${p.encodesVideo || p.isRemux ? `<td><select data-f="output"><option value="" ${tp.output ? "" : "selected"}>${esc(t("Encode (Video tab settings)"))}</option><option value="copy" ${(tp.output ?? "").toLowerCase() === "copy" ? "selected" : ""}>${esc(t("Copy (no re-encoding)"))}</option></select></td>` : ""}` : ""}
        <td class="row"><button class="btn icon ghost sm" data-a="up" title="${esc(t("Move up"))}"><svg><use href="#i-up"/></svg></button>
          <button class="btn icon ghost sm" data-a="down" title="${esc(t("Move down"))}"><svg><use href="#i-down"/></svg></button>
          ${removable ? `<button class="btn icon ghost sm" data-a="rm-track" title="${esc(standalone ? t("Remove") : t("Remove the added file ({0})", tp.file.split(/[\\/]/).pop()))}"><svg><use href="#i-x"/></svg></button>` : ""}</td>
      </tr>`;
    }).join("")}</tbody></table>`}
    ${kind === "attachment" && !p.isDemux && rows.length ? `<p class="muted small">${esc(t("Untick an attachment of the file to leave it out; added files can be removed with ✕. Attachments need MKV."))}</p>` : ""}
    ${p.isDemux ? `<p class="muted small">${esc(t("Demux: the ticked tracks, attachments and chapters are extracted to separate files."))}</p>` : ""}
  </div>`;
}

/** Events of the track lists (any tab): returns true when handled. */
function trackListInput(e) {
  const tr = e.target.closest("tr[data-i]"), f = e.target.dataset.f;
  if (!tr || !f || e.target.type === "checkbox" || e.target.type === "radio" || e.target.tagName === "SELECT") return false;
  const tp = materialize()[+tr.dataset.i];
  tp[f] = f === "delayMs" ? Number(e.target.value) || 0 : e.target.value || null;
  save();
  return true;
}
function trackListChange(e) {
  const tr = e.target.closest("tr[data-i]"), f = e.target.dataset.f;
  if (!tr || !f || !(e.target.type === "checkbox" || e.target.type === "radio" || e.target.tagName === "SELECT")) return false;
  const l = materialize(), tp = l[+tr.dataset.i];
  if (e.target.tagName === "SELECT") tp[f] = e.target.value || null;
  else { tp[f] = e.target.checked; if (f === "include" && tp.include) tp.burn = false; }
  saveAndRender();
  return true;
}
async function trackListClick(b) {
  const a = b.dataset.a;
  if (a === "add-ext") { pickExternal(); return true; }
  if (a === "add-att") { pickAttachments(); return true; }
  if (a === "upload-ext") { await saveNow(); pickFiles(null, ed.id); return true; }
  const tr = b.closest("tr[data-i]"); if (!tr) return false;
  const l = materialize(), i = +tr.dataset.i, k = l[i].kind;
  if (a === "rm-track") {
    const file = l[i].file, standalone = k === "attachment" && l[i].stream < 0;
    if (standalone) l.splice(i, 1); else for (let j = l.length - 1; j >= 0; j--) if (l[j].file === file) l.splice(j, 1);
    if (!l.some(x => x.file === file)) ed.edit.externalFiles = (ed.edit.externalFiles ?? []).filter(x => x !== file);
    saveAndRender(); return true;
  }
  if (a === "up" || a === "down") {
    const same = l.map((x, j) => ({ x, j })).filter(o => o.x.kind === k).map(o => o.j);
    const target = same[same.indexOf(i) + (a === "up" ? -1 : 1)];
    if (target == null) return true;
    [l[i], l[target]] = [l[target], l[i]];
    renderTab(); save(); return true;
  }
  return false;
}

/** Language, title, delay, default and forced of a track: the first row of its page. */
function labelsHtml(tp) {
  return `<div class="grid4">
    <label class="field"><span>${esc(t("Language"))}</span><input data-tf="language" value="${esc(tp.language ?? "")}" maxlength="3" placeholder="und"></label>
    <label class="field"><span>${esc(t("Title"))}</span><input data-tf="title" value="${esc(tp.title ?? "")}"></label>
    <label class="field"><span>${esc(t("Delay (ms)"))}</span><input type="number" step="10" data-tf="delayMs" value="${tp.delayMs || 0}"><small class="muted">${esc(t("Positive = later."))}</small></label>
    <div class="field"><span>&nbsp;</span><div class="row"><label class="check"><input type="checkbox" data-tf="default" ${tp.default ? "checked" : ""}>${esc(t("Default"))}</label>
      <label class="check"><input type="checkbox" data-tf="forced" ${tp.forced ? "checked" : ""}>${esc(t("Forced"))}</label></div></div>
  </div>`;
}
/** A change in a track page's fields (data-tf); returns true when handled. */
function trackFieldChange(e, i, rerenderOn = []) {
  const f = e.target.dataset.tf; if (!f) return false;
  const l = materialize(), tp = l[i];
  if (e.target.type === "checkbox") {
    tp[f] = e.target.checked;
    if (f === "default" && tp.default) l.forEach(x => { if (x !== tp && x.kind === tp.kind) x.default = false; });
  } else if (["delayMs", "bitrate", "channels"].includes(f)) tp[f] = e.target.value === "" || Number(e.target.value) === 0 && f !== "delayMs" ? (f === "delayMs" ? 0 : null) : Number(e.target.value);
  else tp[f] = e.target.value || null;
  if (rerenderOn.includes(f)) saveAndRender(); else save();
  return true;
}

// ---------------------------------------------------------------- track tab views (mirror of KnA.Core/Queue/TrackTabs.cs)
const isActive = tp => tp.include || tp.burn;
const activeOf = (l, kind) => l.map((tp, i) => ({ tp, i })).filter(x => x.tp.kind === kind && isActive(x.tp)).map(x => x.i);
const inactiveOf = (l, kind) => l.map((tp, i) => ({ tp, i })).filter(x => x.tp.kind === kind && !isActive(x.tp) && !x.tp.copy).map(x => x.i);
const audioShort = { Copy: "copy", Aac: "AAC", HeAac: "HE-AAC", Ac3: "AC-3", Eac3: "E-AC-3", Flac: "FLAC", Alac: "ALAC", TrueHd: "TrueHD", Dts: "DTS", Mp3: "MP3", Pcm16: "PCM 16", Pcm24: "PCM 24", Pcm32Float: "PCM 32f" };
function outputLabel(tp) {
  if (tp.kind === "subtitle") {
    if (tp.burn) return t("burned");
    const o = (tp.output ?? "copy").toLowerCase();
    return { copy: t("copy"), srt: "SRT", ass: "ASS", webvtt: "WebVTT", mov_text: "mov_text", ocr: "OCR" }[o] ?? o;
  }
  if (tp.kind !== "audio") return tp.output ?? "";
  const c = Object.keys(audioShort).concat(["Opus", "Vorbis", "WavPack"]).find(x => x.toLowerCase() === (tp.output ?? "").toLowerCase());
  if (!c) return t("automatic");
  return c === "Copy" ? t("copy") : audioShort[c] ?? c;
}
function tabLabel(tp) {
  let s = (tp.stream >= 0 ? "#" + tp.stream : "") + (tp.language ? " " + tp.language : "");
  if (tp.file !== srcPath()) s += " · " + tp.file.split(/[\\/]/).pop();
  s = s.trim() + " → " + outputLabel(tp);
  return s.length > 40 ? s.slice(0, 39) + "…" : s;
}
const langGroups = [["it", "ita"], ["en", "eng"], ["ja", "jpn"], ["fr", "fre", "fra"], ["de", "ger", "deu"], ["es", "spa"], ["pt", "por"], ["ru", "rus"], ["zh", "chi", "zho"],
  ["ko", "kor"], ["nl", "dut", "nld"], ["pl", "pol"], ["sv", "swe"], ["no", "nor", "nob", "nno"], ["da", "dan"], ["fi", "fin"], ["cs", "cze", "ces"], ["el", "gre", "ell"],
  ["tr", "tur"], ["ar", "ara"], ["he", "heb"], ["hu", "hun"], ["ro", "rum", "ron"], ["uk", "ukr"], ["hi", "hin"], ["th", "tha"], ["vi", "vie"], ["id", "ind"]];
function nativeName(code) {
  if (!code || code.toLowerCase() === "und") return null;
  const two = langGroups.find(g => g.includes(code.toLowerCase()))?.[0] ?? code.toLowerCase();
  try { const n = new Intl.DisplayNames([two], { type: "language" }).of(two); return n ? n[0].toLocaleUpperCase(two) + n.slice(1) : code; } catch { return code; }
}
function copyTitle(tp) {
  const name = nativeName(tp.language) ?? (tp.title || null), codec = outputLabel(tp);
  return name ? `${name} (${codec})` : codec;
}
/** A codec/format change on a copy: a title still written by KnA follows the new codec. */
function retitle(before, tp) {
  if (tp.copy > 0 && outputLabel(before) !== outputLabel(tp) && (!before.title || before.title === copyTitle(before))) tp.title = copyTitle(tp);
}
function duplicateTrack(l, i) {
  const tp = l[i];
  const copy = Math.max(...l.filter(x => x.file === tp.file && x.stream === tp.stream && x.kind === tp.kind).map(x => x.copy ?? 0)) + 1;
  const dup = { ...cloneSettings(tp), copy, include: true, burn: false, default: false };
  dup.title = copyTitle(dup);
  l.splice(i + 1, 0, dup);
  return i + 1;
}
function removeTrack(l, i) {
  if (l[i].copy > 0) l.splice(i, 1); else { l[i].include = false; l[i].burn = false; }
}
function activateTrack(l, i) {
  const [tp] = l.splice(i, 1);
  tp.include = true; tp.burn = false;
  const act = activeOf(l, tp.kind), at = act.length ? act[act.length - 1] + 1 : Math.min(i, l.length);
  l.splice(at, 0, tp);
  return at;
}
function moveTrack(l, i, delta) {
  const act = activeOf(l, l[i].kind), target = act[act.indexOf(i) + delta];
  if (target == null) return i;
  [l[i], l[target]] = [l[target], l[i]];
  return target;
}
const burnTrack = (l, i) => l.forEach((x, j) => { if (x.kind === "subtitle") { x.burn = j === i; if (j === i) x.include = false; } });

/**
 * The Audio / Subtitles tab view: one tab per active track (tabs even with a single track), "+" to add a track of
 * the file or of an added file, duplicate the current one or add external files; ✕ on a tab removes it; ← → order
 * the tracks (= order in the output file).
 */
function trackTabs(kind) {
  const l = tracks(), act = activeOf(l, kind), inact = inactiveOf(l, kind);
  const current = act.includes(ed.trackSub[kind]) ? ed.trackSub[kind] : act[0] ?? null;
  ed.trackSub[kind] = current;
  const menu = `<div class="track-add" hidden>${inact.map(i => { const tp = l[i]; return `<button type="button" data-a="tab-add" data-i="${i}">${esc(t("Add {0}", trackName(tp) + (tp.title ? " · " + tp.title : "") + (streamInfo(tp)?.details ? " · " + streamInfo(tp).details : "")))}</button>`; }).join("")}
    ${current != null ? `${inact.length ? "<hr>" : ""}<button type="button" data-a="tab-dup">${esc(t("Duplicate the current track"))}</button>` : ""}
    <hr><button type="button" data-a="add-ext">${esc(t("Add external files…"))}</button><button type="button" data-a="upload-ext">${esc(t("Upload from this device…"))}</button></div>`;
  const plus = `<span class="track-plus"><button type="button" class="btn sm" data-a="tab-plus" title="${esc(kind === "audio" ? t("Add an audio track") : t("Add a subtitle track"))}"><b>+</b></button>${menu}</span>`;
  if (current == null)
    return { html: `<p class="muted small">${esc(kind === "audio" ? t("No audio track in the output: add one with +.") : t("No subtitle track in the output: add one with +."))}</p>${plus}`, current: null };
  const nav = `<nav class="tabs subtabs track-tabs" data-subtabs="${kind}">${act.map(i => { const tp = l[i]; return `<button type="button" data-st="${i}" class="${i === current ? "on" : ""}" title="${esc(tp.file)}">${tp.burn ? "🔥 " : ""}${esc(tabLabel(tp))}<span class="tab-x" data-a="tab-rm" data-i="${i}" title="${esc(tp.copy > 0 ? t("Delete this copy") : t("Remove from the output (it can be added again with +)"))}">✕</span></button>`; }).join("")}${plus}</nav>`;
  const tp = l[current], pos = act.indexOf(current);
  const bar = `<div class="row track-bar" style="flex-wrap:wrap;margin-top:10px">
    <button class="btn sm ghost" data-a="tab-left" ${pos > 0 ? "" : "disabled"} title="${esc(t("Earlier in the output file"))}">←</button>
    <button class="btn sm ghost" data-a="tab-right" ${pos < act.length - 1 ? "" : "disabled"} title="${esc(t("Later in the output file"))}">→</button>
    <button class="btn sm" data-a="tab-dup" title="${esc(kind === "audio" ? t("The same source track once more, with its own settings (e.g. in Opus and in Vorbis).") : t("The same source track once more, with its own settings (e.g. as ASS and as SRT)."))}">${esc(t("Duplicate"))}</button>
    <span class="muted small">${esc(t("Track {0} of {1} in the output", pos + 1, act.length) + (tp.copy > 0 ? " · " + t("copy {0} of #{1}", tp.copy, tp.stream) : "") + " · " + trackName(tp) + (streamInfo(tp)?.details ? " · " + streamInfo(tp).details : ""))}</span></div>`;
  return { html: nav + bar, current: { tp, i: current } };
}

function pickAttachments() {
  state.browse.external = true;
  state.browse.mode = "all";
  state.browse.onPick = async paths => {
    const list = materialize();
    for (const f of paths) {
      if (list.some(x => x.kind === "attachment" && x.stream < 0 && x.file === f)) continue;
      const name = f.split(/[\\/]/).pop(), ext = (name.includes(".") ? name.split(".").pop() : "").toLowerCase();
      const entry = { file: f, stream: -1, kind: "attachment", include: true, title: name, codec: /^(ttf|otf|ttc|otc|woff2?)$/.test(ext) ? "font" : ext, delayMs: 0, default: false, forced: false, burn: false };
      let last = -1; list.forEach((x, j) => { if (x.kind === "attachment") last = j; });
      if (last < 0) list.push(entry); else list.splice(last + 1, 0, entry);
    }
    await saveNow(true);
  };
  document.dispatchEvent(new CustomEvent("kna:browse", { detail: { external: true } }));
}

function pickExternal() {
  state.browse.external = true;
  state.browse.mode = null;
  state.browse.onPick = async paths => {
    const files = paths.filter(p => !ed.edit.externalFiles.includes(p));
    ed.edit.externalFiles = [...ed.edit.externalFiles, ...files];
    await saveNow(true);
    toast(t("{0} file(s) added: tick the tracks to use.", files.length), "ok");
  };
  document.dispatchEvent(new CustomEvent("kna:browse", { detail: { external: true } }));
}

// ---------------------------------------------------------------- parameter pages
// Every parameter of a program, read from its own help: all the options of a page with their value field and
// description; ticking one sets it. Models keep the values: command-line text, or one FFmpeg filter's options.
const optionCache = new Map();
async function loadOptions(tool, s) {
  const key = tool + "|" + JSON.stringify([s.codec, s.backend, s.encoder?.program, s.profile, s.tenBit]);
  if (!optionCache.has(key)) optionCache.set(key, await api("POST", "/api/tools/options", { tool, settings: s }));
  return optionCache.get(key);
}
function cmdModel(get, set) {
  let pairs = parsePairs(get() ?? "");
  const emit = () => set(formatPairs(pairs) || null);
  return {
    get: n => { const p = pairs.find(x => x[0] === n); return p ? (p[1] ?? "") : null; },
    set: (n, v) => { const p = pairs.find(x => x[0] === n); if (p) p[1] = v; else pairs.push([n, v]); emit(); },
    unset: n => { pairs = pairs.filter(x => x[0] !== n); emit(); },
    others: known => formatPairs(pairs.filter(x => !known.has(x[0]))),
    setOthers: (known, text) => { pairs = [...pairs.filter(x => known.has(x[0])), ...parsePairs(text).filter(x => !known.has(x[0]))]; emit(); },
  };
}
function filterModel(call, onChange) {
  return {
    get: n => { const o = call.options.find(x => x[0] === n); return o ? o[1] : null; },
    set: (n, v) => { const o = call.options.find(x => x[0] === n); if (o) o[1] = v ?? ""; else call.options.push([n, v ?? ""]); onChange(); },
    unset: n => { call.options = call.options.filter(x => x[0] !== n); onChange(); },
    others: known => call.options.filter(x => !known.has(x[0])).map(([k, v]) => k ? `${k}=${v}` : v).join(":"),
    setOthers: (known, text) => { call.options = [...call.options.filter(x => known.has(x[0])), ...splitOpts(text).filter(x => !known.has(x[0]) && x[1] !== "")]; onChange(); },
  };
}

function optionRow(o, m) {
  const v = m.get(o.name), on = v !== null, flag = !o.argument;
  const init = flag ? "" : o.default ?? o.values?.[0] ?? "";
  const input = flag ? `<small class="muted">${esc(t("switch (no value)"))}</small>`
    : o.values?.length ? `<select data-pv ${on ? "" : "disabled"}>${[...new Set([...(v && !o.values.includes(v) ? [v] : []), ...o.values])].map(x => `<option ${x === (on ? v : init) ? "selected" : ""}>${esc(x)}</option>`).join("")}</select>`
    : `<input class="mono" data-pv value="${esc(v ?? "")}" placeholder="${esc(o.default ?? o.argument ?? "")}" ${on ? "" : "disabled"}>`;
  return `<div class="param-opt ${on ? "on" : ""}" data-name="${esc(o.name)}" data-init="${esc(init)}" data-flag="${flag ? 1 : ""}">
    <label class="check"><input type="checkbox" data-pset ${on ? "checked" : ""}><code>${esc(o.name)}${o.argument && !flag ? ` &lt;${esc(o.argument)}&gt;` : ""}</code></label>${input}
    ${o.description ? `<small class="muted">${esc(o.description)}</small>` : ""}</div>`;
}

/** One page of options in `el`, bound to the model `m`; `others` adds the free-text field. */
function mountPage(el, options, m, others = true, example = "") {
  const known = new Set(options.map(o => o.name));
  el.innerHTML = `<input class="pp-filter" placeholder="${esc(t("Filter the parameters (name or description)…"))}">
    <div class="pp-rows">${options.slice(0, 400).map(o => optionRow(o, m)).join("") || `<small class="muted">${esc(t("No parameter matches."))}</small>`}</div>
    ${options.length > 400 ? `<small class="muted">${esc(t("{0} more: narrow the filter.", options.length - 400))}</small>` : ""}
    ${others ? `<label class="field"><span>${esc(t("Other parameters (written as text)"))}</span><input class="mono" data-pothers value="${esc(m.others(known))}" placeholder="${esc(example)}"></label>` : ""}`;
  const rows = $(".pp-rows", el);
  el.oninput = e => {
    e.stopPropagation();
    if (!e.target.classList.contains("pp-filter")) return;
    const q = e.target.value.trim().toLowerCase();
    const list = q ? options.filter(o => o.name.toLowerCase().includes(q) || (o.description ?? "").toLowerCase().includes(q)) : options;
    rows.innerHTML = list.slice(0, 400).map(o => optionRow(o, m)).join("") || `<small class="muted">${esc(t("No parameter matches."))}</small>`;
  };
  el.onchange = e => {
    e.stopPropagation();
    if (e.target.dataset.pothers != null) { m.setOthers(known, e.target.value); return; }
    const row = e.target.closest(".param-opt"); if (!row) return;
    const name = row.dataset.name, valEl = row.querySelector("[data-pv]");
    if (e.target.dataset.pset != null) {
      row.classList.toggle("on", e.target.checked);
      if (valEl) valEl.disabled = !e.target.checked;
      if (!e.target.checked) { m.unset(name); return; }
      if (valEl && !valEl.value) valEl.value = row.dataset.init;
      m.set(name, row.dataset.flag ? null : valEl?.value ?? "");
    } else if (e.target.dataset.pv != null && row.querySelector("[data-pset]").checked) m.set(name, e.target.value);
  };
  el.onclick = e => e.stopPropagation();
}

/** A flat list of every parameter of `tool` (audio / subtitle encoders, rigaya's --vpp-*, one FFmpeg filter). */
async function mountFlat(el, tool, m, example) {
  el.innerHTML = `<small class="muted">${esc(t("Reading the program's options…"))}</small>`;
  try {
    const r = await loadOptions(tool, ed.settings);
    el.innerHTML = `<small class="muted">${esc(r.options.length ? t("{0} parameters of {1}, read from its help. Applied last: they win over KnA's values.", r.options.length, r.program) : t("{0}: its help could not be read; parameters can still be written as text.", r.program))}</small><div></div>`;
    mountPage(el.lastElementChild, r.options, m, true, example);
  } catch (err) { el.innerHTML = `<small class="issue err">${esc(err.message)}</small>`; }
}

// "a=1:b=2" ↔ [["a","1"],["b","2"]] and "f1=a=1,f2" ↔ filters (same rules as KnA.Core FilterChain).
function splitTop(text, sep) {
  const out = []; let cur = "", depth = 0, q = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === "\\" && i + 1 < text.length) { cur += c + text[++i]; continue; }
    if (c === "'") q = !q; else if (!q && c === "[") depth++; else if (!q && c === "]") depth = Math.max(0, depth - 1);
    else if (!q && depth === 0 && c === sep) { out.push(cur); cur = ""; continue; }
    cur += c;
  }
  if (cur) out.push(cur);
  return out;
}
const splitOpts = text => splitTop(text ?? "", ":").filter(Boolean).map(o => { const e = o.indexOf("="); return e < 0 ? ["", o] : [o.slice(0, e), o.slice(e + 1)]; });
const parseChain = text => splitTop(text ?? "", ",").map(x => x.trim()).filter(Boolean).map(p => { const e = p.indexOf("="); return { name: e < 0 ? p : p.slice(0, e), options: e < 0 ? [] : splitOpts(p.slice(e + 1)) }; });
const formatChain = list => list.filter(f => f.name).map(f => { const o = f.options.filter(x => x[1] !== "").map(([k, v]) => k ? `${k}=${v}` : v); return o.length ? `${f.name}=${o.join(":")}` : f.name; }).join(",") || null;

// ---------------------------------------------------------------- chapters
const chapterList = () => ed.edit.chapters ?? ed.analysis.files[0].chapters ?? [];
function materializeChapters() { if (!ed.edit.chapters) ed.edit.chapters = cloneSettings(ed.analysis.files[0].chapters ?? []); return ed.edit.chapters; }
const chapterTitle = n => t("Chapter {0}", String(n).padStart(2, "0"));
function addChapter(at) {
  const l = materializeChapters();
  l.push({ start: Math.max(0, at), title: chapterTitle(l.length + 1) });
  l.sort((a, b) => a.start - b.start);
}

function renderChapters() {
  const list = chapterList(), keep = ed.edit.keepChapters !== false;
  return `<div class="section"><h3>${esc(t("Chapters"))}</h3>
    <label class="toggle"><input type="checkbox" data-k="keepChapters" ${keep ? "checked" : ""}><span class="switch"></span><span>${esc(t("Keep chapters"))}</span></label>
    ${!keep ? "" : `<p class="muted small">${esc(ed.edit.chapters ? t("Edited chapters (times on the original timeline: cuts move them automatically).") : t("The chapters of the file ({0}). Any change makes them custom.", list.length))}</p>
    ${list.length ? `<table class="tracks"><tbody>${list.map((c, i) => `<tr data-ci="${i}">
      <td class="muted">${i + 1}.</td>
      <td><input class="mono" data-c="start" value="${fmtStamp(c.start)}" style="width:130px"></td>
      <td><input data-c="title" value="${esc(c.title ?? "")}"></td>
      <td><button class="btn icon ghost sm" data-a="rm-ch" title="${esc(t("Remove"))}"><svg><use href="#i-x"/></svg></button></td></tr>`).join("")}</tbody></table>`
      : `<p class="muted small">${esc(t("No chapters."))}</p>`}
    <div class="row" style="flex-wrap:wrap;margin-top:8px">
      <button class="btn sm" data-a="add-ch">${esc(t("Add chapter"))}</button>
      <button class="btn sm" data-a="ch-5min">${esc(t("Every 5 minutes"))}</button>
      <button class="btn sm ghost" data-a="ch-none">${esc(t("Remove all"))}</button>
      <button class="btn sm ghost" data-a="ch-reset">${esc(t("Back to the file's chapters"))}</button>
    </div>`}
  </div>`;
}


// ---------------------------------------------------------------- trim (visual, several segments)
// ed.parts = kept segments [{start, end|null}] sorted; ed.seg = the one the handles move.
// One segment is stored as trimStart/trimEnd, several as edit.segments (joined, or split into jobs).
function applyParts(dur) {
  const parts = ed.parts.filter(x => (x.end ?? dur) - x.start >= 0.05).sort((a, b) => a.start - b.start);
  if (parts.length > 1) { ed.edit.segments = parts.map(x => ({ start: x.start, end: x.end })); ed.edit.trimStart = ed.edit.trimEnd = null; }
  else {
    ed.edit.segments = null;
    const one = parts[0];
    ed.edit.trimStart = one && one.start > 0.0005 ? one.start : null;
    ed.edit.trimEnd = one && one.end != null && one.end < dur - 0.0005 ? one.end : null;
  }
}

function renderTrim(body) {
  const dur = ed.analysis.files[0].duration || 1;
  const fps = ed.analysis.frameRate || 25;
  const audio = tracks().find(x => x.kind === "audio" && x.include);
  const nThumbs = 20;
  if (!ed.parts) {
    ed.parts = ed.edit.segments?.length > 1 ? ed.edit.segments.map(x => ({ ...x })).sort((a, b) => a.start - b.start)
      : (ed.edit.trimStart != null || ed.edit.trimEnd != null) ? [{ start: ed.edit.trimStart ?? 0, end: ed.edit.trimEnd ?? null }] : [];
    ed.seg = 0;
  }
  ed.seg = Math.min(Math.max(ed.seg ?? 0, 0), Math.max(ed.parts.length - 1, 0));
  const cur = () => ed.parts[ed.seg];
  const curStart = () => cur() && cur().start > 0.0005 ? cur().start : null;
  const curEnd = () => cur()?.end ?? null;
  body.innerHTML = `
    <p class="muted small">${esc(t("The cut is applied last: after imported tracks, delays, filters and burned subtitles."))}</p>
    <div class="timeline" id="tl">
      <div class="thumbs">${Array.from({ length: nThumbs }, (_, i) => `<img alt="" src="/api/jobs/${ed.id}/preview/thumb?t=${((i + 0.5) * dur / nThumbs).toFixed(2)}&w=192">`).join("")}</div>
      ${audio ? `<div class="wave"><img alt="" src="/api/jobs/${ed.id}/preview/waveform?file=${encodeURIComponent(audio.file)}&stream=${audio.stream}&w=1600&h=100"></div>` : ""}
      <div id="shades"></div><div id="others"></div>
      <div class="sel-range" id="rng"></div>
      <div class="handle" id="h-start" title="${esc(t("Trim start"))}"></div>
      <div class="handle" id="h-end" title="${esc(t("Trim end"))}"></div>
      <div class="playhead" id="ph"></div>
    </div>
    <div class="time-row"><span>0:00</span><span id="ph-time"></span><span>${fmtTime(dur)}</span></div>
    <div class="trim-controls">
      <label class="field"><span>${esc(t("Trim start"))}</span><div class="timebox"><input id="in-start" placeholder="00:00:00.000"><button class="btn sm" data-a="set-start" title="${esc(t("Set at playhead"))}">⟵</button></div></label>
      <label class="field"><span>${esc(t("Trim end"))}</span><div class="timebox"><input id="in-end" placeholder="${fmtStamp(dur)}"><button class="btn sm" data-a="set-end" title="${esc(t("Set at playhead"))}">⟵</button></div></label>
      <div class="row" style="justify-content:flex-end;flex-wrap:wrap">
        <button class="btn sm" data-a="nudge" data-d="-1">−1 s</button>
        <button class="btn sm" data-a="nudge" data-d="${(-1 / fps).toFixed(5)}">−1 f</button>
        <button class="btn sm" data-a="nudge" data-d="${(1 / fps).toFixed(5)}">+1 f</button>
        <button class="btn sm" data-a="nudge" data-d="1">+1 s</button>
        <button class="btn sm ghost" data-a="clear">${esc(t("No trim"))}</button>
      </div>
    </div>
    <div id="segs" style="margin-top:8px"></div>
    <div class="row" style="margin:10px 0 6px">
      <div class="segmented" style="max-width:320px"><button type="button" data-a="final" class="${ed.final ? "on" : ""}">${esc(t("Final"))}</button><button type="button" data-a="orig" class="${ed.final ? "" : "on"}">${esc(t("Original"))}</button></div>
      <span class="muted small" id="ph-stamp"></span>
    </div>
    <div class="preview-wrap" id="pv"><img id="pv-img" alt=""><div class="spinner" id="pv-spin" hidden>${esc(t("Rendering…"))}</div></div>`;

  const tl = $("#tl", body);
  const pct = s => Math.min(Math.max(s, 0), dur) / dur * 100;
  const pos = s => pct(s) + "%";
  const renderSegs = () => {
    const many = ed.parts.length > 1;
    const total = ed.parts.reduce((a, x) => a + ((x.end ?? dur) - x.start), 0);
    $("#segs").innerHTML = `
      ${many ? `<div class="chips" style="margin-bottom:6px">${ed.parts.map((x, i) => `<label class="${i === ed.seg ? "on" : ""}">
        <button class="btn sm ${i === ed.seg ? "" : "ghost"} mono" data-a="pick-seg" data-s="${i}">${i === ed.seg ? "●" : "○"} ${i + 1}. ${fmtStamp(x.start)} → ${fmtStamp(x.end ?? dur)}</button>
        <button class="btn icon ghost sm" data-a="rm-seg" data-s="${i}" title="${esc(t("Remove segment"))}"><svg><use href="#i-x"/></svg></button></label>`).join("")}</div>
        <p class="muted small">${esc(t("{0} segments, {1} in total: they are joined into one file, in time order.", ed.parts.length, fmtTime(total)))}</p>` : ""}
      <div class="row" style="flex-wrap:wrap">
        <button class="btn sm" data-a="add-seg" title="${esc(t("Keeps another part of the file: the parts are joined, or become separate jobs."))}"><svg><use href="#i-plus"/></svg><span>${esc(t("Add segment at playhead"))}</span></button>
        <button class="btn sm" data-a="add-ch-ph">${esc(t("Add chapter at playhead"))}</button>
        ${many ? `<button class="btn sm" data-a="split">${esc(t("Create {0} jobs in the queue (one per segment)", ed.parts.length))}</button>` : ""}
      </div>`;
  };
  const layout = () => {
    const s = curStart() ?? 0, e = curEnd() ?? dur;
    $("#h-start").style.left = pos(s); $("#h-end").style.left = pos(e);
    $("#rng").style.left = pos(s); $("#rng").style.width = ((e - s) / dur * 100) + "%";
    const kept = ed.parts.length ? ed.parts.map(x => [x.start, x.end ?? dur]).sort((a, b) => a[0] - b[0]) : [[0, dur]];
    let from = 0, shades = "";
    for (const [a, b] of kept) { if (a > from) shades += `<div class="shade" style="left:${pct(from)}%;width:${pct(a) - pct(from)}%"></div>`; from = Math.max(from, b); }
    if (from < dur) shades += `<div class="shade" style="left:${pct(from)}%;right:0"></div>`;
    $("#shades").innerHTML = shades;
    $("#others").innerHTML = ed.parts.map((x, i) => i === ed.seg ? "" : `<div class="sel-range other" style="left:${pos(x.start)};width:${pct(x.end ?? dur) - pct(x.start)}%"></div>`).join("");
    $("#ph").style.left = pos(ed.playhead);
    $("#ph-time").textContent = fmtStamp(ed.playhead);
    $("#ph-stamp").textContent = `${t("Playhead")}: ${fmtStamp(ed.playhead)}`;
    $("#in-start").value = curStart() != null ? fmtStamp(curStart()) : "";
    $("#in-end").value = curEnd() != null ? fmtStamp(curEnd()) : "";
  };
  const loadFrame = debounce(() => {
    const img = $("#pv-img"); if (!img) return;
    $("#pv-spin").hidden = false;
    img.onload = img.onerror = () => { const sp = $("#pv-spin"); if (sp) sp.hidden = true; };
    img.src = `/api/jobs/${ed.id}/preview/frame?t=${ed.playhead.toFixed(3)}&final=${ed.final}&w=1280&v=${Date.now() % 100000}`;
  }, 250);
  const setTrim = (s, e) => {
    const ns = s > 0.0005 ? Math.min(s, dur) : null;
    const ne = e != null && e < dur - 0.0005 ? Math.max(e, (ns ?? 0) + 0.05) : null;
    const part = { start: ns ?? 0, end: ne };
    if (!ed.parts.length) { ed.parts.push(part); ed.seg = 0; } else ed.parts[ed.seg] = part;
    ed.parts.sort((a, b) => a.start - b.start);
    ed.seg = ed.parts.indexOf(part);
    applyParts(dur);
    layout(); renderSegs(); save();
  };
  const timeAt = ev => { const r = tl.getBoundingClientRect(); return Math.min(Math.max((ev.clientX - r.left) / r.width, 0), 1) * dur; };

  let drag = null;
  tl.onpointerdown = ev => {
    drag = ev.target.id === "h-start" ? "start" : ev.target.id === "h-end" ? "end" : "play";
    tl.setPointerCapture(ev.pointerId);
    tl.onpointermove(ev);
  };
  // Hover preview: the nearest strip thumbnail, bigger, with its time.
  const hover = document.createElement("div");
  hover.className = "tl-hover"; hover.hidden = true;
  hover.innerHTML = `<img alt=""><div></div>`;
  tl.appendChild(hover);
  tl.onpointerleave = () => { hover.hidden = true; };
  tl.onpointermove = ev => {
    if (!drag) {
      const tt = timeAt(ev), thumbs = tl.querySelectorAll(".thumbs img");
      const r = tl.getBoundingClientRect();
      const x = Math.min(Math.max(ev.clientX - r.left, 110), r.width - 110);
      hover.style.left = x + "px";
      const th = thumbs[Math.min(thumbs.length - 1, Math.floor(tt / dur * thumbs.length))];
      hover.querySelector("img").src = th?.complete && th.naturalWidth ? th.src : "";
      hover.querySelector("img").hidden = !(th?.complete && th.naturalWidth);
      hover.querySelector("div").textContent = fmtTime(tt);
      hover.hidden = false;
      return;
    }
    hover.hidden = true;
    const tt = timeAt(ev);
    if (drag === "start") setTrim(Math.min(tt, (curEnd() ?? dur) - 0.05), curEnd());
    else if (drag === "end") setTrim(curStart() ?? 0, Math.max(tt, (curStart() ?? 0) + 0.05));
    ed.playhead = tt;
    layout();
    // Zapping: while dragging only the nearest strip thumbnail (already loaded) is shown; the real frame is
    // rendered when the pointer is released.
    const thumbs = tl.querySelectorAll(".thumbs img"), img = $("#pv-img");
    if (thumbs.length && img) img.src = thumbs[Math.min(thumbs.length - 1, Math.floor(tt / dur * thumbs.length))].src;
  };
  tl.onpointerup = tl.onpointercancel = () => { if (drag) { drag = null; loadFrame(); } };

  body.onchange = e => {
    if (e.target.id === "in-start") setTrim(parseStamp(e.target.value) ?? 0, curEnd());
    if (e.target.id === "in-end") setTrim(curStart() ?? 0, parseStamp(e.target.value));
  };
  body.onclick = guard(async e => {
    const b = e.target.closest("[data-a]"); if (!b) return;
    const a = b.dataset.a;
    if (a === "set-start") setTrim(ed.playhead, curEnd());
    if (a === "set-end") setTrim(curStart() ?? 0, ed.playhead);
    if (a === "clear") { ed.parts = []; ed.seg = 0; applyParts(dur); layout(); renderSegs(); save(); }
    if (a === "nudge") { ed.playhead = Math.min(Math.max(ed.playhead + Number(b.dataset.d), 0), dur); layout(); loadFrame(); }
    if (a === "final" || a === "orig") { ed.final = a === "final"; renderTrim(body); }
    if (a === "pick-seg") { ed.seg = +b.dataset.s; ed.playhead = ed.parts[ed.seg].start; layout(); renderSegs(); loadFrame(); }
    if (a === "rm-seg") { ed.parts.splice(+b.dataset.s, 1); ed.seg = Math.min(ed.seg, Math.max(ed.parts.length - 1, 0)); applyParts(dur); layout(); renderSegs(); save(); }
    if (a === "add-seg") {
      const part = { start: ed.playhead, end: Math.min(dur, ed.playhead + Math.max(1, Math.min(30, dur / 10))) };
      ed.parts.push(part); ed.parts.sort((x, y) => x.start - y.start); ed.seg = ed.parts.indexOf(part);
      applyParts(dur); layout(); renderSegs(); save();
    }
    if (a === "add-ch-ph") { addChapter(ed.playhead); await saveNow(); toast(t("Chapter added at {0}", fmtStamp(ed.playhead)), "ok"); }
    if (a === "split") {
      await saveNow();
      const ids = await post(`/api/jobs/${ed.id}/split-segments`);
      toast(t("{0} jobs created: one per segment.", ids.length), "ok");
      $("#dlg-editor").close();
    }
  });
  layout(); renderSegs(); loadFrame();
}


// ---------------------------------------------------------------- settings form
const formOpts = section => ({
  full: true, section, allowedContainers: ed.analysis.allowedContainers, onChange: s => formChanged(s), onQuietChange: () => save(),
  sizeInfo: async s => (await api("POST", `/api/jobs/${ed.id}/size`, { settings: s, edit: ed.edit })).text,
  onLevel: async s => { ed.settings.encoder = await api("POST", `/api/jobs/${ed.id}/rate`, { settings: s }); saveAndRender(); },
});

/** A change in a form: a new profile asks first; another encoder gets KnA's values for the same quality level. */
async function formChanged(s) {
  const prev = ed.prev;
  if (prev && s.profile !== prev.profile) {
    const name = profileOf(s.profile)?.label ?? s.profile;
    if (!confirm(t("Use the profile \"{0}\"? The video parameters (codec, encoder, depth, container, quality and encoder parameters) and the audio and subtitle formats of the tracks are reset to what this profile allows and chooses.", name))) {
      ed.settings = cloneSettings(prev); renderTab(); return;
    }
    const kind = s.profile;
    ed.settings = cloneSettings(prev);
    return guard(() => materializeNow({ profile: kind }))();
  }
  if (prev && (s.codec !== prev.codec || s.backend !== prev.backend || (s.encoder?.program ?? "FFmpeg") !== (prev.encoder?.program ?? "FFmpeg"))) {
    const e = { ...s.encoder, speed: null, tune: null, extra: null };
    if ((s.encoder?.program ?? "FFmpeg") !== (prev.encoder?.program ?? "FFmpeg") || s.backend !== prev.backend) e.gpuFilters = null;
    if (["Quality", "Auto"].includes(e.rateControl ?? "Auto")) { e.rateControl = "Auto"; e.qualityValue = null; }
    s.encoder = e;
    try { s.encoder = await api("POST", `/api/jobs/${ed.id}/rate`, { settings: s }); } catch { /* keep the cleared values */ }
  }
  ed.settings = s;
  saveAndRender();
}

// ---------------------------------------------------------------- preset
function renderPresetTab(body) {
  body.innerHTML = `<div class="two-col"><div>
      <div class="section"><h3>${esc(t("Import a preset"))}</h3>
        <select id="apply-preset">${(state.presets ?? []).map(x => `<option value="${esc(x.name)}" ${x.name === ed.settings.presetName ? "selected" : ""}>${x.builtIn ? "" : "★ "}${esc(x.name)}</option>`).join("")}</select>
        <div class="row" style="flex-wrap:wrap;margin:6px 0">${[["Video", t("Video")], ["Audio", t("Audio")], ["Subtitles", t("Subtitles")], ["Filters", t("Picture filters")], ["Tracks", t("Track selection")]]
          .map(([v, l]) => `<label class="check"><input type="checkbox" data-part="${v}" checked>${esc(l)}</label>`).join("")}</div>
        <button class="btn sm primary" data-a="apply-preset">${esc(t("Import into the fields"))}</button>
        <p class="muted small">${esc(t("The preset only fills the fields: afterwards every value can be changed in its tab and nothing follows the preset any more. Subtitles or track selection rebuild the track list from the preset's rules."))}</p>
      </div>
      <div class="section"><h3>${esc(t("Save as preset"))}</h3>
        <div class="row"><input id="preset-name" placeholder="${esc(t("Preset name"))}" value="${esc(ed.settings.presetName ?? "")}">
        <button class="btn sm" data-a="save-preset">${esc(t("Save as preset"))}</button></div>
        <p class="muted small">${esc(t("Saves the settings of this file (not the tracks or the trim) for reuse."))}</p>
      </div></div>
    <div><div class="section"><h3>${esc(t("What the preset decides for other files"))}</h3>
      <p class="muted small">${esc(t("Saved with the preset and used when it is applied to other files (their automatic track plan). They don't change this file: its tracks, codecs and formats are set in the Video, Audio and Subtitles tabs."))}</p>
      <div id="rules-form"></div></div></div></div>`;
  renderSettingsForm($("#rules-form", body), ed.settings, { ...formOpts("presetRules"), onChange: () => save() });
  body.onclick = guard(async e => {
    const a = e.target.closest("[data-a]")?.dataset.a;
    if (a === "apply-preset") {
      const name = $("#apply-preset").value;
      const parts = [...body.querySelectorAll("[data-part]")].filter(c => c.checked).map(c => c.dataset.part);
      if (!parts.length) throw new Error(t("Choose at least one part."));
      if (!confirm(t("Import \"{0}\"? The chosen parts of this file's settings are replaced by the preset's values (written into the fields, as if set by hand).", name))) return;
      await materializeNow({ preset: name, parts });
    }
    if (a === "save-preset") {
      const name = $("#preset-name").value.trim();
      if (!name) throw new Error(t("The preset needs a name."));
      await post(`/api/jobs/${ed.id}/preset`, { name, settings: ed.settings, edit: ed.edit });
      ed.settings.presetName = name; save();
      toast(t("Preset \"{0}\" saved", name), "ok");
    }
  });
}

// ---------------------------------------------------------------- video
async function renderVideo(body) {
  const p = profileOf(ed.settings.profile);
  const encodes = p.encodesVideo && !p.isImages;
  body.innerHTML = `${trackList("video")}
    <nav class="tabs subtabs" id="video-sub"><button type="button" data-vs="general">${esc(t("General"))}</button>
      ${encodes ? `<button type="button" data-vs="rate">${esc(t("Quality and speed"))}</button>` : ""}</nav>
    <small class="muted" id="vp-status">${encodes ? esc(t("Reading the program's options…")) : ""}</small>
    <div id="video-page" style="margin-top:12px"></div>`;
  let options = [], program = "";
  const show = () => {
    const sub = ed.videoSub;
    $$("#video-sub button", body).forEach(b => b.classList.toggle("on", b.dataset.vs === sub));
    const page = $("#video-page", body);
    if (sub === "general" || !encodes) { renderSettingsForm(page, ed.settings, formOpts("video")); return; }
    if (sub === "rate") {
      page.innerHTML = `<div class="two-col"><div id="rate-form"></div>
        <div><div class="section"><p class="muted small">${esc(t("Source"))}: ${esc(streamInfo(tracks().find(x => x.kind === "video"))?.details ?? "")}</p>
          <button class="btn sm" data-a="cmd"><svg><use href="#i-term"/></svg><span>${esc(t("Encoder command"))}</span></button>
          <button class="btn sm" data-a="estimate">${esc(t("Estimate the final size"))}</button>
          <p class="muted small" id="estimate">${esc(t("Encodes a few seconds in several places with these settings and predicts the final size."))}</p></div></div></div>`;
      renderSettingsForm($("#rate-form", page), ed.settings, formOpts("videoRate"));
      return;
    }
    const cat = sub.slice(2);
    const list = options.filter(o => o.category === cat);
    page.innerHTML = `<small class="muted">${esc(t("{0} parameters of {1}, read from its help. Applied last: they win over KnA's values.", options.length, program))}</small><div></div>`;
    mountPage(page.lastElementChild, list, cmdModel(() => ed.settings.encoder?.extra, v => { ed.settings.encoder = { ...ed.settings.encoder, extra: v }; save(); }), cat === "Other",
      ed.settings.encoder?.program === "Rigaya" ? "--aq-temporal --bref-mode each" : "-x265-params psy-rd=2:aq-strength=0.8");
  };
  if (!encodes) ed.videoSub = "general";
  show();
  body.oninput = e => { trackListInput(e); };
  body.onchange = e => { trackListChange(e); };
  body.onclick = guard(async e => {
    const vs = e.target.closest("[data-vs]");
    if (vs) { ed.videoSub = vs.dataset.vs; ed.prev = cloneSettings(ed.settings); show(); return; }
    const b = e.target.closest("[data-a]"); if (!b) return;
    if (await trackListClick(b)) return;
    if (b.dataset.a === "cmd") { await saveNow(); showText(t("Encoder command"), await api("GET", `/api/jobs/${ed.id}/command`)); }
    if (b.dataset.a === "estimate") {
      await saveNow();
      $("#estimate").textContent = t("Encoding samples…");
      const r = await api("POST", `/api/jobs/${ed.id}/estimate`, { settings: ed.settings, edit: ed.edit });
      if ($("#estimate")) $("#estimate").textContent = r.text;
    }
  });
  if (!encodes) return;
  // Every parameter of the encoder that will run, one sub-tab per group, read from its own help.
  try {
    const r = await loadOptions("video", ed.settings);
    if (!$("#video-sub", body)) return;
    options = r.options; program = r.program;
    const set = cat => options.filter(o => o.category === cat && cmdModel(() => ed.settings.encoder?.extra, () => {}).get(o.name) !== null).length;
    const cats = (r.categories ?? []).filter(c => options.some(o => o.category === c.value));
    if (!cats.some(c => c.value === "Other")) cats.push({ value: "Other", label: t("Other") });
    $("#video-sub", body).insertAdjacentHTML("beforeend", cats.map(c => `<button type="button" data-vs="p:${esc(c.value)}">${esc(c.label)}${set(c.value) ? ` ●${set(c.value)}` : ""}</button>`).join(""));
    $("#vp-status", body).textContent = options.length ? t("{0}: every parameter is on the pages next to this one, one per group.", program) : t("{0}: its help could not be read; parameters can still be written as text.", program);
    if (ed.videoSub.startsWith("p:")) show(); else $$("#video-sub button", body).forEach(b => b.classList.toggle("on", b.dataset.vs === ed.videoSub));
  } catch (err) { $("#vp-status", body).textContent = err.message; }
}

// ---------------------------------------------------------------- audio / subtitles
const lossless = c => ["Flac", "Alac", "WavPack", "TrueHd", "Pcm16", "Pcm24", "Pcm32Float"].includes(c);
let audioEncoders = null;

async function renderStreams(body, kind) {
  const p = profileOf(ed.settings.profile);
  // Demux: the tracks to extract are ticked in a list.
  if (p.isDemux) {
    body.innerHTML = trackList(kind);
    body.oninput = e => trackListInput(e);
    body.onchange = e => trackListChange(e);
    body.onclick = guard(async e => { const b = e.target.closest("[data-a]"); if (b) await trackListClick(b); });
    return;
  }
  if (kind === "audio" ? !p.usesAudio : !(p.encodesVideo || p.isRemux || p.isImages)) {
    body.innerHTML = `<p class="issue warn">${esc(kind === "audio" ? t("This profile has no audio output.") : t("This profile has no subtitle output."))}</p>`;
    return;
  }
  if (kind === "audio" && !audioEncoders) { try { audioEncoders = await api("GET", "/api/audio/encoders"); } catch { audioEncoders = {}; } }
  const tabs = trackTabs(kind);
  const cur = tabs.current;
  body.innerHTML = `${tabs.html}<div id="track-page" style="margin-top:12px">${cur ? (kind === "audio" ? audioPage(cur.tp) : subtitlePage(cur.tp)) : ""}</div>`;
  const paramsEl = $("[data-track-params]", body);
  if (cur && paramsEl) mountFlat(paramsEl, paramsEl.dataset.trackParams, cmdModel(() => tracks()[cur.i].arguments, v => { materialize()[cur.i].arguments = v; save(); }), paramsEl.dataset.example);
  const setFilter = (k, v) => {
    const tp = materialize()[cur.i], f = { gainDb: 0, normalize: false, downmixStereo: false, sampleRate: null, custom: null, ...(tp.filters ?? {}), [k]: v };
    const on = Math.abs(f.gainDb || 0) > 0.01 || f.normalize || f.downmixStereo || f.sampleRate > 0 || (f.custom ?? "").trim();
    tp.filters = on ? f : null;
    save();
  };
  body.oninput = e => {
    if (!cur) return;
    const af = e.target.dataset.af;
    if (af === "gainDb") { const v = Number(e.target.value); $("#gain-val", body).textContent = v.toFixed(1) + " dB"; return setFilter(af, v); }
    if (af === "custom") return setFilter(af, e.target.value.trim() ? e.target.value : null);
    if (e.target.dataset.tf && e.target.type !== "checkbox" && e.target.tagName !== "SELECT" && !["bitrate"].includes(e.target.dataset.tf)) trackFieldChange(e, cur.i);
  };
  body.onchange = e => {
    if (!cur) return;
    const af = e.target.dataset.af;
    if (af === "normalize") return setFilter(af, e.target.checked);
    if (af === "sampleRate") return setFilter(af, e.target.value === "" ? null : Number(e.target.value));
    if (af) return;
    const f = e.target.dataset.tf;
    if (f === "codec") {
      const tp = materialize()[cur.i], before = { ...tp }, v = e.target.value;
      tp.output = v; tp.encoder = v === "Copy" ? null : audioEncoders?.[v]?.[0]?.value ?? null; tp.arguments = null;
      if (v === "Copy" || lossless(v)) tp.bitrate = null;
      if (v === "Copy") { tp.filters = null; tp.channels = null; }   // a copied track is not filtered
      retitle(before, tp);
      return saveAndRender();
    }
    if (f === "encoder") { const tp = materialize()[cur.i]; tp.encoder = e.target.value; tp.arguments = null; return saveAndRender(); }
    if (f === "format") { const tp = materialize()[cur.i], before = { ...tp }; tp.output = e.target.value; tp.arguments = null; retitle(before, tp); return saveAndRender(); }
    if (f && (e.target.type === "checkbox" || e.target.tagName === "SELECT" || f === "bitrate")) trackFieldChange(e, cur.i, ["channels"]);
  };
  body.onclick = guard(async e => {
    const b = e.target.closest("[data-a]");
    const st = e.target.closest("[data-st]");
    if (st && !b) { ed.trackSub[kind] = +st.dataset.st; return renderStreams(body, kind); }
    if (!b) { $$(".track-add", body).forEach(m => { m.hidden = true; }); return; }
    const a = b.dataset.a;
    if (a === "tab-plus") { const m = $(".track-add", body); m.hidden = !m.hidden; return; }
    if (a === "add-ext") { pickExternal(); return; }
    if (a === "upload-ext") { await saveNow(); pickFiles(null, ed.id); return; }
    if (a === "tab-add") { ed.trackSub[kind] = activateTrack(materialize(), +b.dataset.i); return materializeNow(); }
    if (a === "tab-rm") {
      const l = materialize(), i = +b.dataset.i, pos = activeOf(l, kind).indexOf(i);
      removeTrack(l, i);
      const left = activeOf(l, kind);
      ed.trackSub[kind] = left.length ? left[Math.min(pos, left.length - 1)] : null;
      return saveAndRender();
    }
    if (!cur) return;
    if (a === "tab-dup") { ed.trackSub[kind] = duplicateTrack(materialize(), cur.i); return saveAndRender(); }
    if (a === "tab-left" || a === "tab-right") { ed.trackSub[kind] = moveTrack(materialize(), cur.i, a === "tab-left" ? -1 : 1); renderTab(); return save(); }
    if (a === "burn-this") { burnTrack(materialize(), cur.i); return saveAndRender(); }
    if (a === "keep-track") { const tp = materialize()[cur.i]; tp.burn = false; tp.include = true; tp.output = null; return materializeNow(); }
    if (a === "ocr") {
      await saveNow();
      const tp = tracks()[cur.i];
      await openOcr(ed.id, { ...tp, codec: tp.codec ?? streamInfo(tp)?.codec }, async () => {
        ed.edit = cloneSettings((await api("GET", "/api/state")).jobs.find(j => j.id === ed.id)?.edit ?? ed.edit);
        await materializeNow();
      });
    }
  });
}

function audioPage(tp) {
  const copy = (tp.output ?? "").toLowerCase() === "copy";
  const codec = copy ? "Copy" : tp.output;
  const codecs = (state.options.audioCodecs ?? []).filter(o => o.value !== "Auto");
  const available = audioEncoders?.[codec] ?? [];
  const enc = available.some(x => x.value === tp.encoder) ? tp.encoder : available[0]?.value;
  const opt = (v, l, cur, dis) => `<option value="${esc(v)}" ${String(cur ?? "") === String(v) ? "selected" : ""} ${dis ? "disabled" : ""}>${esc(l)}</option>`;
  return `${labelsHtml(tp)}
    <div class="grid4">
      <label class="field"><span>${esc(t("Codec"))}</span><select data-tf="codec">${codecs.map(c => opt(c.value, c.label + (c.value === "Copy" || audioEncoders?.[c.value]?.length ? "" : " — " + t("no encoder here")), codec, c.value !== "Copy" && !audioEncoders?.[c.value]?.length)).join("")}</select></label>
      ${copy ? "" : `<label class="field"><span>${esc(t("Encoder"))}</span><select data-tf="encoder">${available.map(x => opt(x.value, x.label, enc)).join("")}</select></label>
      ${lossless(codec) ? "" : `<label class="field"><span>${esc(t("Bitrate (kb/s)"))}</span><input type="number" min="0" max="1536" step="16" data-tf="bitrate" value="${tp.bitrate ?? ""}" placeholder="${esc(t("auto"))}">
        <small class="muted">${esc(t("Per stereo pair (scaled for more channels); 0 = KnA's value for the codec."))}</small></label>`}`}
    </div>
    ${copy ? `<p class="muted small">${esc(t("The track is copied as it is: no encoder, no audio filter."))}</p>` : audioFiltersHtml(tp, opt)}
    ${copy ? "" : enc ? `<h3>${esc(t("All {0} parameters", enc))}</h3><div data-track-params="audio:${esc(enc)}" data-example="${esc(enc === "fdkaac" ? "--afterburner 1" : enc === "qaac" ? "--quality 2" : "-aac_coder twoloop")}"></div>` : ""}`;
}

/** The track's own audio filters: volume, loudness normalisation, channels (downmix), sample rate, custom. */
function audioFiltersHtml(tp, opt) {
  const f = tp.filters ?? {};
  return `<div class="section"><h3>${esc(t("Audio filters of this track"))}</h3>
    <div class="grid3">
      <label class="field"><span>${esc(t("Volume"))}: <b id="gain-val">${(f.gainDb ?? 0).toFixed(1)} dB</b></span><input type="range" min="-20" max="20" step="0.5" data-af="gainDb" value="${f.gainDb ?? 0}"></label>
      <label class="field"><span>${esc(t("Channels"))}</span><select data-tf="channels">${[["", t("As the source")], ["8", "7.1"], ["6", "5.1"], ["2", t("Stereo (downmix)")], ["1", t("Mono")]].map(([v, l]) => opt(v, l, tp.channels ?? "")).join("")}</select></label>
      <label class="field"><span>${esc(t("Sample rate"))}</span><select data-af="sampleRate">${[["", t("Keep original")], ["44100", "44.1 kHz"], ["48000", "48 kHz"], ["96000", "96 kHz"]].map(([v, l]) => opt(v, l, f.sampleRate ?? "")).join("")}</select></label>
    </div>
    <label class="toggle"><input type="checkbox" data-af="normalize" ${f.normalize ? "checked" : ""}><span class="switch"></span><span>${esc(t("Normalize loudness (EBU R128)"))}</span></label>
    <label class="field"><span>${esc(t("Custom audio filters (advanced)"))}</span><input class="mono" data-af="custom" value="${esc(f.custom ?? "")}" placeholder="highpass=f=80"></label>
  </div>`;
}

function subtitlePage(tp) {
  const p = profileOf(ed.settings.profile);
  const container = ed.analysis.container || ed.settings.container || p.container;
  const si = streamInfo(tp);
  const bitmap = isPgs(tp);
  const text = !bitmap && !["dvb_subtitle"].includes(tp.codec ?? si?.codec);
  // Same rules as Containers.ChosenSubtitleCodec.
  const possible = v => {
    const c = (tp.codec ?? si?.codec ?? "");
    if (v === "copy") return container === "mkv" ? c !== "mov_text" : ["mp4", "mov"].includes(container) ? c === "mov_text" : container === "webm" ? c === "webvtt" : container === "ts" ? c === "dvb_subtitle" : false;
    if (!text) return false;
    return (container === "mkv" && ["srt", "ass", "webvtt"].includes(v)) || (["mp4", "mov"].includes(container) && v === "mov_text") || (container === "webm" && v === "webvtt");
  };
  const formats = [["copy", t("Copy (no re-encoding)")], ["srt", "SRT"], ["ass", "ASS"], ["webvtt", "WebVTT"], ["mov_text", "mov_text (MP4)"]];
  const cur = tp.output ?? "copy";
  const opt = (v, l, dis) => `<option value="${esc(v)}" ${cur === v ? "selected" : ""} ${dis ? "disabled" : ""}>${esc(l)}</option>`;
  return `${tp.burn ? `<p class="issue info">${esc(t("Burned into the picture: it is drawn on the video (fonts of the file included) and is not a separate track."))}</p>
      <label class="field" style="max-width:220px"><span>${esc(t("Delay (ms)"))}</span><input type="number" step="10" data-tf="delayMs" value="${tp.delayMs || 0}"></label>`
    : `${labelsHtml(tp)}
      <label class="field" style="max-width:360px"><span>${esc(t("Format"))}</span><select data-tf="format">${formats.map(([v, l]) => opt(v, l + (possible(v) ? "" : " — " + t("not possible in {0}", container.toUpperCase())), !possible(v))).join("")}
        ${bitmap ? opt("ocr", t("Text (OCR, picture subtitles)"), false) : ""}</select></label>
      ${cur !== "copy" && cur !== "ocr" ? `<h3>${esc(t("All {0} parameters", cur))}</h3><div data-track-params="subtitle:${esc(cur)}" data-example="-alignment 1"></div>` : ""}`}
    <div class="row" style="flex-wrap:wrap;margin-top:8px">
      ${bitmap ? `<button class="btn sm" data-a="ocr" title="${esc(t("Opens the OCR editor: every picture next to its text, to check and correct, then save as SRT or use in this job."))}">${esc(t("OCR editor…"))}</button>` : ""}
      ${p.usesVideoFilters && !tp.burn ? `<button class="btn sm" data-a="burn-this">${esc(t("Burn this track into the picture"))}</button>` : ""}
      ${tp.burn ? `<button class="btn sm" data-a="keep-track">${esc(t("Keep it as a track instead"))}</button>` : ""}
    </div>`;
}

// ---------------------------------------------------------------- filters
let catalog = null;

function renderFilters(body) {
  const v = ed.settings.videoFilters, o = state.options;
  const crop = v.crop ?? { left: 0, top: 0, right: 0, bottom: 0 };
  const rot = ed.analysis.rotation || 0;
  const p = profileOf(ed.settings.profile);
  const rig = state.options.rigayaEncoders?.[`${ed.settings.codec}|${ed.settings.backend ?? "Software"}`];
  const useRig = !!rig && ed.settings.encoder?.program === "Rigaya";
  const backend = ed.settings.backend ?? "Software";
  const gpuSuffix = { Nvenc: true, Qsv: true, Vaapi: true, Vulkan: true, VideoToolbox: true }[backend];
  const backendLabel = label(o.backends, backend);
  body.innerHTML = `
    <div class="two-col">
      <div>
        <div class="section"><h3>${esc(t("Picture"))}</h3>
          ${!p.usesVideoFilters ? `<p class="issue warn">${esc(t("This profile copies the video: only the orientation can be changed (losslessly)."))}</p>` : ""}
          <div class="grid2">
            <label class="field"><span>${esc(t("Orientation"))}</span><select data-v="orientation">
              <option value="">${esc(t("Automatic (file says {0}°)", rot))}</option>
              ${[0, 90, 180, 270].map(d => `<option value="${d}" ${v.orientation === d ? "selected" : ""}>${d}° ${esc(t("clockwise"))}</option>`).join("")}
            </select></label>
            <div class="field"><span>${esc(t("Mirror"))}</span><div class="row">
              <label class="check"><input type="checkbox" data-v="flipHorizontal" ${v.flipHorizontal ? "checked" : ""}>${esc(t("Horizontal"))}</label>
              <label class="check"><input type="checkbox" data-v="flipVertical" ${v.flipVertical ? "checked" : ""}>${esc(t("Vertical"))}</label></div></div>
          </div>
          ${p.usesVideoFilters ? `
          <label class="field"><span>${esc(t("Deinterlace"))}</span><select data-v="deinterlace"></select></label>
          <div class="field"><span>${esc(t("Crop (pixels, after rotation)"))}</span>
            <div class="grid4">
              ${["left", "top", "right", "bottom"].map(k => `<input type="number" min="0" step="2" data-c="${k}" value="${crop[k] || 0}" title="${esc(t(k[0].toUpperCase() + k.slice(1)))}" placeholder="${esc(t(k[0].toUpperCase() + k.slice(1)))}">`).join("")}
            </div>
            <div class="row" style="margin-top:6px"><button class="btn sm" data-a="autocrop">${esc(t("Detect black borders"))}</button><span class="muted small">${esc(t("left · top · right · bottom"))}</span></div>
          </div>
          <div class="grid3">
            <label class="field"><span>${esc(t("Denoise"))}</span><select data-v="denoise"></select></label>
            <label class="field"><span>${esc(t("Deband"))}</span><select data-v="deband"></select></label>
            <label class="field"><span>${esc(t("Sharpen"))}</span><select data-v="sharpen"></select></label>
          </div>
          <label class="field"><span>${esc(t("Frame rate"))}</span><input type="number" step="0.001" min="1" data-v="fps" value="${v.fps ?? ""}" placeholder="${esc(t("Keep original"))}"></label>
          <label class="field"><span>${esc(t("Custom video filters (advanced)"))}</span><input class="mono" data-v="custom" value="${esc(v.custom ?? "")}" placeholder="eq=saturation=1.1"></label>` : ""}
        </div>
      </div>
      <div>
        ${p.usesVideoFilters && p.encodesVideo && !p.isImages ? `<div class="section"><h3>${esc(t("GPU filters"))}</h3>
          ${useRig ? `<p class="muted small">${esc(t("{0}'s own filters (--vpp-*): they run on the GPU inside the encoder, after the FFmpeg filters.", rig.name))}</p><div id="vpp"></div>`
            : backend === "Software" ? `<p class="muted small">${esc(t("The video is encoded on the CPU: choose a GPU encoder in the Video tab to use its filters."))}</p>`
            : !gpuSuffix ? `<p class="muted small">${esc(t("FFmpeg has no filters for {0}: choose VCEEncC as the encoder program (Video tab) to use its GPU filters.", backendLabel))}</p>`
            : `<p class="muted small">${esc(t("FFmpeg filters of {0}: the frames are uploaded to the GPU after the filters on the left and filtered there, right before the encoder.", backendLabel))}</p><div id="gpu-filters"></div>`}
        </div>` : ""}
        ${p.usesVideoFilters ? `<div class="section"><h3>VapourSynth / AviSynth</h3><div id="fs-editor"></div></div>` : ""}
        <div class="section"><h3>${esc(t("AI assistant"))}</h3>
          <div class="row"><button class="btn sm" data-a="ai-analyze"><svg><use href="#i-spark"/></svg><span>${esc(t("Analyse frames with AI"))}</span></button>
          <button class="btn sm" data-a="ai-ask"><svg><use href="#i-spark"/></svg><span>${esc(t("Ask"))}</span></button></div>
          <p class="muted small">${esc(t("A vision model looks at a few frames and proposes filters and the upscaler. Nothing changes until you apply it."))}</p>
        </div>
      </div>
    </div>`;
  if (p.usesVideoFilters) {
    fillSelect($("[data-v=deinterlace]", body), o.deinterlace, v.deinterlace);
    fillSelect($("[data-v=denoise]", body), o.strengths, v.denoise);
    fillSelect($("[data-v=deband]", body), o.strengths, v.deband);
    fillSelect($("[data-v=sharpen]", body), o.strengths, v.sharpen);
  }
  if ($("#vpp", body)) mountFlat($("#vpp", body), "vpp", cmdModel(() => ed.settings.encoder?.gpuFilters, x => { ed.settings.encoder = { ...ed.settings.encoder, gpuFilters: x }; save(); }), "--vpp-resize spline36 --vpp-nlmeans");
  if ($("#gpu-filters", body)) gpuFilters($("#gpu-filters", body));
  if ($("#fs-editor", body)) frameserver($("#fs-editor", body));

  const upd = e => {
    const el = e.target;
    if (el.dataset.v) {
      let val = el.type === "checkbox" ? el.checked : el.value;
      if (el.dataset.v === "orientation") val = val === "" ? null : Number(val);
      if (el.dataset.v === "fps") val = val === "" ? null : Number(val);
      ed.settings.videoFilters = { ...ed.settings.videoFilters, [el.dataset.v]: val };
    } else if (el.dataset.c) {
      const c = { ...(ed.settings.videoFilters.crop ?? { left: 0, top: 0, right: 0, bottom: 0 }), [el.dataset.c]: Number(el.value) || 0 };
      ed.settings.videoFilters = { ...ed.settings.videoFilters, crop: c };
    } else return;
    save();
  };
  body.oninput = e => { if (e.target.type !== "checkbox" && e.target.tagName !== "SELECT") upd(e); };
  body.onchange = e => upd(e);
  body.onclick = guard(async e => {
    const a = e.target.closest("[data-a]")?.dataset.a;
    const applyAi = s => { ed.settings = { ...s, presetName: null }; materializeNow(); };
    if (a === "ai-analyze") { await saveNow(); await analyzeJob(ed.id, ed.settings, applyAi); }
    if (a === "ai-ask") { await saveNow(); openAssistant(applyAi, ed.id); }
    if (a !== "autocrop") return;
    toast(t("Analysing black borders…"));
    await saveNow();
    const c = await api("GET", `/api/jobs/${ed.id}/cropdetect`);
    if (!c) { toast(t("No black borders found."), "ok"); return; }
    ed.settings.videoFilters = { ...ed.settings.videoFilters, crop: c };
    renderFilters(body); save();
    toast(t("Crop detected: {0}", `${c.left}/${c.top}/${c.right}/${c.bottom}`), "ok");
  });
}

/** The FFmpeg filters of the GPU that encodes: pick from what this FFmpeg offers for it, set their options, order. */
async function gpuFilters(el) {
  const chain = parseChain(ed.settings.videoFilters.gpu);
  const emit = () => { ed.settings.videoFilters = { ...ed.settings.videoFilters, gpu: formatChain(chain) }; save(); };
  let available = [];
  const render = () => {
    el.innerHTML = `<select data-gadd><option value="">${esc(available.length ? t("Add a GPU filter…") : t("This FFmpeg has no filters for this GPU."))}</option>${available.map(f => `<option value="${esc(f.name)}">${esc(f.name)} — ${esc(f.description)}</option>`).join("")}</select>
      ${chain.length ? chain.map((f, i) => `<div class="section" data-gi="${i}" style="margin-top:8px"><div class="row" style="justify-content:space-between"><b class="mono">${i + 1}. ${esc(f.name)}</b>
        <span class="row"><button class="btn icon ghost sm" data-g="up"><svg><use href="#i-up"/></svg></button><button class="btn icon ghost sm" data-g="down"><svg><use href="#i-down"/></svg></button>
        <button class="btn icon ghost sm" data-g="rm" title="${esc(t("Remove"))}"><svg><use href="#i-x"/></svg></button></span></div>
        <details ${f.options.length ? "" : "open"}><summary>${esc(t("Options"))}</summary><div data-gopts></div></details></div>`).join("")
      : `<p class="muted small">${esc(t("No GPU filter: the frames go to the encoder as the filters above leave them."))}</p>`}`;
    $$("[data-gi]", el).forEach(box => { const f = chain[+box.dataset.gi]; mountFlat($("[data-gopts]", box), "filter:" + f.name, filterModel(f, emit), "w=1280:h=720"); });
  };
  el.onchange = e => {
    e.stopPropagation();
    if (e.target.dataset.gadd == null || !e.target.value) return;
    chain.push({ name: e.target.value, options: [] }); emit(); render();
  };
  el.oninput = e => e.stopPropagation();
  el.onclick = e => {
    e.stopPropagation();
    const b = e.target.closest("[data-g]"); if (!b) return;
    const i = +b.closest("[data-gi]").dataset.gi;
    if (b.dataset.g === "rm") chain.splice(i, 1);
    if (b.dataset.g === "up" && i > 0) [chain[i - 1], chain[i]] = [chain[i], chain[i - 1]];
    if (b.dataset.g === "down" && i < chain.length - 1) [chain[i + 1], chain[i]] = [chain[i], chain[i + 1]];
    emit(); render();
  };
  render();
  try { available = (await loadOptions("gpufilters", ed.settings)).options; render(); } catch (err) { toast(err.message, "err"); }
}

/**
 * VapourSynth / AviSynth+ like StaxRip, without its clunkiness: filters from a catalogue grouped by purpose, a form
 * for each one's parameters, the script written from the list (or by hand).
 */
async function frameserver(el) {
  if (ed.analysis.files[0].path.match(/\.(vpy|avs)$/i)) { el.innerHTML = `<p class="muted small">${esc(t("The source is already a script: its video (and audio, if it sets one) is used directly."))}</p>`; return; }
  if (!catalog) { try { catalog = await api("GET", "/api/frameserver/catalog"); } catch (err) { el.textContent = err.message; return; } }
  let templates = [];
  try { templates = await api("GET", "/api/frameserver/templates"); } catch { /* optional */ }
  const fs = () => ed.settings.frameserver ?? { kind: "None" };
  const setFs = (x, structural) => { ed.settings.frameserver = x; if (structural) guard(() => materializeNow())(); else save(); };
  const opt = (v, l, cur) => `<option value="${esc(v)}" ${String(cur ?? "") === String(v) ? "selected" : ""}>${esc(l)}</option>`;
  const render = async () => {
    const f = fs(), kind = f.kind ?? "None", byHand = f.chain == null;
    const filters = catalog.filters.filter(x => x.kind === kind);
    const cats = [...new Map(filters.map(x => [x.category, x.categoryLabel])).entries()];
    ed.fsCat = cats.some(c => c[0] === ed.fsCat) ? ed.fsCat : cats[0]?.[0];
    let script = "";
    if (kind !== "None" && !byHand) { try { script = (await api("POST", "/api/frameserver/script", f)).script; } catch { /* preview only */ } }
    el.innerHTML = `<label class="field"><span>${esc(t("Frameserver"))}</span><select data-fs="kind">
        ${[["None", t("None (FFmpeg decodes the file)")], ["VapourSynth", "VapourSynth"], ["AviSynth", "AviSynth+"]].map(([v2, l]) => opt(v2, l, kind)).join("")}</select></label>
      ${kind === "None" ? "" : `<div class="grid2">
        <label class="field"><span>${esc(t("Mode"))}</span><select data-fs="mode">${opt("list", t("Filters from the list"), byHand ? "hand" : "list")}${opt("hand", t("Script written by hand"), byHand ? "hand" : "list")}</select></label>
        <label class="field"><span>${esc(t("Source filter"))}</span><select data-fs="source">${catalog.sources.map(s => opt(s.value, s.label, f.source ?? "lsmas")).join("")}</select></label></div>
      ${byHand ? `<label class="field"><span>${esc(t("Template"))}</span><select data-fs="template"><option value="">—</option>${templates.filter(x => x.kind === kind).map((x, i) => `<option value="${i}">${esc(x.name)}</option>`).join("")}</select></label>
        <label class="field"><span>${esc(t("Script"))} <small class="muted">({source} = ${esc(t("this file"))})</small></span>
          <textarea class="mono" rows="10" data-fs="script" spellcheck="false">${esc(f.script ?? "")}</textarea></label>`
      : `<div class="grid2"><label class="field"><span>${esc(t("Category"))}</span><select data-fs="cat">${cats.map(([v2, l]) => opt(v2, l, ed.fsCat)).join("")}</select></label>
        <label class="field"><span>${esc(t("Filter"))}</span><select data-fs="add"><option value="">${esc(t("Add a filter…"))}</option>${filters.filter(x => x.category === ed.fsCat).map(x => `<option value="${esc(x.id)}">${esc(x.name)} — ${esc(x.description)}</option>`).join("")}</select></label></div>
        ${(f.chain ?? []).length ? f.chain.map((c, i) => {
          const d = catalog.filters.find(x => x.id === c.id);
          return `<div class="section" data-fi="${i}" style="margin-top:8px"><div class="row" style="justify-content:space-between"><div><b>${i + 1}. ${esc(d?.name ?? c.id)}</b>
            <div class="muted small">${esc(d ? d.description + (d.requires && d.requires !== "core" ? " · " + t("Needs: {0}", d.requires) : "") : t("Unknown filter: left out of the script."))}</div></div>
            <span class="row"><button class="btn icon ghost sm" data-fsa="up"><svg><use href="#i-up"/></svg></button><button class="btn icon ghost sm" data-fsa="down"><svg><use href="#i-down"/></svg></button>
            <button class="btn icon ghost sm" data-fsa="rm" title="${esc(t("Remove"))}"><svg><use href="#i-x"/></svg></button></span></div>
            <div class="grid3">${(d?.params ?? []).map(pp => {
              const val = c.values?.[pp.name] ?? pp.default;
              const input = pp.type === "Bool" ? `<input type="checkbox" data-fp="${esc(pp.name)}" ${String(val).toLowerCase() === "true" ? "checked" : ""}>`
                : pp.type === "Choice" ? `<select data-fp="${esc(pp.name)}">${(pp.choices ?? []).map(ch => opt(ch, ch, val)).join("")}</select>`
                : pp.type === "Text" ? `<input class="mono" data-fp="${esc(pp.name)}" value="${esc(val)}">`
                : `<input type="number" data-fp="${esc(pp.name)}" value="${esc(val)}" min="${pp.min}" max="${pp.max}" step="${pp.type === "Int" ? 1 : pp.max - pp.min > 20 ? 1 : 0.05}">`;
              return `<label class="field"><span>${esc(pp.label)}</span>${input}${pp.help ? `<small class="muted">${esc(pp.help)}</small>` : ""}</label>`;
            }).join("")}</div></div>`;
        }).join("") : `<p class="muted small">${esc(t("No filter yet: the script only opens the file. Add filters in the order they should run."))}</p>`}
        <details><summary>${esc(t("Script written from the list"))}</summary><pre class="mono small">${esc(script)}</pre></details>`}
      <label class="toggle"><input type="checkbox" data-fs="audioFromScript" ${f.audioFromScript ? "checked" : ""}><span class="switch"></span><span>${esc(t("Use the audio produced by the script"))}</span></label>
      <small class="muted">${esc(t("VapourSynth: video = output 0, audio = output 1 (vspipe). AviSynth: AudioDub in the script. Decoding happens in the script, without hardware acceleration."))}</small>`}`;
  };
  el.oninput = e => {
    e.stopPropagation();
    if (e.target.dataset.fs === "script") { ed.settings.frameserver = { ...fs(), script: e.target.value }; save(); }
  };
  el.onchange = async e => {
    e.stopPropagation();
    const k = e.target.dataset.fs, f = fs();
    if (k === "kind") { const kind = e.target.value; setFs({ ...f, kind, chain: kind === "None" ? null : kind === f.kind ? f.chain : [], script: kind === f.kind ? f.script : null }, true); return; }
    if (k === "mode") {
      if (e.target.value === "hand") { const s = (await api("POST", "/api/frameserver/script", f)).script; setFs({ ...f, chain: null, script: s }); }
      else setFs({ ...f, chain: [] });
      return render();
    }
    if (k === "source") { setFs({ ...f, source: e.target.value }); return render(); }
    if (k === "template") { const tpl = templates.filter(x => x.kind === f.kind)[Number(e.target.value)]; if (tpl) { setFs({ ...f, script: tpl.body }); render(); } return; }
    if (k === "cat") { ed.fsCat = e.target.value; return render(); }
    if (k === "add" && e.target.value) { setFs({ ...f, chain: [...(f.chain ?? []), { id: e.target.value, values: null }] }); return render(); }
    if (k === "audioFromScript") { setFs({ ...f, audioFromScript: e.target.checked }, true); return; }
    const fp = e.target.dataset.fp;
    if (fp) {
      const i = +e.target.closest("[data-fi]").dataset.fi, chain = cloneSettings(f.chain);
      chain[i].values = { ...(chain[i].values ?? {}), [fp]: e.target.type === "checkbox" ? String(e.target.checked) : e.target.value };
      setFs({ ...f, chain });
    }
  };
  el.onclick = e => {
    e.stopPropagation();
    const b = e.target.closest("[data-fsa]"); if (!b) return;
    const f = fs(), chain = cloneSettings(f.chain ?? []), i = +b.closest("[data-fi]").dataset.fi;
    if (b.dataset.fsa === "rm") chain.splice(i, 1);
    if (b.dataset.fsa === "up" && i > 0) [chain[i - 1], chain[i]] = [chain[i], chain[i - 1]];
    if (b.dataset.fsa === "down" && i < chain.length - 1) [chain[i + 1], chain[i]] = [chain[i], chain[i + 1]];
    setFs({ ...f, chain }); render();
  };
  render();
}

// ---------------------------------------------------------------- output
function renderOutput(body) {
  const p = profileOf(ed.settings.profile);
  const dur = ed.analysis.files[0].duration || 1;
  body.innerHTML = `
    <div class="two-col">
      <div><div id="out-form"></div>
        ${(ed.analysis.allowedContainers?.length ?? 0) > 0 && (p.encodesVideo || p.isRemux) ? `<p class="muted small">${esc(t("Only the containers that can hold every final stream (video, audio and subtitle formats, attachments) are offered."))}</p>` : ""}
        ${p.encodesVideo || p.isRemux || p.isImages || p.isDemux ? trackList("attachment") : ""}
        ${p.isKeyframes || p.isImages ? "" : renderChapters()}
      </div>
      <div>
        <label class="field"><span>${esc(t("Destination folder"))}</span><input id="out-folder" class="mono" value="${esc(ed.edit.outputFolder ?? "")}" placeholder="${esc(t("Same as the general setting"))}"></label>
        <div class="row"><button class="btn sm" data-a="cmd"><svg><use href="#i-term"/></svg><span>${esc(t("FFmpeg command"))}</span></button></div>
      </div>
    </div>`;
  renderSettingsForm($("#out-form", body), ed.settings, formOpts("output"));
  body.oninput = e => {
    if (trackListInput(e)) return;
    const ch = e.target.closest("tr[data-ci]");
    if (ch && e.target.dataset.c === "title") { const l = materializeChapters(); l[+ch.dataset.ci].title = e.target.value || null; save(); }
  };
  body.onchange = e => {
    if (e.target.id === "out-folder") { ed.edit.outputFolder = e.target.value.trim() || null; return save(); }
    if (e.target.dataset.k === "keepChapters") { ed.edit.keepChapters = e.target.checked; return saveAndRender(); }
    const ch = e.target.closest("tr[data-ci]");
    if (ch && e.target.dataset.c === "start") {
      const v = parseStamp(e.target.value), l = materializeChapters(), c = l[+ch.dataset.ci];
      if (v == null) { e.target.value = fmtStamp(c.start); return; }
      c.start = Math.max(0, v); l.sort((a, b) => a.start - b.start);
      return saveAndRender();
    }
    trackListChange(e);
  };
  body.onclick = guard(async e => {
    const b = e.target.closest("[data-a]"); if (!b) return;
    if (await trackListClick(b)) return;
    const a = b.dataset.a;
    if (a === "cmd") { await saveNow(); showText(t("FFmpeg command"), await api("GET", `/api/jobs/${ed.id}/command`)); }
    if (a === "add-ch") { const l = chapterList(); addChapter(l.length ? Math.min(l[l.length - 1].start + 300, Math.max(0, dur - 1)) : 0); return saveAndRender(); }
    if (a === "ch-5min") { const l = []; for (let x = 0; x < Math.max(dur, 1); x += 300) l.push({ start: x, title: chapterTitle(l.length + 1) }); ed.edit.chapters = l; return saveAndRender(); }
    if (a === "ch-none") { ed.edit.chapters = []; return saveAndRender(); }
    if (a === "ch-reset") { ed.edit.chapters = null; return saveAndRender(); }
    if (a === "rm-ch") { materializeChapters().splice(+b.closest("tr[data-ci]").dataset.ci, 1); return saveAndRender(); }
  });
}

// ---------------------------------------------------------------- preview
function renderPreview(body) {
  const dur = ed.analysis.files[0].duration || 1;
  body.innerHTML = `
    <div class="field"><span>${esc(t("Position"))}: <b id="pv-t">${fmtStamp(ed.playhead)}</b></span>
      <input type="range" id="pv-range" min="0" max="${dur.toFixed(3)}" step="0.04" value="${ed.playhead.toFixed(3)}"></div>
    <div class="compare">
      <figure><div class="preview-wrap"><img id="pv-o" alt=""></div><figcaption>${esc(t("Original"))}</figcaption></figure>
      <figure><div class="preview-wrap"><img id="pv-f" alt=""><div class="spinner" id="pv-fs" hidden>${esc(t("Rendering…"))}</div></div><figcaption>${esc(t("Final (filters, subtitles, HDR conversion)"))}</figcaption></figure>
    </div>
    <div class="row" style="margin-top:12px">
      <button class="btn" data-a="clip"><svg><use href="#i-play"/></svg><span>${esc(t("Play a 6 s clip of the result"))}</span></button>
      <span class="muted small">${esc(t("Rendered quickly in H.264, SDR: for checking timing and look, not quality."))}</span>
    </div>
    <div class="preview-wrap" id="clip-box" hidden style="margin-top:10px"><video id="clip" controls playsinline></video><div class="spinner" id="clip-spin" hidden>${esc(t("Rendering…"))}</div></div>`;
  const load = debounce(() => {
    const tt = ed.playhead.toFixed(3);
    $("#pv-o").src = `/api/jobs/${ed.id}/preview/frame?t=${tt}&final=false&w=960`;
    $("#pv-fs").hidden = false;
    const f = $("#pv-f");
    f.onload = f.onerror = () => { const s = $("#pv-fs"); if (s) s.hidden = true; };
    f.src = `/api/jobs/${ed.id}/preview/frame?t=${tt}&final=true&w=960&v=${Date.now() % 100000}`;
  }, 300);
  // Zapping: dragging the slider only moves the time ("input"); the frames are rendered on release ("change",
  // also sent by each keyboard step).
  body.oninput = e => {
    if (e.target.id !== "pv-range") return;
    ed.playhead = Number(e.target.value);
    $("#pv-t").textContent = fmtStamp(ed.playhead);
  };
  body.onchange = e => { if (e.target.id === "pv-range") load(); };
  body.onclick = e => {
    if (!e.target.closest("[data-a=clip]")) return;
    $("#clip-box").hidden = false;
    $("#clip-spin").hidden = false;
    const v = $("#clip");
    v.oncanplay = () => { $("#clip-spin").hidden = true; v.play().catch(() => {}); };
    v.onerror = () => { $("#clip-spin").hidden = true; toast(t("The preview could not be generated."), "err"); };
    v.src = `/api/jobs/${ed.id}/preview/clip?t=${ed.playhead.toFixed(3)}&len=6&v=${Date.now() % 100000}`;
  };
  load();
}

/** Mirrors JobSettings.Merge: the chosen parts come from the preset, the rest stays. */

export function editorJobChanged(job) {
  if (ed && ed.id === job.id) ed.job = job;
}
