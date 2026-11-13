// File manager (browse / download / upload on the server), resumable uploads and smart drag & drop.
import { $, $$, esc, state, t, api, post, toast, guard, fmtSize, fillSelect } from "./core.js";

const SUB = [".ass", ".ssa", ".srt", ".vtt", ".sup", ".sub", ".idx", ".smi", ".ttml", ".mks"];
const AUDIO = [".mka", ".aac", ".ac3", ".eac3", ".ec3", ".dts", ".dtshd", ".thd", ".mlp", ".flac", ".opus", ".ogg", ".mp3", ".wav", ".w64", ".m4a", ".wv", ".mp2", ".aiff", ".aif"];
const FONT = [".ttf", ".otf", ".ttc", ".otc", ".woff", ".woff2"];
const ext = n => (n.match(/\.[^.\\/]+$/)?.[0] ?? "").toLowerCase();
const stem = n => n.replace(/^.*[\\/]/, "").replace(/\.[^.]+$/, "");
const isVideo = n => state.options.extensions.includes(ext(n));
const isExternal = n => SUB.includes(ext(n)) || AUDIO.includes(ext(n)) || FONT.includes(ext(n));
/** "Ep01.ita.ass" belongs to "Ep01.mkv" (same rule as the server). */
export const belongsTo = (external, video) => {
  const v = stem(video).toLowerCase(), e = stem(external).toLowerCase();
  return v.length > 0 && e.startsWith(v) && (e.length === v.length || ". _-[(".includes(e[v.length]));
};
const kindIcon = k => ({ dir: "i-folder", root: "i-folder", disc: "i-disc", video: "i-film" }[k] ?? "i-file");
const sepOf = p => p.includes("\\") ? "\\" : "/";
const join = (dir, name) => dir + (dir.endsWith(sepOf(dir)) ? "" : sepOf(dir)) + name;

// ================================================================ file manager
const fm = { path: null, selected: new Set(), entries: [], writable: false };

export const openFiles = guard(async path => {
  await fmBrowse(path ?? fm.path);
  $("#dlg-files").showModal();
});

async function fmBrowse(path) {
  const r = await api("GET", "/api/browse?" + new URLSearchParams({ ...(path ? { path } : {}), mode: "all", hidden: $("#fm-hidden").checked }));
  fm.path = r.path; fm.entries = r.entries; fm.writable = r.writable; fm.selected.clear();
  const crumbs = [`<button type="button" data-p="">${esc(t("Roots"))}</button>`];
  if (r.path) {
    const sep = sepOf(r.path);
    const root = r.roots.find(x => r.path === x || r.path.startsWith(x.endsWith(sep) ? x : x + sep)) ?? "";
    crumbs.push(`<button type="button" data-p="${esc(root)}">${esc(root)}</button>`);
    let acc = root;
    for (const part of r.path.slice(root.length).split(/[\\/]/).filter(Boolean)) {
      acc = join(acc, part);
      crumbs.push(`<button type="button" data-p="${esc(acc)}">${esc(part)}</button>`);
    }
  }
  $("#fm-crumbs").innerHTML = crumbs.join(`<span class="muted">›</span>`) + (r.path && !r.writable ? `<span class="chip">${esc(t("read-only"))}</span>` : "");
  $("#fm-list").innerHTML = r.entries.map(e => `
    <div class="entry ${e.isDirectory ? "dir" : "file"} k-${e.kind}" data-p="${esc(e.path)}" data-dir="${e.isDirectory}" data-kind="${e.kind}" draggable="${r.path ? "true" : "false"}">
      <input type="checkbox" ${r.path ? "" : "disabled"}>
      <svg><use href="#${kindIcon(e.kind)}"/></svg>
      <span class="name">${esc(e.name)}${e.kind === "disc" ? ` <span class="chip">${esc(t("disc"))}</span>` : ""}</span>
      <span class="size">${e.isDirectory ? "" : fmtSize(e.size)}</span>
      <span class="row-actions">
        ${r.path ? `<a class="btn ghost icon sm" href="/api/files/download?path=${encodeURIComponent(e.path)}" download title="${esc(e.isDirectory ? t("Download as ZIP") : t("Download"))}"><svg><use href="#i-download"/></svg></a>` : ""}
        ${["video", "disc"].includes(e.kind) || (e.isDirectory && r.path) ? `<button class="btn ghost icon sm" data-q title="${esc(t("Add to queue"))}"><svg><use href="#i-plus"/></svg></button>` : ""}
      </span>
    </div>`).join("") || `<p class="muted" style="padding:16px">${esc(t("Empty folder."))}</p>`;
  $("#fm-upload").disabled = !r.writable;
  $("#fm-mkdir").disabled = !r.writable;
  fmSel();
}
function fmSel() {
  const n = fm.selected.size;
  $("#fm-sel").textContent = n ? t("{0} selected", n) : (fm.path ?? t("Choose a root"));
  $("#fm-download").disabled = n === 0;
  $("#fm-queue").disabled = n === 0;
}
$("#fm-hidden").onchange = guard(() => fmBrowse(fm.path));
$("#fm-close").onclick = () => $("#dlg-files").close();
$("#fm-crumbs").onclick = guard(async e => { const b = e.target.closest("button"); if (b) await fmBrowse(b.dataset.p || null); });
$("#fm-list").onclick = guard(async e => {
  const row = e.target.closest(".entry"); if (!row) return;
  if (e.target.closest("a")) return; // download link
  const p = row.dataset.p;
  if (e.target.closest("[data-q]")) return queuePaths([p]);
  const cb = $("input", row);
  if (e.target.matches("input")) { e.target.checked ? fm.selected.add(p) : fm.selected.delete(p); }
  else if (row.dataset.dir === "true") return fmBrowse(p);
  else if (!cb.disabled) { cb.checked = !cb.checked; cb.checked ? fm.selected.add(p) : fm.selected.delete(p); }
  fmSel();
});
$("#fm-download").onclick = () => {
  // One download per item (folders arrive as ZIP), spaced so the browser doesn't block them.
  [...fm.selected].forEach((p, i) => setTimeout(() => {
    const a = document.createElement("a");
    a.href = "/api/files/download?path=" + encodeURIComponent(p); a.download = ""; document.body.append(a); a.click(); a.remove();
  }, i * 400));
};
$("#fm-queue").onclick = guard(() => queuePaths([...fm.selected]));
$("#fm-mkdir").onclick = guard(async () => {
  const name = prompt(t("Folder name"));
  if (!name) return;
  await post("/api/files/mkdir", { path: join(fm.path, name.replace(/[\\/]/g, "_")) });
  await fmBrowse(fm.path);
});
$("#fm-upload").onclick = () => pickFiles(fm.path);
// Rows can be dragged onto the queue (or onto a job to attach subtitles/audio).
$("#fm-list").addEventListener("dragstart", e => {
  const row = e.target.closest(".entry"); if (!row) return;
  const paths = fm.selected.has(row.dataset.p) ? [...fm.selected] : [row.dataset.p];
  e.dataTransfer.setData("application/x-kna-paths", JSON.stringify(paths));
  e.dataTransfer.effectAllowed = "copy";
});

/** Server paths → smart drop (classify, confirm when needed, apply). */
export async function queuePaths(paths, targetJob = null) {
  const plan = await post("/api/drop/classify", { paths, targetJob });
  if (plan.needsConfirmation) {
    const lines = plan.items.map(i => `• ${i.path.split(/[\\/]/).pop()} — ${describe(i)}`).slice(0, 25).join("\n");
    if (!confirm(t("KnA will do this:") + "\n\n" + lines + (plan.items.length > 25 ? "\n…" : "") + "\n\n" + t("Continue?"))) return;
  }
  if (!plan.hasWork) { toast(plan.items.map(describe).join(" · ") || t("Nothing to do."), "err"); return; }
  const r = await post("/api/drop/apply", { paths, targetJob, settings: state.form });
  summary(r);
}
function describe(i) {
  return ({
    Video: t("queued"), Disc: t("disc: every title is queued"), Folder: t("folder: {0} video(s), {1} disc(s)", i.videos, i.discs),
    External: t("attached to its video"), Preset: t("preset imported"),
  })[i.kind] ?? i.reason ?? i.kind;
}
function summary(r) {
  const parts = [];
  if (r.added) parts.push(t("Added {0} file(s)", r.added));
  if (r.attached) parts.push(t("{0} attached", r.attached));
  if (r.presets) parts.push(t("{0} preset(s) imported", r.presets));
  toast(parts.join(" · ") || t("Nothing to do."), parts.length ? "ok" : "err");
  for (const m of r.messages ?? []) toast(m, "err");
}

// ================================================================ uploads
const up = { items: [], folder: null, running: false, abort: null, attachTo: null };

export function pickFiles(folder = null, attachTo = null) {
  const input = $("#upload-input");
  input.value = "";
  input.onchange = () => openUpload([...input.files].map(f => ({ file: f, rel: f.name })), folder, attachTo);
  input.click();
}

/** items: [{ file: File, rel: "sub/folder/name.ext" }] */
export async function openUpload(items, folder = null, attachTo = null) {
  if (!items.length) return;
  if (items.length > 500 && !confirm(t("{0} files: upload them all?", items.length))) return;
  const info = await api("GET", "/api/info");
  up.folder = folder ?? info.uploadDir;
  up.attachTo = attachTo;
  const queued = [...state.jobs.values()];
  const videos = items.filter(i => isVideo(i.rel));
  up.items = items.map(i => {
    const n = i.rel;
    const it = { ...i, name: n.split("/").pop(), size: i.file.size, sent: 0, state: "wait", note: "" };
    if (n.toLowerCase().endsWith(".json")) { it.kind = "preset"; it.note = t("preset: imported, not uploaded"); }
    else if (isVideo(n)) { it.kind = "video"; it.note = ext(n) === ".iso" ? t("disc image") : ""; }
    else if (isExternal(n)) {
      it.kind = "external";
      const v = attachTo ? null : videos.filter(x => belongsTo(n, x.rel)).sort((a, b) => stem(b.rel).length - stem(a.rel).length)[0];
      const j = attachTo ? state.jobs.get(attachTo) : v ? null : queued.filter(q => belongsTo(n, q.fileName)).sort((a, b) => stem(b.fileName).length - stem(a.fileName).length)[0];
      it.video = v; it.job = j?.id ?? null;
      it.note = v ? t("joins {0}", v.rel.split("/").pop()) : j ? t("joins the queued {0}", j.fileName) : t("no matching video: only uploaded");
    }
    else { it.kind = "other"; it.note = t("not a media file: only uploaded"); }
    return it;
  });
  if (maxBytes(info) && up.items.some(i => i.size > info.maxUploadBytes))
    toast(t("Some files are larger than this server accepts ({0}).", fmtSize(info.maxUploadBytes)), "err");
  // Videos first, so the subtitles/audio that belong to them find their job.
  up.items.sort((a, b) => ({ video: 0, external: 1, other: 2, preset: 3 }[a.kind] - { video: 0, external: 1, other: 2, preset: 3 }[b.kind]));
  $("#up-folder").value = up.folder;
  fillSelect($("#up-preset"), [{ value: "", label: t("Current settings (side panel)") }, ...state.presets.map(p => ({ value: p.name, label: "★ " + p.name }))], state.form.presetName ?? "");
  $("#up-title").textContent = attachTo ? t("Upload and attach to {0}", state.jobs.get(attachTo)?.fileName ?? "") : t("Upload");
  renderUpload();
  $("#dlg-upload").showModal();
}
const maxBytes = info => info.maxUploadBytes ?? 0;

function renderUpload() {
  const total = up.items.reduce((a, i) => a + i.size, 0), sent = up.items.reduce((a, i) => a + i.sent, 0);
  $("#up-list").innerHTML = up.items.map((i, n) => `
    <div class="up-item ${i.state}" data-n="${n}">
      <svg><use href="#${i.kind === "video" ? "i-film" : "i-file"}"/></svg>
      <div class="min0"><div class="ellipsis" title="${esc(i.rel)}">${esc(i.rel)}</div>
        <div class="muted small">${esc(i.note)}${i.error ? " — " + esc(i.error) : ""}</div>
        ${i.kind !== "preset" ? `<div class="bar"><i style="width:${i.size ? (100 * i.sent / i.size).toFixed(1) : 100}%"></i></div>` : ""}</div>
      <span class="small">${fmtSize(i.size)}</span>
      <span class="badge ${i.state === "done" ? "done" : i.state === "error" ? "error" : i.state === "run" ? "running" : "ready"}">${esc({ wait: t("Waiting"), run: t("Uploading"), done: t("Done"), error: t("Error") }[i.state])}</span>
    </div>`).join("");
  $("#up-total").textContent = `${fmtSize(sent)} / ${fmtSize(total)}`;
  $("#up-start").disabled = up.running || up.items.every(i => i.state === "done");
  $("#up-note").textContent = t("Uploads resume where they stopped: drop the same file again after a disconnection.");
}

$("#up-pick").onclick = () => {
  // Reuse the file manager to choose the destination.
  $("#dlg-upload").close();
  openFiles(up.folder).then(() => {
    const dlg = $("#dlg-files");
    const done = () => { dlg.removeEventListener("close", done); if (fm.path && fm.writable) up.folder = fm.path; $("#up-folder").value = up.folder; $("#dlg-upload").showModal(); };
    dlg.addEventListener("close", done);
    toast(t("Open the destination folder, then close this window."), "ok");
  });
};
$("#up-close").onclick = $("#up-cancel").onclick = () => {
  up.abort?.abort();
  $("#dlg-upload").close();
};
$("#up-start").onclick = guard(async () => {
  up.running = true; renderUpload();
  const preset = $("#up-preset").value, enqueue = $("#up-enqueue").checked;
  const sources = new Map(); // video rel → path on the server
  try {
    for (const it of up.items.filter(i => i.state !== "done")) {
      try {
        it.state = "run"; it.error = null; renderUpload();
        if (it.kind === "preset") {
          const p = JSON.parse(await it.file.text());
          const body = p.settings ? { name: p.name ?? stem(it.name), description: p.description ?? null, settings: p.settings } : { name: stem(it.name), description: null, settings: p };
          await post("/api/presets", body);
          it.state = "done"; continue;
        }
        const folder = it.rel.includes("/") ? join(up.folder, it.rel.split("/").slice(0, -1).join(sepOf(up.folder))) : up.folder;
        if (folder !== up.folder) await post("/api/files/mkdir", { path: folder });
        const path = await uploadOne(it, folder);
        const r = await post(`/api/uploads/${it.id}/complete`, {
          enqueue: enqueue && it.kind === "video", preset: preset || null, settings: preset ? null : state.form,
          attachTo: it.kind === "external" && (up.attachTo || it.job) ? (up.attachTo || it.job) : null,
          attachToSource: it.kind === "external" && it.video ? sources.get(it.video.rel) ?? null : null,
        });
        if (it.kind === "video") sources.set(it.rel, r.path);
        it.state = "done";
        it.note = r.attached ? t("attached") : r.added ? t("queued") : it.note;
      } catch (e) {
        if (e.name === "AbortError") throw e;
        it.state = "error"; it.error = e.message;
      }
      renderUpload();
    }
    const ok = up.items.filter(i => i.state === "done").length, bad = up.items.filter(i => i.state === "error").length;
    toast(t("{0} uploaded", ok) + (bad ? " · " + t("{0} failed", bad) : ""), bad ? "err" : "ok");
    if (!bad) setTimeout(() => $("#dlg-upload").open && !up.running && $("#dlg-upload").close(), 1200);
  } finally { up.running = false; renderUpload(); }
});

/** Sends one file in 16 MB chunks; resumes from the offset the server reports (409) and retries network errors. */
async function uploadOne(it, folder) {
  const c = await post("/api/uploads", { name: it.name, size: it.size, folder });
  it.id = c.id; it.sent = c.offset;
  const chunk = c.chunk || 16 << 20;
  let failures = 0;
  while (it.sent < it.size) {
    up.abort = new AbortController();
    const end = Math.min(it.size, it.sent + chunk);
    try {
      const res = await fetch(`/api/uploads/${it.id}?offset=${it.sent}`, { method: "PUT", body: it.file.slice(it.sent, end), signal: up.abort.signal, headers: { "Content-Type": "application/octet-stream" } });
      const j = await res.json().catch(() => ({}));
      if (res.status === 409) { it.sent = j.offset; continue; }
      if (!res.ok) throw new Error(j.detail || j.title || res.statusText);
      it.sent = j.offset; failures = 0;
      renderUpload();
    } catch (e) {
      if (e.name === "AbortError") { await api("DELETE", `/api/uploads/${it.id}`).catch(() => {}); throw e; }
      if (++failures > 5) throw e;
      await new Promise(r => setTimeout(r, 1000 * failures));
      const s = await api("GET", `/api/uploads/${it.id}`).catch(() => null);
      if (s) it.sent = s.offset;
    }
  }
  return folder;
}

// ================================================================ drag & drop
let dragDepth = 0;
const hasFiles = e => [...(e.dataTransfer?.types ?? [])].some(x => x === "Files" || x === "application/x-kna-paths");
document.addEventListener("dragenter", e => {
  if (!hasFiles(e) || document.querySelector("dialog[open]:not(#dlg-files)")) return;
  dragDepth++;
  const internal = e.dataTransfer.types.includes("application/x-kna-paths");
  $("#drop-hint").textContent = internal ? t("Drop on the queue to add, or on a job to attach.") : t("Videos are queued, subtitles / audio / fonts join their video, presets are imported.");
  $("#drop-overlay").hidden = internal; // server paths: the queue itself is the target
});
document.addEventListener("dragleave", () => { if (--dragDepth <= 0) { dragDepth = 0; $("#drop-overlay").hidden = true; $$(".job.drop-target").forEach(x => x.classList.remove("drop-target")); } });
document.addEventListener("dragover", e => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = "copy";
  const job = e.target.closest?.(".job");
  $$(".job.drop-target").forEach(x => x !== job && x.classList.remove("drop-target"));
  job?.classList.add("drop-target");
});
document.addEventListener("drop", guard(async e => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  dragDepth = 0;
  $("#drop-overlay").hidden = true;
  const job = e.target.closest?.(".job")?.dataset.id ?? null;
  $$(".job.drop-target").forEach(x => x.classList.remove("drop-target"));
  const internal = e.dataTransfer.getData("application/x-kna-paths");
  if (internal) {
    $("#dlg-files").close();
    return queuePaths(JSON.parse(internal), job);
  }
  const items = await collect(e.dataTransfer);
  if (job && items.some(i => isVideo(i.rel))) toast(t("Videos dropped on a job are uploaded and queued as new jobs."), "ok");
  await openUpload(items, null, job && items.every(i => !isVideo(i.rel)) ? job : null);
}));

/** Files and whole folders (Chrome, Edge, Firefox, Safari) with their relative paths. */
async function collect(dt) {
  const entries = [...dt.items ?? []].map(i => i.webkitGetAsEntry?.()).filter(Boolean);
  if (!entries.length) return [...dt.files].map(f => ({ file: f, rel: f.name }));
  const out = [];
  const walk = async (entry, prefix) => {
    if (entry.isFile) {
      const f = await new Promise((res, rej) => entry.file(res, rej));
      if (!f.name.startsWith(".")) out.push({ file: f, rel: prefix + f.name });
    } else if (entry.isDirectory) {
      const reader = entry.createReader();
      let batch;
      do {
        batch = await new Promise((res, rej) => reader.readEntries(res, rej));
        for (const child of batch) await walk(child, prefix + entry.name + "/");
      } while (batch.length);
    }
  };
  for (const en of entries) await walk(en, "");
  return out;
}
