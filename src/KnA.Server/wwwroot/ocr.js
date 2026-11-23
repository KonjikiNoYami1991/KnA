// OCR editor: picture subtitles (PGS, DVD) → text, like Subtitle Edit's OCR window, with confidence colours,
// a "to check" filter, italics per line, reading again only the selected lines, find/replace, Enter = keep and next.
import { $, $$, esc, state, t, api, toast, guard } from "./core.js";

let oc = null;   // { id, lines, cur, sel:Set, poll, onUsed }
const stamp = s => { const ms = Math.round(s * 1000); const h = Math.floor(ms / 3600000), m = Math.floor(ms / 60000) % 60, sec = Math.floor(ms / 1000) % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:${String(sec).padStart(2, "0")}.${String(ms % 1000).padStart(3, "0")}`; };
const parse = v => { const m = /^(?:(\d+):)?(\d+):(\d+(?:[.,]\d+)?)$/.exec((v ?? "").trim()); return m ? (+(m[1] ?? 0)) * 3600 + (+m[2]) * 60 + parseFloat(m[3].replace(",", ".")) : null; };
const toCheck = l => l.confidence >= 0 && (!l.text.trim() || l.confidence < 85);
const mark = l => l.confidence < 0 ? "" : !l.text.trim() || l.confidence < 60 ? "bad" : l.confidence < 85 ? "warn" : "ok";

export async function openOcr(jobId, track, onUsed) {
  $("#ocr-sub").textContent = `#${track.stream} ${track.codec ?? ""} ${track.language ?? ""}`;
  $("#ocr-list").innerHTML = `<p class="muted small" style="padding:10px">${esc(t("Reading…"))}</p>`;
  $("#dlg-ocr").showModal();
  const r = await api("POST", "/api/ocr/sessions", { jobId, file: track.file, stream: track.stream, copy: track.copy ?? 0 });
  oc = { id: r.id, lines: [], cur: 0, sel: new Set(), onUsed };
  const o = r.options ?? {};
  $("#ocr-engine").value = o.engine ?? "tesseract";
  $("#ocr-lang").value = o.language ?? "";
  $("#ocr-lang").placeholder = t("from the track ({0})", r.language ?? "und");
  $("#ocr-italic").checked = o.detectItalic !== false;
  $("#ocr-fix").checked = o.fixCommonErrors !== false;
  paddleCheck();
  await refresh();
  if (oc.lines.every(l => l.confidence < 0)) await run(null);
}

// PaddleOCR is not in the packages (about 1 GB): when chosen and missing, one click installs it here.
const paddleCheck = () => { $("#ocr-paddle").hidden = !($("#ocr-engine").value === "paddleocr" && !state.tools?.paddleOcr); };
$("#ocr-engine").onchange = paddleCheck;

const options = () => ({ engine: $("#ocr-engine").value, language: $("#ocr-lang").value.trim() || null,
  detectItalic: $("#ocr-italic").checked, fixCommonErrors: $("#ocr-fix").checked });

async function refresh() {
  const r = await api("GET", `/api/ocr/${oc.id}`);
  oc.lines = r.lines;
  $("#ocr-status").textContent = r.busy ? `${r.done} / ${oc.lines.length}` : t("{0} lines to check.", r.toCheck);
  renderList();
  show();
  return r;
}

function renderList() {
  const only = $("#ocr-only").checked;
  $("#ocr-list").innerHTML = oc.lines.filter(l => !only || toCheck(l)).map(l => `<div class="ocr-row ${l.index === oc.cur ? "on" : ""} ${oc.sel.has(l.index) ? "sel" : ""}" data-i="${l.index}">
    <span class="mk ${mark(l)}"></span><span class="muted">${l.index + 1}</span><span class="mono small">${stamp(l.start).slice(0, 8)} → ${stamp(l.end).slice(0, 8)}</span>
    <span class="tx">${esc(l.text.replace(/\n/g, " ") || (l.confidence < 0 ? "…" : t("(no text)")))}</span>
    <span class="muted small">${l.italic ? "𝘪" : ""}${l.forced ? " F" : ""}${l.edited ? " ✎" : ""}</span><span class="muted small">${l.confidence < 0 ? "" : Math.round(l.confidence)}</span></div>`).join("");
}

function show() {
  const l = oc.lines[oc.cur]; if (!l) return;
  $("#ocr-img").src = `/api/ocr/${oc.id}/image/${l.index}`;
  $("#ocr-text").value = l.text;
  $("#ocr-it").checked = l.italic;
  $("#ocr-start").value = stamp(l.start);
  $("#ocr-end").value = stamp(l.end);
}

async function commit(extra = {}) {
  const l = oc.lines[oc.cur]; if (!l) return;
  const text = $("#ocr-text").value.replace(/\r/g, "");
  if (text === l.text && !Object.keys(extra).length) return;
  oc.lines[oc.cur] = await api("PUT", `/api/ocr/${oc.id}/lines/${l.index}`, { text, ...extra });
  renderList();
}

function move(d) {
  const visible = oc.lines.filter(l => !$("#ocr-only").checked || toCheck(l)).map(l => l.index);
  const at = visible.indexOf(oc.cur);
  oc.cur = visible[Math.min(Math.max(at + d, 0), visible.length - 1)] ?? oc.cur;
  renderList(); show();
  $(`.ocr-row[data-i="${oc.cur}"]`)?.scrollIntoView({ block: "nearest" });
  $("#ocr-text").focus();
}

async function run(only) {
  await commit();
  const r = await api("POST", `/api/ocr/${oc.id}/run`, { options: options(), only });
  if (!r.started) return;
  clearInterval(oc.poll);
  oc.poll = setInterval(guard(async () => { const s = await refresh(); if (!s.busy) clearInterval(oc.poll); }), 1000);
}

$("#ocr-close").onclick = () => { clearInterval(oc?.poll); if (oc) api("DELETE", `/api/ocr/${oc.id}`).catch(() => {}); $("#dlg-ocr").close(); };
$("#ocr-only").onchange = () => renderList();
$("#ocr-list").onclick = e => {
  const row = e.target.closest(".ocr-row"); if (!row) return;
  commit();
  const i = +row.dataset.i;
  if (e.ctrlKey || e.metaKey) { oc.sel.has(i) ? oc.sel.delete(i) : oc.sel.add(i); }
  else { oc.sel = new Set([i]); oc.cur = i; show(); }
  renderList();
};
$("#ocr-text").onkeydown = e => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); commit().then(() => move(1)); } };
$("#ocr-text").onblur = () => commit();
$("#ocr-it").onchange = () => commit({ italic: $("#ocr-it").checked });
$("#ocr-start").onchange = () => { const v = parse($("#ocr-start").value); if (v != null) commit({ start: v }); };
$("#ocr-end").onchange = () => { const v = parse($("#ocr-end").value); if (v != null) commit({ end: v }); };
$("#dlg-ocr").addEventListener("keydown", e => {
  if (e.altKey && (e.key === "ArrowUp" || e.key === "ArrowDown")) { e.preventDefault(); commit().then(() => move(e.key === "ArrowUp" ? -1 : 1)); }
  if (e.ctrlKey && e.key.toLowerCase() === "i") { e.preventDefault(); $("#ocr-it").checked = !$("#ocr-it").checked; commit({ italic: $("#ocr-it").checked }); }
});
$("#dlg-ocr").onclick = guard(async e => {
  const a = e.target.closest("[data-a]")?.dataset.a; if (!a || !oc) return;
  if (a === "install-paddle") {
    const b = $("#ocr-paddle"); b.disabled = true;
    try {
      for (const id of ["paddleocr", "paddleocr-models"]) { $("#ocr-status").textContent = t("Installing {0}…", id); await api("POST", `/api/components/${id}/install`); }
      state.tools = (await api("GET", "/api/state")).tools;
      $("#ocr-status").textContent = t("Installed {0}.", "PaddleOCR");
    } finally { b.disabled = false; paddleCheck(); }
    return;
  }
  if (a === "run") return run(null);
  if (a === "run-sel") return run([...(oc.sel.size ? oc.sel : new Set([oc.cur]))]);
  if (a === "prev") { await commit(); move(-1); }
  if (a === "next") { await commit(); move(1); }
  if (a === "next-check") {
    await commit();
    const next = oc.lines.find(l => l.index > oc.cur && toCheck(l)) ?? oc.lines.find(toCheck);
    if (!next) return toast(t("Nothing left to check."), "ok");
    oc.cur = next.index; renderList(); show(); $(`.ocr-row[data-i="${oc.cur}"]`)?.scrollIntoView({ block: "nearest" });
  }
  if (a === "replace") {
    const r = await api("POST", `/api/ocr/${oc.id}/replace`, { find: $("#ocr-find").value, replace: $("#ocr-repl").value, regex: $("#ocr-rx").checked });
    toast(t("{0} lines changed.", r.changed), "ok"); await refresh();
  }
  if (a === "save" || a === "use") {
    await commit();
    const r = await api("POST", `/api/ocr/${oc.id}/save${a === "use" ? "?use=true" : ""}`);
    toast(t("OCR: {0} subtitles written to {1}.", oc.lines.filter(l => l.text.trim()).length, r.path), "ok");
    if (a === "use") { $("#ocr-close").click(); oc.onUsed?.(); }
  }
});
