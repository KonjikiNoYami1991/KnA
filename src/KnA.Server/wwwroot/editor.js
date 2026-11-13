// Per-file editor: tracks, visual trim, filters, output, preview.
// Every change is saved to the job and re-validated by the server (debounced).
import { $, $$, esc, state, t, api, post, toast, guard, fmtStamp, parseStamp, fmtTime, label, profileOf, fillSelect, debounce, showText } from "./core.js";
import { renderSettingsForm, cloneSettings } from "./form.js";

let ed = null;
const kinds = ["video", "audio", "subtitle", "attachment"];
const kindLabel = k => ({ video: t("Video"), audio: t("Audio"), subtitle: t("Subtitles"), attachment: t("Attachments") }[k] ?? k);

export async function openEditor(id, tab = "tracks") {
  const job = state.jobs.get(id);
  if (!job) return;
  ed = { id, job, settings: cloneSettings(job.settings), edit: cloneSettings(job.edit), analysis: null, tab, playhead: job.edit.trimStart ?? 0, final: true };
  $("#ed-title").textContent = job.fileName;
  $("#ed-sub").textContent = job.folder;
  $$("#ed-tabs button").forEach(b => b.classList.toggle("on", b.dataset.tab === tab));
  $("#ed-body").innerHTML = `<p class="muted">${esc(t("Loading…"))}</p>`;
  $("#ed-issues").innerHTML = "";
  $("#dlg-editor").showModal();
  try {
    ed.analysis = await api("GET", `/api/jobs/${id}/analysis`);
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
  ({ tracks: renderTracks, trim: renderTrim, filters: renderFilters, output: renderOutput, preview: renderPreview })[ed.tab](body);
}

// ---------------------------------------------------------------- tracks
function streamInfo(tp) {
  const f = ed.analysis.files.find(x => x.path === tp.file);
  return f?.streams.find(s => s.index === tp.stream);
}

function renderTracks(body) {
  const list = tracks();
  const src = ed.analysis.files[0].path;
  const externals = ed.edit.externalFiles;
  const p = profileOf(ed.settings.profile);
  body.innerHTML = `
    <div class="row" style="margin-bottom:10px;flex-wrap:wrap">
      <button class="btn sm" data-a="add-ext"><svg><use href="#i-plus"/></svg><span>${esc(t("Add external files…"))}</span></button>
      <button class="btn sm ghost" data-a="reset"><svg><use href="#i-redo"/></svg><span>${esc(t("Reset to automatic"))}</span></button>
      <span class="muted small">${esc(ed.edit.tracks ? t("Custom track plan.") : t("Automatic plan (from the subtitle mode). Any change makes it custom."))}</span>
    </div>
    ${externals.length ? `<div class="chips" style="margin-bottom:10px">${externals.map(f => `<label title="${esc(f)}">${esc(f.split(/[\\/]/).pop())} <button class="btn icon ghost sm" data-a="rm-ext" data-f="${esc(f)}" title="${esc(t("Remove"))}"><svg><use href="#i-x"/></svg></button></label>`).join("")}</div>` : ""}
    <p class="muted small">${esc(t("Delays shift a single track (positive = later). The trim is applied afterwards, on the final timeline."))}</p>
    ${kinds.map(k => {
      const rows = list.map((tp, i) => ({ tp, i })).filter(x => x.tp.kind === k);
      if (!rows.length) return "";
      return `<div class="section"><h3><span class="kind ${k}">${esc(kindLabel(k))}</span></h3>
      <table class="tracks"><thead><tr>
        <th>${esc(t("Use"))}</th><th>${esc(t("Source"))}</th><th>${esc(t("Stream"))}</th>
        ${k !== "attachment" ? `<th>${esc(t("Language"))}</th>` : ""}<th class="hide-sm">${esc(t("Title"))}</th>
        ${k !== "attachment" && k !== "video" ? `<th>${esc(t("Delay (ms)"))}</th>` : k === "video" ? `<th>${esc(t("Delay (ms)"))}</th>` : ""}
        ${k === "audio" || k === "subtitle" ? `<th>${esc(t("Default"))}</th><th>${esc(t("Forced"))}</th>` : ""}
        ${k === "subtitle" && p.usesVideoFilters ? `<th>${esc(t("Burn"))}</th>` : ""}
        <th></th></tr></thead><tbody>
        ${rows.map(({ tp, i }) => {
          const si = streamInfo(tp);
          const isSrc = tp.file === src;
          return `<tr class="${tp.include || tp.burn ? "" : "off"}" data-i="${i}">
            <td><input type="checkbox" data-f="include" ${tp.include ? "checked" : ""}></td>
            <td><span class="src-badge ellipsis ${isSrc ? "source" : ""}" title="${esc(tp.file)}">${esc(isSrc ? t("Source") : tp.file.split(/[\\/]/).pop())}</span></td>
            <td><span class="codec">${tp.stream >= 0 ? "#" + tp.stream + " " : ""}${esc(tp.codec ?? si?.codec ?? "")}</span><div class="details">${esc(si?.details ?? "")}</div></td>
            ${k !== "attachment" ? `<td><input class="lang" data-f="language" value="${esc(tp.language ?? "")}" maxlength="3" placeholder="und"></td>` : ""}
            <td class="hide-sm"><input data-f="title" value="${esc(tp.title ?? "")}"></td>
            ${k !== "attachment" ? `<td><input class="delay" type="number" step="10" data-f="delayMs" value="${tp.delayMs || 0}"></td>` : ""}
            ${k === "audio" || k === "subtitle" ? `<td><input type="checkbox" data-f="default" ${tp.default ? "checked" : ""}></td><td><input type="checkbox" data-f="forced" ${tp.forced ? "checked" : ""}></td>` : ""}
            ${k === "subtitle" && p.usesVideoFilters ? `<td><input type="radio" name="burn" data-f="burn" ${tp.burn ? "checked" : ""}></td>` : ""}
            <td class="row"><button class="btn icon ghost sm" data-a="up" title="${esc(t("Move up"))}"><svg><use href="#i-up"/></svg></button>
              <button class="btn icon ghost sm" data-a="down" title="${esc(t("Move down"))}"><svg><use href="#i-down"/></svg></button></td>
          </tr>`;
        }).join("")}
      </tbody></table>
      ${k === "subtitle" && p.usesVideoFilters && rows.some(r => r.tp.burn) ? `<button class="btn sm ghost" data-a="no-burn">${esc(t("Don't burn any subtitle"))}</button>` : ""}
      </div>`;
    }).join("")}`;

  body.oninput = e => {
    const tr = e.target.closest("tr[data-i]"); const f = e.target.dataset.f;
    if (!tr || !f || e.target.type === "checkbox" || e.target.type === "radio") return;
    const tp = materialize()[+tr.dataset.i];
    tp[f] = f === "delayMs" ? Number(e.target.value) || 0 : e.target.value;
    save();
  };
  body.onchange = e => {
    const tr = e.target.closest("tr[data-i]"); const f = e.target.dataset.f;
    if (!tr || !f || !(e.target.type === "checkbox" || e.target.type === "radio")) return;
    const list2 = materialize(); const tp = list2[+tr.dataset.i];
    if (f === "burn") list2.forEach(x => { if (x.kind === "subtitle") x.burn = x === tp; });
    else tp[f] = e.target.checked;
    if (f === "default" && e.target.checked) list2.forEach(x => { if (x !== tp && x.kind === tp.kind) x.default = false; });
    renderTracks(body);
    save();
  };
  body.onclick = guard(async e => {
    const b = e.target.closest("button[data-a]"); if (!b) return;
    const a = b.dataset.a;
    if (a === "add-ext") return pickExternal();
    if (a === "reset") { ed.edit.tracks = null; return saveAndRender(); }
    if (a === "rm-ext") {
      const f = b.dataset.f;
      ed.edit.externalFiles = ed.edit.externalFiles.filter(x => x !== f);
      if (ed.edit.tracks) ed.edit.tracks = ed.edit.tracks.filter(x => x.file !== f);
      return saveAndRender();
    }
    if (a === "no-burn") { materialize().forEach(x => x.burn = false); renderTracks(body); return save(); }
    const tr = b.closest("tr[data-i]"); if (!tr) return;
    const list2 = materialize(); const i = +tr.dataset.i; const k = list2[i].kind;
    const same = list2.map((x, j) => ({ x, j })).filter(o => o.x.kind === k).map(o => o.j);
    const pos = same.indexOf(i), target = same[pos + (a === "up" ? -1 : 1)];
    if (target == null) return;
    [list2[i], list2[target]] = [list2[target], list2[i]];
    renderTracks(body); save();
  });
}

function pickExternal() {
  state.browse.external = true;
  state.browse.onPick = async paths => {
    const files = paths.filter(p => !ed.edit.externalFiles.includes(p));
    ed.edit.externalFiles = [...ed.edit.externalFiles, ...files];
    if (ed.edit.tracks) {
      // New streams are offered unchecked; the server adds them to the plan in saveNow.
    }
    await saveNow(true);
    toast(t("{0} file(s) added: tick the tracks to use.", files.length), "ok");
  };
  document.dispatchEvent(new CustomEvent("kna:browse", { detail: { external: true } }));
}

// ---------------------------------------------------------------- trim (visual)
function renderTrim(body) {
  const dur = ed.analysis.files[0].duration || 1;
  const fps = ed.analysis.frameRate || 25;
  const audio = tracks().find(x => x.kind === "audio" && x.include);
  const nThumbs = 12;
  const s0 = ed.edit.trimStart ?? 0, e0 = ed.edit.trimEnd ?? dur;
  body.innerHTML = `
    <p class="muted small">${esc(t("The cut is applied last: after imported tracks, delays, filters and burned subtitles."))}</p>
    <div class="timeline" id="tl">
      <div class="thumbs">${Array.from({ length: nThumbs }, (_, i) => `<img loading="lazy" alt="" src="/api/jobs/${ed.id}/preview/thumb?t=${((i + 0.5) * dur / nThumbs).toFixed(2)}&w=192">`).join("")}</div>
      ${audio ? `<div class="wave"><img alt="" src="/api/jobs/${ed.id}/preview/waveform?file=${encodeURIComponent(audio.file)}&stream=${audio.stream}&w=1600&h=100"></div>` : ""}
      <div class="shade" id="sh-l"></div><div class="shade" id="sh-r"></div>
      <div class="sel-range" id="rng"></div>
      <div class="handle" id="h-start" title="${esc(t("Trim start"))}"></div>
      <div class="handle" id="h-end" title="${esc(t("Trim end"))}"></div>
      <div class="playhead" id="ph"></div>
    </div>
    <div class="time-row"><span>0:00</span><span id="ph-time"></span><span>${fmtTime(dur)}</span></div>
    <div class="trim-controls">
      <label class="field"><span>${esc(t("Trim start"))}</span><div class="timebox"><input id="in-start" value="${ed.edit.trimStart != null ? fmtStamp(s0) : ""}" placeholder="00:00:00.000"><button class="btn sm" data-a="set-start" title="${esc(t("Set at playhead"))}">⟵</button></div></label>
      <label class="field"><span>${esc(t("Trim end"))}</span><div class="timebox"><input id="in-end" value="${ed.edit.trimEnd != null ? fmtStamp(e0) : ""}" placeholder="${fmtStamp(dur)}"><button class="btn sm" data-a="set-end" title="${esc(t("Set at playhead"))}">⟵</button></div></label>
      <div class="row" style="justify-content:flex-end;flex-wrap:wrap">
        <button class="btn sm" data-a="nudge" data-d="-1">−1 s</button>
        <button class="btn sm" data-a="nudge" data-d="${(-1 / fps).toFixed(5)}">−1 f</button>
        <button class="btn sm" data-a="nudge" data-d="${(1 / fps).toFixed(5)}">+1 f</button>
        <button class="btn sm" data-a="nudge" data-d="1">+1 s</button>
        <button class="btn sm ghost" data-a="clear">${esc(t("No trim"))}</button>
      </div>
    </div>
    <div class="row" style="margin:10px 0 6px">
      <div class="segmented" style="max-width:320px"><button type="button" data-a="final" class="${ed.final ? "on" : ""}">${esc(t("Final"))}</button><button type="button" data-a="orig" class="${ed.final ? "" : "on"}">${esc(t("Original"))}</button></div>
      <span class="muted small" id="ph-stamp"></span>
    </div>
    <div class="preview-wrap" id="pv"><img id="pv-img" alt=""><div class="spinner" id="pv-spin" hidden>${esc(t("Rendering…"))}</div></div>`;

  const tl = $("#tl", body);
  const pos = s => (Math.min(Math.max(s, 0), dur) / dur * 100) + "%";
  const layout = () => {
    const s = ed.edit.trimStart ?? 0, e = ed.edit.trimEnd ?? dur;
    $("#h-start").style.left = pos(s); $("#h-end").style.left = pos(e);
    $("#rng").style.left = pos(s); $("#rng").style.width = ((e - s) / dur * 100) + "%";
    $("#sh-l").style.left = "0"; $("#sh-l").style.width = pos(s);
    $("#sh-r").style.left = pos(e); $("#sh-r").style.right = "0";
    $("#ph").style.left = pos(ed.playhead);
    $("#ph-time").textContent = fmtStamp(ed.playhead);
    $("#ph-stamp").textContent = `${t("Playhead")}: ${fmtStamp(ed.playhead)}`;
  };
  const loadFrame = debounce(() => {
    const img = $("#pv-img"); if (!img) return;
    $("#pv-spin").hidden = false;
    img.onload = img.onerror = () => { const sp = $("#pv-spin"); if (sp) sp.hidden = true; };
    img.src = `/api/jobs/${ed.id}/preview/frame?t=${ed.playhead.toFixed(3)}&final=${ed.final}&w=1280&v=${Date.now() % 100000}`;
  }, 250);
  const setTrim = (s, e) => {
    ed.edit.trimStart = s > 0.0005 ? Math.min(s, dur) : null;
    ed.edit.trimEnd = e != null && e < dur - 0.0005 ? Math.max(e, (ed.edit.trimStart ?? 0) + 0.05) : null;
    $("#in-start").value = ed.edit.trimStart != null ? fmtStamp(ed.edit.trimStart) : "";
    $("#in-end").value = ed.edit.trimEnd != null ? fmtStamp(ed.edit.trimEnd) : "";
    layout(); save();
  };
  const timeAt = ev => { const r = tl.getBoundingClientRect(); return Math.min(Math.max((ev.clientX - r.left) / r.width, 0), 1) * dur; };

  let drag = null;
  tl.onpointerdown = ev => {
    drag = ev.target.id === "h-start" ? "start" : ev.target.id === "h-end" ? "end" : "play";
    tl.setPointerCapture(ev.pointerId);
    tl.onpointermove(ev);
  };
  tl.onpointermove = ev => {
    if (!drag) return;
    const tt = timeAt(ev);
    if (drag === "start") setTrim(Math.min(tt, (ed.edit.trimEnd ?? dur) - 0.05), ed.edit.trimEnd);
    else if (drag === "end") setTrim(ed.edit.trimStart ?? 0, Math.max(tt, (ed.edit.trimStart ?? 0) + 0.05));
    ed.playhead = drag === "play" ? tt : tt;
    layout(); loadFrame();
  };
  tl.onpointerup = () => { drag = null; };

  body.onchange = e => {
    if (e.target.id === "in-start") setTrim(parseStamp(e.target.value) ?? 0, ed.edit.trimEnd);
    if (e.target.id === "in-end") setTrim(ed.edit.trimStart ?? 0, parseStamp(e.target.value));
  };
  body.onclick = e => {
    const b = e.target.closest("[data-a]"); if (!b) return;
    const a = b.dataset.a;
    if (a === "set-start") setTrim(ed.playhead, ed.edit.trimEnd);
    if (a === "set-end") setTrim(ed.edit.trimStart ?? 0, ed.playhead);
    if (a === "clear") setTrim(0, null);
    if (a === "nudge") { ed.playhead = Math.min(Math.max(ed.playhead + Number(b.dataset.d), 0), dur); layout(); loadFrame(); }
    if (a === "final" || a === "orig") { ed.final = a === "final"; renderTrim(body); }
  };
  layout(); loadFrame();
}

// ---------------------------------------------------------------- filters
function renderFilters(body) {
  const v = ed.settings.videoFilters, a = ed.settings.audioFilters, o = state.options;
  const crop = v.crop ?? { left: 0, top: 0, right: 0, bottom: 0 };
  const rot = ed.analysis.rotation || 0;
  const p = profileOf(ed.settings.profile);
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
          <label class="field"><span>${esc(t("Custom video filters (advanced)"))}</span><input class="mono" data-v="custom" value="${esc(v.custom ?? "")}" placeholder="eq=saturation=1.1"></label>
        </div>
      </div>
      <div>
        ${p.usesVideoFilters ? `<div class="section"><h3>VapourSynth / AviSynth</h3>
          ${ed.analysis.files[0].path.match(/\.(vpy|avs)$/i) ? `<p class="muted small">${esc(t("The source is already a script: its video (and audio, if it sets one) is used directly."))}</p>` : `
          <div class="grid2">
            <label class="field"><span>${esc(t("Frameserver"))}</span><select data-fs="kind">
              ${[["None", t("None (FFmpeg decodes the file)")], ["VapourSynth", "VapourSynth"], ["AviSynth", "AviSynth+"]].map(([v2, l]) => `<option value="${v2}" ${(ed.settings.frameserver?.kind ?? "None") === v2 ? "selected" : ""}>${esc(l)}</option>`).join("")}
            </select></label>
            <label class="field"><span>${esc(t("Template"))}</span><select data-fs="template"><option value="">—</option></select></label>
          </div>
          ${(ed.settings.frameserver?.kind ?? "None") !== "None" ? `
            <label class="field"><span>${esc(t("Script"))} <small class="muted">({source} = ${esc(t("this file"))})</small></span>
              <textarea class="mono" rows="10" data-fs="script" spellcheck="false">${esc(ed.settings.frameserver?.script ?? "")}</textarea></label>
            <label class="toggle"><input type="checkbox" data-fs="audioFromScript" ${ed.settings.frameserver?.audioFromScript ? "checked" : ""}><span class="switch"></span>
              <span>${esc(t("Use the audio produced by the script"))}</span></label>
            <small class="muted">${esc(t("VapourSynth: video = output 0, audio = output 1 (vspipe). AviSynth: AudioDub in the script. Decoding happens in the script, without hardware acceleration."))}</small>` : ""}`}
        </div>` : ""}
        <div class="section"><h3>${esc(t("Audio"))}</h3>
          ${!p.usesAudio ? `<p class="issue warn">${esc(t("This profile has no audio output."))}</p>` : ""}
          <label class="field"><span>${esc(t("Volume"))}: <b id="gain-val">${(a.gainDb ?? 0).toFixed(1)} dB</b></span><input type="range" min="-20" max="20" step="0.5" data-a2="gainDb" value="${a.gainDb ?? 0}"></label>
          <label class="toggle"><input type="checkbox" data-a2="normalize" ${a.normalize ? "checked" : ""}><span class="switch"></span><span>${esc(t("Normalize loudness (EBU R128)"))}</span></label>
          <label class="toggle"><input type="checkbox" data-a2="downmixStereo" ${a.downmixStereo ? "checked" : ""}><span class="switch"></span><span>${esc(t("Downmix to stereo"))}</span></label>
          <label class="field"><span>${esc(t("Sample rate"))}</span><select data-a2="sampleRate">
            ${[["", t("Keep original")], ["44100", "44.1 kHz"], ["48000", "48 kHz"], ["96000", "96 kHz"]].map(([vv, l]) => `<option value="${vv}" ${String(a.sampleRate ?? "") === vv ? "selected" : ""}>${esc(l)}</option>`).join("")}
          </select></label>
          <label class="field"><span>${esc(t("Custom audio filters (advanced)"))}</span><input class="mono" data-a2="custom" value="${esc(a.custom ?? "")}" placeholder="highpass=f=80"></label>
          <p class="muted small">${esc(t("Audio filters force re-encoding of the affected tracks."))}</p>
        </div>
      </div>
    </div>`;
  fillSelect($("[data-v=deinterlace]", body), o.deinterlace, v.deinterlace);
  const tplSel = $("[data-fs=template]", body);
  if (tplSel) api("GET", "/api/frameserver/templates").then(list => {
    const kind = ed.settings.frameserver?.kind ?? "None";
    tplSel.innerHTML = `<option value="">—</option>` + list.filter(x => x.kind === kind).map((x, i) => `<option value="${i}">${esc(x.name)}</option>`).join("");
    tplSel.onchange = () => {
      const tpl = list.filter(x => x.kind === kind)[Number(tplSel.value)];
      if (!tpl) return;
      ed.settings.frameserver = { ...(ed.settings.frameserver ?? {}), script: tpl.body };
      renderFilters(body); saveAndRender();
    };
  }).catch(() => {});
  fillSelect($("[data-v=denoise]", body), o.strengths, v.denoise);
  fillSelect($("[data-v=deband]", body), o.strengths, v.deband);
  fillSelect($("[data-v=sharpen]", body), o.strengths, v.sharpen);

  const upd = (e, final) => {
    const el = e.target;
    if (el.dataset.v) {
      let val = el.type === "checkbox" ? el.checked : el.value;
      if (el.dataset.v === "orientation") val = val === "" ? null : Number(val);
      if (el.dataset.v === "fps") val = val === "" ? null : Number(val);
      ed.settings.videoFilters = { ...ed.settings.videoFilters, [el.dataset.v]: val };
    } else if (el.dataset.c) {
      const c = { ...(ed.settings.videoFilters.crop ?? { left: 0, top: 0, right: 0, bottom: 0 }), [el.dataset.c]: Number(el.value) || 0 };
      ed.settings.videoFilters = { ...ed.settings.videoFilters, crop: c };
    } else if (el.dataset.fs) {
      if (el.dataset.fs === "template") return;
      const val = el.type === "checkbox" ? el.checked : el.value;
      ed.settings.frameserver = { ...(ed.settings.frameserver ?? { kind: "None" }), [el.dataset.fs]: val };
      if (el.dataset.fs === "kind" && val !== "None" && !(ed.settings.frameserver.script ?? "").trim()) {
        // Start from the first template of that frameserver.
        return api("GET", "/api/frameserver/templates").then(list => {
          const tpl = list.find(x => x.kind === val);
          ed.settings.frameserver = { ...ed.settings.frameserver, script: tpl?.body ?? "" };
          renderFilters(body); saveAndRender();
        });
      }
      if (el.dataset.fs === "kind" || el.dataset.fs === "audioFromScript") { if (final) { renderFilters(body); saveAndRender(); } return; }
    } else if (el.dataset.a2) {
      let val = el.type === "checkbox" ? el.checked : el.value;
      if (el.dataset.a2 === "gainDb") { val = Number(val); $("#gain-val").textContent = val.toFixed(1) + " dB"; }
      if (el.dataset.a2 === "sampleRate") val = val === "" ? null : Number(val);
      ed.settings.audioFilters = { ...ed.settings.audioFilters, [el.dataset.a2]: val };
    } else return;
    final ? save() : save();
  };
  body.oninput = e => { if (e.target.type !== "checkbox" && e.target.tagName !== "SELECT") upd(e); };
  body.onchange = e => upd(e, true);
  body.onclick = guard(async e => {
    if (!e.target.closest("[data-a=autocrop]")) return;
    toast(t("Analysing black borders…"));
    await saveNow();
    const c = await api("GET", `/api/jobs/${ed.id}/cropdetect`);
    if (!c) { toast(t("No black borders found."), "ok"); return; }
    ed.settings.videoFilters = { ...ed.settings.videoFilters, crop: c };
    renderFilters(body); save();
    toast(t("Crop detected: {0}", `${c.left}/${c.top}/${c.right}/${c.bottom}`), "ok");
  });
}

// ---------------------------------------------------------------- output
function renderOutput(body) {
  const p = profileOf(ed.settings.profile);
  const src = ed.analysis.files[0];
  body.innerHTML = `
    <div class="two-col">
      <div><div id="out-form"></div></div>
      <div>
        ${p.isImages || p.isDemux ? `<label class="field"><span>${esc(t("Destination folder"))}</span><input id="out-folder" value="${esc(ed.edit.outputFolder ?? "")}" placeholder="${esc(t("Next to the source file"))}"></label>` : ""}
        ${p.isDemux ? `<div class="section"><h3>${esc(t("Streams to extract"))}</h3>
          ${src.streams.filter(s => s.kind !== "attachment").map(s => `<label class="check" style="display:flex;margin:4px 0"><input type="checkbox" data-ds="${s.index}" ${!ed.settings.demux.streams || ed.settings.demux.streams.includes(s.index) ? "checked" : ""}>
            <span class="kind ${s.kind}">${esc(kindLabel(s.kind))}</span> #${s.index} ${esc(s.codec)} ${esc(s.language ?? "")} ${esc(s.title ?? "")}</label>`).join("")}</div>` : ""}
        <div class="section"><h3>${esc(t("Preset"))}</h3>
          <div class="row"><input id="preset-name" placeholder="${esc(t("Preset name"))}" value="${esc(ed.settings.presetName ?? "")}">
          <button class="btn sm" data-a="save-preset">${esc(t("Save as preset"))}</button></div>
          <p class="muted small">${esc(t("Saves the settings of this file (not the tracks or the trim) for reuse."))}</p>
        </div>
        <div class="row"><button class="btn sm" data-a="cmd"><svg><use href="#i-term"/></svg><span>${esc(t("FFmpeg command"))}</span></button></div>
      </div>
    </div>`;
  renderSettingsForm($("#out-form", body), ed.settings, { full: true, allowedContainers: ed.analysis.allowedContainers, onChange: () => saveAndRender() });
  body.onchange = e => {
    if (e.target.id === "out-folder") { ed.edit.outputFolder = e.target.value.trim() || null; save(); }
    if (e.target.dataset.ds != null) {
      const all = src.streams.filter(s => s.kind !== "attachment").map(s => s.index);
      const chosen = $$("[data-ds]", body).filter(c => c.checked).map(c => Number(c.dataset.ds));
      ed.settings.demux = { ...ed.settings.demux, streams: chosen.length === all.length ? null : chosen };
      save();
    }
  };
  body.onclick = guard(async e => {
    const a = e.target.closest("[data-a]")?.dataset.a;
    if (a === "save-preset") {
      const name = $("#preset-name").value.trim();
      if (!name) throw new Error(t("The preset needs a name."));
      await post("/api/presets", { name, description: null, settings: ed.settings });
      toast(t("Preset \"{0}\" saved", name), "ok");
    }
    if (a === "cmd") { await saveNow(); showText(t("FFmpeg command"), await api("GET", `/api/jobs/${ed.id}/command`)); }
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
  body.oninput = e => {
    if (e.target.id !== "pv-range") return;
    ed.playhead = Number(e.target.value);
    $("#pv-t").textContent = fmtStamp(ed.playhead);
    load();
  };
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

export function editorJobChanged(job) {
  if (ed && ed.id === job.id) ed.job = job;
}
